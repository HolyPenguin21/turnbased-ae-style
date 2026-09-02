using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ReconOnly Air Recon operational pass. It deliberately runs AFTER provisioned missions: air is
    // an information fallback, not a way to steal an aircraft from higher-priority work. No cached
    // route survives a transition. Every outbound AND return move is exactly one adjacent hex,
    // resolved by the existing authoritative aviation/movement paths, followed by live
    // visibility/IntelAge refresh and a fresh safety/reward decision.
    //
    // AiTaskKind.AirRecon is retained only as the EXISTING landing-slot reservation primitive.
    // V2 never calls AiAviationSupport.ContinueSortie for these actors: ReconAssignment + the live
    // planner own direction/mode, while the task contributes only LandingHex capacity ownership and
    // the ordinary first-move Energy-reservation seam already understood by AiAviationSupport.
    internal static class ReconAirExecutor
    {
        private const int MaxAirActorsPerTurn = 2;

        public static IEnumerator RunFallback(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot)
        {
            if (!AiStrategyV2Scope.IsReconOnly || player == null || root == null || ctx?.Map == null
                || snapshot?.Self == null)
                yield break;

            var used = new HashSet<int>();
            int actorsUsed = 0;

            var active = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && AviationRules.IsValidAirArmy(a)
                    && a.Controller != null && a.CurrentMovement > 0
                    && !AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                    && ReconAssignmentRegistry.TryGet(player, a.Id, out _))
                .OrderBy(a => a.Id)
                .ToList();

            foreach (ArmyData air in active)
            {
                if (actorsUsed >= MaxAirActorsPerTurn) break;
                yield return RunActor(player, root, ctx, snapshot, air);
                used.Add(air.Id);
                actorsUsed++;
            }

            if (actorsUsed >= MaxAirActorsPerTurn)
                yield break;

            var candidates = ArmyRegistry.AllForOwner(player)
                .Where(a => a != null && !used.Contains(a.Id)
                    && AviationRules.IsValidAirArmy(a) && a.Controller != null && a.CurrentMovement > 0
                    && AviationRules.IsOwnedAirfieldAt(a.Hex, player)
                    && AiTaskRegistry.TaskFor(player, a) == null)
                .OrderBy(a => a.HasActivatedThisTurn ? 0 : 1)
                .ThenBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationEnergyCost)
                .ThenBy(a => a.HasActivatedThisTurn ? 0 : a.ActivationApCost)
                .ThenBy(a => a.Id)
                .ToList();

            foreach (ArmyData air in candidates)
            {
                if (actorsUsed >= MaxAirActorsPerTurn) break;
                bool moved = false;
                yield return RunActor(player, root, ctx, snapshot, air, value => moved = value);
                if (moved)
                    actorsUsed++;
            }

            if (actorsUsed >= MaxAirActorsPerTurn)
                yield break;

            ReconMode requestedMode = RequestedMode(snapshot);
            foreach (HexCoord airfieldHex in AiAviationSupport.OwnedAirfieldHexes(player).ToList())
            {
                if (actorsUsed >= MaxAirActorsPerTurn) break;
                ArmyData stored = AviationRules.FindAirfieldAt(airfieldHex, player);
                if (stored == null || stored.Members.Count < AiConfig.aviationLaunchMinReadyAircraft)
                    continue;

                var storedAircraft = stored.Members.ToList();
                if (!AiAviationSupport.CanAffordLaunch(root, player, storedAircraft))
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] airfield=({airfieldHex.Q},{airfieldHex.R}) "
                        + $"aircraft={storedAircraft.Count} — launch AP/Energy unavailable; skip");
                    continue;
                }

                var launchCandidate = new AirStrikeTask.LaunchCandidate(airfieldHex, null, storedAircraft);
                ReconAirStepPlanner.StepChoice? first = ReconAirStepPlanner.PickFromStorage(
                    player, ctx, launchCandidate, snapshot, requestedMode, ctx.TurnNumber);
                if (!first.HasValue || first.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                    continue;

                bool firstVisitedBefore = VisionSystem.IsVisited(player, first.Value.Hex);
                var beforeIds = new HashSet<int>(ArmyRegistry.AllForOwner(player)
                    .Where(AviationRules.IsValidAirArmy).Select(a => a.Id));
                var launchDecision = new AiDecision
                {
                    Kind = AiActionKind.LaunchAirRecon,
                    ExistingArmy = null,
                    TargetHex = airfieldHex,
                    AircraftToLaunch = storedAircraft,
                    AirActionHex = first.Value.Hex,
                    AirLandingHex = first.Value.LandingHex,
                    Score = first.Value.Score,
                    Category = AiTaskCategory.Reconnaissance,
                    Reason = $"V2 Air Recon — {requestedMode} one-step launch; {first.Value.Reason}",
                };

                yield return AiAviationSupport.LaunchRoutine(player, launchDecision, ctx, AiTaskKind.AirRecon);

                ArmyData launched = ArmyRegistry.AllForOwner(player)
                    .Where(a => a != null && AviationRules.IsValidAirArmy(a) && !beforeIds.Contains(a.Id))
                    .OrderBy(a => a.Id)
                    .FirstOrDefault();
                if (launched == null)
                    continue;

                AiTask reservationTask = AiTaskRegistry.TaskFor(player, launched);
                if (launched.Hex.Equals(airfieldHex))
                {
                    RemoveAirReconReservation(player, launched);
                    AiDebugLog.Write($"[AI][V2][Recon][Air][Storage] actor=#{launched.Id} launch formed but "
                        + "first step made no progress; V2 assignment not started");
                    continue;
                }

                if (reservationTask != null && reservationTask.Kind == AiTaskKind.AirRecon)
                {
                    reservationTask.AirOutbound = true;
                    reservationTask.TargetHex = first.Value.Hex;
                    reservationTask.LandingHex = first.Value.LandingHex;
                }

                ReconAssignment assignment = ReconAssignmentRegistry.GetOrCreate(player, launched.Id,
                    airfieldHex, first.Value.Hex, requestedMode, ctx.TurnNumber);
                ReconAssignmentRegistry.MarkProgress(player, launched.Id, ctx.TurnNumber);
                AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
                LogVisitedInvariant(player, first.Value.Hex, firstVisitedBefore, "storage-launch-first-step");
                AiDebugLog.Write($"[AI][V2][Recon][Air][Handoff] actor=#{launched.Id} "
                    + $"launch=({airfieldHex.Q},{airfieldHex.R}) first=({launched.Hex.Q},{launched.Hex.R}) "
                    + $"mode={assignment.Mode}; V1 task retained only as landing-slot reservation");

                used.Add(launched.Id);
                actorsUsed++;

                if (launched.Controller != null && launched.CurrentMovement > 0
                    && !AviationRules.IsOwnedAirfieldAt(launched.Hex, player))
                    yield return RunActor(player, root, ctx, snapshot, launched, initialStepAlreadyMoved: true);
            }
        }

        private static IEnumerator RunActor(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot, ArmyData initial, Action<bool> movedCallback = null,
            bool initialStepAlreadyMoved = false)
        {
            bool movedAny = initialStepAlreadyMoved;
            int armyId = initial.Id;
            int guard = Math.Max(2, initial.CurrentMovement + 2);

            while (guard-- > 0)
            {
                ArmyData air = Resolve(player, armyId);
                if (air == null || !AviationRules.IsValidAirArmy(air) || air.Controller == null)
                {
                    ReconAssignmentRegistry.Retire(player, armyId, "air mover lost / invalid");
                    if (air != null) RemoveAirReconReservation(player, air);
                    break;
                }

                if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
                    break;

                bool atAirfield = AviationRules.IsOwnedAirfieldAt(air.Hex, player);
                if (atAirfield && movedAny)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air][Return] actor=#{armyId} landed at "
                        + $"({air.Hex.Q},{air.Hex.R}); sortie complete");
                    ReconAssignmentRegistry.Retire(player, armyId, "air recon landed");
                    RemoveAirReconReservation(player, air);
                    break;
                }
                if (air.CurrentMovement <= 0)
                    break;

                ReconMode mode = RequestedMode(snapshot);
                if (ReconAssignmentRegistry.TryGet(player, armyId, out ReconAssignment existing))
                    mode = existing.Mode;

                ReconAirStepPlanner.StepChoice? choice =
                    ReconAirStepPlanner.Pick(player, ctx, air, snapshot, mode, ctx.TurnNumber);

                bool mustReturn = !atAirfield
                    && (!choice.HasValue || choice.Value.Score < ReconAirStepPlanner.MinimumUsefulScore);
                if (mustReturn)
                {
                    HexCoord? returnStep = PickReturnStep(player, ctx.Map, air, out HexCoord landing,
                        out string returnReason);
                    if (!returnStep.HasValue)
                    {
                        AiDebugLog.Write($"[AI][V2][Recon][Air][Return] actor=#{armyId} at "
                            + $"({air.Hex.Q},{air.Hex.R}) — no reachable owned-airfield step; hold position");
                        break;
                    }

                    AiTask reservation = EnsureAirReconReservation(player, air, landing, outbound: false);
                    if (reservation == null)
                    {
                        AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} return blocked — another task owns aircraft");
                        break;
                    }
                    AiDebugLog.Write($"[AI][V2][Recon][Air][Return] actor=#{armyId} "
                        + $"({air.Hex.Q},{air.Hex.R})->({returnStep.Value.Q},{returnStep.Value.R}) "
                        + $"landing=({landing.Q},{landing.R}) {returnReason}");
                    bool moved = false;
                    yield return MoveOne(player, ctx, air, returnStep.Value, "V2 Air Recon — safe return",
                        reservation, () => moved = true);
                    movedAny |= moved;
                    if (!moved) break;
                    ReconAssignmentRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                    continue;
                }

                if (!choice.HasValue || choice.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                    break;

                ReconAssignment assignment = ReconAssignmentRegistry.GetOrCreate(player, armyId, air.Hex,
                    choice.Value.Hex, mode, ctx.TurnNumber);
                mode = assignment.Mode;
                AiTask reservationTask = EnsureAirReconReservation(player, air,
                    choice.Value.LandingHex, outbound: true, target: choice.Value.Hex);
                if (reservationTask == null)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} step blocked — another task owns aircraft");
                    break;
                }

                if (!AiTurnController.CanIssueMoveNow(root, player, air, ctx.Map, choice.Value.Hex, reservationTask))
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} cannot afford/issue first step "
                        + $"AP{choice.Value.ActivationAp:0.#}/E{choice.Value.ActivationEnergy:0.#}; cancel/return");
                    if (atAirfield)
                    {
                        ReconAssignmentRegistry.Retire(player, armyId, "air recon activation unaffordable");
                        RemoveAirReconReservation(player, air);
                    }
                    break;
                }

                bool stepMoved = false;
                yield return MoveOne(player, ctx, air, choice.Value.Hex,
                    $"V2 Air Recon — {mode} one-step live replan", reservationTask, () => stepMoved = true);
                movedAny |= stepMoved;
                if (!stepMoved) break;
                ReconAssignmentRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
            }

            movedCallback?.Invoke(movedAny);
        }

        private static IEnumerator MoveOne(PlayerSetupData player, AiTurnContext ctx, ArmyData air,
            HexCoord next, string reason, AiTask reservationTask, Action onMoved)
        {
            HexCoord before = air.Hex;
            bool visitedBefore = VisionSystem.IsVisited(player, next);
            var decision = AiDecision.Move(air, next, reason, reservationTask, 0f, AiTaskCategory.Reconnaissance);
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

            ArmyData live = Resolve(player, air.Id);
            HexCoord after = live != null ? live.Hex : trace.EndHex;
            if (!after.Equals(before))
                onMoved?.Invoke();

            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            LogVisitedInvariant(player, next, visitedBefore, "live-step");
            AiDebugLog.Write($"[AI][V2][Recon][Air][Observe] actor=#{air.Id} "
                + $"({before.Q},{before.R})->({after.Q},{after.R}) intel refreshed; groundVisitedWrite=0");
        }

        private static HexCoord? PickReturnStep(PlayerSetupData player, HexMap map, ArmyData air,
            out HexCoord landing, out string reason)
        {
            landing = default;
            reason = null;

            HexCoord? sameTurn = AiAviationSupport.TryReplan(air, map, player);
            if (sameTurn.HasValue)
            {
                landing = sameTurn.Value;
                reason = "same-turn safest return";
                return FirstStep(map, air.Hex, landing);
            }

            AiAviationSupport.MultiTurnSortie? multi =
                AiAviationSupport.TryReplanMultiTurnReturn(air, map, player);
            if (multi.HasValue)
            {
                landing = multi.Value.LandingHex;
                reason = $"multi-turn safest return t{multi.Value.RequiredTurns}";
                HexPath p = multi.Value.PathFromActionToLanding;
                if (p != null && p.Hexes.Count > 1)
                    return p.Hexes[1];
                return FirstStep(map, air.Hex, landing);
            }
            return null;
        }

        private static HexCoord? FirstStep(HexMap map, HexCoord from, HexCoord to)
        {
            if (from.Equals(to)) return to;
            HexPath path = HexPathfinder.FindPath(map, from, to, flatCost: true);
            return path != null && path.Hexes.Count > 1 ? path.Hexes[1] : (HexCoord?)null;
        }

        private static AiTask EnsureAirReconReservation(PlayerSetupData player, ArmyData air,
            HexCoord landing, bool outbound, HexCoord? target = null)
        {
            AiTask task = AiTaskRegistry.TaskFor(player, air);
            if (task == null)
            {
                task = new AiTask { Kind = AiTaskKind.AirRecon, Army = air };
                AiTaskRegistry.Add(player, task);
            }
            if (task.Kind != AiTaskKind.AirRecon)
                return null;

            task.AirOutbound = outbound;
            task.LandingHex = landing;
            task.TargetHex = target ?? landing;
            return task;
        }

        private static void RemoveAirReconReservation(PlayerSetupData player, ArmyData air)
        {
            AiTask task = air != null ? AiTaskRegistry.TaskFor(player, air) : null;
            if (task == null || task.Kind != AiTaskKind.AirRecon)
                return;
            AiResourceReservation.Release(task);
            AiTaskRegistry.Remove(player, task);
        }

        private static void LogVisitedInvariant(PlayerSetupData player, HexCoord hex, bool visitedBefore, string phase)
        {
            bool visitedAfter = VisionSystem.IsVisited(player, hex);
            if (!visitedBefore && visitedAfter)
                AiDebugLog.Write($"[AI][V2][Recon][Air][INVARIANT-FAIL] phase={phase} aircraft unexpectedly "
                    + $"marked ground Visited at ({hex.Q},{hex.R})");
        }

        private static ReconMode RequestedMode(WorldSnapshot snapshot)
        {
            float explore = snapshot?.MapKnowledge?.ExplorableUnknownFrac ?? 0f;
            float refresh = ReconIntelSnapshotRegistry.StalePressure(snapshot);
            return refresh > explore ? ReconMode.Refresh : ReconMode.Explore;
        }

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a != null && a.Id == armyId);
    }
}
