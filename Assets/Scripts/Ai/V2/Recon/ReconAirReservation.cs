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
    internal static class ReconAirReservationPrepass
    {
        // Would the AIR-01 route scorer actually launch this slot? Mirrors the executor's own gates
        // for BOTH a ready standalone wing and a hangar launch: a route (`Pick` / `PickFromStorage`)
        // whose score clears `MinimumUsefulScore`, AND the Energy opportunity policy — with the
        // Energy already reserved by earlier slots this pass folded in so several candidates cannot
        // each pass against the full stockpile.
        //
        // Round 4 — INTERNAL (was private) so ReconAssignmentPlanner's real air-candidate builder
        // (BuildCandidates) can call the SAME feasibility check this read-only sizing pass uses,
        // instead of re-deriving a second copy. Assignment calls it with committedEnergyThisPass=0
        // and provisionalWedges=null (a single-mission candidate probe has no running per-pass
        // budget/wedge state to fold in — Provisioning's later sequential AP/Energy claim against
        // session.ApClaimed is what actually prevents double-spending across missions THIS pass,
        // exactly mirroring how ground Assignment/BuildCandidates also ignores the aggregate AP
        // budget and leaves it to Provisioning).
        internal static bool SlotWouldFly(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snap, ReconMode globalMode, AirObservationSlot slot, int committedEnergyThisPass,
            IReadOnlyList<ReconSector> provisionalWedges, out HexCoord chosenHex)
        {
            chosenHex = default;
            if (ctx?.Map == null)
                return true; // bare harness — leave the final gate to the executor

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
                    return false;

                bool airborne = ReconAirSortieRegistry.TryGet(player, wing.Id, out ReconAirSortieState real);
                scoringCtx.ExcludeSortieId = airborne ? real.SortieId : -1;
                ReconAirSortieState projected = ProjectScoringSortie(player, ctx, wing);
                // R5 review fix — a Hold- or Return-phase wing is NOT Observation capacity. The
                // executor ignores the AIR-01 forward `Pick` result once the sortie is Return-bound
                // (it flies PickReturnStep toward the airfield instead) or ends the turn aloft on a
                // Hold — so a strategic forward hex clearing MinimumUsefulScore here would reserve
                // ObservationDeficit relief the executor never delivers. The wing still consumes an
                // air-actor slot and keeps its recovery AP/Energy reservation (done by the caller
                // before this probe); it just does not count toward ReservedAirborneWings.
                if (projected != null
                    && (projected.Phase == ReconAirPhase.Hold || projected.Phase == ReconAirPhase.Return))
                    return false;

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
                    return false;
                List<UnitData> subset = ReconAirCapacityPolicy.SelectReconLaunchSubset(airfield.Members);
                if (subset.Count == 0 || !AiAirSortiePlanner.CanAffordLaunch(root, player, subset))
                    return false;
                var candidate = new AirLaunchCandidate(slot.AirfieldHex, null, subset);
                choice = ReconAirStepPlanner.PickFromStorage(player, ctx, candidate, snap, globalMode, ctx.TurnNumber, scoringCtx);
                launchEnergy = subset.Sum(u => u != null ? u.LaunchEnergyCost : 0);
                excludeArmyId = -1;
            }

            if (!choice.HasValue)
                return false;

            // AI-MGR — the reservation decision itself: Resource Outlook -> Hand/Deck Energy
            // Pressure -> Sortie Value -> Reservation Decision. Replaces the old flat
            // "route clears MinimumUsefulScore + ReconAirEnergyPolicy hard/soft gate" pair with one
            // staged, diagnosable evaluator — having an aircraft + a legal route is no longer, on
            // its own, an entitlement to protect AP/Energy.
            AviationReservationDecision decision = AviationSortieReservationEvaluator.EvaluateRecon(
                player, root, ctx.Map, slot.Ap, launchEnergy, choice.Value.Score,
                excludeArmyId, 0, committedEnergyThisPass);
            AiDebugLog.Write(decision.ToLog(slot.ActorId.HasValue
                ? $"actor=#{slot.ActorId.Value}" : $"airfield=({slot.AirfieldHex.Q},{slot.AirfieldHex.R})"));
            if (!decision.ShouldReserve)
                return false;
            chosenHex = choice.Value.Hex;
            return true;
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
