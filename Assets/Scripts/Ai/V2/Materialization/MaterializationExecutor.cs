using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MATERIALIZATION EXECUTOR  (Strategy V2 — Strategic Manager, Step 8B)
    // ===========================================================================================
    //  Runs one MaterializationPlan through the CANONICAL gameplay APIs, step by step, on the live
    //  world — never fabricating a card, mutating the hand outside the one boundary here, or
    //  simulating an Equipment/generation effect. Order: generate -> attach -> deploy.
    //
    //  LOGICAL ATOMICITY, NOT a transaction. The chain was fully preflighted and its whole cost
    //  reserved before this call. Each real action is then taken against the LIVE state: after a
    //  step, its actual result is used and the next step is re-checked. There is NO rollback of a
    //  gameplay action that already succeeded:
    //    · Challenge lost            -> AP/resources stay spent, chain stops, StateChanged if the
    //                                   world actually moved, generator use is reported as
    //                                   attempted so the pass never retries it.
    //    · attach fails after a win  -> the generated card stays in hand, chain stops.
    //    · deploy fails              -> whatever exists, exists; CardPlayExecutor already keeps a
    //                                   created empty army as a reusable asset.
    //  The real AP delta measured on PlayerRoot is reported, so the axis ledger stays honest even
    //  on a partial failure.
    // ===========================================================================================
    public static class MaterializationExecutor
    {
        // The Research/Production mint step, shared so the Phase-B non-combat lane can
        // generate → deploy an Aviation / Base / Facility card too
        // (NonCombatCardPlayer owns that deploy; MaterializationExecutor only bodies Unit/Hero
        // chains). Same rules: eligibility re-check, full AP/resource affordability, Research
        // reveal, probabilistic Challenge, and cap-exempt mint into hand on a win.
        public readonly struct GenerationOutcome
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
            // AI-only reservation policy is checked BEFORE the shared gameplay transaction:
            // a protected resource may reject this plan, but must never reveal/pay first.
            if (!GenerationSource.FitsReservedAffordability(root, player, ctx, g.CardDef, authority))
                return new GenerationOutcome(false, false, null, false,
                    "generation resources reserved since planning");

            bool wasHidden = g.Hero != null && g.Hero.IsHidden;
            int ap0 = root.ActionPoints;
            int h0 = root.GetResource(ResourceType.Human), e0 = root.GetResource(ResourceType.Energy),
                m0 = root.GetResource(ResourceType.Materials), t0 = root.GetResource(ResourceType.Tech);

            // All gameplay-side revalidation, canonical-root validation, Research reveal and
            // irreversible payment are owned by the same transaction the human path uses.
            if (!ResearchProductionSystem.TryStartAttempt(player, root, g.Hero, g.FacilityHex,
                    g.Mode, g.CardDef, ctx.ResearchProductionCatalog, out string why))
                return new GenerationOutcome(false, false, null, false,
                    $"generation no longer valid ({why})");

            // Every paid attachment Challenge enters the same history, regardless of the
            // consumer or the roll result. Rejected attempts and existing hand cards do not.
            bool historyChanged = g.CardDef.cardType == CardType.Equipment;
            if (historyChanged)
                DevelopmentDiversity.RecordAttempt(player, ctx.TurnNumber, g.CardDef);

            bool costMoved = ap0 != root.ActionPoints
                || h0 != root.GetResource(ResourceType.Human)
                || e0 != root.GetResource(ResourceType.Energy)
                || m0 != root.GetResource(ResourceType.Materials)
                || t0 != root.GetResource(ResourceType.Tech);

            ResearchProductionSystem.ChallengeOutcome outcome =
                ResearchProductionSystem.RollChallenge(g.Hero, g.CardDef);
            if (!outcome.Success)
                return new GenerationOutcome(true, false, null,
                    historyChanged || costMoved || (g.Mode == ResearchProductionMode.Research && wasHidden),
                    $"Challenge lost ({outcome.Successes}/{outcome.Required})");

            CardData minted = ResearchProductionSystem.MintCard(g.CardDef);
            hand.AddCard(minted);
            return new GenerationOutcome(true, true, minted, true, null);
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

            // The candidate was admitted against a previous snapshot. Check the whole canonical
            // chain again BEFORE its first irreversible step so a mid-turn resource change cannot
            // make this executor spend another axis's newly protected resources. No substitute
            // budget model: the same ReservesOkAfterChain is used by feasibility.
            if (!StrategicSpendability.ReservesOkAfterChain(root, ctx, plan, player, authority))
            {
                res.FailReason = "chain no longer fits AP/spendable reserves";
                return res;
            }

            if (!MaterializationFeasibility.PreflightIfExisting(player, root, hand, ctx, plan))
            {
                res.PlacementStale = true;
                res.FailReason = "chain sources, slot or placement changed before payment";
                return res;
            }

            if (plan.AttackRefitPrimaryId.HasValue
                && (snap == null || snap.TurnNumber != ctx.TurnNumber
                    || !AttackBaseRefitPolicy.Validate(plan, snap, player, out var refitHandoff, out _)
                    || !AttackBaseRefitPolicy.FollowupStillCurrent(plan, player, ctx, refitHandoff, root)
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
            int apStart = root.ActionPoints;
            int h0 = root.GetResource(ResourceType.Human), e0 = root.GetResource(ResourceType.Energy),
                m0 = root.GetResource(ResourceType.Materials), t0 = root.GetResource(ResourceType.Tech);
            // A standalone CardPlayExecutor.Play already stamps a deployment (including a partial
            // failed deploy that changed AP/resources or created an army). An enclosing chain must
            // stamp only when no child has done so, while still stamping generate/attach-only
            // partial failures. Both execution paths report the same current version.
            void StampResources(bool childAlreadyStamped = false)
            {
                int dh = h0 - root.GetResource(ResourceType.Human), de = e0 - root.GetResource(ResourceType.Energy),
                    dm = m0 - root.GetResource(ResourceType.Materials), dt = t0 - root.GetResource(ResourceType.Tech);
                res.ResourcesSpent = (dh | de | dm | dt) == 0
                    ? null : new ResourceCost { human = dh, energy = de, materials = dm, tech = dt };
                res.StateVersionAfter = WorldDeltaLifecycle.StampAction(res.StateChanged, childAlreadyStamped);
            }

            // ---------------------------------------------------------------- 1. generate ----
            CardData generated = null;
            if (plan.Generation != null)
            {
                GenerationOutcome go = TryGenerate(plan.Generation, player, root, hand, ctx, authority);
                res.GenerationAttempted = go.Attempted;
                if (go.Attempted)
                    res.AttemptedGenerationUseKey = plan.Generation.UseKey;
                if (go.StateChanged) res.StateChanged = true;
                if (!go.Success)
                {
                    res.ApSpent = apStart - root.ActionPoints;
                    StampResources();
                    res.FailReason = go.FailReason;
                    return res;
                }
                generated = go.Minted;
                res.Generated = true;
            }

            // ----------------------------------------------- 2. resolve base + equipment ----
            CardData baseCard = plan.GeneratedBaseDef != null ? generated : plan.BaseCardInHand;
            CardData equipmentCard = plan.GeneratedEquipmentDef != null ? generated : plan.EquipmentInHand;

            if (baseCard == null || !hand.Hand.Contains(baseCard))
            {
                res.ApSpent = apStart - root.ActionPoints;
                StampResources();
                res.FailReason = "base card missing after generation";
                return res;
            }

            // ---------------------------------------------------------------- 3. attach ----
            if (plan.UsesEquipment)
            {
                if (equipmentCard == null || equipmentCard == baseCard || !hand.Hand.Contains(equipmentCard))
                {
                    res.ApSpent = apStart - root.ActionPoints;
                    StampResources();
                    res.FailReason = "equipment card missing";
                    return res;
                }
                if (!EquipmentSystem.TryAttach(equipmentCard, baseCard, root, out string attachFail))
                {
                    res.ApSpent = apStart - root.ActionPoints;
                    StampResources();
                    res.FailReason = $"attach failed ({attachFail})";
                    return res;
                }
                // The executor owns the hand boundary — the same rule CardPlayExecutor holds for a
                // deploy: EquipmentSystem.TryAttach does not touch the hand; the equipment card
                // leaves it HERE, exactly once, only on a successful attach.
                hand.RemoveCard(equipmentCard);
                DevelopmentOutcomeTelemetry.RecordAttachment(player, ctx.TurnNumber, equipmentCard);
                res.Attached = true;
                res.StateChanged = true;
            }

            // ---------------------------------------------------------------- 4. deploy ----
            // ARCH-02 §35 — the executor takes the plan's placement verbatim. If it no longer
            // preflights (a generate step just minted, or an earlier chain this pass consumed the
            // slot), that is a STALE PLAN: report it and let the caller refresh + replan. The
            // executor never enumerates a replacement placement of its own.
            CardPlayPlan deployPlan = plan.Deploy.Bind(baseCard);
            if (!CardPlayExecutor.Preflight(player, root, hand, ctx, deployPlan, out string preflightFail))
            {
                res.ApSpent = apStart - root.ActionPoints;
                StampResources();
                res.PlacementStale = true;
                res.FailReason = $"deploy placement stale after chain ({preflightFail ?? "preflight failed"}); "
                    + "caller must refresh and replan";
                return res;
            }

            CardPlayResult play = CardPlayExecutor.Play(player, root, hand, ctx, deployPlan);
            res.CardDeployed = play.Deployed;
            res.ApSpent = apStart - root.ActionPoints;
            if (play.StateChanged)
                res.StateChanged = true;
            // CardPlayExecutor has already bumped for any deployment-side mutation. Do not bump
            // twice for the same chain; generation/attachment-only failures still bump above.
            StampResources(childAlreadyStamped: play.StateChanged);
            if (play.Deployed && plan.AttackRefitPrimaryId.HasValue)
            {
                bool handed = GroundCombatReinforcementTransaction.ApplyLocalRefit(plan, player, root, ctx, hand, play.ArmyShell, authority);
                res.ApSpent = apStart - root.ActionPoints;
                res.Deployed = handed;
                res.PlacementStale = !handed;
                res.FailReason = handed ? null : "refit handoff rejected; deployed card remains at base";
                StampResources(childAlreadyStamped: true);
                res.ArmyCreated = play.ArmyCreated;
                return res;
            }
            res.ArmyCreated = play.ArmyCreated;
            res.Deployed = play.Deployed;
            if (!play.Deployed)
                res.FailReason = play.FailReason ?? "deploy failed";
            return res;
        }
    }
}
