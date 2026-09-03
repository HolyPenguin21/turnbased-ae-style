using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Aviation;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AI-RECON-01 — RECON AIR RESERVATION / CAPACITY PREPASS
    // ===========================================================================================
    //  Runs BEFORE DemandLayer and StrategicManager Phase A. It answers ONE question:
    //
    //     "Which concrete air actor / launch subset do we count as GUARANTEED Observation capacity
    //      this turn, and how much AP / Energy must be protected so that capacity really exists?"
    //
    //  It does NOT do strategic air targeting, route scoring or multi-turn sortie planning — that
    //  is AIR-01 / AIR-02. Its only outputs are:
    //    · the set of reserved air actor ids + a guaranteed-lane count that ReconCapacitySnapshot
    //      (and therefore DemandLayer's "do I still need another ground Scout?" decision) trusts
    //      INSTEAD of a fresh, unpinned ReconAirCapacityPolicy re-evaluation;
    //    · ProtectedAp / ProtectedEnergy, exposed through AiResourceReservation.V2ExtraReservation
    //      so every V2 spend path that already calls AiResourceReservation.Available()
    //      (GenerationSource, MaterializationCandidateBuilder, StrategicMaintenancePolicy,
    //      StrategicManager.ReservesOkAfterChain, ReconAirCapacityPolicy itself) nets it out —
    //      Phase A can no longer spend the sortie's activation resources out from under the
    //      capacity model. The pipeline also debits ProtectedAp off the AxisBudgetLedger and the
    //      allocator's physical Energy pool.
    //
    //  The protection is CLEARED after TaskExecutor's terminal ReconAirExecutor.RunFallback has had
    //  its chance to launch, so Strategic Manager Phase B sees the real remaining pool (the sortie
    //  either launched — a real spend already reflected in PlayerRoot — or it did not and there is
    //  no more air work this turn).
    // ===========================================================================================
    internal sealed class ReconAirReservationState
    {
        public int Turn = -1;
        public readonly HashSet<int> ReservedAirActorIds = new HashSet<int>();
        // Airfields whose hangar launch subset is a reserved sortie (storage slots have no army id
        // yet). Telemetry / accounting only — the executor re-derives the concrete subset.
        public readonly HashSet<HexCoord> ReservedAirfieldHexes = new HashSet<HexCoord>();
        public int ReservedAirborneWings;
        public int ReservedLaunchSorties;
        public int GuaranteedObservationLanes;
        public float ProtectedAp;
        public float ProtectedEnergy;
        public string Explain = "no air reservation";

        public void Reset(int turn)
        {
            Turn = turn;
            ReservedAirActorIds.Clear();
            ReservedAirfieldHexes.Clear();
            ReservedAirborneWings = 0;
            ReservedLaunchSorties = 0;
            GuaranteedObservationLanes = 0;
            ProtectedAp = 0f;
            ProtectedEnergy = 0f;
            Explain = "no air reservation";
        }

        // Zero the resource protection but keep the reservation identity/counts for logging +
        // capacity accounting for the rest of the turn.
        public void ClearProtection()
        {
            ProtectedAp = 0f;
            ProtectedEnergy = 0f;
        }

        public int ProtectedFor(ResourceType type) =>
            type == ResourceType.Energy ? UnityEngine.Mathf.CeilToInt(ProtectedEnergy) : 0;
    }

    internal static class ReconAirReservationRegistry
    {
        private static readonly Dictionary<PlayerSetupData, ReconAirReservationState> ByPlayer =
            new Dictionary<PlayerSetupData, ReconAirReservationState>();

        public static ReconAirReservationState GetOrCreate(PlayerSetupData player)
        {
            if (player == null)
                return new ReconAirReservationState();
            if (!ByPlayer.TryGetValue(player, out ReconAirReservationState s))
                ByPlayer[player] = s = new ReconAirReservationState();
            return s;
        }

        // The reservation as it applies to `turn` — an empty state if the last prepass was for a
        // different turn, so stale protection can never leak forward.
        public static ReconAirReservationState ForTurn(PlayerSetupData player, int turn)
        {
            ReconAirReservationState s = GetOrCreate(player);
            return s.Turn == turn ? s : new ReconAirReservationState();
        }

        public static void Clear() => ByPlayer.Clear();
    }

    internal static class ReconAirReservationPrepass
    {
        public static void Run(WorldSnapshot snap, PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            IReadOnlyList<MissionIntent> activeIntents, ActorCommitments commitments,
            IReadOnlyList<ReconObjective> reconObjectives)
        {
            ReconAirReservationState state = ReconAirReservationRegistry.GetOrCreate(player);
            int turn = snap?.TurnNumber ?? 0;
            state.Reset(turn);

            // Install (or refresh) the shared spend-path hook every turn V2 owns.
            AiResourceReservation.V2ExtraReservation = (p, t) =>
                ReconAirReservationRegistry.ForTurn(p, turn).ProtectedFor(t);

            if (player == null || root == null)
            {
                AiDebugLog.Write("[AI][V2][ReconAirRes] no player/root — nothing reserved");
                return;
            }

            // How many GENERIC Observation lanes (Refresh / Surveil, non-stealth) are worth
            // guaranteeing with air: runnable generic observation objectives, capped by the desired
            // observation concurrency minus the generic observation lanes already claimed by a
            // ground/air actor.
            var obsRunnable = (reconObjectives ?? System.Array.Empty<ReconObjective>())
                .Where(o => o != null && o.BaseValue > 0f
                    && o.Kind != ReconObjectiveKind.Explore
                    && o.Stealth != StealthRequirement.Required && !(o.DetectionRisk > 0f))
                .OrderByDescending(o => o.BaseValue).ThenBy(o => o.IntentKey)
                .ToList();

            var activeObsLaneActors = new HashSet<int>();
            if (activeIntents != null && commitments != null)
                foreach (MissionIntent i in activeIntents)
                    if (i?.Scout != null && !i.Scout.RequiresStealth
                        && i.Scout.Kind != ScoutTargetKind.Explore
                        && i.PreferredMoverArmyId.HasValue
                        && commitments.IsArmyClaimed(i.PreferredMoverArmyId.Value))
                        activeObsLaneActors.Add(i.PreferredMoverArmyId.Value);

            int desiredObs = ReconConcurrencyPolicy.DesiredForClass(
                snap, obsRunnable, ReconConcurrencyPolicy.ReconCoverageClass.Observation);
            int observationNeed = UnityEngine.Mathf.Clamp(
                obsRunnable.Count, 0, UnityEngine.Mathf.Max(0, desiredObs - activeObsLaneActors.Count));

            ReconAirObservationDetail detail = ReconAirCapacityPolicy.EvaluateDetailed(player, root);
            ReconMode mode = ReconAirExecutor.RequestedMode(player, snap);

            // ONE authoritative greedy, in the executor's own order, over a SINGLE cumulative AP /
            // Energy budget (so several sorties never each pass against the full stockpile) with the
            // AIR-01 route gate applied per candidate (so a route-invalid earlier aircraft cannot
            // hide a valid later one).
            int apLeft = detail.ApBudgetBase;
            int energyLeft = detail.EnergyBudgetBase;
            int slotsUsed = 0;
            int reservedEnergyThisPass = 0;
            int airborneProbed = 0, airborneStuck = 0, launchProbed = 0, launchRejected = 0;

            // R3 review fix — wedges (from our Citadel) reserved by an accepted STORAGE launch this
            // pass. They have no live army yet, so the next SlotWouldFly probe would not see them;
            // feeding them forward stops two reserved launch sorties both claiming one wedge and
            // producing a GuaranteedObservationLanes count that collapses in execution.
            HexCoord citadelHex = snap?.Self != null ? snap.Self.Citadel : default;
            var provisionalWedges = new List<ReconSector>();

            // Airborne recon wings first. They consume an executor slot regardless, and their owed
            // recovery AP/Energy is ALWAYS protected. NOTE: EnergyBudgetBase already netted out the
            // ReconAirEnergyPolicy "committed" term, which INCLUDES every unactivated in-flight
            // recon/strike wing's owed Energy — so do NOT subtract wing.Energy from energyLeft again
            // (that was a double-count that could turn already-executable aviation into a false
            // deficit and needlessly materialise a ground scout). Likewise reservedEnergyThisPass
            // tracks only NEW spare-launch Energy — an airborne wing is already in `committed`.
            // AP is not pre-committed anywhere, so apLeft IS decremented per wing. Whether the wing
            // can really activate is decided by SlotWouldFly -> ReconAirEnergyPolicy (which excludes
            // the wing itself and sees the true stock).
            foreach (AirObservationSlot wing in detail.AirborneWings)
            {
                if (slotsUsed >= ReconAirCapacityPolicy.MaxAirReconActorsPerTurn)
                    break;
                airborneProbed++;
                slotsUsed++;
                state.ProtectedAp += wing.Ap;
                state.ProtectedEnergy += wing.Energy;
                apLeft -= wing.Ap;

                if (apLeft >= 0 && SlotWouldFly(player, root, ctx, snap, mode, wing, reservedEnergyThisPass,
                        provisionalWedges, out _))
                {
                    state.ReservedAirborneWings++;
                    if (wing.ActorId.HasValue)
                        state.ReservedAirActorIds.Add(wing.ActorId.Value);
                }
                else
                {
                    airborneStuck++;   // recovery protected, but not observation capacity
                }
            }

            int launchNeed = UnityEngine.Mathf.Max(0, observationNeed - state.ReservedAirborneWings);
            foreach (AirObservationSlot slot in detail.SpareCandidatesInOrder)
            {
                if (slotsUsed >= ReconAirCapacityPolicy.MaxAirReconActorsPerTurn
                    || state.ReservedLaunchSorties >= launchNeed)
                    break;
                launchProbed++;
                if (slot.Ap > apLeft || slot.Energy > energyLeft)
                {
                    launchRejected++;
                    continue;   // executor moves on to the next candidate in order
                }
                if (!SlotWouldFly(player, root, ctx, snap, mode, slot, reservedEnergyThisPass,
                        provisionalWedges, out HexCoord slotChosenHex))
                {
                    launchRejected++;
                    continue;
                }
                apLeft -= slot.Ap;
                energyLeft -= slot.Energy;
                reservedEnergyThisPass += slot.Energy;
                state.ProtectedAp += slot.Ap;
                state.ProtectedEnergy += slot.Energy;
                state.ReservedLaunchSorties++;
                slotsUsed++;
                if (slot.ActorId.HasValue)
                    state.ReservedAirActorIds.Add(slot.ActorId.Value);
                else
                    state.ReservedAirfieldHexes.Add(slot.AirfieldHex);
                // Every accepted LAUNCH slot (ready wing on its airfield OR hangar subset) has no
                // live ReconAssignment during the prepass — it only gets one when it actually flies
                // in the executor — so the live wedge scan cannot see it. Record its chosen wedge so
                // the next SlotWouldFly probe does. The airborne-wings loop above is exempt: those
                // wings already hold a ReconAssignment and are counted live.
                if (ctx?.Map != null)
                    provisionalWedges.Add(ReconDirectionModel.Sector(citadelHex, slotChosenHex));
            }

            state.GuaranteedObservationLanes = state.ReservedAirborneWings + state.ReservedLaunchSorties;
            state.Explain = $"guaranteedObsLanes={state.GuaranteedObservationLanes} "
                + $"(airborne {state.ReservedAirborneWings}/{airborneProbed} stuck {airborneStuck} + "
                + $"launch {state.ReservedLaunchSorties}/{launchProbed} rejected {launchRejected}) "
                + $"protAp={state.ProtectedAp:0.#} protEnergy={state.ProtectedEnergy:0.#} "
                + $"obsNeed={observationNeed} desiredObs={desiredObs} activeObsLanes={activeObsLaneActors.Count} "
                + $"apLeft={apLeft} energyLeft={energyLeft} mode={mode}";
            AiDebugLog.Write($"[AI][V2][ReconAirRes] {state.Explain}");
        }

        // Would the AIR-01 route scorer actually launch this slot? Mirrors the executor's own gates
        // for BOTH a ready standalone wing and a hangar launch: a route (`Pick` / `PickFromStorage`)
        // whose score clears `MinimumUsefulScore`, AND the Energy opportunity policy — with the
        // Energy already reserved by earlier slots this pass folded in so several candidates cannot
        // each pass against the full stockpile.
        private static bool SlotWouldFly(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
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
            // per-actor MODE (a durable ReconAssignment wins over the global RequestedMode), and a
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
                if (projected != null && projected.Phase == ReconAirPhase.Hold)
                    return false; // executor would end the turn aloft here — no forward recon, not capacity

                ReconMode mode = airborne
                    && ReconAssignmentRegistry.TryGet(player, wing.Id, out ReconAssignment asg)
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
                if (subset.Count == 0 || !AiAviationSupport.CanAffordLaunch(root, player, subset))
                    return false;
                var candidate = new AirStrikeTask.LaunchCandidate(slot.AirfieldHex, null, subset);
                choice = ReconAirStepPlanner.PickFromStorage(player, ctx, candidate, snap, globalMode, ctx.TurnNumber, scoringCtx);
                launchEnergy = subset.Sum(u => u != null ? u.LaunchEnergyCost : 0);
                excludeArmyId = -1;
            }

            if (!choice.HasValue || choice.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                return false;
            if (!ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map, launchEnergy, choice.Value.Score,
                    excludeArmyId, committedEnergyThisPass).Allowed)
                return false;
            chosenHex = choice.Value.Hex;
            return true;
        }

        // Read-only projection of the ReconAirSortieState the executor will pass Pick for THIS wing
        // this turn — turn-start phase resolution (Hold re-open / must-recover) mirrored WITHOUT
        // calling BeginTurn(), so the reservation probe never mutates the real sortie lifecycle.
        // A ready standalone wing (no live sortie) gets the same fresh Outbound state the executor
        // seeds at the wing's hex before its first step.
        private static ReconAirSortieState ProjectScoringSortie(PlayerSetupData player, AiTurnContext ctx, ArmyData wing)
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
                    && AiAviationSupport.CanSafelyEndTurnAirborne(wing, ctx.Map, player);
                int projIdx = real.AirborneTurnIndex
                    + (wouldBeNewTurn && real.LastProcessedTurn >= 0 ? 1 : 0);
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

        // Called after TaskExecutor's terminal air fallback — drop the resource protection so
        // Phase B / telemetry see the real remaining pool.
        public static void ReleaseProtection(PlayerSetupData player)
        {
            ReconAirReservationState s = ReconAirReservationRegistry.GetOrCreate(player);
            if (s.ProtectedAp > 0f || s.ProtectedEnergy > 0f)
                AiDebugLog.Write($"[AI][V2][ReconAirRes] release protection (protAp {s.ProtectedAp:0.#} "
                    + $"protEnergy {s.ProtectedEnergy:0.#}) after terminal air fallback");
            s.ClearProtection();
        }
    }
}
