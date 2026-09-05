using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI-RECON-01 / RECON-AIR-02 (round 5) — SHARED AIR-RECON "WOULD THIS SLOT FLY" PRIMITIVE
    // ===========================================================================================
    //  Round 3 removed the separate pre-Demand AP/Energy reservation ledger this file used to own.
    //  Round 5 (RECON-AIR-02) removes the second thing it still owned after that: a SEPARATE
    //  orchestrated capacity-sizing STAGE (`Run`) with its own per-turn registry/state
    //  (ReconAirReservationState / ReconAirReservationRegistry), called before DemandLayer and read
    //  back by ReconCapacitySnapshot.Build. That was a second capacity authority parallel to
    //  ReconAssignmentPlanner.MeasureCapacity (the ONE canonical "how much of this is there ANY
    //  usable actor for" answer for ground). The greedy sizing loop that used to live in `Run` now
    //  lives in ReconAssignmentPlanner.MeasureAirCapacity — called directly by DemandLayer,
    //  recomputed fresh every call (no cross-call state, no registry), the same way the ground
    //  witness numbers in MeasureCapacity always have been.
    //
    //  What remains HERE is only the feasibility PRIMITIVE both that sizing pass and
    //  ReconAssignmentPlanner.AppendAirCandidates' real per-mission Assignment need to agree on:
    //  "would the AIR-01 route scorer actually launch this slot, right now, at all" — a route
    //  (`Pick` / `PickFromStorage`) whose score clears `MinimumUsefulScore`, AND the Energy
    //  opportunity policy. This is a STRUCTURAL "can anything useful happen" question (fine for
    //  capacity sizing); it is NOT proof that a specific actor can serve a SPECIFIC mission target
    //  (that is RECON-AIR-04 / AppendAirCandidates' own job, using the SAME Pick/PickFromStorage
    //  primitive but anchored at the mission's actual target).
    // ===========================================================================================
    // Round 7 (Problem 2) — the STRUCTURAL half of "would this slot fly": actor/route/target-progress/
    // mode feasibility ONLY. No AP, no Energy, no reservation-value judgement — this is what
    // capability measurement (ReconAssignmentPlanner.MeasureAirCapacity, Demand-facing) is allowed to
    // read. CAPABILITY != FUNDING: Demand's question is "does an executor exist that COULD satisfy
    // this Recon class", never "does it have Energy today and is spending it worthwhile right now".
    internal readonly struct AirStructuralFeasibility
    {
        public readonly bool Feasible;
        public readonly HexCoord ChosenHex;
        public readonly int LaunchEnergy;   // this candidate's own real launch/activation Energy cost
        public readonly float RouteScore;   // AIR-01 route score — an ECONOMICS input, carried through
        public readonly int ExcludeArmyId;  // the actor being evaluated (-1 for a not-yet-formed launch)

        public AirStructuralFeasibility(bool feasible, HexCoord chosenHex, int launchEnergy, float routeScore, int excludeArmyId)
        {
            Feasible = feasible;
            ChosenHex = chosenHex;
            LaunchEnergy = launchEnergy;
            RouteScore = routeScore;
            ExcludeArmyId = excludeArmyId;
        }

        internal static readonly AirStructuralFeasibility No = new AirStructuralFeasibility(false, default, 0, 0f, -1);
    }

    internal static class ReconAirReservationPrepass
    {
        // Round 7 (Problem 2) — STRUCTURAL feasibility only: actor exists / belongs to AI / aircraft
        // usable / correct Recon capability type / not destroyed / not conflicting-committed / a
        // tactical route or progress is in-principle possible (the AIR-01 route scorer's own gates —
        // it already encodes terrain/threat/mode-appropriateness, never AP or Energy). Explicitly
        // does NOT check root.ActionPoints, EnergyBudgetBase, slot.Ap/Energy against any budget, or
        // run AviationSortieReservationEvaluator — those are activation ECONOMICS
        // (EvaluateAirActivationEconomics below), a strategic resource-spend decision that must
        // happen later (Provisioning), never during capability measurement.
        internal static AirStructuralFeasibility EvaluateAirStructuralFeasibility(PlayerSetupData player,
            AiTurnContext ctx, WorldSnapshot snap, ReconMode globalMode, AirObservationSlot slot,
            IReadOnlyList<ReconSector> provisionalWedges)
        {
            if (ctx?.Map == null)
                return new AirStructuralFeasibility(true, default, 0, 0f, slot.ActorId ?? -1); // bare harness

            ReconAirStepPlanner.StepChoice? choice;
            int launchEnergy;
            int excludeArmyId;
            // R3/R4 review fix — probe with the SAME scoring inputs the executor will hand Pick:
            // this sortie's own footprint excluded from "recent coverage by another sortie", the
            // air slots reserved-but-not-launched this pass as sector coverage, the executor's own
            // per-actor MODE (a durable ReconPatrolState wins over the global RequestedMode), and a
            // read-only PROJECTION of the sortie's turn-start phase / trail so trail-overlap and
            // lateral shaping match. Without the last two a continuing Outbound wing scored ~0.30
            // higher here than in the executor and could be reserved as capacity the executor then
            // rejects below MinimumUsefulScore.
            var scoringCtx = new AirReconScoringContext { ProvisionalWedgeClaims = provisionalWedges };

            if (slot.ActorId.HasValue)
            {
                ArmyData wing = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a != null && a.Id == slot.ActorId.Value);
                if (wing == null)
                    return AirStructuralFeasibility.No;

                bool airborne = ReconAirSortieRegistry.TryGet(player, wing.Id, out ReconAirSortieState real);
                scoringCtx.ExcludeSortieId = airborne ? real.SortieId : -1;
                ReconAirSortieState projected = ProjectScoringSortie(player, ctx, wing);
                // R5 review fix — a Hold- or Return-phase wing is NOT Observation capacity. The
                // executor ignores the AIR-01 forward `Pick` result once the sortie is Return-bound
                // (it flies PickReturnStep toward the airfield instead) or ends the turn aloft on a
                // Hold — so a strategic forward hex clearing MinimumUsefulScore here would reserve
                // ObservationDeficit relief the executor never delivers. The wing still consumes an
                // air-actor slot; it just does not count toward ReservedAirborneWings.
                if (projected != null
                    && (projected.Phase == ReconAirPhase.Hold || projected.Phase == ReconAirPhase.Return))
                    return AirStructuralFeasibility.No;

                ReconMode mode = airborne
                    && ReconPatrolStateRegistry.TryGet(player, wing.Id, out ReconPatrolState asg)
                    ? asg.Mode : globalMode;
                choice = ReconAirStepPlanner.Pick(player, ctx, wing, snap, mode, ctx.TurnNumber, projected, scoringCtx);
                launchEnergy = wing.HasActivatedThisTurn ? 0 : UnityEngine.Mathf.Max(0, wing.ActivationEnergyCost);
                excludeArmyId = wing.Id;
            }
            else
            {
                ArmyData airfield = AviationRules.FindAirfieldAt(slot.AirfieldHex, player);
                if (airfield == null || airfield.Members.Count < UnityEngine.Mathf.Max(1, AiConfig.aviationLaunchMinReadyAircraft))
                    return AirStructuralFeasibility.No;
                List<UnitData> subset = ReconAirCapacityPolicy.SelectReconLaunchSubset(airfield.Members);
                // Structural only — "enough ready aircraft exist to form a legal Recon subset", NOT
                // whether Energy exists today to launch it (that is CanAffordLaunch, an economics
                // check — moved to EvaluateAirActivationEconomics's caller).
                if (subset.Count == 0)
                    return AirStructuralFeasibility.No;
                var candidate = new AirLaunchCandidate(slot.AirfieldHex, null, subset);
                choice = ReconAirStepPlanner.PickFromStorage(player, ctx, candidate, snap, globalMode, ctx.TurnNumber, scoringCtx);
                launchEnergy = subset.Sum(u => u != null ? u.LaunchEnergyCost : 0);
                excludeArmyId = -1;
            }

            if (!choice.HasValue)
                return AirStructuralFeasibility.No;

            return new AirStructuralFeasibility(true, choice.Value.Hex, launchEnergy, choice.Value.Score, excludeArmyId);
        }

        // Round 7 (Problem 2) — ACTIVATION ECONOMICS: may use Energy/AP/resource-outlook/reservation
        // value. Demand/capability-measurement must NEVER call this — its proper callers are (a) the
        // real per-pass sizing loop that already owns its own cumulative AP/Energy budget
        // (MeasureAirCapacity's greedy loop, which now calls this explicitly instead of getting it
        // for free inside SlotWouldFly) and (b) Provisioning's live sanity check before actually
        // claiming resources for an assigned actor.
        internal static bool EvaluateAirActivationEconomics(PlayerSetupData player, PlayerRoot root, HexMap map,
            int apCost, AirStructuralFeasibility structural, int committedApThisPass, int committedEnergyThisPass,
            out AviationReservationDecision decision)
        {
            // AI-MGR — the reservation decision itself: Resource Outlook -> Hand/Deck Energy
            // Pressure -> Sortie Value -> Reservation Decision. Having an aircraft + a legal route is
            // not, on its own, an entitlement to protect AP/Energy.
            decision = AviationSortieReservationEvaluator.EvaluateRecon(player, root, map, apCost,
                structural.LaunchEnergy, structural.RouteScore, structural.ExcludeArmyId,
                committedApThisPass, committedEnergyThisPass);
            return decision.ShouldReserve;
        }

        // Composition of both stages — kept for Assignment-time REAL per-mission candidate building
        // (ReconAssignmentPlanner.AppendAirCandidates / BuildFeasibleAirPool), which runs AFTER a
        // mission is already funded and legitimately needs the full "would this slot actually fly"
        // answer, economics included. Round 4 — INTERNAL (was private) so Assignment can call the
        // SAME check this sizing pass uses, instead of re-deriving a second copy.
        internal static bool SlotWouldFly(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snap, ReconMode globalMode, AirObservationSlot slot, int committedEnergyThisPass,
            IReadOnlyList<ReconSector> provisionalWedges, out HexCoord chosenHex)
        {
            AirStructuralFeasibility structural = EvaluateAirStructuralFeasibility(
                player, ctx, snap, globalMode, slot, provisionalWedges);
            chosenHex = structural.ChosenHex;
            if (!structural.Feasible)
                return false;

            bool ok = EvaluateAirActivationEconomics(player, root, ctx?.Map, slot.Ap, structural,
                0, committedEnergyThisPass, out AviationReservationDecision decision);
            AiDebugLog.Write(decision.ToLog(slot.ActorId.HasValue
                ? $"actor=#{slot.ActorId.Value}" : $"airfield=({slot.AirfieldHex.Q},{slot.AirfieldHex.R})"));
            return ok;
        }

        // Read-only projection of the ReconAirSortieState the executor will pass Pick for THIS wing
        // this turn — turn-start phase resolution (Hold re-open / must-recover) mirrored WITHOUT
        // calling BeginTurn(), so the reservation probe never mutates the real sortie lifecycle.
        // A ready standalone wing (no live sortie) gets the same fresh Outbound state the executor
        // seeds at the wing's hex before its first step.
        internal static ReconAirSortieState ProjectScoringSortie(PlayerSetupData player, AiTurnContext ctx, ArmyData wing)
        {
            if (wing == null)
                return null;
            var proj = new ReconAirSortieState { SortieId = -1 };

            if (ReconAirSortieRegistry.TryGet(player, wing.Id, out ReconAirSortieState real))
            {
                proj.LaunchHex = real.LaunchHex;
                proj.Trail.AddRange(real.Trail);
                proj.ClaimedSector = real.ClaimedSector;
                proj.HasClaim = real.HasClaim;
                proj.BestOutboundStepScore = real.BestOutboundStepScore;

                bool wouldBeNewTurn = real.LastProcessedTurn != ctx.TurnNumber;
                bool canRemain = ctx.Map != null
                    && AiAirSortiePlanner.CanEndTurnHereAndRecover(wing, ctx.Map, player);
                // Turn arithmetic, not a BeginTurn() increment — matches the executor's own
                // AirborneTurnsElapsed model exactly (AI-AIR-02 review P1: no drift when a turn's
                // RunActor pass is skipped).
                int projIdx = real.AirborneTurnsElapsed(ctx.TurnNumber);
                bool mustRecover = projIdx >= 1 && !canRemain;

                ReconAirPhase phase = real.Phase;
                if (phase == ReconAirPhase.Hold)
                    phase = wouldBeNewTurn
                        ? (mustRecover ? ReconAirPhase.Return : ReconAirPhase.Outbound)
                        : ReconAirPhase.Hold;
                if (mustRecover && phase == ReconAirPhase.Outbound)
                    phase = ReconAirPhase.Return;
                proj.Phase = phase;
            }
            else
            {
                proj.Phase = ReconAirPhase.Outbound;
                proj.LaunchHex = wing.Hex;
                proj.Trail.Add(wing.Hex);
            }
            return proj;
        }
    }
}
