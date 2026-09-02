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

                // §40–44 — a routine refresh sortie must not dip into Energy a playable high-value
                // hand card (or another in-flight AirRecon activation) still needs.
                int storageLaunchEnergy = storedAircraft.Sum(u => u != null ? u.LaunchEnergyCost : 0);
                ReconAirEnergyDecision storageEnergy = ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map,
                    storageLaunchEnergy, first.Value.Score, excludeArmyId: -1);
                AiDebugLog.Write(storageEnergy.ToLog($"airfield=({airfieldHex.Q},{airfieldHex.R})"));
                if (!storageEnergy.Allowed)
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
                // Seed the per-sortie boomerang/phase state from the real launch hex so the trail
                // and Outbound phase begin at the airfield, not one hex out.
                ReconAirSortieState launchSortie = ReconAirSortieRegistry.GetOrCreate(player, launched.Id, airfieldHex);
                launchSortie.RecordStep(launched.Hex);
                launchSortie.BestOutboundStepScore = Math.Max(launchSortie.BestOutboundStepScore, first.Value.Score);
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
                    ReconAirSortieRegistry.Retire(player, armyId);
                    if (air != null) RemoveAirReconReservation(player, air);
                    break;
                }

                if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
                    break;

                bool atAirfield = AviationRules.IsOwnedAirfieldAt(air.Hex, player);
                if (atAirfield && movedAny)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Landing at "
                        + $"({air.Hex.Q},{air.Hex.R}); sortie complete");
                    ReconAssignmentRegistry.Retire(player, armyId, "air recon landed");
                    ReconAirSortieRegistry.Retire(player, armyId);
                    RemoveAirReconReservation(player, air);
                    break;
                }
                if (air.CurrentMovement <= 0)
                    break;

                ReconAirSortieState sortie = ReconAirSortieRegistry.GetOrCreate(player, armyId, air.Hex);

                ReconMode mode = RequestedMode(snapshot);
                if (ReconAssignmentRegistry.TryGet(player, armyId, out ReconAssignment existing))
                    mode = existing.Mode;

                ReconAirStepPlanner.StepChoice? choice =
                    ReconAirStepPlanner.Pick(player, ctx, air, snapshot, mode, ctx.TurnNumber, sortie);

                // Phase transitions (spec §34). While still Outbound, a single pivot step is taken
                // once marginal information gain has dropped or the MP left after the step would
                // barely cover the proven return; after that the sortie is Return-bound.
                if (!atAirfield && sortie.Phase == ReconAirPhase.Outbound && choice.HasValue)
                {
                    sortie.BestOutboundStepScore = Math.Max(sortie.BestOutboundStepScore, choice.Value.Score);
                    int mpSlackAfterStep = air.CurrentMovement - choice.Value.RouteCost;
                    bool marginalDrop = sortie.BestOutboundStepScore > 0.01f
                        && choice.Value.Score <= AiConfigV2.airReconTurningMarginalGainFloor * sortie.BestOutboundStepScore;
                    // The MP-reserve pivot is a same-turn boomerang concept only. A deliberate
                    // multi-turn sortie keeps its own fuel-safety proof in AiAviationSupport and
                    // must not be turned back after one step.
                    bool returnReserve = choice.Value.RequiredTurns <= 1
                        && mpSlackAfterStep <= AiConfigV2.airReconTurningMpReserveSlack;
                    if (marginalDrop || returnReserve)
                    {
                        string why = returnReserve ? "return_reserve" : "marginal_gain";
                        AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Turning reason={why} "
                            + $"stepScore={choice.Value.Score:0.00} best={sortie.BestOutboundStepScore:0.00} "
                            + $"mpSlackAfter={mpSlackAfterStep}");
                        sortie.Phase = ReconAirPhase.Return;
                    }
                }

                bool forwardStepUseful = choice.HasValue
                    && choice.Value.Score >= ReconAirStepPlanner.MinimumUsefulScore;
                if (!atAirfield && sortie.Phase == ReconAirPhase.Outbound && !forwardStepUseful)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Turning reason=no_safe_forward_step");
                    sortie.Phase = ReconAirPhase.Return;
                }

                bool mustReturn = !atAirfield
                    && (sortie.Phase == ReconAirPhase.Return || !forwardStepUseful);
                if (mustReturn)
                {
                    HexCoord? returnStep = PickReturnStep(player, ctx.Map, air, sortie, out HexCoord landing,
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
                    // §34 — a Return step may still be informative when it coincides with the safe
                    // way home, but never a detour that risks the landing: the step itself is always
                    // returnStep.
                    bool alsoInformative = choice.HasValue && choice.Value.Hex.Equals(returnStep.Value)
                        && choice.Value.Score >= ReconAirStepPlanner.MinimumUsefulScore;
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Return "
                        + $"({air.Hex.Q},{air.Hex.R})->({returnStep.Value.Q},{returnStep.Value.R}) "
                        + $"landing=({landing.Q},{landing.R}) informative={(alsoInformative ? 1 : 0)} {returnReason}");
                    bool moved = false;
                    yield return MoveOne(player, ctx, air, returnStep.Value, "V2 Air Recon — safe return",
                        reservation, () => moved = true);
                    movedAny |= moved;
                    if (!moved) break;
                    ArmyData afterReturn = Resolve(player, armyId);
                    if (afterReturn != null) sortie.RecordStep(afterReturn.Hex);
                    ReconAssignmentRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                    continue;
                }

                if (!forwardStepUseful)
                    break;

                // §40–44 — Energy opportunity cost is charged once, at the launching activation.
                // A wing already airborne this turn has paid it; a wing sitting on its own airfield
                // about to take its first step this turn must clear the same reserve a storage
                // launch does.
                if (atAirfield && !air.HasActivatedThisTurn)
                {
                    ReconAirEnergyDecision energy = ReconAirEnergyPolicy.Evaluate(player, root, ctx.Map,
                        air.ActivationEnergyCost, choice.Value.Score, air.Id);
                    AiDebugLog.Write(energy.ToLog($"actor=#{armyId}"));
                    if (!energy.Allowed)
                    {
                        ReconAssignmentRegistry.Retire(player, armyId, "air recon energy opportunity cost");
                        RemoveAirReconReservation(player, air);
                        break;
                    }
                }

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
                // Track the planner's latest outbound landing so the first Return-phase step has a
                // baseline to apply landing hysteresis against (spec §38).
                sortie.ChosenLandingHex = choice.Value.LandingHex;
                sortie.HasChosenLanding = true;

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
                    $"V2 Air Recon — {mode} {sortie.Phase} one-step live replan", reservationTask, () => stepMoved = true);
                movedAny |= stepMoved;
                if (!stepMoved) break;
                ArmyData afterStep = Resolve(player, armyId);
                if (afterStep != null) sortie.RecordStep(afterStep.Hex);
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
            ReconAirSortieState sortie, out HexCoord landing, out string reason)
        {
            landing = default;
            reason = null;

            HexCoord? sameTurn = AiAviationSupport.TryReplan(air, map, player);
            if (sameTurn.HasValue)
            {
                landing = ApplyLandingHysteresis(player, map, air, sortie, sameTurn.Value, out string h);
                reason = "same-turn safest return" + h;
                return FirstStep(map, air.Hex, landing);
            }

            AiAviationSupport.MultiTurnSortie? multi =
                AiAviationSupport.TryReplanMultiTurnReturn(air, map, player);
            if (multi.HasValue)
            {
                landing = ApplyLandingHysteresis(player, map, air, sortie, multi.Value.LandingHex, out string h);
                reason = $"multi-turn safest return t{multi.Value.RequiredTurns}" + h;
                if (landing.Equals(multi.Value.LandingHex))
                {
                    HexPath p = multi.Value.PathFromActionToLanding;
                    if (p != null && p.Hexes.Count > 1)
                        return p.Hexes[1];
                }
                return FirstStep(map, air.Hex, landing);
            }
            return null;
        }

        // Spec §38 — once a sortie has locked a landing base, keep it across steps unless it is no
        // longer a viable return target, or the fresh candidate is clearly better (materially more
        // forward, or materially cheaper on the remaining route). Prevents airfield A<->B ping-pong
        // on the way home while still letting a genuinely superior base win.
        private static HexCoord ApplyLandingHysteresis(PlayerSetupData player, HexMap map, ArmyData air,
            ReconAirSortieState sortie, HexCoord candidate, out string reason)
        {
            if (sortie == null || !sortie.HasChosenLanding)
            {
                if (sortie != null) { sortie.ChosenLandingHex = candidate; sortie.HasChosenLanding = true; }
                reason = $" landing=adopt({candidate.Q},{candidate.R})";
                return candidate;
            }
            if (sortie.ChosenLandingHex.Equals(candidate))
            {
                reason = $" landing=keep({candidate.Q},{candidate.R})";
                return candidate;
            }
            if (!ReturnLandingStillViable(player, map, air, sortie.ChosenLandingHex))
            {
                reason = $" landing=switch(prev_unreachable ({sortie.ChosenLandingHex.Q},{sortie.ChosenLandingHex.R}) "
                    + $"-> ({candidate.Q},{candidate.R}))";
                sortie.ChosenLandingHex = candidate;
                return candidate;
            }

            int prevForward = AiAviationSupport.NearestKnownEnemyDistance(player, sortie.ChosenLandingHex);
            int newForward = AiAviationSupport.NearestKnownEnemyDistance(player, candidate);
            int prevCost = PathCostOrMax(map, air, sortie.ChosenLandingHex);
            int newCost = PathCostOrMax(map, air, candidate);
            bool muchMoreForward = prevForward != int.MaxValue && newForward != int.MaxValue
                && prevForward - newForward >= AiConfigV2.airReconLandingSwitchForwardMargin;
            bool muchCheaper = prevCost - newCost >= AiConfigV2.airReconLandingSwitchCostMargin;
            if (muchMoreForward || muchCheaper)
            {
                reason = $" landing=switch(forward {prevForward}->{newForward} cost {prevCost}->{newCost})";
                sortie.ChosenLandingHex = candidate;
                return candidate;
            }
            reason = $" landing=keep_hysteresis(prev ({sortie.ChosenLandingHex.Q},{sortie.ChosenLandingHex.R}) "
                + $"vs cand ({candidate.Q},{candidate.R}) forward {prevForward}/{newForward} cost {prevCost}/{newCost})";
            return sortie.ChosenLandingHex;
        }

        private static bool ReturnLandingStillViable(PlayerSetupData player, HexMap map, ArmyData air, HexCoord landing)
        {
            if (!AviationRules.IsOwnedAirfieldAt(landing, player))
                return false;
            if (AiAviationSupport.FreeLandingCapacity(landing, player, air) < air.Members.Count)
                return false;
            HexPath path = HexPathfinder.FindPath(map, air.Hex, landing, flatCost: true);
            if (path == null)
                return false;
            if (AviationRules.PathMoveCost(air, path) > air.CurrentMovement)
                return false;
            int baseline = AiAviationSupport.KnownAaExposureAt(player, air.Hex);
            return AiAviationSupport.KnownAaExposure(player, path) - baseline <= 0;
        }

        private static int PathCostOrMax(HexMap map, ArmyData air, HexCoord landing)
        {
            HexPath path = HexPathfinder.FindPath(map, air.Hex, landing, flatCost: true);
            return path != null ? AviationRules.PathMoveCost(air, path) : int.MaxValue;
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
