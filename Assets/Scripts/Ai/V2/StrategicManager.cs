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
    //    Phase A — FulfillDemands (BEFORE mission planning). For each demand, in value order:
    //              MaterializationCandidateBuilder enumerates every legal chain -> rejects the
    //              infeasible (RequiredTraits, axis entitlement AND real-AP room for the demand's
    //              MinimumFollowupAp + housekeeping reserve, whole-chain resource cost, hand
    //              capacity at every intermediate state, generator use not already claimed) ->
    //              ranks the survivors as finished plans -> executes the best via
    //              MaterializationExecutor -> debits the requesting axis by the REAL AP spent.
    //              Bounded by maxDemandFulfillmentActionsPerTurn and maxGenerationActionsPerTurn.
    //              Follow-up AP is RESERVED, not spent.
    //
    //    Phase B — UseSurplus (AFTER mission execution + operational refresh). Bounded greedy over
    //              GENUINELY remaining real AP/resources; may also proactively generate / attach.
    //              Cannot touch a generator use / hand slot Phase A reserved (the same
    //              MaterializationReservation is carried over). Refreshes the operational snapshot
    //              after every successful chain so scarcity is recomputed honestly.
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

        // Pass-local generator-use / generation-budget ownership. Phase A creates it; the pipeline
        // carries the SAME instance into Phase B so surplus preparation cannot re-use a generator
        // Phase A already spent or exceed the shared per-turn generation bound.
        public MaterializationReservation Reservation;

        public void AddDebit(DesireAxis a, float ap)
        {
            ApDebited.TryGetValue(a, out float cur);
            ApDebited[a] = cur + ap;
        }
    }

    public static class StrategicManager
    {
        // ----------------------------------------------------------------- PHASE A ----
        public static StrategicPhaseResult FulfillDemands(WorldSnapshot snap, PlayerSetupData player,
            PlayerRoot root, AiHandData hand, AiTurnContext ctx, AxisBudgetLedger ledger,
            IReadOnlyList<AxisDemand> demands, ActorCommitments commitments)
        {
            var result = new StrategicPhaseResult { Reservation = new MaterializationReservation() };
            if (demands == null || demands.Count == 0 || player == null || root == null || hand == null || ledger == null)
                return result;

            // ACCUMULATIVE follow-up reservation, per axis, across every demand this phase.
            var reservedFollowupByAxis = new Dictionary<DesireAxis, float>();

            int actions = 0;
            foreach (AxisDemand demand in demands
                .OrderByDescending(d => d.Value)
                .ThenBy(d => (int)d.RequestingAxis))
            {
                float deficit = demand.DesiredAmount;
                while (deficit > 0f && actions < AiConfigV2.maxDemandFulfillmentActionsPerTurn)
                {
                    reservedFollowupByAxis.TryGetValue(demand.RequestingAxis, out float reserved);
                    CapabilityInventory inv = CapabilityInventory.Build(snap, player, commitments);

                    (MaterializationPlan plan, float followupAp)? pick = MaterializationCandidateBuilder.BestForDemand(
                        snap, player, root, hand, ctx, demand, ledger, commitments, reserved, result.Reservation, inv);
                    if (pick == null)
                    {
                        AiDebugLog.Write($"[AI][V2]   strat.A — {demand}: no feasible useful chain "
                            + $"({DesireAxes.Abbrev(demand.RequestingAxis)} entitlement "
                            + $"{F(ledger.Balance(demand.RequestingAxis))}, followup reserved {F(reserved)})");
                        break;
                    }

                    MaterializationPlan plan = pick.Value.plan;
                    MaterializationResult play = MaterializationExecutor.Execute(snap, player, root, hand, ctx, plan, commitments);
                    if (plan.Generation != null)
                        result.Reservation.RecordGenerationAttempt(plan.Generation, play);
                    if (play.StateChanged)
                        result.StateChanged = true;
                    if (play.ApSpent > 0f)
                    {
                        ledger.Debit(demand.RequestingAxis, play.ApSpent);
                        result.AddDebit(demand.RequestingAxis, play.ApSpent);
                    }

                    if (!play.Deployed)
                    {
                        AiDebugLog.Write($"[AI][V2]   strat.A — {demand}: {plan.Kind} chain did not deploy "
                            + $"({play.FailReason}); gen={(play.Generated ? 1 : 0)} att={(play.Attached ? 1 : 0)}");
                        // Refresh so a partial mutation (a minted card, spent resources, a reveal)
                        // is visible to the next demand's enumeration.
                        if (play.StateChanged)
                            snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                        break;
                    }

                    reservedFollowupByAxis[demand.RequestingAxis] = reserved + pick.Value.followupAp;
                    actions++;
                    result.CardsPlayed++;
                    deficit -= 1f;
                    AiDebugLog.Write($"[AI][V2]   strat.A — {demand}: {plan.Kind} "
                        + $"@{plan.Deploy.Hex.Q},{plan.Deploy.Hex.R} "
                        + $"(ap {F(play.ApSpent)} -> {DesireAxes.Abbrev(demand.RequestingAxis)}, {plan.Deploy.Kind}, "
                        + $"followup {F(pick.Value.followupAp)}ap reserved, {plan.StableKey})");

                    // Refresh own operational state so the next pick's CapabilityInventory /
                    // generation eligibility / hand read see the entity just materialised.
                    snap = WorldAnalysis.RefreshOperationalState(snap, player, root, hand, ctx);
                }
            }

            if (result.CardsPlayed > 0)
                AiDebugLog.Write($"[AI][V2] strat.A — {result.CardsPlayed} chain(s), ledger now " + ledger.DebugLine());
            return result;
        }

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
