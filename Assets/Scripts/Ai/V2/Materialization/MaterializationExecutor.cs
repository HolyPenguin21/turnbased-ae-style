using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MATERIALIZATION EXECUTOR
    // ===========================================================================================
    //  Runs one MaterializationPlan through the canonical gameplay APIs on the live world, in the
    //  order generate -> attach -> deploy, and a stand-alone Challenge (ExecuteGeneration).
    //
    //  Each step is taken against the live state and the next one is re-checked; a step that
    //  succeeded is never rolled back:
    //    · Challenge lost            -> AP/resources stay spent, the chain stops, the generator use
    //                                   is reported as attempted so the pass never retries it.
    //    · attach fails after a win  -> the generated card stays in hand, the chain stops.
    //    · deploy fails              -> whatever exists, exists; CardPlayExecutor keeps a created
    //                                   empty army as a reusable asset.
    //  ApSpent is the AP really spent (V2ActionOutcome.ApSpentSince), partial failures included.
    // ===========================================================================================
    public static class MaterializationExecutor
    {
        // The Research/Production mint step, shared by the chains here, the non-combat plays
        // (NonCombatPlayExecutor) and ExecuteGeneration: eligibility re-check, affordability,
        // Research reveal, the Challenge roll and a cap-exempt mint into hand on a win.
        internal readonly struct GenerationOutcome
        {
            public readonly bool Attempted;
            public readonly bool Success;
            public readonly CardData Minted;
            public readonly bool StateChanged;
            public readonly string FailReason;

            public GenerationOutcome(bool attempted, bool success, CardData minted,
                bool stateChanged, string failReason)
            {
                Attempted = attempted;
                Success = success;
                Minted = minted;
                StateChanged = stateChanged;
                FailReason = failReason;
            }
        }

        internal static GenerationOutcome TryGenerate(GenerationStep g, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, SpendAuthority authority = default)
        {
            if (g == null || g.CardDef == null || player == null || root == null || hand == null
                || ctx?.ResearchProductionCatalog == null)
                return new GenerationOutcome(false, false, null, false,
                    "no generation step/catalog/args");
            if (!object.ReferenceEquals(ctx.ResearchProductionCatalog.ResolveResult(g.ChallengeCard), g.CardDef))
                return new GenerationOutcome(false, false, null, false, "production output changed since planning");
            if (!ResearchProductionSystem.TryQuoteAttempt(player, root, g.Hero, g.FacilityHex, g.Mode,
                    g.ChallengeCard, ctx.ResearchProductionCatalog, out _, out _, resourceForecast: true))
                return new GenerationOutcome(false, false, null, false, "generation source changed");
            // AI-only reservation policy is checked BEFORE the shared gameplay transaction:
            // a protected resource may reject this plan, but must never reveal/pay first.
            int rateBeforeFunding = root.TradeRate;
            if (!GenerationSource.FitsReservedAffordability(root, player, ctx, g.ChallengeCard, authority)
                || !TradeFunding.Fund(player, root, ctx, g.GenerationResourceCost, authority))
                return new GenerationOutcome(false, false, null, root.TradeRate != rateBeforeFunding,
                    "generation resources reserved since planning");

            bool wasHidden = g.Hero != null && g.Hero.IsHidden;
            int ap0 = root.ActionPoints;
            int h0 = root.GetResource(ResourceType.Human), e0 = root.GetResource(ResourceType.Energy),
                m0 = root.GetResource(ResourceType.Materials), t0 = root.GetResource(ResourceType.Tech);

            // All gameplay-side revalidation, canonical-root validation, Research reveal and
            // irreversible payment are owned by the same transaction the human path uses.
            if (!ResearchProductionSystem.TryStartAttempt(player, root, g.Hero, g.FacilityHex,
                    g.Mode, g.ChallengeCard, ctx.ResearchProductionCatalog, out string why, out ProductionAttempt attempt))
                return new GenerationOutcome(false, false, null, root.TradeRate != rateBeforeFunding,
                    $"generation no longer valid ({why})");

            // Every paid attachment Challenge enters the same history, regardless of the
            // consumer or the roll result. Rejected attempts and existing hand cards do not.
            bool historyChanged = g.ChallengeCard.cardType == CardType.Equipment;
            if (historyChanged)
                DevelopmentDiversity.RecordAttempt(player, ctx.TurnNumber, g.CardDef);

            bool costMoved = ap0 != root.ActionPoints
                || h0 != root.GetResource(ResourceType.Human)
                || e0 != root.GetResource(ResourceType.Energy)
                || m0 != root.GetResource(ResourceType.Materials)
                || t0 != root.GetResource(ResourceType.Tech);

            ResearchProductionSystem.ChallengeOutcome outcome =
                ResearchProductionSystem.RollChallenge(g.Hero, g.ChallengeCard);
            if (!outcome.Success)
            {
                attempt.Complete(false);
                return new GenerationOutcome(true, false, null,
                    root.TradeRate != rateBeforeFunding || historyChanged || costMoved || (g.Mode == ResearchProductionMode.Research && wasHidden),
                    $"Challenge lost ({outcome.Successes}/{outcome.Required})");
            }

            CardData minted = attempt.Complete(true);
            hand.AddCard(minted);
            return new GenerationOutcome(true, true, minted, true, null);
        }

        // A Challenge as a whole action of its own (a Development upgrade's equipment, a missing
        // operator): the minted card stays in hand for a later play. One world receipt for a paid
        // attempt, won or lost.
        internal static MaterializationResult ExecuteGeneration(GenerationStep g, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, SpendAuthority authority = default)
        {
            int apBefore = root != null ? root.ActionPoints : 0;
            GenerationOutcome go = TryGenerate(g, player, root, hand, ctx, authority);
            var res = new MaterializationResult
            {
                GenerationAttempted = go.Attempted,
                Generated = go.Success,
                GeneratedCard = go.Minted,
                StateChanged = go.StateChanged,
                ApSpent = V2ActionOutcome.ApSpentSince(apBefore, root),
                AttemptedGenerationUseKey = go.Attempted ? g.UseKey : null,
                FailReason = go.Success ? null : go.FailReason,
            };
            res.StateVersionAfter = WorldDeltaLifecycle.StampAction(go.Attempted && go.StateChanged);
            return res;
        }

        public static MaterializationResult Execute(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, MaterializationPlan plan, ActorCommitments commitments,
            SpendAuthority authority = default)
        {
            var res = new MaterializationResult();
            if (plan == null || player == null || root == null || hand == null || ctx == null)
            {
                res.FailReason = "missing args";
                return res;
            }

            if (!MaterializationFeasibility.PreflightIfExisting(player, root, hand, ctx, plan))
            {
                res.PlacementStale = true;
                res.FailReason = "chain sources, slot or placement changed before payment";
                return res;
            }

            if (!plan.AttackRefitPrimaryId.HasValue && plan.Deploy.Army != null
                && !MaterializationDeliveryPolicy.CardPreservesAttackTaskRoute(snap, plan.Deploy.Army,
                    plan.BaseCardInHand?.Definition ?? plan.GeneratedBaseDef, AiPower.ProjectMaterialization(plan)))
            {
                res.PlacementStale = true;
                res.FailReason = "card placement loses the active Attack route before payment";
                return res;
            }
            if (plan.AttackRefitPrimaryId.HasValue
                && (snap == null || snap.TurnNumber != ctx.TurnNumber
                    || !AttackBaseRefitPolicy.Validate(plan, snap, player, WorldSnapshot.IntentsOf(snap),
                        out var refitHandoff, out _)
                    || !AttackBaseRefitPolicy.FollowupStillCurrent(plan, player, WorldSnapshot.IntentsOf(snap),
                        ctx, refitHandoff, root)
                    || !AttackBaseRefitPolicy.OnwardFunded(plan, player, root, ctx, plan.ApCost,
                        AttackBaseRefitPolicy.FinalRoster(AiV2Util.ResolveArmy(player,
                            plan.AttackRefitPrimaryId.Value), refitHandoff))
                    || StrategicSpendability.SpendableAp(player, root, ctx, authority)
                        < plan.ApCost + plan.AttackRefitFollowupAp))
            {
                res.PlacementStale = true;
                res.FailReason = "Attack refit changed or no funded onward activation";
                return res;
            }
            if (plan.Generation != null && !ResearchProductionSystem.TryQuoteAttempt(player, root,
                    plan.Generation.Hero, plan.Generation.FacilityHex, plan.Generation.Mode,
                    plan.Generation.ChallengeCard, ctx.ResearchProductionCatalog, out _, out _, resourceForecast: true))
            { res.FailReason = "generation source changed before funding"; return res; }
            int rateBeforeFunding = root.TradeRate;
            if (StrategicSpendability.SpendableAp(player, root, ctx, authority) < plan.ApCost)
            { res.FailReason = "chain AP changed"; return res; }
            bool funded = TradeFunding.Fund(player, root, ctx, plan.ResCost, authority);
            if (root.TradeRate != rateBeforeFunding)
            { res.StateChanged = true; res.StateVersionAfter = WorldDeltaLifecycle.Current; }
            if (!funded) { res.FailReason = "chain funding changed"; return res; }
            // Admitted against an earlier snapshot: re-check the whole chain with the feasibility
            // rule (ReservesOkAfterChain) before the first irreversible step.
            if (!StrategicSpendability.ReservesOkAfterChain(root, ctx, plan, player, authority))
            {
                res.FailReason = "chain no longer fits AP/spendable reserves";
                return res;
            }

            bool chainChanged = false;
            int apStart = root.ActionPoints;
            // One receipt per chain: CardPlayExecutor.Play stamps its own deployment (a partial one
            // included); the chain stamps only what no child stamped (generate/attach failures).
            void StampVersion(bool childAlreadyStamped = false) =>
                res.StateVersionAfter = WorldDeltaLifecycle.StampAction(chainChanged, childAlreadyStamped);

            // ---------------------------------------------------------------- 1. generate ----
            CardData generated = null;
            if (plan.Generation != null)
            {
                GenerationOutcome go = TryGenerate(plan.Generation, player, root, hand, ctx, authority);
                res.GenerationAttempted = go.Attempted;
                if (go.Attempted)
                    res.AttemptedGenerationUseKey = plan.Generation.UseKey;
                if (go.StateChanged) { res.StateChanged = true; chainChanged |= go.Attempted; }
                if (!go.Success)
                {
                    res.ApSpent = V2ActionOutcome.ApSpentSince(apStart, root);
                    StampVersion();
                    res.FailReason = go.FailReason;
                    return res;
                }
                generated = go.Minted;
                res.Generated = true;
                res.GeneratedCard = generated;
            }

            // ----------------------------------------------- 2. resolve base + equipment ----
            CardData baseCard = plan.GeneratedBaseDef != null ? generated : plan.BaseCardInHand;
            CardData equipmentCard = plan.GeneratedEquipmentDef != null ? generated : plan.EquipmentInHand;

            if (baseCard == null || !hand.Hand.Contains(baseCard))
            {
                res.ApSpent = V2ActionOutcome.ApSpentSince(apStart, root);
                StampVersion();
                res.FailReason = "base card missing after generation";
                return res;
            }

            // ---------------------------------------------------------------- 3. attach ----
            if (plan.UsesEquipment)
            {
                if (equipmentCard == null || equipmentCard == baseCard || !hand.Hand.Contains(equipmentCard))
                {
                    res.ApSpent = V2ActionOutcome.ApSpentSince(apStart, root);
                    StampVersion();
                    res.FailReason = "equipment card missing";
                    return res;
                }
                if (!EquipmentSystem.TryAttach(equipmentCard, baseCard, root, out string attachFail))
                {
                    res.ApSpent = V2ActionOutcome.ApSpentSince(apStart, root);
                    StampVersion();
                    res.FailReason = $"attach failed ({attachFail})";
                    return res;
                }
                // The executor owns the hand boundary — the same rule CardPlayExecutor holds for a
                // deploy: EquipmentSystem.TryAttach does not touch the hand; the equipment card
                // leaves it HERE, exactly once, only on a successful attach.
                hand.RemoveCard(equipmentCard);
                DevelopmentOutcomeTelemetry.RecordAttachment(player, ctx.TurnNumber, equipmentCard);
                res.Attached = true;
                res.StateChanged = true; chainChanged = true;
            }

            // ---------------------------------------------------------------- 4. deploy ----
            // The plan's placement is taken verbatim. If it no longer preflights (a mint just
            // happened, or an earlier chain took the slot) the plan is stale: report it for a
            // refresh + replan; never pick another placement here.
            CardPlayPlan deployPlan = plan.Deploy.Bind(baseCard);
            if (!CardPlayExecutor.Preflight(player, root, hand, ctx, deployPlan, out string preflightFail))
            {
                res.ApSpent = V2ActionOutcome.ApSpentSince(apStart, root);
                StampVersion();
                res.PlacementStale = true;
                res.FailReason = $"deploy placement stale after chain ({preflightFail ?? "preflight failed"}); "
                    + "caller must refresh and replan";
                return res;
            }

            CardPlayResult play = CardPlayExecutor.Play(player, root, hand, ctx, deployPlan);
            res.CardDeployed = play.Deployed;
            res.ApSpent = V2ActionOutcome.ApSpentSince(apStart, root);
            if (play.StateChanged)
            { res.StateChanged = true; chainChanged = true; }
            // CardPlayExecutor already stamped any deployment-side mutation.
            StampVersion(childAlreadyStamped: play.StateChanged);
            if (play.Deployed && plan.AttackRefitPrimaryId.HasValue)
            {
                bool handed = GroundCombatReinforcementTransaction.ApplyLocalRefit(plan, player, root, ctx, hand, play.ArmyShell, authority,
                    snap?.Intents);
                res.ApSpent = V2ActionOutcome.ApSpentSince(apStart, root);
                res.Deployed = handed;
                res.PlacementStale = !handed;
                res.FailReason = handed ? null : "refit handoff rejected; deployed card remains at base";
                StampVersion(childAlreadyStamped: true);
                return res;
            }
            res.Deployed = play.Deployed;
            if (!play.Deployed)
                res.FailReason = play.FailReason ?? "deploy failed";
            return res;
        }
    }
}

