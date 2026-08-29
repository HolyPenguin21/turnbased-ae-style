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
                GenerationStep g = plan.Generation;
                res.AttemptedGenerationUseKey = g.UseKey;

                if (!ResearchProductionSystem.IsEligible(player, g.FacilityHex, g.Mode, out string why)
                    || !ResearchProductionSystem.ActorStillQualifies(player, g.Hero, g.FacilityHex, g.Mode))
                {
                    res.FailReason = $"generation no longer valid ({why ?? "hero moved"})";
                    return res;
                }
                if (!hand.HasFreeSlot)
                {
                    res.FailReason = "no hand slot for the generated card";
                    return res;
                }
                if (!ResearchProductionSystem.CanAffordCard(root, g.CardDef))
                {
                    res.FailReason = "generation resources unaffordable";
                    return res;
                }

                bool wasHidden = g.Hero != null && g.Hero.IsHidden;
                int h0 = root.GetResource(ResourceType.Human), e0 = root.GetResource(ResourceType.Energy),
                    m0 = root.GetResource(ResourceType.Materials), t0 = root.GetResource(ResourceType.Tech);

                // Research reveals the Researcher — a consequence of choosing to Research, whether
                // or not the roll wins (parity with AiDevelopmentPlanner). Production never reveals.
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
                {
                    res.StateChanged = resMoved || (g.Mode == ResearchProductionMode.Research && wasHidden);
                    res.ApSpent = apStart - root.ActionPoints;
                    res.FailReason = $"Challenge lost ({outcome.Successes}/{outcome.Required})";
                    return res;
                }

                generated = ResearchProductionSystem.MintCard(g.CardDef);
                hand.Hand.Add(generated);
                res.Generated = true;
                res.StateChanged = true;
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
