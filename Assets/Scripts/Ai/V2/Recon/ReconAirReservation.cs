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
    //  produce ANY useful step for this slot right now" — a `Pick` whose score
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
        public readonly float ActivationAp;
        public readonly int LaunchEnergy;   // this candidate's own real launch/activation Energy cost
        public readonly float RouteScore;   // AIR-01 route score — an ECONOMICS input, carried through
        public readonly int ExcludeArmyId;  // the actor being evaluated (real existing actor)
        public readonly int RequiredTurns;
        public readonly int NextTurnEnergy; // one activation protected if this candidate ends airborne
        public readonly int NextTurnAp;     // fresh activation AP required on that next turn

        public AirStructuralFeasibility(bool feasible, HexCoord chosenHex, int launchEnergy, float routeScore,
            int excludeArmyId, float activationAp = 0f, int requiredTurns = 1, int nextTurnEnergy = 0,
            int nextTurnAp = 0)
        {
            ActivationAp = activationAp;
            Feasible = feasible;
            ChosenHex = chosenHex;
            LaunchEnergy = launchEnergy;
            RouteScore = routeScore;
            ExcludeArmyId = excludeArmyId;
            RequiredTurns = requiredTurns;
            NextTurnEnergy = nextTurnEnergy;
            NextTurnAp = nextTurnAp;
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
            IReadOnlyList<ReconSector> provisionalWedges, HexCoord? missionFocusHex = null,
            List<string> diagnostics = null)
        {
            if (ctx?.Map == null)
                return new AirStructuralFeasibility(true, default, 0, 0f, slot.ActorId); // bare harness

            ArmyData wing = AiV2Util.ResolveArmy(player, slot.ActorId);
            if (wing == null)
                return AirStructuralFeasibility.No;

            var scoringCtx = new AirReconScoringContext { ProvisionalWedgeClaims = provisionalWedges };
            (ReconAirSortieState projected, int excludeSortieId, ReconMode mode) =
                BuildScoringContextForWing(player, ctx, wing, globalMode);
            scoringCtx.ExcludeSortieId = excludeSortieId;
            if (projected != null
                && (projected.Phase == ReconAirPhase.Hold || projected.Phase == ReconAirPhase.Return))
            {
                diagnostics?.Add($"phase={projected.Phase}");
                return AirStructuralFeasibility.No;
            }

            ReconAirStepPlanner.StepChoice? choice = ReconAirStepPlanner.Pick(
                player, ctx, wing, snap, mode, ctx.TurnNumber, projected, scoringCtx,
                missionFocusHex: missionFocusHex, diagnostics: diagnostics);
            int launchEnergy = wing.HasActivatedThisTurn ? 0
                : UnityEngine.Mathf.Max(0, wing.ActivationEnergyCost);
            int excludeArmyId = wing.Id;

            // Structural capacity is not "the scorer returned SOME non-hard-rejected route" — it is
            // "a route the real Assignment/Execution layer would call actionable". Pick() itself does
            // not apply MinimumUsefulScore (it just returns the best survivor), and
            // AppendAirCandidates / MeasureAirCapacity both require score >= MinimumUsefulScore — so
            // gate here too, or capacity witnesses a lane Assignment then refuses (phantom capacity).
            if (!choice.HasValue || choice.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
            {
                diagnostics?.Add(choice.HasValue
                    ? $"best ({choice.Value.Hex.Q},{choice.Value.Hex.R}) score={choice.Value.Score:0.00} < min"
                    : "no_candidate_step");
                return AirStructuralFeasibility.No;
            }

            bool spansNextTurn = choice.Value.RequiredTurns > 1;
            int nextTurnEnergy = spansNextTurn ? UnityEngine.Mathf.Max(0, wing.ActivationEnergyCost) : 0;
            int nextTurnAp = spansNextTurn ? UnityEngine.Mathf.Max(0, wing.ActivationApCost) : 0;
            return new AirStructuralFeasibility(true, choice.Value.Hex, launchEnergy, choice.Value.Score,
                excludeArmyId, choice.Value.ActivationAp, choice.Value.RequiredTurns, nextTurnEnergy,
                nextTurnAp);
        }

        // Shared scorer INPUTS for one wing, used by BOTH EvaluateAirStructuralFeasibility (capacity)
        // and ReconAssignmentPlanner.AppendAirCandidates (real per-mission RouteScore) so the two —
        // and the executor — can never score the same continuing sortie differently again. Every
        // input the AIR-01 route scorer actually reads is resolved here, once:
        //   · Projected       — the read-only turn-start ReconAirSortieState the executor will hand
        //                       Pick (phase / trail / claim), so Outbound/Turning shaping, trail
        //                       overlap and lateral novelty all match the executor.
        //   · ExcludeSortieId — this wing's own live sortie id, so its OWN coverage is not counted
        //                       as "recently covered by another sortie". -1 for a wing with no live
        //                       Recon sortie (a ready idle wing).
        //   · EffectiveMode   — a durable per-actor ReconPatrolState.Mode wins over `globalMode`,
        //                       the SAME precedence AirReconStepDirector applies at execution (a
        //                       plain TryGet, independent of whether a live sortie state exists).
        //                       `globalMode` is the caller's AirReconModePolicy.RequestedMode.
        internal static (ReconAirSortieState Projected, int ExcludeSortieId, ReconMode EffectiveMode)
            BuildScoringContextForWing(PlayerSetupData player, AiTurnContext ctx, ArmyData wing, ReconMode globalMode)
        {
            int excludeSortieId = ReconAirSortieRegistry.TryGet(player, wing.Id, out ReconAirSortieState real)
                ? real.SortieId : -1;
            ReconMode effectiveMode = AirReconModePolicy.EffectiveMode(player, wing.Id, globalMode);
            return (ProjectScoringSortie(player, ctx, wing), excludeSortieId, effectiveMode);
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
                int safeEnds = AviationRange.SafeUnlandedEndsRemaining(wing);

                proj.Phase = ReconAirSortieLifecycle.PhaseAfterHold(
                    real.Phase, wouldBeNewTurn, safeEnds);
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
