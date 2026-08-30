using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    public sealed class StrategicPhaseResult
    {
        public bool StateChanged;
        public int CardsPlayed;
        public readonly Dictionary<DesireAxis, float> ApDebited = new Dictionary<DesireAxis, float>();
        public MaterializationReservation Reservation;

        public void AddDebit(DesireAxis a, float ap)
        {
            ApDebited.TryGetValue(a, out float cur);
            ApDebited[a] = cur + ap;
        }
    }

    // The single owner of V2 strategic Unit/Hero/Recce materialization. Phase A closes explicit
    // capability demands against the shared axis ledger; Phase B spends only genuinely remaining
    // capacity and never crosses hard AP/resource reserves.
    public static class StrategicManager
    {
        private sealed class DemandState
        {
            public AxisDemand Demand;
            public float Remaining;
            public int Ordinal;
            public bool Blocked;
        }

        private readonly struct PhaseACandidate
        {
            public readonly DemandState State;
            public readonly MaterializationPlan Plan;
            public readonly float FollowupAp;

            public PhaseACandidate(DemandState state, MaterializationPlan plan, float followupAp)
            {
                State = state;
                Plan = plan;
                FollowupAp = followupAp;
            }
        }

        public static StrategicPhaseResult FulfillDemands(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisBudgetLedger ledger,
            IReadOnlyList<AxisDemand> demands, ActorCommitments commitments)
        {
            var result = new StrategicPhaseResult { Reservation = new MaterializationReservation() };
            if (demands == null || demands.Count == 0 || player == null || root == null || hand == null || ledger == null)
                return result;

            var states = demands
                .Select((d, i) => new DemandState
                {
                    Demand = d,
                    Remaining = d != null ? Mathf.Max(0f, d.DesiredAmount) : 0f,
                    Ordinal = i,
                })
                .Where(s => s.Demand != null && s.Remaining > 0f)
                .ToList();
            if (states.Count == 0)
                return result;

            var reservedFollowupByAxis = new Dictionary<DesireAxis, float>();
            int chainAttempts = 0;
            while (chainAttempts < AiConfigV2.maxDemandFulfillmentActionsPerTurn)
            {
                List<DemandState> active = states.Where(s => !s.Blocked && s.Remaining > 0f).ToList();
                if (active.Count == 0)
                    break;

                CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
                var feasible = new List<PhaseACandidate>();

                foreach (DemandState state in active)
                {
                    AxisDemand demand = state.Demand;
                    reservedFollowupByAxis.TryGetValue(demand.RequestingAxis, out float reserved);
                    (MaterializationPlan plan, float followupAp)? pick = MaterializationCandidateBuilder.BestForDemand(
                        snap, player, root, hand, ctx, demand, ledger, commitments, reserved, result.Reservation, inv);
                    if (pick != null)
                        feasible.Add(new PhaseACandidate(state, pick.Value.plan, pick.Value.followupAp));
                }

                if (feasible.Count == 0)
                {
                    foreach (DemandState state in active)
                    {
                        AxisDemand d = state.Demand;
                        reservedFollowupByAxis.TryGetValue(d.RequestingAxis, out float reserved);
                        string diag = MaterializationDiagnostics.ExplainNoChain(
                            snap, player, root, hand, ctx, d, ledger, commitments, reserved);
                        AiDebugLog.Write($"[AI][V2]   strat.A — {d}: no feasible useful chain "
                            + $"({DesireAxes.Abbrev(d.RequestingAxis)} entitlement "
                            + $"{F(ledger.Balance(d.RequestingAxis))}, followup reserved {F(reserved)}); {diag}");
                    }
                    break;
                }

                PhaseACandidate selected = feasible
                    .OrderBy(c => ConsumesTraitRequiredByOtherFeasibleDemand(c, feasible) ? 1 : 0)
                    .ThenByDescending(ArbitrationScore)
                    .ThenByDescending(c => c.State.Demand.Value)
                    .ThenBy(c => (int)c.State.Demand.RequestingAxis)
                    .ThenBy(c => c.State.Ordinal)
                    .ThenBy(c => c.Plan.StableKey, System.StringComparer.Ordinal)
                    .First();

                AxisDemand chosenDemand = selected.State.Demand;
                MaterializationPlan plan = selected.Plan;
                MaterializationResult play = MaterializationExecutor.Execute(
                    snap, player, root, hand, ctx, plan, commitments);
                chainAttempts++;

                if (plan.Generation != null)
                    result.Reservation.RecordGenerationAttempt(plan.Generation, play);
                if (play.StateChanged)
                    result.StateChanged = true;
                if (play.ApSpent > 0f)
                {
                    ledger.Debit(chosenDemand.RequestingAxis, play.ApSpent);
                    result.AddDebit(chosenDemand.RequestingAxis, play.ApSpent);
                }

                if (!play.Deployed)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.A — {chosenDemand}: {plan.Kind} chain did not deploy "
                        + $"({play.FailReason}); gen={(play.Generated ? 1 : 0)} att={(play.Attached ? 1 : 0)}");
                    if (play.StateChanged)
                        snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                    if (!play.StateChanged && plan.Generation == null)
                        selected.State.Blocked = true;
                    continue;
                }

                reservedFollowupByAxis.TryGetValue(chosenDemand.RequestingAxis, out float alreadyReserved);
                reservedFollowupByAxis[chosenDemand.RequestingAxis] = alreadyReserved + selected.FollowupAp;
                float delivered = DeliveredCapabilityAmount(chosenDemand, plan);
                selected.State.Remaining = Mathf.Max(0f, selected.State.Remaining - delivered);
                result.CardsPlayed++;

                AiDebugLog.Write($"[AI][V2]   strat.A — {chosenDemand}: {plan.Kind} "
                    + $"@{plan.Deploy.Hex.Q},{plan.Deploy.Hex.R} "
                    + $"(ap {F(play.ApSpent)} -> {DesireAxes.Abbrev(chosenDemand.RequestingAxis)}, {plan.Deploy.Kind}, "
                    + $"followup {F(selected.FollowupAp)}ap reserved, {plan.StableKey})");

                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.A — {result.CardsPlayed} chain(s), ledger now " + ledger.DebugLine());
            return result;
        }

        private static bool ConsumesTraitRequiredByOtherFeasibleDemand(
            PhaseACandidate candidate, IReadOnlyList<PhaseACandidate> feasible)
        {
            TraitPreference spareTraits = candidate.Plan.ExpectedTraits & ~candidate.State.Demand.RequiredTraits;
            if (spareTraits == TraitPreference.None)
                return false;

            foreach (PhaseACandidate other in feasible)
            {
                if (ReferenceEquals(other.State, candidate.State))
                    continue;
                if ((other.State.Demand.RequiredTraits & spareTraits) != TraitPreference.None)
                    return true;
            }
            return false;
        }

        private static float DeliveredCapabilityAmount(AxisDemand demand, MaterializationPlan plan)
        {
            if (demand == null || plan == null)
                return 1f;
            switch (demand.Capability)
            {
                case CapabilityKind.FieldCombatPower:
                case CapabilityKind.GarrisonCombatPower:
                    CardDefinition d = plan.BaseCardInHand?.Definition ?? plan.GeneratedBaseDef;
                    return d != null ? Mathf.Max(1f, AiPower.ToPowerUnit(d).BasePower) : 1f;
                default:
                    return 1f;
            }
        }

        private static float ArbitrationScore(PhaseACandidate c) =>
            Mathf.Max(0f, c.State.Demand.Value) * Mathf.Max(0.0001f, c.Plan.Score);

        public static StrategicPhaseResult UseSurplus(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, ActorCommitments commitments,
            MaterializationReservation carriedReservation)
        {
            var result = new StrategicPhaseResult
            {
                Reservation = carriedReservation ?? new MaterializationReservation(),
            };
            if (player == null || root == null || hand == null || ctx == null)
                return result;

            for (int i = 0; i < AiConfigV2.maxSurplusActionsPerTurn; i++)
            {
                CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
                (MaterializationPlan plan, float utility)? pick = MaterializationCandidateBuilder.BestSurplus(
                    snap, player, root, hand, ctx, inv, commitments, result.Reservation);
                if (pick == null)
                    break;

                MaterializationPlan plan = pick.Value.plan;
                SurplusAdmission admission = SurplusAdmissionPolicy.Evaluate(root, player, plan);
                if (pick.Value.utility < admission.EffectiveThreshold)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — defer {plan.StableKey} util {F(pick.Value.utility)} "
                        + $"< threshold {F(admission.EffectiveThreshold)} (base {F(admission.BaseThreshold)}, "
                        + $"apSlack {F(admission.ApSlack)}, resSlack {F(admission.ResourceSlackFactor)}), stop");
                    break;
                }

                AiDebugLog.Write($"[AI][V2]   strat.B — admit {plan.StableKey} util {F(pick.Value.utility)} "
                    + $">= threshold {F(admission.EffectiveThreshold)} (base {F(admission.BaseThreshold)}, "
                    + $"apSlack {F(admission.ApSlack)}, resSlack {F(admission.ResourceSlackFactor)})");

                bool handWasFull = !hand.HasFreeSlot;
                MaterializationResult play = MaterializationExecutor.Execute(snap, player, root, hand, ctx, plan, commitments);
                if (plan.Generation != null)
                    result.Reservation.RecordGenerationAttempt(plan.Generation, play);
                if (play.StateChanged)
                    result.StateChanged = true;
                if (!play.Deployed)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — {plan.Kind} chain did not deploy ({play.FailReason}); stop");
                    if (play.StateChanged)
                        snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                    break;
                }
                result.CardsPlayed++;
                AiDebugLog.Write($"[AI][V2]   strat.B — {plan.Kind} util {F(pick.Value.utility)} "
                    + $"(ap {F(play.ApSpent)}, {plan.Deploy.Kind}, {plan.StableKey})");

                if (AiConfigV2.surplusAllowDraw && handWasFull && hand.HasFreeSlot
                    && root.ActionPoints - ctx.DrawApCost
                        >= AiConfigV2.housekeepingApReserve + AiConfigV2.surplusApReserve
                    && CardDrawExecutor.TryCycle(root, hand, ctx))
                    result.StateChanged = true;

                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.B — {result.CardsPlayed} surplus chain(s) played");
            return result;
        }

        internal static bool ReservesOkAfterChain(PlayerRoot root, MaterializationPlan plan)
        {
            if (root == null || plan == null)
                return false;
            float apAfter = root.ActionPoints - plan.ApCost;
            if (apAfter < AiConfigV2.housekeepingApReserve + AiConfigV2.surplusApReserve)
                return false;

            ResourceCost cost = plan.ResCost;
            if (cost == null)
                return true;
            PlayerSetupData player = root.Setup;
            return AiResourceReservation.Available(root, player, ResourceType.Human) - cost.human >= AiConfigV2.surplusHumanReserve
                && AiResourceReservation.Available(root, player, ResourceType.Energy) - cost.energy >= AiConfigV2.surplusEnergyReserve
                && AiResourceReservation.Available(root, player, ResourceType.Materials) - cost.materials >= AiConfigV2.surplusMaterialsReserve
                && AiResourceReservation.Available(root, player, ResourceType.Tech) - cost.tech >= AiConfigV2.surplusTechReserve;
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
