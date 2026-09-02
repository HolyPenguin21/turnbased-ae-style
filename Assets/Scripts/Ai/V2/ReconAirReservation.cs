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

            // Airborne wings are already committed capacity — the executor continues them regardless
            // of a fresh useful step, so they are always kept, and they owe their own first-
            // activation Energy this turn.
            foreach (int id in detail.AirborneWingIds)
                state.ReservedAirActorIds.Add(id);
            state.ReservedAirborneWings = detail.AirborneWingIds.Count;
            state.ProtectedEnergy += detail.AirborneUnactivatedEnergy;

            // Reserve launch slots only up to the still-unmet observation need, and only slots the
            // AIR-01 route scorer would ACTUALLY fly — a physically launchable aircraft with no
            // strategically useful route is NOT guaranteed capacity (that was the phantom-capacity
            // path: DemandLayer suppresses a ground scout, the executor then declines the sortie).
            // Slots are pre-ordered exactly as ReconAirExecutor launches them; the concrete aircraft
            // the executor finally picks is re-derived by the SAME deterministic rule
            // (ReconAirCapacityPolicy.SelectReconLaunchSubset), so this stays reservation ownership,
            // not a second launch planner.
            ReconMode mode = ReconAirExecutor.RequestedMode(player, snap);
            int launchNeed = UnityEngine.Mathf.Max(0, observationNeed - state.ReservedAirborneWings);
            int probed = 0, rejected = 0;
            foreach (AirObservationSlot slot in detail.AcceptedSpareSlots)
            {
                if (state.ReservedLaunchSorties >= launchNeed)
                    break;
                probed++;
                if (!SlotWouldFly(player, root, ctx, snap, mode, slot))
                {
                    rejected++;
                    continue;
                }
                state.ReservedLaunchSorties++;
                state.ProtectedAp += slot.Ap;
                state.ProtectedEnergy += slot.Energy;
                if (slot.ActorId.HasValue)
                    state.ReservedAirActorIds.Add(slot.ActorId.Value);
                else
                    state.ReservedAirfieldHexes.Add(slot.AirfieldHex);
            }

            state.GuaranteedObservationLanes = state.ReservedAirborneWings + state.ReservedLaunchSorties;
            state.Explain = $"guaranteedObsLanes={state.GuaranteedObservationLanes} "
                + $"(airborne {state.ReservedAirborneWings} + launch {state.ReservedLaunchSorties}) "
                + $"protAp={state.ProtectedAp:0.#} protEnergy={state.ProtectedEnergy:0.#} "
                + $"obsNeed={observationNeed} desiredObs={desiredObs} activeObsLanes={activeObsLaneActors.Count} "
                + $"spareAvail={detail.SpareSorties} probed={probed} routeRejected={rejected} mode={mode}";
            AiDebugLog.Write($"[AI][V2][ReconAirRes] {state.Explain}");
        }

        // Would the AIR-01 route scorer actually launch this slot? Mirrors the executor's own gates
        // (afford + Pick / PickFromStorage useful-score + Energy opportunity policy).
        private static bool SlotWouldFly(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snap, ReconMode mode, AirObservationSlot slot)
        {
            if (ctx?.Map == null)
                return true; // bare harness — leave the final gate to the executor

            if (slot.ActorId.HasValue)
            {
                ArmyData wing = ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a != null && a.Id == slot.ActorId.Value);
                if (wing == null)
                    return false;
                return ReconAirStepPlanner.Pick(player, ctx, wing, snap, mode, ctx.TurnNumber) != null;
            }

            ArmyData airfield = AviationRules.FindAirfieldAt(slot.AirfieldHex, player);
            if (airfield == null || airfield.Members.Count < UnityEngine.Mathf.Max(1, AiConfig.aviationLaunchMinReadyAircraft))
                return false;
            List<UnitData> subset = ReconAirCapacityPolicy.SelectReconLaunchSubset(airfield.Members);
            if (subset.Count == 0 || !AiAviationSupport.CanAffordLaunch(root, player, subset))
                return false;
            var candidate = new AirStrikeTask.LaunchCandidate(slot.AirfieldHex, null, subset);
            ReconAirStepPlanner.StepChoice? choice = ReconAirStepPlanner.PickFromStorage(
                player, ctx, candidate, snap, mode, ctx.TurnNumber);
            if (!choice.HasValue || choice.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                return false;
            int launchEnergy = subset.Sum(u => u != null ? u.LaunchEnergyCost : 0);
            return ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map, launchEnergy, choice.Value.Score,
                excludeArmyId: -1).Allowed;
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
