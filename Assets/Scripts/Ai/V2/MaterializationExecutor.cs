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
    //    · Challenge lost            -> resources stay spent, chain stops, StateChanged if the
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
        // AI-MGR-01 review-r4 finding 9b — the Research/Production mint step, factored out so the
        // Phase-B non-combat lane can generate → deploy an Aviation / Base / Facility card too
        // (NonCombatCardPlayer owns that deploy; MaterializationExecutor only bodies Unit/Hero
        // chains). Same rules: eligibility re-check, hand slot, affordability, Research reveal,
        // ResourceCost-only (no AP), probabilistic Challenge, mint into hand on a win.
        public readonly struct GenerationOutcome
        {
            public readonly bool Success;
            public readonly CardData Minted;
            public readonly bool StateChanged;
            public readonly string FailReason;

            public GenerationOutcome(bool success, CardData minted, bool stateChanged, string failReason)
            {
                Success = success;
                Minted = minted;
                StateChanged = stateChanged;
                FailReason = failReason;
            }
        }

        internal static GenerationOutcome TryGenerate(GenerationStep g, PlayerSetupData player,
            PlayerRoot root, AiHandData hand)
        {
            if (g == null)
                return new GenerationOutcome(false, null, false, "no generation step");

            if (!ResearchProductionSystem.IsEligible(player, g.FacilityHex, g.Mode, out string why)
                || !ResearchProductionSystem.ActorStillQualifies(player, g.Hero, g.FacilityHex, g.Mode))
                return new GenerationOutcome(false, null, false,
                    $"generation no longer valid ({why ?? "hero moved"})");
            if (!hand.HasFreeSlot)
                return new GenerationOutcome(false, null, false, "no hand slot for the generated card");
            if (!ResearchProductionSystem.CanAffordCard(root, g.CardDef))
                return new GenerationOutcome(false, null, false, "generation resources unaffordable");

            bool wasHidden = g.Hero != null && g.Hero.IsHidden;
            int h0 = root.GetResource(ResourceType.Human), e0 = root.GetResource(ResourceType.Energy),
                m0 = root.GetResource(ResourceType.Materials), t0 = root.GetResource(ResourceType.Tech);

            // Research reveals the Researcher whether or not the roll wins (parity with
            // AiDevelopmentPlanner). Production never reveals.
            ResearchProductionSystem.ApplyResearchReveal(g.Mode, g.Hero);
            // ResourceCost only — the Challenge costs the player no AP. Never refunded.
            ResearchProductionSystem.PayCardCost(root, g.CardDef);

            bool resMoved = h0 != root.GetResource(ResourceType.Human)
                || e0 != root.GetResource(ResourceType.Energy)
                || m0 != root.GetResource(ResourceType.Materials)
                || t0 != root.GetResource(ResourceType.Tech);

            ResearchProductionSystem.ChallengeOutcome outcome =
                ResearchProductionSystem.RollChallenge(g.Hero, g.CardDef);
            if (!outcome.Success)
                return new GenerationOutcome(false, null,
                    resMoved || (g.Mode == ResearchProductionMode.Research && wasHidden),
                    $"Challenge lost ({outcome.Successes}/{outcome.Required})");

            CardData minted = ResearchProductionSystem.MintCard(g.CardDef);
            hand.Hand.Add(minted);
            return new GenerationOutcome(true, minted, true, null);
        }

        public static MaterializationResult Execute(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, MaterializationPlan plan, ActorCommitments commitments)
        {
            var res = new MaterializationResult();
            if (plan == null || player == null || root == null || hand == null || ctx == null)
            {
                res.FailReason = "missing args";
                return res;
            }

            int apStart = root.ActionPoints;

            // ---------------------------------------------------------------- 1. generate ----
            CardData generated = null;
            if (plan.Generation != null)
            {
                res.AttemptedGenerationUseKey = plan.Generation.UseKey;
                GenerationOutcome go = TryGenerate(plan.Generation, player, root, hand);
                if (go.StateChanged) res.StateChanged = true;
                if (!go.Success)
                {
                    res.ApSpent = apStart - root.ActionPoints;
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
                res.FailReason = "base card missing after generation";
                return res;
            }

            // ---------------------------------------------------------------- 3. attach ----
            if (plan.UsesEquipment)
            {
                if (equipmentCard == null || equipmentCard == baseCard || !hand.Hand.Contains(equipmentCard))
                {
                    res.ApSpent = apStart - root.ActionPoints;
                    res.FailReason = "equipment card missing";
                    return res;
                }
                if (!EquipmentSystem.TryAttach(equipmentCard, baseCard, root, out string attachFail))
                {
                    res.ApSpent = apStart - root.ActionPoints;
                    res.FailReason = $"attach failed ({attachFail})";
                    return res;
                }
                // The executor owns the hand boundary — the same rule CardPlayExecutor holds for a
                // deploy: EquipmentSystem.TryAttach does not touch the hand; the equipment card
                // leaves it HERE, exactly once, only on a successful attach.
                hand.Hand.Remove(equipmentCard);
                res.Attached = true;
                res.StateChanged = true;
            }

            // ---------------------------------------------------------------- 4. deploy ----
            CardPlayPlan deployPlan = plan.Deploy.Bind(baseCard);
            if (!CardPlayExecutor.Preflight(player, root, hand, ctx, deployPlan, out _))
            {
                // The pre-mint placement option is stale (a generated base never had a validated
                // one). Re-pick any legal option now.
                bool soloOnly = plan.FinalCapability == CapabilityKind.ScoutCapability;
                PlacementOption? fresh = PlacementSelector
                    .BuildOptions(snap, player, baseCard.Definition, commitments, soloOnly)
                    .Select(o => (PlacementOption?)o)
                    .FirstOrDefault(o => CardPlayExecutor.Preflight(player, root, hand, ctx, o.Value.Bind(baseCard), out _));
                if (fresh == null)
                {
                    res.ApSpent = apStart - root.ActionPoints;
                    res.FailReason = "no legal deployment after chain";
                    return res;
                }
                deployPlan = fresh.Value.Bind(baseCard);
            }

            CardPlayResult play = CardPlayExecutor.Play(player, root, hand, ctx, deployPlan);
            res.ApSpent = apStart - root.ActionPoints;
            if (play.StateChanged)
                res.StateChanged = true;
            res.Deployed = play.Deployed;
            if (!play.Deployed)
                res.FailReason = play.FailReason ?? "deploy failed";
            return res;
        }
    }
}
