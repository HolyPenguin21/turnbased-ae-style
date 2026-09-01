using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Diagnostics-only probe. It never chooses or executes a plan and therefore cannot diverge
    // strategic policy; it reports which broad gate prevented Phase A from finding any chain.
    internal static class MaterializationDiagnostics
    {
        public static string ExplainNoChain(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root,
            AiHandData hand, AiTurnContext ctx, AxisDemand demand, AxisBudgetLedger ledger,
            ActorCommitments commitments, float reservedFollowup)
        {
            if (demand == null || hand == null)
                return "diag unavailable";

            int matching = 0, traitMatching = 0, placements = 0, preflight = 0;
            int opDeliver = 0, resReject = 0;
            float minDirectNeed = float.PositiveInfinity;
            var failures = new Dictionary<string, int>();
            bool solo = demand.Capability == CapabilityKind.ScoutCapability;
            foreach (CardData card in hand.Hand.Where(c => c?.Definition != null))
            {
                CardDefinition def = card.Definition;
                bool recce = AbilityParams.AbilitiesHaveAnyRecce(def.grantedAbilities);
                bool cap = demand.Capability == CapabilityKind.ScoutCapability ? recce
                    : demand.Capability == CapabilityKind.Hero ? def.cardType == CardType.Hero && !recce
                    : !recce && (def.cardType == CardType.Unit || def.cardType == CardType.Hero);
                if (!cap || def.isAviation)
                    continue;
                matching++;

                bool needsStealth = (demand.RequiredTraits & TraitPreference.Stealth) != 0;
                IReadOnlyList<string> projectedAbilities = def.grantedAbilities;
                bool hasStealth = AbilityParams.AbilitiesHaveAnyStealth(projectedAbilities);
                if (card.Equipment?.equipment != null)
                {
                    projectedAbilities = EquipmentSystem.EffectiveAbilities(def.grantedAbilities, card.Equipment.equipment);
                    hasStealth = AbilityParams.AbilitiesHaveAnyStealth(projectedAbilities);
                }
                if (needsStealth && !hasStealth)
                    continue;
                traitMatching++;

                foreach (PlacementOption opt in PlacementSelector.BuildOptions(snap, player, def, commitments, solo))
                {
                    placements++;
                    if (CardPlayExecutor.Preflight(player, root, hand, ctx, opt.Bind(card), out string reason))
                    {
                        preflight++;
                        int stealthSurcharge = needsStealth ? AiConfigV2.scoutOptionalStealthAp : 0;
                        float deployAp = card.EffectivePlayApCost
                            + (opt.Kind == DeploymentKind.NewArmy ? ArmyActions.CreateArmyApCost : 0f);
                        var diagnosticPlan = new MaterializationPlan
                        {
                            BaseCardInHand = card,
                            ProjectedAbilities = projectedAbilities,
                            Deploy = opt,
                            FinalCapability = demand.Capability,
                        };
                        // §15 — mirror the REAL candidate builder's next gates so the reported
                        // postGate cannot say "passes" while every candidate is discarded here.
                        if (!MaterializationCandidateBuilder.CanDeliverDemandOperationally(diagnosticPlan, demand))
                        {
                            string k = $"{card.Definition.displayName}: {opt.Kind} cannot operationally deliver {demand.Capability}";
                            failures.TryGetValue(k, out int oc);
                            failures[k] = oc + 1;
                            continue;
                        }
                        opDeliver++;
                        ResourceCost chainCost = Game.Ai.AiCardCost.PlayResources(card);
                        if (chainCost != null && !Game.Ai.AiCardCost.CanAffordPlayResources(root, player, card))
                        {
                            resReject++;
                            continue;
                        }
                        float followupAp = CapabilityQualityEvaluator.ProjectedActivationApCost(diagnosticPlan)
                            + stealthSurcharge + demand.MinimumFollowupAp;
                        minDirectNeed = System.Math.Min(minDirectNeed, deployAp + reservedFollowup + followupAp);
                        continue;
                    }

                    string detailed = DetailFailure(root, player, card, reason);
                    failures.TryGetValue(detailed, out int count);
                    failures[detailed] = count + 1;
                }
            }

            float axis = ledger != null ? ledger.Balance(demand.RequestingAxis) : 0f;
            float discrete = ledger != null ? ledger.DiscreteAdmissionBudget(demand.RequestingAxis) : axis;
            int ap = root != null ? root.ActionPoints : 0;
            string failText = failures.Count == 0
                ? "-"
                : string.Join(" | ", failures.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                    .Select(kv => kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key));

            string postGate;
            string directNeed = "-";
            if (!float.IsPositiveInfinity(minDirectNeed))
            {
                directNeed = minDirectNeed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                float eps = AiConfigV2.allocatorSliceEpsilon;
                if (minDirectNeed > discrete + eps)
                    postGate = "axis-budget";
                else if (root != null && root.ActionPoints - minDirectNeed - AiConfigV2.housekeepingApReserve < -eps)
                    postGate = "global-ap";
                else
                    postGate = "direct-passes-post-preflight";
            }
            else if (preflight > 0 && opDeliver == 0)
                postGate = "operational-delivery-gate";  // §15 — preflight passed, no placement can deliver
            else if (preflight > 0 && resReject > 0 && resReject == opDeliver)
                postGate = "chain-resources";
            else
                postGate = "-";

            return $"diag hand={hand.Hand.Count} {AiCardLog.Hand(hand)} freeSlot={(hand.HasFreeSlot ? 1 : 0)} "
                + $"match={matching} trait={traitMatching} placements={placements} preflight={preflight} "
                + $"opDeliver={opDeliver} resReject={resReject} "
                + $"fails=[{failText}] directNeedMin={directNeed} postGate={postGate} "
                + $"axis={axis:0.##} discrete={discrete:0.##} ap={ap} followupReserved={reservedFollowup:0.##}";
        }

        private static string DetailFailure(PlayerRoot root, PlayerSetupData player, CardData card, string reason)
        {
            string cardName = card?.Definition?.displayName ?? "?";
            if (root == null || player == null || card == null)
                return $"{cardName}: {reason ?? "preflight rejected"}";

            ResourceCost cost = Game.Ai.AiCardCost.PlayResources(card);
            if (cost != null && !Game.Ai.AiCardCost.CanAffordPlayResources(root, player, card))
            {
                return $"{cardName}: resources need H/E/M/T={cost.human}/{cost.energy}/{cost.materials}/{cost.tech} "
                    + $"have={Available(ResourceType.Human)}/{Available(ResourceType.Energy)}/"
                    + $"{Available(ResourceType.Materials)}/{Available(ResourceType.Tech)}";
            }

            return $"{cardName}: {reason ?? "preflight rejected"}";

            int Available(ResourceType type) =>
                UnityEngine.Mathf.FloorToInt(Game.Ai.AiResourceReservation.Available(root, player, type));
        }
    }
}
