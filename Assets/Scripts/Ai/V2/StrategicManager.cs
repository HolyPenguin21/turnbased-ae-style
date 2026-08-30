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
    // ===========================================================================================
    //  STRATEGIC MANAGER  (Strategy V2 — centralized card play + capability preparation)
    // ===========================================================================================
    //  NOT a DesireAxis, NO radar slice. The single owner of V2 strategic Unit/Hero/Recce
    //  materialization decisions. Strategic axes only expose AxisDemand[] — they never choose a
    //  card, generate one, attach equipment, create an army, pick a placement, or touch the hand.
    //
    //  Step 8B: a demand is closed by the best COMPLETE materialization chain, not a single card.
    //  Four chain shapes, at most one generation + one attach + one deploy each
    //  (MaterializationPlan): Direct / AttachDeploy / GenerateDeploy / GenerateAttachDeploy.
    //  Generation = the existing Research/Production mechanism, and ONLY when a qualifying Hero
    //  already stands on the Facility this turn (no positioning, no multi-turn planning). The
    //  whole chain's AP + R/H/M/T cost is what is compared and reserved; RequiredTraits are a
    //  hard feasibility gate on the projected END result, PreferredTraits only a ranking bonus.
    //
    //    Phase A — FulfillDemands (BEFORE mission planning). Every iteration asks ALL still-unmet
    //              demands for their best currently-feasible complete chain, then arbitrates the
    //              resulting (Demand, Plan) pairs globally. A plan that would consume a trait
    //              required by another feasible demand, while its own demand does not require that
    //              trait, is protected behind the constrained demand. This prevents a generic job
    //              from consuming the only Stealth-capable card/equipment before a Stealth-required
    //              job. The chosen chain executes through MaterializationExecutor and its REAL AP
    //              delta is charged to demand.RequestingAxis. Follow-up AP is RESERVED, not spent.
    //              Bounded by maxDemandFulfillmentActionsPerTurn and maxGenerationActionsPerTurn.
    //
    //    Phase B — UseSurplus (AFTER mission execution + operational refresh). Bounded greedy over
    //              GENUINELY remaining real AP/resources; may also proactively generate / attach.
    //              The same MaterializationReservation is carried from Phase A, so the shared
    //              generation-attempt cap and exact attempted-card set survive the phase boundary.
    //              Refreshes the operational snapshot after every successful chain.
    //
    //  Reusable-army policy: an empty ArmyData is a paid, reusable asset. For a solo (Recce /
    //  ScoutCapability) card only a shell-at-hex or a fresh army is legal; for a plain Unit/Hero
    //  an existing suitable army / garrison with room is preferred over paying CreateArmy AP.
    // ===========================================================================================
    public sealed class StrategicPhaseResult
    {
        public bool StateChanged;
        public int CardsPlayed;
        public readonly Dictionary<DesireAxis, float> ApDebited = new Dictionary<DesireAxis, float>();

        // Pass-local generation-attempt ownership. Phase A creates it; the pipeline carries the
        // SAME instance into Phase B so the exact (hero, facility, mode, card) combination is not
        // retried and the shared per-turn generation-attempt cap cannot be exceeded.
        public MaterializationReservation Reservation;

        public void AddDebit(DesireAxis a, float ap)
        {
            ApDebited.TryGetValue(a, out float cur);
            ApDebited[a] = cur + ap;
        }
    }

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

        // ----------------------------------------------------------------- PHASE A ----
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

            // ACCUMULATIVE follow-up reservation, per axis, across every fulfilled demand this phase.
            var reservedFollowupByAxis = new Dictionary<DesireAxis, float>();

            int chainAttempts = 0;
            while (chainAttempts < AiConfigV2.maxDemandFulfillmentActionsPerTurn)
            {
                List<DemandState> active = states.Where(s => !s.Blocked && s.Remaining > 0f).ToList();
                if (active.Count == 0)
                    break;

                CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);
                var feasible = new List<PhaseACandidate>();

                // GLOBAL arbitration input: ask every unmet demand for its best chain against the
                // SAME live state before executing anything.
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
                        AiDebugLog.Write($"[AI][V2]   strat.A — {d}: no feasible useful chain "
                            + $"({DesireAxes.Abbrev(d.RequestingAxis)} entitlement "
                            + $"{F(ledger.Balance(d.RequestingAxis))}, followup reserved {F(reserved)})");
                    }
                    break;
                }

                // SCOR conflict rule: a less-constrained demand may not consume a projected trait
                // that another CURRENTLY FEASIBLE unmet demand requires. This is deliberately
                // lexicographic, not a soft score: losing the only hard-trait supply is irreversible
                // for the rest of the pass, while delaying a generic demand by one iteration is not.
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

                    // Re-plan the same demand when the chain changed either gameplay state or the
                    // generation-attempt reservation: a minted card / successful attach may now be
                    // a cheaper direct continuation. Only a clean no-op failure is blocked, because
                    // repeating it against identical state could spin until the safety bound.
                    if (play.StateChanged)
                        snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                    if (!play.StateChanged && plan.Generation == null)
                        selected.State.Blocked = true;
                    continue;
                }

                reservedFollowupByAxis.TryGetValue(chosenDemand.RequestingAxis, out float alreadyReserved);
                reservedFollowupByAxis[chosenDemand.RequestingAxis] = alreadyReserved + selected.FollowupAp;
                selected.State.Remaining = Mathf.Max(0f, selected.State.Remaining - 1f);
                result.CardsPlayed++;

                AiDebugLog.Write($"[AI][V2]   strat.A — {chosenDemand}: {plan.Kind} "
                    + $"@{plan.Deploy.Hex.Q},{plan.Deploy.Hex.R} "
                    + $"(ap {F(play.ApSpent)} -> {DesireAxes.Abbrev(chosenDemand.RequestingAxis)}, {plan.Deploy.Kind}, "
                    + $"followup {F(selected.FollowupAp)}ap reserved, {plan.StableKey})");

                // Rebuild all candidates after every mutation: a hand card, equipment card, army
                // slot, generator attempt, resource balance or capability count may have changed.
                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.A — {result.CardsPlayed} chain(s), ledger now " + ledger.DebugLine());
            return result;
        }

        // A plan is "protected" only when its own demand does NOT require a trait the plan carries,
        // and some other currently-feasible unmet demand DOES require that same trait. Today only
        // Stealth can reach ExpectedTraits, so this is exact for the currently-proven classifier and
        // automatically extends when additional reliable traits are added later.
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

        // Demand.Value is the cross-demand strategic value; Plan.Score is the already-computed
        // complete-chain quality/cost/placement score within that demand. Multiplication preserves
        // both scales without introducing another tuning constant.
        private static float ArbitrationScore(PhaseACandidate c) =>
            Mathf.Max(0f, c.State.Demand.Value) * Mathf.Max(0.0001f, c.Plan.Score);

        // ----------------------------------------------------------------- PHASE B ----
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
                if (pick.Value.utility < AiConfigV2.surplusUtilityThreshold)
                {
                    AiDebugLog.Write($"[AI][V2]   strat.B — best feasible utility {F(pick.Value.utility)} < threshold "
                        + $"{F(AiConfigV2.surplusUtilityThreshold)}, stop");
                    break;
                }

                MaterializationPlan plan = pick.Value.plan;
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

                // Cycle ONLY when the play actually relieved hand pressure and the AP reserve still
                // holds after the draw.
                if (AiConfigV2.surplusAllowDraw && handWasFull && hand.HasFreeSlot
                    && root.ActionPoints - ctx.DrawApCost
                        >= AiConfigV2.housekeepingApReserve + AiConfigV2.surplusApReserve
                    && CardDrawExecutor.TryCycle(root, hand, ctx))
                {
                    result.StateChanged = true;
                }

                snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.B — {result.CardsPlayed} surplus chain(s) played");
            return result;
        }

        // Whole-chain reserve check for Phase B — the Step 8B analogue of the pre-8B
        // ReservesOkAfter(CardPlayPlan): real AP after the whole chain must stay above
        // housekeeping + surplus reserves, and every per-resource surplus floor must hold.
        internal static bool ReservesOkAfterChain(PlayerRoot root, MaterializationPlan plan)
        {
            if (plan == null)
                return false;
            float apAfter = root.ActionPoints - plan.ApCost;
            if (apAfter < AiConfigV2.housekeepingApReserve + AiConfigV2.surplusApReserve)
                return false;

            ResourceCost cost = plan.ResCost;
            if (cost == null)
                return true;
            return root.GetResource(ResourceType.Human) - cost.human >= AiConfigV2.surplusHumanReserve
                && root.GetResource(ResourceType.Energy) - cost.energy >= AiConfigV2.surplusEnergyReserve
                && root.GetResource(ResourceType.Materials) - cost.materials >= AiConfigV2.surplusMaterialsReserve
                && root.GetResource(ResourceType.Tech) - cost.tech >= AiConfigV2.surplusTechReserve;
        }

        private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
