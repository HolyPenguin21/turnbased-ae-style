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

        // Lifecycle distinction for continuity + telemetry (2026-08-31 review follow-up).
        //  Replaced      — this result is the SUPERSEDED stale mission; a live replacement was
        //                  synthesised for its mover. 0 AP, not a success.
        //  IsReplacement — this result belongs to the synthesised replacement mission (its own
        //                  fresh StableMissionKey). Counted once as a replacement, and normally
        //                  as an execution attempt / success on its own merits.
        //  Source        — the ProvisionedMission that produced this result. The caller uses it to
        //                  register a REPLACEMENT (whose proposal was never in the pre-execution
        //                  RegisterProposals set) into the MissionOutcomeLedger before recording
        //                  its execution, so continuity/reconciliation sees it too — not only
        //                  telemetry.
        public bool Replaced;
        public bool IsReplacement;
        public ProvisionedMission Source;

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
        public int StateVersionAfter = -1;

        // ARCH-02 §36 — the common lifecycle projection. StateChanged is the honest floor: only a
        // real movement step or a stealth entry moved the world. Reaching a goal that was already
        // satisfied (StaleNoOp) is a success but NOT a state change.
        public V2ActionOutcome Outcome
        {
            get
            {
                bool moved = StepsMoved > 0;
                bool succeeded = ReachedGoal || moved;
                bool changed = moved || EnteredStealth;   // NOT ReachedGoal — a stale no-op changed nothing
                return new V2ActionOutcome(
                    succeeded: succeeded, stateChanged: changed, apSpent: ApSpent,
                    resourcesSpent: null, played: false, generated: false, attached: false,
                    moved: moved, created: false, needsReplan: false,
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
            WorldSnapshot snapshot = null, IReadOnlyCollection<HexCoord> reservedExploreFoci = null)
        {
            if (ctx?.Map == null)
                yield break;

            // Strategic acceptance evidence can exist even when allocation/provisioning produces no
            // executable Scout this turn.
            if (provisioned == null || provisioned.Count == 0)
            {
                if (AiStrategyV2Scope.IsReconOnly)
                {
                    ReconAcceptanceAudit.BeginTurn(player, ctx.TurnNumber);
                    ReconAcceptanceAudit.Summarize(player, ctx.TurnNumber);
                }
                yield break;
            }

            var queue = new List<ProvisionedMission>(provisioned);
            if (AiStrategyV2Scope.IsReconOnly)
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
                    IsReplacement = pm.IsReplacement,
                    Source = pm,
                };

                int apBefore = root != null ? root.ActionPoints : 0;
                ArmyData army = Resolve(player, pm.MoverArmyId);
                if (army == null)
                {
                    result.StartHex = pm.ExecutionHex;
                    result.FinalHex = pm.ExecutionHex;
                    result.StopReason = ExecutionStopReason.MoverLost;
                    result.ApSpent = 0f;
                    ApCheck(pm, apBefore, root, result);
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
                    result.StateVersionAfter = V2StateVersion.Current;   // nothing mutated
                    result.StopReason = validity == MissionValidity.StaleMoverLost
                        ? ExecutionStopReason.MoverLost
                        : validity == MissionValidity.StaleGoalMet
                            ? ExecutionStopReason.ReachedGoal
                            : ExecutionStopReason.TargetInvalidated;
                    ApCheck(pm, apBefore, root, result);
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
                    results.Add(result);
                    continue;
                }

                if (pm.Kind == MissionKind.Raid)
                {
                    yield return RunRaid(player, root, ctx, pm, result, apBefore);
                    ApCheck(pm, apBefore, root, result);
                    StampVersion(result);
                    results.Add(result);
                    continue;
                }

                // Future mission kinds must opt into an executor explicitly. Never silently treat
                // an unknown mission as Scout or let it mutate the world through a fallback path.
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.ApSpent = 0f;
                ApCheck(pm, apBefore, root, result);
                results.Add(result);
                AiDebugLog.Write($"[AI][V2] exec [{pm.Mission?.AttemptId}] {pm.Key} — unsupported mission kind {pm.Kind}");
            }

            // TaskExecutor owns the batch lifecycle, so the summary is still written when the last
            // provisioned Scout becomes stale, loses its mover, or otherwise never enters the Ground
            // executor. Individual Ground hooks may summarize earlier; the collector is idempotent
            // and automatically reopens the summary if later evidence changes a status.
            if (AiStrategyV2Scope.IsReconOnly)
                ReconAcceptanceAudit.Summarize(player, ctx.TurnNumber);
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
                if (++iterations > maxIterations)
                {
                    stop = ExecutionStopReason.MoveRejected;
                    break;
                }

                army = Resolve(player, pm.MoverArmyId);
                if (army == null || army.Owner != player)
                {
                    stop = ExecutionStopReason.MoverLost;
                    break;
                }
                if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
                {
                    stop = ExecutionStopReason.BattleStarted;
                    break;
                }

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
                if (army.CurrentMovement <= 0)
                {
                    stop = ExecutionStopReason.OutOfMovement;
                    break;
                }

                HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, pm.ExecutionHex);
                if (next == null)
                {
                    stop = ExecutionStopReason.NoSafeStep;
                    break;
                }

                HexCoord before = army.Hex;
                var decision = AiDecision.Move(army, next.Value,
                    $"V2 raid — strike #{pm.RaidTargetArmyId} at ({pm.ExecutionHex.Q},{pm.ExecutionHex.R})", 0f);
                var trace = new AiMoveExecutionTrace();
                yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

                army = Resolve(player, pm.MoverArmyId);
                HexCoord endHex = army != null ? army.Hex : trace.EndHex;
                if (!endHex.Equals(before))
                    result.StepsMoved++;
                result.FinalHex = endHex;

                if (trace.BattleOccurred)
                {
                    stop = ExecutionStopReason.BattleStarted;
                    break;
                }
                if (trace.HexEventOccurred)
                {
                    stop = ExecutionStopReason.HexEventStarted;
                    break;
                }
                if (army == null)
                {
                    stop = ExecutionStopReason.MoverLost;
                    break;
                }
                if (endHex.Equals(before))
                {
                    stop = ExecutionStopReason.MoveRejected;
                    break;
                }
            }

            result.FinalHex = Resolve(player, pm.MoverArmyId)?.Hex ?? result.FinalHex;
            result.StopReason = stop;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            AiDebugLog.Write($"[AI][V2] exec [{pm.Mission?.AttemptId}] {pm.Key} — raid "
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
            if (result.StepsMoved > 0 || result.EnteredStealth)
                V2StateVersion.Bump();
            result.StateVersionAfter = V2StateVersion.Current;
        }
    }
}
