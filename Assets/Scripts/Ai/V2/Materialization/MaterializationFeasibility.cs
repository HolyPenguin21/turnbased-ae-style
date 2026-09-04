using System.Collections.Generic;
using Game.Cards;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ARCH-02 §9 / DoD "Feasibility отделена от enumeration/scoring" — the Phase-A per-chain
    // feasibility gate. Given an already-constructed plan it decides whether the chain fits the
    // requesting axis's discrete AP entitlement, real AP after the housekeeping reserve, the
    // owner-aware spendable persistent resources and a free hand slot, and whether the placement
    // could operationally deliver the demand. No enumeration, no scoring. Body verbatim.
    internal static class MaterializationFeasibility
    {
        internal static void AddIfFeasibleA(
            List<(MaterializationPlan plan, float followupAp, TraitPreference proj)> sink,
            MaterializationPlan p, AxisDemand demand, CardDefinition baseDef, int stealthSurcharge,
            float reservedFollowupAp, float axisBudget, float eps, PlayerRoot root, AiHandData hand,
            PlayerSetupData player, AiTurnContext ctx)
        {
            // Operational shortages may not spend a card on a placement whose live capability delta
            // is known in advance to be zero. Garrison placement is preparation, not Field/Hero
            // delivery; a solo Hero shell/new army is likewise reserve-only until it has an escort.
            if (!MaterializationDeliveryPolicy.CanDeliverDemandOperationally(p, demand))
                return;

            float activationAp = p != null
                ? CapabilityQualityEvaluator.ProjectedActivationApCost(p)
                : (baseDef != null ? baseDef.activationApCost : AiConfigV2.scoutNotionalActivationAp);
            float followupAp = activationAp + stealthSurcharge + demand.MinimumFollowupAp;
            float need = p.ApCost + reservedFollowupAp + followupAp;
            if (need > axisBudget + eps) return;
            if (root.ActionPoints - need - AiConfigV2.housekeepingApReserve < -eps) return;
            if (!StrategicSpendability.FitsSpendableResources(player, root, ctx, p.ResCost)) return;
            if (p.HandSlotsNeededAtPeak > 0 && !hand.HasFreeSlot) return;
            sink.Add((p, followupAp, p.ExpectedTraits));
        }
    }
}
