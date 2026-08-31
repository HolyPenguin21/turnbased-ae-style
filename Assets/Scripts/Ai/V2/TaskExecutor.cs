using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // Executes provisioned V2 missions through the canonical movement path. Scout movement is
    // deliberately one hex per iteration so every step can settle vision/contact/event/battle state
    // before the next decision. Explore may tactically continue after its strategic focus is reached
    // while movement remains; Surveil never retargets and Raid keeps its own loop below.
    public enum ExecutionStopReason
    {
        ReachedGoal,
        OutOfMovement,
        NoSafeStep,
        EnemyDiscovered,
        NeutralDiscovered,
        BattleStarted,
        HexEventStarted,
        MoverLost,
        TargetInvalidated,
        MoveRejected,
        RequiredStealthUnavailable,
        ObservationUnavailable,
    }

    public sealed class ExecutionResult
    {
        public StableMissionKey Key;
        public HexCoord StartHex;
        public HexCoord FinalHex;
        public int StepsMoved;
        public bool ReachedGoal;
        public float ApSpent;
        public ExecutionStopReason StopReason;
        public bool EnteredStealth;
    }

    internal static class TaskExecutor
    {
        public static IEnumerator Execute(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            IReadOnlyList<ProvisionedMission> provisioned, List<ExecutionResult> results)
        {
            if (provisioned == null || provisioned.Count == 0 || ctx?.Map == null)
                yield break;

            foreach (ProvisionedMission pm in provisioned)
            {
                var result = new ExecutionResult { Key = pm.Key };
                ArmyData army = Resolve(player, pm.MoverArmyId);
                if (army == null)
                {
                    result.StartHex = pm.ExecutionHex;
                    result.FinalHex = pm.ExecutionHex;
                    result.StopReason = ExecutionStopReason.MoverLost;
                    results.Add(result);
                    AiDebugLog.Write($"[AI][V2] exec {pm.Key} — mover #{pm.MoverArmyId} gone before first step");
                    continue;
                }

                result.StartHex = army.Hex;
                result.FinalHex = army.Hex;
                int apBefore = root != null ? root.ActionPoints : 0;

                if (pm.Kind == MissionKind.Raid)
                {
                    yield return RunRaid(player, root, ctx, pm, result, apBefore);
                    results.Add(result);
                    continue;
                }

                if (pm.ScoutKind == ScoutTargetKind.Surveil && IsSurveilSatisfied(player, pm))
                {
                    result.ReachedGoal = true;
                    result.StopReason = ExecutionStopReason.ReachedGoal;
                    result.ApSpent = 0f;
                    results.Add(result);
                    AiDebugLog.Write($"[AI][V2] exec {pm.Key} — surveil already satisfied before start — no movement, 0 AP");
                    continue;
                }

                if (pm.StealthApReserved)
                {
                    if (!TryEnterRequiredStealth(root, army, out bool enteredStealth))
                    {
                        result.FinalHex = army.Hex;
                        result.StopReason = ExecutionStopReason.RequiredStealthUnavailable;
                        result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                        results.Add(result);
                        AiDebugLog.Write($"[AI][V2] exec {pm.Key} — WARN mover #{pm.MoverArmyId} could not enter required stealth; mission aborted for this turn");
                        continue;
                    }
                    result.EnteredStealth |= enteredStealth;
                }

                int maxIterations = army.CurrentMovement + 1;
                int iterations = 0;
                ExecutionStopReason stop = ExecutionStopReason.OutOfMovement;
                HexCoord executionHex = pm.ExecutionHex;
                bool primaryExploreSatisfied = false;

                HashSet<int> knownEnemyIds = KnownIds(AiMapMemory.AllKnownEnemySightings(player));
                HashSet<int> knownNeutralIds = KnownIds(AiMapMemory.AllKnownNeutralSightings(player));

                while (true)
                {
                    if (++iterations > maxIterations)
                    {
                        stop = ExecutionStopReason.MoveRejected;
                        break;
                    }

                    army = Resolve(player, pm.MoverArmyId);
                    if (army == null || army.Owner != player) { stop = ExecutionStopReason.MoverLost; break; }
                    if (!AiArmyRoles.IsSoloRecce(army)) { stop = ExecutionStopReason.MoverLost; break; }
                    if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive) { stop = ExecutionStopReason.BattleStarted; break; }

                    if (pm.ScoutKind == ScoutTargetKind.Surveil)
                    {
                        if (IsSurveilSatisfied(player, pm))
                        {
                            result.ReachedGoal = true;
                            stop = ExecutionStopReason.ReachedGoal;
                            break;
                        }
                        if (army.Hex.Equals(executionHex))
                        {
                            stop = ExecutionStopReason.ObservationUnavailable;
                            break;
                        }
                        if (ScoutExecutionSafety.VantageBlockedNow(player, executionHex, ctx.TurnNumber))
                        {
                            stop = ExecutionStopReason.TargetInvalidated;
                            break;
                        }
                    }
                    else
                    {
                        bool goalSatisfied = army.Hex.Equals(executionHex) || VisionSystem.IsVisited(player, executionHex);
                        if (goalSatisfied)
                        {
                            if (!primaryExploreSatisfied)
                            {
                                primaryExploreSatisfied = true;
                                result.ReachedGoal = true;
                            }

                            if (army.CurrentMovement <= 0)
                            {
                                stop = ExecutionStopReason.OutOfMovement;
                                break;
                            }

                            HexCoord? continuation = ScoutExploreContinuation.Pick(player, ctx.Map, army, ctx.TurnNumber);
                            if (!continuation.HasValue)
                            {
                                stop = ExecutionStopReason.ReachedGoal;
                                break;
                            }

                            HexCoord oldGoal = executionHex;
                            executionHex = continuation.Value;
                            AiDebugLog.Write($"[AI][V2] exec {pm.Key} — explore follow-through ({army.Hex.Q},{army.Hex.R}) "
                                + $"primary=({oldGoal.Q},{oldGoal.R}) next=({executionHex.Q},{executionHex.R}) "
                                + $"movement={army.CurrentMovement}");
                        }

                        if (AiMapMemory.KnownEnemySightingAt(player, executionHex).HasValue)
                        {
                            stop = ExecutionStopReason.TargetInvalidated;
                            break;
                        }
                    }

                    if (army.CurrentMovement <= 0) { stop = ExecutionStopReason.OutOfMovement; break; }

                    HexCoord? next = VisitHexTask.FindNextSafeStep(ctx.Map, army, executionHex);
                    if (next == null) { stop = ExecutionStopReason.NoSafeStep; break; }

                    HexCoord before = army.Hex;
                    var decision = AiDecision.Move(army, next.Value,
                        $"V2 recon — {pm.ScoutKind} toward ({executionHex.Q},{executionHex.R})",
                        null, 0f, AiTaskCategory.Reconnaissance);
                    var trace = new AiMoveExecutionTrace();
                    yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);
                    result.EnteredStealth |= trace.EnteredStealthThisStep;

                    army = Resolve(player, pm.MoverArmyId);
                    HexCoord endHex = army != null ? army.Hex : trace.EndHex;
                    bool moved = !endHex.Equals(before);
                    if (moved)
                        result.StepsMoved++;
                    result.FinalHex = endHex;

                    if (army == null) { stop = ExecutionStopReason.MoverLost; break; }
                    if (trace.BattleOccurred) { stop = ExecutionStopReason.BattleStarted; break; }
                    if (trace.HexEventOccurred) { stop = ExecutionStopReason.HexEventStarted; break; }

                    if (pm.ScoutKind == ScoutTargetKind.Surveil && IsSurveilSatisfied(player, pm))
                    {
                        result.ReachedGoal = true;
                        stop = ExecutionStopReason.ReachedGoal;
                        break;
                    }

                    if (!moved)
                    {
                        stop = ExecutionStopReason.MoveRejected;
                        break;
                    }

                    HashSet<int> enemyNow = KnownIds(AiMapMemory.AllKnownEnemySightings(player));
                    HashSet<int> neutralNow = KnownIds(AiMapMemory.AllKnownNeutralSightings(player));
                    int[] newEnemyIds = enemyNow.Where(id => !knownEnemyIds.Contains(id)).ToArray();
                    int[] newNeutralIds = neutralNow.Where(id => !knownNeutralIds.Contains(id)).ToArray();
                    knownEnemyIds = enemyNow;
                    knownNeutralIds = neutralNow;
                    if (newEnemyIds.Length > 0)
                    {
                        StrategicInterruptRegistry.MarkDiscovery(player, ctx.TurnNumber, newEnemyIds);
                        AiDebugLog.Write($"[AI][V2] strategic interrupt — scout discovered enemy army id(s) [{string.Join(",", newEnemyIds)}]");
                        stop = ExecutionStopReason.EnemyDiscovered;
                        break;
                    }
                    if (newNeutralIds.Length > 0)
                    {
                        StrategicInterruptRegistry.MarkDiscovery(player, ctx.TurnNumber, newNeutralIds);
                        AiDebugLog.Write($"[AI][V2] strategic interrupt — scout discovered neutral army id(s) [{string.Join(",", newNeutralIds)}]");
                        stop = ExecutionStopReason.NeutralDiscovered;
                        break;
                    }
                }

                result.FinalHex = army?.Hex ?? result.FinalHex;
                result.StopReason = stop;
                result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                results.Add(result);

                AiDebugLog.Write($"[AI][V2] exec {pm.Key} — ({result.StartHex.Q},{result.StartHex.R})→"
                    + $"({result.FinalHex.Q},{result.FinalHex.R}) steps {result.StepsMoved} "
                    + $"ap −{result.ApSpent.ToString("0.#", CultureInfo.InvariantCulture)} stop {stop}"
                    + (result.ReachedGoal ? " (goal)" : ""));
            }
        }

        private static IEnumerator RunRaid(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            ExecutionStopReason stop = ExecutionStopReason.OutOfMovement;
            ArmyData army = Resolve(player, pm.MoverArmyId);
            int maxIterations = (army?.CurrentMovement ?? 0) + 1;
            int iterations = 0;

            while (true)
            {
                if (++iterations > maxIterations) { stop = ExecutionStopReason.MoveRejected; break; }

                army = Resolve(player, pm.MoverArmyId);
                if (army == null || army.Owner != player) { stop = ExecutionStopReason.MoverLost; break; }
                if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive) { stop = ExecutionStopReason.BattleStarted; break; }

                if (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, pm.RaidTargetArmyId))
                {
                    result.ReachedGoal = true;
                    stop = ExecutionStopReason.ReachedGoal;
                    break;
                }

                if (army.Hex.Equals(pm.ExecutionHex))
                {
                    stop = ExecutionStopReason.EnemyDiscovered;
                    break;
                }
                if (army.CurrentMovement <= 0) { stop = ExecutionStopReason.OutOfMovement; break; }

                HexCoord? next = VisitHexTask.FindNextSafeStep(ctx.Map, army, pm.ExecutionHex);
                if (next == null) { stop = ExecutionStopReason.NoSafeStep; break; }

                HexCoord before = army.Hex;
                var decision = AiDecision.Move(army, next.Value,
                    $"V2 raid — strike #{pm.RaidTargetArmyId} at ({pm.ExecutionHex.Q},{pm.ExecutionHex.R})",
                    null, 0f, AiTaskCategory.Aggression);
                var trace = new AiMoveExecutionTrace();
                yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

                army = Resolve(player, pm.MoverArmyId);
                HexCoord endHex = army != null ? army.Hex : trace.EndHex;
                if (!endHex.Equals(before))
                    result.StepsMoved++;
                result.FinalHex = endHex;

                if (trace.BattleOccurred) { stop = ExecutionStopReason.BattleStarted; break; }
                if (trace.HexEventOccurred) { stop = ExecutionStopReason.HexEventStarted; break; }
                if (army == null) { stop = ExecutionStopReason.MoverLost; break; }
                if (endHex.Equals(before)) { stop = ExecutionStopReason.MoveRejected; break; }
            }

            result.FinalHex = Resolve(player, pm.MoverArmyId)?.Hex ?? result.FinalHex;
            result.StopReason = stop;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            AiDebugLog.Write($"[AI][V2] exec {pm.Key} — raid ({result.StartHex.Q},{result.StartHex.R})→"
                + $"({result.FinalHex.Q},{result.FinalHex.R}) steps {result.StepsMoved} "
                + $"ap −{result.ApSpent.ToString("0.#", CultureInfo.InvariantCulture)} stop {stop}"
                + (result.ReachedGoal ? " (target gone)" : ""));
        }

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);

        private static bool IsSurveilSatisfied(PlayerSetupData player, ProvisionedMission pm) =>
            pm.ScoutKind == ScoutTargetKind.Surveil
            && ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex, pm.TrackedArmyId, pm.BaselineObservedTurn);

        private static bool TryEnterRequiredStealth(PlayerRoot root, ArmyData army, out bool entered)
        {
            entered = false;
            if (army == null || army.Members.Count == 0)
                return false;
            if (army.Members.Any(m => m.IsHidden))
                return true;
            if (army.HasActivatedThisTurn)
                return false;
            var scout = army.Members[0];
            if (!StealthSystem.CanEnterStealth(scout))
                return false;
            if (root == null || !root.CanSpendActionPoints(army.ActivationApCost + 1))
                return false;
            root.SpendActionPoints(1);
            StealthSystem.EnterStealth(scout);
            entered = true;
            return true;
        }

        private static HashSet<int> KnownIds(IEnumerable<AiMapMemory.KnownEnemySighting> sightings)
        {
            var set = new HashSet<int>();
            foreach (AiMapMemory.KnownEnemySighting s in sightings)
                set.Add(s.ArmyId);
            return set;
        }
    }
}
