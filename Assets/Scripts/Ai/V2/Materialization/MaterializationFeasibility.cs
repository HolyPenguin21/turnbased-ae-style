using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ARCH-02 §9 / DoD "Feasibility отделена от enumeration/scoring" — the per-chain feasibility
    // stage. It takes the RAW shapes MaterializationChainEnumerator produced and admits the ones
    // that are legally playable now (CardPlayExecutor.Preflight), fit the requesting axis's
    // discrete AP entitlement / real AP after the housekeeping reserve / the owner-aware spendable
    // persistent resources / a free hand slot (Phase A), or pass the reserves-after-chain guard
    // and the unresolved strategic-claim delivery gate (Phase B). No enumeration, no scoring.
    internal static class MaterializationFeasibility
    {
        // Phase A — admit the raw shapes for one demand, returning the scored-stage input tuple
        // (plan, followupAp, projected traits).
        internal static List<(MaterializationPlan plan, float followupAp, TraitPreference proj)> FilterForDemand(
            IReadOnlyList<MaterializationPlan> raw, PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, AxisDemand demand, AxisBudgetLedger ledger, float reservedFollowupAp)
        {
            float eps = AiConfigV2.allocatorSliceEpsilon;
            float axisBudget = ledger.DiscreteAdmissionBudget(demand.RequestingAxis);
            int stealthSurcharge = (demand.RequiredTraits & TraitPreference.Stealth) != 0
                ? AiConfigV2.scoutOptionalStealthAp : 0;

            var sink = new List<(MaterializationPlan plan, float followupAp, TraitPreference proj)>();
            foreach (MaterializationPlan p in raw)
            {
                if (p == null) continue;
                if (!PreflightIfExisting(player, root, hand, ctx, p))
                    continue;
                CardDefinition baseDef = p.BaseCardInHand?.Definition ?? p.GeneratedBaseDef;
                AddIfFeasibleA(sink, p, demand, baseDef, stealthSurcharge, reservedFollowupAp,
                    axisBudget, eps, root, hand, player, ctx);
            }
            return sink;
        }

        // Phase B — admit the raw surplus shapes: legally playable now, not blocked by an
        // unresolved strategic claim on its base card, hand-slot-feasible, reserves still OK.
        internal static List<MaterializationPlan> FilterSurplus(IReadOnlyList<MaterializationPlan> raw,
            PlayerSetupData player, PlayerRoot root, AiHandData hand, AiTurnContext ctx,
            MaterializationReservation reservation)
        {
            var sink = new List<MaterializationPlan>();
            foreach (MaterializationPlan p in raw)
            {
                if (p == null) continue;
                if (!PreflightIfExisting(player, root, hand, ctx, p))
                    continue;
                // §2 — if this card is still strategically relevant to an unresolved capability
                // demand, Phase B may only spend it on a placement that would actually deliver that
                // capability. Otherwise it stays in hand until Phase A resolves the demand.
                AxisDemand strategicClaim = UnresolvedClaimFor(reservation, p.FinalCapability, p.ProjectedAbilities);
                if (strategicClaim != null
                    && !MaterializationDeliveryPolicy.CanDeliverDemandOperationally(p, strategicClaim))
                    continue;
                if (p.HandSlotsNeededAtPeak > 0 && !hand.HasFreeSlot)
                    continue;
                if (!StrategicSpendability.ReservesOkAfterChain(root, ctx, p, player))
                    continue;
                sink.Add(p);
            }
            return sink;
        }

        // Direct / AttachDeploy carry an existing hand card and a fully-resolved placement — those
        // must still legally play right now. A Generate* shape has no card to preflight pre-mint
        // (MaterializationExecutor re-validates the placement after minting).
        private static bool PreflightIfExisting(PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx, MaterializationPlan p)
        {
            if (p.Generation != null || p.BaseCardInHand == null)
                return true;
            return CardPlayExecutor.Preflight(player, root, hand, ctx, p.Deploy.Bind(p.BaseCardInHand), out _);
        }

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

        // The best still-unresolved strategic demand a surplus card would be relevant to, or null.
        internal static AxisDemand UnresolvedClaimFor(MaterializationReservation reservation,
            CapabilityKind cap, IReadOnlyList<string> projectedAbilities)
        {
            if (reservation == null || reservation.UnresolvedDemands.Count == 0)
                return null;
            TraitPreference projTraits = MaterializationChainMatching.TraitsOf(projectedAbilities);
            // See MaterializationReservation.BestUnresolvedDemandFor — a still-deferred persistence
            // demand must not grant the strategic-claim ap/resource affordability relaxation either.
            return reservation.UnresolvedDemands
                .Where(d => d != null && !d.IsPersistenceDeferred && d.DesiredAmount > 0f && d.Capability == cap
                    && (projTraits & d.RequiredTraits) == d.RequiredTraits)
                .OrderByDescending(d => d.Value)
                .ThenBy(d => (int)d.RequestingAxis)
                .FirstOrDefault();
        }
    }
}
