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
    // resolved by AiTurnController.MoveArmyRoutine, followed by live visibility/intel refresh and
    // a fresh safety/reward decision.
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

            // Continue already-airborne V2 Recon actors first. A fuel-limited multi-turn sortie is
            // a commitment to get home safely before a fresh aircraft is launched.
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

            // Fresh sorties start only from an owned airfield and only from an unclaimed live air
            // army. Storage launch stays on V1's dedicated launch path for now; V2 never fabricates
            // an ArmyData or bypasses AviationActions merely to get a plane into the air.
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
        }

        private static IEnumerator RunActor(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            WorldSnapshot snapshot, ArmyData initial, Action<bool> movedCallback = null)
        {
            bool movedAny = false;
            int armyId = initial.Id;
            int guard = Math.Max(2, initial.CurrentMovement + 2);

            while (guard-- > 0)
            {
                ArmyData air = Resolve(player, armyId);
                if (air == null || !AviationRules.IsValidAirArmy(air) || air.Controller == null)
                {
                    ReconAssignmentRegistry.Retire(player, armyId, "air mover lost / invalid");
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
                    break;
                }
                if (air.CurrentMovement <= 0)
                    break; // multi-turn assignment remains durable for next turn

                ReconMode mode = RequestedMode(snapshot);
                if (ReconAssignmentRegistry.TryGet(player, armyId, out ReconAssignment existing))
                    mode = existing.Mode;

                ReconAirStepPlanner.StepChoice? choice =
                    ReconAirStepPlanner.Pick(player, ctx.Map, air, snapshot, mode, ctx.TurnNumber);

                // An airborne aircraft must return whenever marginal information no longer clears
                // the opportunity floor or no complete safe onward sortie exists. A fresh aircraft
                // simply stays parked — no AP/Energy is spent for a low-value sortie.
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

                    AiDebugLog.Write($"[AI][V2][Recon][Air][Return] actor=#{armyId} "
                        + $"({air.Hex.Q},{air.Hex.R})->({returnStep.Value.Q},{returnStep.Value.R}) "
                        + $"landing=({landing.Q},{landing.R}) {returnReason}");
                    bool moved = false;
                    yield return MoveOne(player, ctx, air, returnStep.Value, "V2 Air Recon — safe return",
                        () => moved = true);
                    movedAny |= moved;
                    if (!moved) break;
                    ReconAssignmentRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                    continue;
                }

                if (!choice.HasValue || choice.Value.Score < ReconAirStepPlanner.MinimumUsefulScore)
                    break;

                // Fresh actor: create the durable actor identity only AFTER a real safe/useful first
                // step exists. The adjacent choice is merely the strategic anchor/heading seed, not
                // a cached destination; Pick() is called again after the move.
                ReconAssignment assignment = ReconAssignmentRegistry.GetOrCreate(player, armyId, air.Hex,
                    choice.Value.Hex, mode, ctx.TurnNumber);
                mode = assignment.Mode;

                if (!AiTurnController.CanIssueMoveNow(root, player, air, ctx.Map, choice.Value.Hex))
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} cannot afford/issue first step "
                        + $"AP{choice.Value.ActivationAp:0.#}/E{choice.Value.ActivationEnergy:0.#}; cancel/return");
                    if (atAirfield)
                        ReconAssignmentRegistry.Retire(player, armyId, "air recon activation unaffordable");
                    break;
                }

                bool stepMoved = false;
                yield return MoveOne(player, ctx, air, choice.Value.Hex,
                    $"V2 Air Recon — {mode} one-step live replan", () => stepMoved = true);
                movedAny |= stepMoved;
                if (!stepMoved) break;
                ReconAssignmentRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
            }

            movedCallback?.Invoke(movedAny);
        }

        private static IEnumerator MoveOne(PlayerSetupData player, AiTurnContext ctx, ArmyData air,
            HexCoord next, string reason, Action onMoved)
        {
            HexCoord before = air.Hex;
            bool visitedBefore = VisionSystem.IsVisited(player, next);
            var decision = AiDecision.Move(air, next, reason, null, 0f, AiTaskCategory.Reconnaissance);
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

            ArmyData live = Resolve(player, air.Id);
            HexCoord after = live != null ? live.Hex : trace.EndHex;
            if (!after.Equals(before))
                onMoved?.Invoke();

            // VisionSystem normally fires the sidecar event itself; the explicit stamp makes the
            // per-step Recon invariant local and obvious even if a future visibility optimization
            // coalesces events. This writes ONLY V2 IntelAge, never Visited/EverSeen.
            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);

            bool visitedAfter = VisionSystem.IsVisited(player, next);
            if (!visitedBefore && visitedAfter)
                AiDebugLog.Write($"[AI][V2][Recon][Air][INVARIANT-FAIL] aircraft step unexpectedly marked ground "
                    + $"Visited at ({next.Q},{next.R})");
            else
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
