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
    //  RECON-AIR — SHARED AIR-RECON STRUCTURAL FEASIBILITY PRIMITIVE
    // ===========================================================================================
    //  This file used to own an AP/Energy reservation ledger, then a separate capacity-sizing stage
    //  with its own registry, then (until now) a second economics entry point
    //  (EvaluateAirActivationEconomics / SlotWouldFly). All of that is gone.
    //
    //  What remains HERE is ONLY the STRUCTURAL feasibility primitive both the capability-sizing
    //  pass (ReconAssignmentPlanner.MeasureAirCapacity) and real per-mission Assignment
    //  (ReconAssignmentPlanner.AppendAirCandidates) need to agree on: "would the AIR-01 route scorer
    //  produce ANY useful step for this slot right now" — a `Pick` / `PickFromStorage` whose score
    //  clears `MinimumUsefulScore`. NO AP, NO Energy, NO hand/deck/income judgement. The strategic
    //  "is this sortie worth paying for" question has exactly one owner —
    //  ProvisioningManager.AirSortieReservationAdmission -> AviationSortieReservationEvaluator.
    // ===========================================================================================
    // The STRUCTURAL feasibility of an air slot: actor/route/target-progress/mode ONLY. No AP, no
    // Energy, no reservation-value judgement. CAPABILITY != FUNDING: the question is "does an
    // executor exist that COULD satisfy this Recon class", never "does it have Energy today and is
    // spending it worthwhile right now" (that is AviationSortieReservationEvaluator, at Provisioning).
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
        // STRUCTURAL feasibility only: actor exists / belongs to AI / aircraft usable / correct Recon
        // capability type / not destroyed / not conflicting-committed / a tactical route or progress
        // is in-principle possible (the AIR-01 route scorer's own gates — it already encodes
        // terrain/threat/mode-appropriateness, never AP or Energy). Explicitly does NOT check
        // root.ActionPoints, slot.Ap/Energy against any budget, or run AviationSortieReservation-
        // Evaluator — that activation ECONOMICS decision belongs solely to
        // ProvisioningManager.AirSortieReservationAdmission, never to capability measurement.
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

                (ReconAirSortieState projected, int excludeSortieId) = BuildScoringStateForWing(player, ctx, wing);
                bool airborne = excludeSortieId >= 0;
                scoringCtx.ExcludeSortieId = excludeSortieId;
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
                // whether Energy exists today to launch it (that is CanAffordLaunch, a hard gate at
                // Provisioning/execution time, and the strategic worth-it call in
                // AviationSortieReservationEvaluator).
                if (subset.Count == 0)
                    return AirStructuralFeasibility.No;
                var candidate = new AirLaunchCandidate(slot.AirfieldHex, null, subset);
                choice = ReconAirStepPlanner.PickFromStorage(player, ctx, candidate, snap, globalMode, ctx.TurnNumber, scoringCtx);
                launchEnergy = subset.Sum(u => u != null ? u.LaunchEnergyCost : 0);
                excludeArmyId = -1;
            }

            // Structural capacity is not "the scorer returned SOME non-hard-rejected route" — it is
            // "a route the real Assignment/Execution layer would call actionable". Pick() itself does
            // not apply MinimumUsefulScore (it just returns the best survivor), and
            // AppendAirCandidates / MeasureAirCapacity both require score >= MinimumUsefulScore — so
            // gate here too, or capacity witnesses a lane Assignment then refuses (phantom capacity).
            if (!choice.HasValue || choice.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                return AirStructuralFeasibility.No;

            return new AirStructuralFeasibility(true, choice.Value.Hex, launchEnergy, choice.Value.Score, excludeArmyId);
        }

        // Shared projected-scoring inputs for one wing, used by BOTH EvaluateAirStructuralFeasibility
        // (capacity) and ReconAssignmentPlanner.AppendAirCandidates (real per-mission RouteScore) so
        // the two can never score the same continuing sortie differently again:
        //   · Projected       — the read-only turn-start ReconAirSortieState the executor will hand
        //                       Pick (phase / trail / claim), so Outbound/Turning shaping, trail
        //                       overlap and lateral novelty all match the executor.
        //   · ExcludeSortieId — this wing's own live sortie id, so its OWN coverage is not counted
        //                       as "recently covered by another sortie". -1 for a wing with no live
        //                       Recon sortie (a ready idle wing).
        internal static (ReconAirSortieState Projected, int ExcludeSortieId) BuildScoringStateForWing(
            PlayerSetupData player, AiTurnContext ctx, ArmyData wing)
        {
            int excludeSortieId = ReconAirSortieRegistry.TryGet(player, wing.Id, out ReconAirSortieState real)
                ? real.SortieId : -1;
            return (ProjectScoringSortie(player, ctx, wing), excludeSortieId);
        }

        // ACTIVATION ECONOMICS — "is THIS sortie strategically worth its AP/Energy this turn?" — is
        // NOT here. It has exactly one owner: ProvisioningManager.AirSortieReservationAdmission,
        // which feeds the exact per-actor cost + the mission-specific route score Assignment already
        // resolved straight into AviationSortieReservationEvaluator. Nothing in Recon capability
        // measurement or the tactical/execution layers re-derives it.

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
