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
    // Executes provisioned V2 missions through authoritative game paths. TaskExecutor now owns
    // only the shared mission lifecycle/accounting shell plus Raid execution. Ground Scout
    // execution is delegated to ReconGroundExecutor, whose durable ReconPatrolState and live
    // one-step planner replace the old fixed Explore focus + single follow-through loop.
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
        StepCompleted,
    }

    public sealed class ExecutionResult : IV2ActionResult
    {
        public StableMissionKey Key;
        public HexCoord StartHex;
        public HexCoord FinalHex;
        public int StepsMoved;
        public bool ReachedGoal;
        public float ApSpent;
        public ExecutionStopReason StopReason;
        public bool EnteredStealth;
        public bool StealthChanged;
        public bool InfrastructureChanged;
        public bool RaidOperationStarted;

        // Provisioned mission that produced this execution ledger row.
        public ProvisionedMission Source;

        // RECON-AIR-06 — the REAL ArmyId this mission's actor resolved to, when it can differ from
        // ProvisionedMission.MoverArmyId. Ground matches the provisioned id; Raid stamps that
        // same real id when the operation starts. Air's AirLaunch is the one case that DOES differ:
        // Assignment bound the mission to a synthetic per-airfield negative id (no
        // ArmyData exists yet), and only once the aircraft actually launches does a real ArmyId
        // exist — that real id belongs in MissionContinuity from then on, not the synthetic key.
        public int? ActualActorArmyId;

        // Spec §1/§7 (review P1 #1) — set by the continuous ground Recon executor when ReachedGoal
        // is true only because the CURRENT focus hex (a live waypoint) was satisfied, while the
        // actor's durable Explore/Refresh role is still runnable. The ledger then classifies this
        // as a ProductiveStop, NOT a Completed objective, so the durable MissionIntent is kept and
        // re-focused next turn instead of being retired — the churn the rework was meant to end.
        public bool DurableRoleContinues;

        // Spec §2 — the requested Recon movement never started because the actor was ALREADY
        // combat-locked before its first step (BattleStarted with zero progress on iteration 1).
        // The ledger treats that as a recoverable Blocked, not a structural Failed: the durable
        // Recon role survives and is retried once the actor can leave combat. A battle/event that
        // interrupts a scout AFTER it has moved / entered stealth / made a discovery is a
        // ProductiveStop instead and never sets this.
        public bool BlockedBeforeMovement;

        // ARCH-02 §36 — a stale-goal mission that did nothing (revalidation found the objective
        // already satisfied: ReachedGoal=true, 0 AP, no movement). It "succeeded" in that the
        // objective is met, but it changed NOTHING — the common contract must not report
        // StateChanged for it.
        public bool StaleNoOp;
        public bool NeedsReplan;
        public int PlannedAtStateVersion = -1;
        public int StateVersionBefore = -1;
        public int StateVersionAfter = -1;
        public V2ResourceStamp ResourcesBefore;
        public V2ResourceStamp ResourcesAfter;

        // ARCH-02 §36 — the common lifecycle projection. StateChanged is the honest floor:
        // movement, stealth transition, or infrastructure ownership/destruction. Reaching a goal
        // that was already satisfied (StaleNoOp) is a success but NOT a state change.
        public V2ActionOutcome Outcome
        {
            get
            {
                bool moved = StepsMoved > 0;
                bool succeeded = ReachedGoal || moved || InfrastructureChanged;
                bool changed = moved || EnteredStealth || StealthChanged || InfrastructureChanged;
                // ReachedGoal alone remains a stale no-op; capture/ownership mutation is explicit.
                return new V2ActionOutcome(
                    succeeded: succeeded, stateChanged: changed, apSpent: ApSpent,
                    resourcesSpent: null, played: false, generated: false, attached: false,
                    moved: moved, created: false, needsReplan: NeedsReplan,
                    stateVersionAfter: StateVersionAfter,
                    failReason: succeeded ? (StaleNoOp && !moved ? "goal already satisfied (no-op)" : null)
                                          : StopReason.ToString());
            }
        }
    }

    internal static class TaskExecutor
    {
        // `snapshot` is passed through to the per-mission executors. ARCH-02 §35 — the terminal
        // air-recon pass is NO LONGER run here: the orchestrator plans it (AirReconPlanner) and
        // runs it (ReconAirExecutor.Execute) as its own stage after this returns.
        public static IEnumerator Execute(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            IReadOnlyList<ProvisionedMission> provisioned, List<ExecutionResult> results,
            WorldSnapshot snapshot = null, bool enforceFreshPlan = false)
        {
            if (ctx?.Map == null)
                yield break;

            // Strategic acceptance evidence can exist even when allocation/provisioning produces no
            // executable Scout this turn.
            if (provisioned == null || provisioned.Count == 0)
            {
                if (AiStrategyV2Scope.IsFocusScoped)
                {
                    ReconAcceptanceAudit.BeginTurn(player, ctx.TurnNumber);
                    ReconAcceptanceAudit.Summarize(player, ctx.TurnNumber);
                }
                yield break;
            }

            var queue = new List<ProvisionedMission>(provisioned);
            if (AiStrategyV2Scope.IsFocusScoped)
            {
                ReconAcceptanceAudit.BeginTurn(player, ctx.TurnNumber);
                ReconAcceptanceAudit.RecordThreeScoutBatch(player, ctx.TurnNumber, queue);
            }

            for (int missionIndex = 0; missionIndex < queue.Count; missionIndex++)
            {
                ProvisionedMission pm = queue[missionIndex];
                var result = new ExecutionResult
                {
                    Key = pm.Key,
                    Source = pm,
                    PlannedAtStateVersion = pm.PlannedAtStateVersion,
                    StateVersionBefore = V2StateVersion.Current,
                    ResourcesBefore = AiV2Trace.Stamp(root),
                };

                if (enforceFreshPlan && pm.PlannedAtStateVersion >= 0
                    && !V2StateVersion.IsCurrent(pm.PlannedAtStateVersion))
                {
                    result.StartHex = pm.ExecutionHex;
                    result.FinalHex = pm.ExecutionHex;
                    result.StopReason = ExecutionStopReason.TargetInvalidated;
                    result.NeedsReplan = true;
                    result.ApSpent = 0f;
                    StrategicInterruptRegistry.Mark(player, ctx.TurnNumber,
                        StrategicInvalidationReason.External, actorIds: new[] { pm.MoverArmyId });
                    CompleteResult(result, root);
                    results.Add(result);
                    AiDebugLog.Write($"[AI][V2] exec [{pm.Mission?.AttemptId}] {pm.Key} — stale plan "
                        + $"planned@v{pm.PlannedAtStateVersion}, current=v{V2StateVersion.Current}; no command issued");
                    continue;
                }

                int apBefore = root != null ? root.ActionPoints : 0;
                ArmyData army = Resolve(player, pm.MoverArmyId);
                if (army == null)
                {
                    result.StartHex = pm.ExecutionHex;
                    result.FinalHex = pm.ExecutionHex;
                    result.StopReason = ExecutionStopReason.MoverLost;
                    result.ApSpent = 0f;
                    ApCheck(pm, apBefore, root, result);
                    CompleteResult(result, root);
                    results.Add(result);
                    ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId, "mover gone before execution");
                    AiDebugLog.Write($"[AI][V2] exec [{pm.Mission?.AttemptId}] {pm.Key} — mover #{pm.MoverArmyId} gone before first step");
                    continue;
                }

                result.StartHex = army.Hex;
                result.FinalHex = army.Hex;

                MissionValidity validity = MissionRevalidator.Validate(player, root, ctx, pm);

                // ARCH-02 §35 — the executor does NOT synthesise a replacement mission for a
                // stale-goal Scout. It records the stale outcome; MissionContinuityLayer.Reconcile
                // + the mission planner re-target the durable ReconPatrolState on the next pass.
                if (MissionRevalidator.IsStale(validity))
                {
                    result.FinalHex = army.Hex;
                    result.ApSpent = 0f;
                    result.ReachedGoal = validity == MissionValidity.StaleGoalMet;
                    result.StaleNoOp = validity == MissionValidity.StaleGoalMet;
                    result.DurableRoleContinues = result.ReachedGoal
                        && pm.Mission?.FromDurableIntent == true
                        && pm.Kind == MissionKind.Scout
                        && pm.ScoutKind != ScoutTargetKind.Surveil;
                    result.StateVersionAfter = V2StateVersion.Current;   // nothing mutated
                    result.StopReason = validity == MissionValidity.StaleMoverLost
                        ? ExecutionStopReason.MoverLost
                        : validity == MissionValidity.StaleGoalMet
                            ? ExecutionStopReason.ReachedGoal
                            : ExecutionStopReason.TargetInvalidated;
                    ApCheck(pm, apBefore, root, result);
                    CompleteResult(result, root);
                    results.Add(result);
                    if (validity == MissionValidity.StaleMoverLost)
                        ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId, "mission revalidation lost mover");
                    AiDebugLog.Write($"[AI][V2] exec [{pm.Mission?.AttemptId}] {pm.Key} — revalidation: {validity}; "
                        + "no movement, 0 AP");
                    continue;
                }

                if (pm.Kind == MissionKind.Scout)
                {
                    yield return ReconGroundExecutor.Run(player, root, ctx, pm, result, apBefore,
                        queue, missionIndex, snapshot);
                    ApCheck(pm, apBefore, root, result);
                    StampVersion(result);
                    CompleteResult(result, root);
                    results.Add(result);
                    continue;
                }

                if (pm.Kind == MissionKind.Raid)
                {
                    yield return RunRaid(player, root, ctx, pm, result, apBefore);
                    ApCheck(pm, apBefore, root, result);
                    StampVersion(result);
                    CompleteResult(result, root);
                    results.Add(result);
                    continue;
                }

                // Future mission kinds must opt into an executor explicitly. Never silently treat
                // an unknown mission as Scout or let it mutate the world through a fallback path.
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.ApSpent = 0f;
                ApCheck(pm, apBefore, root, result);
                CompleteResult(result, root);
                results.Add(result);
                AiDebugLog.Write($"[AI][V2] exec [{pm.Mission?.AttemptId}] {pm.Key} — unsupported mission kind {pm.Kind}");
            }

            // TaskExecutor owns the batch lifecycle, so the summary is still written when the last
            // provisioned Scout becomes stale, loses its mover, or otherwise never enters the Ground
            // executor. Individual Ground hooks may summarize earlier; the collector is idempotent
            // and automatically reopens the summary if later evidence changes a status.
            if (AiStrategyV2Scope.IsFocusScoped)
                ReconAcceptanceAudit.Summarize(player, ctx.TurnNumber);
        }

        // Compatibility adapter for the current Full/Aggression batch path. The target is
        // revalidated before every adjacent move, but the adapter keeps executing steps until the
        // same terminal conditions as the previous loop.
        private static IEnumerator RunRaid(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            ArmyData initialArmy = Resolve(player, pm.MoverArmyId);
            int maxIterations = (initialArmy?.CurrentMovement ?? 0) + 1;
            int iterations = 0;
            ExecutionStopReason stop = ExecutionStopReason.OutOfMovement;

            while (true)
            {
                if (++iterations > maxIterations)
                {
                    stop = ExecutionStopReason.MoveRejected;
                    break;
                }

                yield return RunRaidStepCore(player, root, ctx, pm, result);
                stop = result.StopReason;
                if (stop != ExecutionStopReason.StepCompleted)
                    break;
            }

            FinishRaid(player, root, pm, result, apBefore, stop);
        }

        // One admitted Raid task step. It executes at most one adjacent MoveArmyRoutine command.
        // Objective identity stays fixed, while its last honestly-known hex and route are resolved
        // again immediately before the command.
        internal static IEnumerator RunRaidStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            yield return RunRaidStepCore(player, root, ctx, pm, result);
            FinishRaid(player, root, pm, result, apBefore, result.StopReason);
        }

        private static IEnumerator RunRaidStepCore(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result)
        {
            ArmyData army = Resolve(player, pm?.MoverArmyId ?? -1);
            if (army == null || army.Owner != player)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                yield break;
            }
            if (ctx?.Map == null)
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = true;
                yield break;
            }
            if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
            {
                result.StopReason = ExecutionStopReason.BattleStarted;
                yield break;
            }

            if (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, pm.RaidTargetArmyId))
            {
                result.ReachedGoal = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
                yield break;
            }

            AiMapMemory.KnownEnemySighting? target = FindRaidSighting(
                player, pm.RaidTargetArmyId);
            if (!target.HasValue)
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = true;
                yield break;
            }

            HexCoord targetHex = target.Value.Hex;
            pm.ExecutionHex = targetHex;
            pm.RaidLastKnownHex = targetHex;
            pm.RaidTargetIsNeutral = target.Value.Owner != null && target.Value.Owner.IsNeutral;

            if (army.Hex.Equals(targetHex))
            {
                result.StopReason = ExecutionStopReason.EnemyDiscovered;
                yield break;
            }
            if (army.CurrentMovement <= 0)
            {
                result.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }

            HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, targetHex);
            if (!next.HasValue)
            {
                result.StopReason = ExecutionStopReason.NoSafeStep;
                result.NeedsReplan = true;
                yield break;
            }

            HexCoord before = army.Hex;
            var decision = AiDecision.Move(army, next.Value,
                $"V2 raid — strike #{pm.RaidTargetArmyId} at ({targetHex.Q},{targetHex.R})", 0f);
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

            army = Resolve(player, pm.MoverArmyId);
            HexCoord endHex = army != null ? army.Hex : trace.EndHex;
            bool moved = !endHex.Equals(before);
            if (moved)
                result.StepsMoved++;
            result.FinalHex = endHex;

            bool operationStarted = moved || trace.BattleOccurred || trace.HexEventOccurred;
            result.RaidOperationStarted |= operationStarted;
            if (operationStarted)
                result.ActualActorArmyId = pm.MoverArmyId;

            if (trace.BattleOccurred)
            {
                result.StopReason = ExecutionStopReason.BattleStarted;
                yield break;
            }
            if (trace.HexEventOccurred)
            {
                result.StopReason = ExecutionStopReason.HexEventStarted;
                yield break;
            }
            if (army == null)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                yield break;
            }
            if (!moved)
            {
                result.StopReason = ExecutionStopReason.MoveRejected;
                yield break;
            }

            result.StopReason = army.CurrentMovement > 0
                ? ExecutionStopReason.StepCompleted
                : ExecutionStopReason.OutOfMovement;
        }

        private static AiMapMemory.KnownEnemySighting? FindRaidSighting(
            PlayerSetupData player, int targetArmyId)
        {
            foreach (AiMapMemory.KnownEnemySighting sighting in
                AiMapMemory.AllKnownEnemySightings(player))
                if (sighting.ArmyId == targetArmyId)
                    return sighting;
            foreach (AiMapMemory.KnownEnemySighting sighting in
                AiMapMemory.AllKnownNeutralSightings(player))
                if (sighting.ArmyId == targetArmyId)
                    return sighting;
            return null;
        }

        private static void FinishRaid(PlayerSetupData player, PlayerRoot root,
            ProvisionedMission pm, ExecutionResult result, int apBefore,
            ExecutionStopReason stop)
        {
            result.FinalHex = Resolve(player, pm?.MoverArmyId ?? -1)?.Hex ?? result.FinalHex;
            result.StopReason = stop;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            AiDebugLog.Write($"[AI][V2] exec [{pm?.Mission?.AttemptId}] {pm?.Key} — raid "
                + $"({result.StartHex.Q},{result.StartHex.R})→({result.FinalHex.Q},{result.FinalHex.R}) "
                + $"steps {result.StepsMoved} ap −{result.ApSpent.ToString("0.#", CultureInfo.InvariantCulture)} "
                + $"stop {stop}" + (result.ReachedGoal ? " (target gone)" : ""));
        }

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);

        // §2.1 — the real AP the turn's pool lost while this mission executed must equal the AP the
        // ExecutionResult reports it spent. Both executors derive ApSpent from the same physical
        // turn-pool delta; this catches any action path that reports less/more than it really used.
        private static void ApCheck(ProvisionedMission pm, int apBefore, PlayerRoot root,
            ExecutionResult result) =>
            AiV2Trace.CheckExecutionAp(pm?.Mission?.AttemptId, apBefore,
                root != null ? root.ActionPoints : apBefore,
                result != null ? result.ApSpent : 0f);

        // ARCH-02 §36 — bump the shared state version iff this mission execution actually moved the
        // world (a real step or a stealth entry), then stamp it onto the result.
        private static void StampVersion(ExecutionResult result)
        {
            if (result == null) return;
            if (result.StepsMoved > 0 || result.EnteredStealth
                || result.StealthChanged || result.InfrastructureChanged)
                V2StateVersion.Bump();
            result.StateVersionAfter = V2StateVersion.Current;
        }

        private static void CompleteResult(ExecutionResult result, PlayerRoot root)
        {
            if (result == null) return;
            if (result.StateVersionAfter < 0)
                result.StateVersionAfter = V2StateVersion.Current;
            result.ResourcesAfter = AiV2Trace.Stamp(root);
        }
    }
}
