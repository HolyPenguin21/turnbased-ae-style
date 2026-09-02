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
    // execution is delegated to ReconGroundExecutor, whose durable ReconAssignment and live
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
    }

    internal static class TaskExecutor
    {
        // `snapshot` is used by the bounded stale-Explore replacement picker and by the terminal
        // Air Recon pass. Ground Recon never follows it as a route: live tactical transitions still
        // read current world state after every authoritative movement step. Air receives only the
        // frozen strategic snapshot for coarse direction/mode; its IntelAge and AA checks are live.
        public static IEnumerator Execute(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            IReadOnlyList<ProvisionedMission> provisioned, List<ExecutionResult> results,
            WorldSnapshot snapshot = null, IReadOnlyCollection<HexCoord> reservedExploreFoci = null)
        {
            if (ctx?.Map == null)
                yield break;

            // Strategic acceptance evidence can exist even when allocation/provisioning produces no
            // executable Scout this turn. Air Recon is also intentionally a terminal fallback, so
            // it must still get a chance to continue/return a multi-turn aircraft on an otherwise
            // empty mission batch.
            if (provisioned == null || provisioned.Count == 0)
            {
                if (AiStrategyV2Scope.IsReconOnly)
                    ReconAcceptanceAudit.BeginTurn(player, ctx.TurnNumber);
                yield return ReconAirExecutor.RunFallback(player, root, ctx, snapshot);
                if (AiStrategyV2Scope.IsReconOnly)
                    ReconAcceptanceAudit.Summarize(player, ctx.TurnNumber);
                yield break;
            }

            var queue = new List<ProvisionedMission>(provisioned);
            if (AiStrategyV2Scope.IsReconOnly)
            {
                ReconAcceptanceAudit.BeginTurn(player, ctx.TurnNumber);
                ReconAcceptanceAudit.RecordThreeScoutBatch(player, ctx.TurnNumber, queue);
            }
            int replacementsUsed = 0;

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
                    ReconAssignmentRegistry.Retire(player, pm.MoverArmyId, "mover gone before execution");
                    AiDebugLog.Write($"[AI][V2] exec [{pm.Mission?.AttemptId}] {pm.Key} — mover #{pm.MoverArmyId} gone before first step");
                    continue;
                }

                result.StartHex = army.Hex;
                result.FinalHex = army.Hex;

                MissionValidity validity = MissionRevalidator.Validate(player, root, ctx, pm);

                // The proposal/ledger layer may still replace a stale Explore attempt before it
                // executes. This is mission accounting, not actor identity: ReconAssignment is
                // actor-keyed and survives replacement, while the fresh proposal simply provides a
                // better strategic anchor for the same live per-step executor.
                HashSet<HexCoord> takenFoci = null;
                if (validity == MissionValidity.StaleGoalMet)
                {
                    takenFoci = new HashSet<HexCoord>();
                    for (int qi = 0; qi < queue.Count; qi++)
                        if (qi != missionIndex && queue[qi] != null)
                            takenFoci.Add(queue[qi].ExecutionHex);
                    if (reservedExploreFoci != null)
                        foreach (HexCoord h in reservedExploreFoci)
                            takenFoci.Add(h);
                }

                if (pm.Kind == MissionKind.Scout
                    && validity == MissionValidity.StaleGoalMet
                    && replacementsUsed < AiConfigV2.maxReplacementMissionsPerPass
                    && MissionRevalidator.TryPickReplacementExploreFocus(snapshot, player, pm, army.Hex,
                        takenFoci, out HexCoord replFocus)
                    && !VisionSystem.IsVisited(player, replFocus))
                {
                    ProvisionedMission repl = MissionRevalidator.BuildExploreReplacement(pm, replFocus, player);
                    if (!MissionRevalidator.IsStale(MissionRevalidator.Validate(player, root, ctx, repl)))
                    {
                        result.FinalHex = army.Hex;
                        result.ApSpent = 0f;
                        result.ReachedGoal = true;
                        result.Replaced = true;
                        result.StopReason = ExecutionStopReason.ReachedGoal;
                        ApCheck(pm, apBefore, root, result);
                        results.Add(result);

                        queue.Add(repl);
                        replacementsUsed++;
                        AiDebugLog.Write($"[AI][V2] exec [{pm.Mission?.AttemptId}] {pm.Key} — stale proposal "
                            + $"superseded → {repl.Key} actor=#{repl.MoverArmyId} "
                            + $"anchor=({replFocus.Q},{replFocus.R}); durable ReconAssignment retained");
                        AiDebugLog.Write($"[AI][V2] exec [{repl.Mission?.AttemptId}] replacementOf={pm.Mission?.AttemptId} "
                            + $"stableKey={repl.Key}");
                        continue;
                    }
                }

                if (MissionRevalidator.IsStale(validity))
                {
                    result.FinalHex = army.Hex;
                    result.ApSpent = 0f;
                    result.ReachedGoal = validity == MissionValidity.StaleGoalMet;
                    result.StopReason = validity == MissionValidity.StaleMoverLost
                        ? ExecutionStopReason.MoverLost
                        : validity == MissionValidity.StaleGoalMet
                            ? ExecutionStopReason.ReachedGoal
                            : ExecutionStopReason.TargetInvalidated;
                    ApCheck(pm, apBefore, root, result);
                    results.Add(result);
                    if (validity == MissionValidity.StaleMoverLost)
                        ReconAssignmentRegistry.Retire(player, pm.MoverArmyId, "mission revalidation lost mover");
                    AiDebugLog.Write($"[AI][V2] exec [{pm.Mission?.AttemptId}] {pm.Key} — revalidation: {validity}; "
                        + "no movement, 0 AP");
                    continue;
                }

                if (pm.Kind == MissionKind.Scout)
                {
                    yield return ReconGroundExecutor.Run(player, root, ctx, pm, result, apBefore,
                        queue, missionIndex, snapshot);
                    ApCheck(pm, apBefore, root, result);
                    results.Add(result);
                    continue;
                }

                if (pm.Kind == MissionKind.Raid)
                {
                    yield return RunRaid(player, root, ctx, pm, result, apBefore);
                    ApCheck(pm, apBefore, root, result);
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

            // Air Recon is terminal by design: provisioned ground Recon/other allowed work gets
            // first claim on the turn pool; only then may an aircraft spend remaining AP/Energy.
            // This also makes its activation costs real opportunity costs rather than budget it can
            // pre-empt from a funded ground mission.
            yield return ReconAirExecutor.RunFallback(player, root, ctx, snapshot);

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

                HexCoord? next = VisitHexTask.FindNextSafeStep(ctx.Map, army, pm.ExecutionHex);
                if (next == null)
                {
                    stop = ExecutionStopReason.NoSafeStep;
                    break;
                }

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
    }
}
