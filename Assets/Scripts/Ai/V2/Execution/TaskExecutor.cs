using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
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
        public bool CombatChanged;
        public bool RaidOperationStarted;
        // Immutable execution fact consumed by Continuity. A reinforcement handoff may change the
        // RaidIntent phase before the outcome ledger reconciles it, so actor-role ownership must
        // never be inferred from the intent's already-mutated current phase.
        public bool RaidReinforcementHandoffAttempted;

        // Set when an Economy builder reaches its BuildExtraction/FoundBase target this step but
        // the infrastructure itself is not up yet (that's Phase A's job next admission). Nothing in
        // WorldSnapshot changed yet, so without this explicit fact PublishStepObservationDelta sees
        // no typed invalidation and the typed loop stops before Phase A ever gets a chance to build.
        public bool EconomyDeliveryReady;

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
        // A real hero-led mover was created from a garrison candidate.
        public bool ActorMaterialized;
        // A direct Economy actor's roster and/or donor intent was prepared.
        public bool EconomyPrepared;
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
                // 2026-09-14 review round 6 (P2) — a successful hero extraction IS a genuine
                // successful action, not merely a state change; it used to report
                // StateChanged=true/Succeeded=false, which telemetry/WasGenuineExecution read as a
                // contradiction.
                bool succeeded = ReachedGoal || moved || InfrastructureChanged || CombatChanged
                    || ActorMaterialized || EconomyPrepared;
                bool changed = moved || EnteredStealth || StealthChanged
                    || InfrastructureChanged || CombatChanged || ActorMaterialized || EconomyPrepared;
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
        // 2026-09-14 review round 7 (P1 — two execution lifecycles) — three helpers factor out
        // logic Execute() and ExecuteStep() each independently re-implemented: the stale-plan
        // short-circuit, the mover-resolve-failed short-circuit, and the MissionRevalidator
        // stale-goal short-circuit. Each is parameterized to reproduce EXACTLY the per-caller
        // behavior the two loops already had (Execute's batch model never signals NeedsReplan on a
        // stale mover/goal and logs a "mover gone"/revalidation line ExecuteStep's incremental model
        // doesn't; ExecuteStep does the opposite) — unifying an unexplained observable difference
        // silently would risk changing AI behavior for whichever caller didn't have it, so those
        // differences are preserved as explicit parameters rather than erased.
        //
        // Scout dispatch itself (ReconGroundExecutor.Run vs RunStep) stays UNMERGED on purpose —
        // see docs/ai-economy-mover-materialization-decision-tree.md, "Review round 6", for why:
        // Execute's multi-step lookahead and ExecuteStep's single-atomic-step contract are two
        // different behaviors, not two copies of the same one.
        private static bool TryHandleStalePlan(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result,
            List<ExecutionResult> results, bool enforceFreshPlan, bool logStalePlan)
        {
            if (!enforceFreshPlan || pm.PlannedAtStateVersion < 0
                || V2StateVersion.IsCurrent(pm.PlannedAtStateVersion))
                return false;

            result.StartHex = pm.ExecutionHex;
            result.FinalHex = pm.ExecutionHex;
            result.StopReason = ExecutionStopReason.TargetInvalidated;
            result.NeedsReplan = true;
            result.ApSpent = 0f;
            StrategicInterruptRegistry.Mark(player, ctx.TurnNumber,
                StrategicInvalidationReason.External, actorIds: new[] { pm.MoverArmyId });
            CompleteResult(result, root);
            results.Add(result);
            ReleaseEconomyReservation(player, ctx, pm);
            if (logStalePlan)
                AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm.Mission)}] {pm.Key} — stale plan "
                    + $"planned@v{pm.PlannedAtStateVersion}, current=v{V2StateVersion.Current}; no command issued");
            return true;
        }

        // Resolves the mover; on success stamps result.StartHex/FinalHex and returns the army via
        // `army` (caller proceeds). On failure fully populates+adds `result` and returns null.
        private static bool TryResolveMoverOrHandleGone(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result,
            List<ExecutionResult> results, int apBefore, bool setNeedsReplan, bool logMoverGone,
            string retireReason, out ArmyData army)
        {
            army = Resolve(player, pm.MoverArmyId);
            if (army != null)
            {
                result.StartHex = army.Hex;
                result.FinalHex = army.Hex;
                return false;
            }

            result.StartHex = pm.ExecutionHex;
            result.FinalHex = pm.ExecutionHex;
            result.StopReason = ExecutionStopReason.MoverLost;
            result.ApSpent = 0f;
            if (setNeedsReplan)
                result.NeedsReplan = true;
            ApCheck(pm, apBefore, root, result);
            CompleteResult(result, root);
            results.Add(result);
            ReleaseEconomyReservation(player, ctx, pm);
            ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId, retireReason);
            if (logMoverGone)
                AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm.Mission)}] {pm.Key} — mover #{pm.MoverArmyId} gone before first step");
            return true;
        }

        private static bool TryHandleStaleValidity(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ArmyData army, ExecutionResult result,
            List<ExecutionResult> results, int apBefore, bool setNeedsReplan, bool logRevalidation,
            string retireReason)
        {
            MissionValidity validity = MissionRevalidator.Validate(player, root, ctx, pm);
            if (!MissionRevalidator.IsStale(validity))
                return false;

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
            if (setNeedsReplan)
                result.NeedsReplan = validity != MissionValidity.StaleGoalMet;
            ApCheck(pm, apBefore, root, result);
            CompleteResult(result, root);
            results.Add(result);
            ReleaseEconomyReservation(player, ctx, pm);
            if (validity == MissionValidity.StaleMoverLost)
                ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId, retireReason);
            if (logRevalidation)
                AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm.Mission)}] {pm.Key} — revalidation: {validity}; "
                    + "no movement, 0 AP");
            return true;
        }

        // 2026-09-14 review round 9 (P1 — two execution lifecycles, final) — the ONE per-mission
        // step lifecycle: stale-plan check, deferred-Economy materialization, mover resolve, stale-
        // goal revalidation, kind dispatch, AP/version/resource stamping, result recording,
        // reservation release. Both Execute() (batch adapter, `singleStepOnly: false`) and
        // ExecuteStep() (`singleStepOnly: true`) call this and NOTHING ELSE per mission — neither
        // re-implements any of it. `singleStepOnly` is an execution-STRATEGY switch, not a second
        // lifecycle: it picks which of Recon/Raid's own two execution modes applies (continuous
        // multi-step lookahead — Run/RunRaid — vs one atomic step — RunStep/RunRaidStep, the
        // pre-existing difference those subsystems already expose), and reproduces the two small,
        // pre-existing observable differences between the old duplicated bodies (Execute's batch
        // model never sets NeedsReplan on a lost/stale mover or an unsupported kind, and logs where
        // ExecuteStep's incremental model doesn't; ExecuteStep does the reverse) — preserved
        // explicitly rather than silently unified, since neither was ever explained as a bug.
        private static IEnumerator ExecuteMissionCore(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result,
            List<ExecutionResult> results, bool enforceFreshPlan, WorldSnapshot snapshot,
            List<ProvisionedMission> queue, int missionIndex, bool singleStepOnly)
        {
            if (TryHandleStalePlan(player, root, ctx, pm, result, results,
                    enforceFreshPlan, logStalePlan: !singleStepOnly))
                yield break;

            int apBefore = root != null ? root.ActionPoints : 0;
            // 2026-09-14 review round 5 (P1) — a deferred Economy garrison-extraction mission
            // carries a SYNTHETIC negative MoverArmyId (ProvisioningManager.
            // SyntheticGarrisonExtractionActorId) — the same "actor does not exist yet" pattern
            // ScoutExecutorKind.AirLaunch already uses. Materialization happens HERE, first, before
            // Resolve/MissionRevalidator/anything else below ever sees the synthetic id, and is ALSO
            // a terminal step of its own — see the helper's own comment for why this never falls
            // through to movement in the same call.
            if (TryHandleDeferredEconomyMaterialization(player, root, ctx, pm, result, apBefore))
            {
                ApCheck(pm, apBefore, root, result);
                StampVersion(result);
                CompleteResult(result, root);
                results.Add(result);
                if (result.StopReason != ExecutionStopReason.StepCompleted)
                    ReleaseEconomyReservation(player, ctx, pm);
                yield break;
            }
            if (TryResolveMoverOrHandleGone(player, root, ctx, pm, result, results, apBefore,
                    setNeedsReplan: singleStepOnly, logMoverGone: !singleStepOnly,
                    singleStepOnly ? "mover gone before atomic execution" : "mover gone before execution",
                    out ArmyData army))
                yield break;

            // ARCH-02 §35 — the executor does NOT synthesise a replacement mission for a
            // stale-goal Scout. It records the stale outcome; MissionContinuityLayer.Reconcile
            // + the mission planner re-target the durable ReconPatrolState on the next pass.
            if (TryHandleStaleValidity(player, root, ctx, pm, army, result, results, apBefore,
                    setNeedsReplan: singleStepOnly, logRevalidation: !singleStepOnly,
                    retireReason: singleStepOnly
                        ? "atomic mission revalidation lost mover" : "mission revalidation lost mover"))
                yield break;

            if (pm.Kind == MissionKind.Scout)
            {
                if (singleStepOnly)
                {
                    var soloQueue = new List<ProvisionedMission> { pm };
                    var control = new ReconGroundExecutor.StepControl();
                    yield return ReconGroundExecutor.RunStep(player, root, ctx, pm, result, apBefore,
                        soloQueue, 0, snapshot, control);
                }
                else
                {
                    yield return ReconGroundExecutor.Run(player, root, ctx, pm, result, apBefore,
                        queue, missionIndex, snapshot);
                }
                ApCheck(pm, apBefore, root, result);
                StampVersion(result);
                CompleteResult(result, root);
                results.Add(result);
                yield break;
            }

            if (pm.Kind == MissionKind.Raid)
            {
                if (singleStepOnly)
                    yield return RunRaidStep(player, root, ctx, pm, result, apBefore);
                else
                    yield return RunRaid(player, root, ctx, pm, result, apBefore);
                ApCheck(pm, apBefore, root, result);
                StampVersion(result);
                CompleteResult(result, root);
                results.Add(result);
                yield break;
            }

            if (pm.Kind == MissionKind.Economy)
            {
                yield return RunEconomyStep(player, root, ctx, pm, result, apBefore);
                ApCheck(pm, apBefore, root, result);
                StampVersion(result);
                CompleteResult(result, root);
                results.Add(result);
                if (result.StopReason != ExecutionStopReason.StepCompleted)
                    ReleaseEconomyReservation(player, ctx, pm);
                yield break;
            }

            // Future mission kinds must opt into an executor explicitly. Never silently treat
            // an unknown mission as Scout or let it mutate the world through a fallback path.
            result.StopReason = ExecutionStopReason.TargetInvalidated;
            result.NeedsReplan = singleStepOnly;
            result.ApSpent = 0f;
            ApCheck(pm, apBefore, root, result);
            CompleteResult(result, root);
            results.Add(result);
            ReleaseEconomyReservation(player, ctx, pm);
            if (!singleStepOnly)
                AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm.Mission)}] {pm.Key} — unsupported mission kind {pm.Kind}");
        }

        // `snapshot` is passed through to the per-mission executors. ARCH-02 §35 — the terminal
        // air-recon pass is NO LONGER run here: the orchestrator plans it (AirReconPlanner) and
        // runs it (ReconAirExecutor.Execute) as its own stage after this returns. A thin adapter
        // over ExecuteMissionCore (see that method's own comment) — this loop owns only queue
        // iteration and the ReconAcceptanceAudit summary bookkeeping, nothing about a mission's own
        // lifecycle.
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
                yield return ExecuteMissionCore(player, root, ctx, pm, result, results,
                    enforceFreshPlan, snapshot, queue, missionIndex, singleStepOnly: false);
            }

            // TaskExecutor owns the batch lifecycle, so the summary is still written when the last
            // provisioned Scout becomes stale, loses its mover, or otherwise never enters the Ground
            // executor. Individual Ground hooks may summarize earlier; the collector is idempotent
            // and automatically reopens the summary if later evidence changes a status.
            if (AiStrategyV2Scope.IsFocusScoped)
                ReconAcceptanceAudit.Summarize(player, ctx.TurnNumber);
        }

        // Mid-turn orchestration door for exactly one already-provisioned Ground/Raid task step.
        // Selection, allocation and provisioning stay outside this execution owner; validation,
        // command dispatch, AP invariants and version/resource stamping stay inside
        // ExecuteMissionCore, called here with a singleton queue and singleStepOnly: true.
        internal static IEnumerator ExecuteStep(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm, List<ExecutionResult> results, WorldSnapshot snapshot = null,
            bool enforceFreshPlan = true)
        {
            if (ctx?.Map == null || pm == null || results == null)
                yield break;

            var result = new ExecutionResult
            {
                Key = pm.Key,
                Source = pm,
                PlannedAtStateVersion = pm.PlannedAtStateVersion,
                StateVersionBefore = V2StateVersion.Current,
                ResourcesBefore = AiV2Trace.Stamp(root),
            };
            var soloQueue = new List<ProvisionedMission> { pm };
            yield return ExecuteMissionCore(player, root, ctx, pm, result, results,
                enforceFreshPlan, snapshot, soloQueue, 0, singleStepOnly: true);
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

            // AGG-RAID §10 — three atomic legs. Execution never picks a different target, a
            // different base, re-scores anything, or creates a replacement mission: it carries out
            // exactly the plan Provisioning pinned onto `pm`.
            if (pm.RaidPhase == RaidMissionPhase.Return)
            {
                yield return RunRaidReturnStep(player, root, ctx, pm, result, army);
                yield break;
            }
            if (pm.RaidPhase == RaidMissionPhase.Reinforcement)
            {
                yield return RunRaidReinforcementStep(player, root, ctx, pm, result, army);
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

            // AGG-RAID P0#2 — defensive re-check only; RaidObjectiveEvaluator.IsNeutralRaidTarget is
            // the ONE canonical neutrality decision, already applied by Provisioning before this
            // step was ever scheduled. A target that flips to a non-neutral owner between
            // provisioning and this execution step (e.g. another AI player claimed it mid-turn)
            // must not be attacked — Raid targets neutrals only.
            if (!RaidObjectiveEvaluator.IsNeutralRaidTarget(target.Value.Owner))
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = true;
                yield break;
            }

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

        // =====================================================================================
        //  AGG-RAID §10 — RETURN leg: at most ONE step of the primary toward the already-chosen
        //  base. The base is never re-selected here.
        // =====================================================================================
        private static IEnumerator RunRaidReturnStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, ArmyData army)
        {
            HexCoord home = pm.RaidDestinationHex;
            pm.ExecutionHex = home;
            if (army.Hex.Equals(home))
            {
                result.ReachedGoal = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
                yield break;
            }
            if (army.CurrentMovement <= 0)
            {
                result.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }
            HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, home);
            if (!next.HasValue)
            {
                result.StopReason = ExecutionStopReason.NoSafeStep;
                result.NeedsReplan = true;
                yield break;
            }

            HexCoord before = army.Hex;
            var decision = AiDecision.Move(army, next.Value,
                $"V2 raid — return to base ({home.Q},{home.R})", 0f);
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

            army = Resolve(player, pm.MoverArmyId);
            HexCoord endHex = army != null ? army.Hex : trace.EndHex;
            bool moved = !endHex.Equals(before);
            if (moved) result.StepsMoved++;
            result.FinalHex = endHex;
            result.RaidOperationStarted |= moved;
            if (moved) result.ActualActorArmyId = pm.MoverArmyId;

            if (trace.BattleOccurred) { result.StopReason = ExecutionStopReason.BattleStarted; yield break; }
            if (trace.HexEventOccurred) { result.StopReason = ExecutionStopReason.HexEventStarted; yield break; }
            if (army == null)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                yield break;
            }
            if (!moved) { result.StopReason = ExecutionStopReason.MoveRejected; yield break; }
            if (army.Hex.Equals(home))
            {
                result.ReachedGoal = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
                yield break;
            }
            result.StopReason = army.CurrentMovement > 0
                ? ExecutionStopReason.StepCompleted
                : ExecutionStopReason.OutOfMovement;
        }

        // =====================================================================================
        //  AGG-RAID §10 — REINFORCEMENT leg. Either exactly ONE transit step of the SUPPORT army,
        //  or (once it stands on the primary's hex) exactly ONE atomic transfer/swap transaction
        //  with NO movement in the same step. The primary never moves in this leg.
        // =====================================================================================
        private static IEnumerator RunRaidReinforcementStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, ArmyData support)
        {
            ArmyData primary = Resolve(player, pm.RaidPrimaryArmyId);
            if (primary == null || primary.Owner != player || primary.Members.Count == 0)
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = true;
                yield break;
            }

            HexCoord rendezvous = primary.Hex;
            pm.ExecutionHex = rendezvous;

            if (!support.Hex.Equals(rendezvous))
            {
                if (support.CurrentMovement <= 0)
                {
                    result.StopReason = ExecutionStopReason.OutOfMovement;
                    yield break;
                }
                HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, support, rendezvous);
                if (!next.HasValue)
                {
                    result.StopReason = ExecutionStopReason.NoSafeStep;
                    result.NeedsReplan = true;
                    yield break;
                }
                HexCoord before = support.Hex;
                var decision = AiDecision.Move(support, next.Value,
                    $"V2 raid — reinforcement convoy to primary #{primary.Id} at ({rendezvous.Q},{rendezvous.R})", 0f);
                var trace = new AiMoveExecutionTrace();
                yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

                support = Resolve(player, pm.MoverArmyId);
                HexCoord endHex = support != null ? support.Hex : trace.EndHex;
                bool moved = !endHex.Equals(before);
                if (moved) result.StepsMoved++;
                result.FinalHex = endHex;
                result.RaidOperationStarted |= moved;

                if (trace.BattleOccurred) { result.StopReason = ExecutionStopReason.BattleStarted; yield break; }
                if (trace.HexEventOccurred) { result.StopReason = ExecutionStopReason.HexEventStarted; yield break; }
                if (support == null)
                {
                    result.StopReason = ExecutionStopReason.MoverLost;
                    result.NeedsReplan = true;
                    yield break;
                }
                if (!moved) { result.StopReason = ExecutionStopReason.MoveRejected; yield break; }
                // A transfer is a SEPARATE step: never move and hand off in the same one.
                result.StopReason = ExecutionStopReason.StepCompleted;
                yield break;
            }

            // ---- the atomic handoff transaction ------------------------------------------
            result.RaidReinforcementHandoffAttempted = true;
            bool handoffOk = ApplyReinforcementHandoff(player, ctx, pm, support, primary,
                out int transferred, out string detail);
            AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm.Mission)}] {pm.Key} — raid "
                + $"reinforcement handoff support #{support.Id} -> primary #{primary.Id}: "
                + $"{(handoffOk ? "OK" : "REJECTED")} moved={transferred} {detail}");

            if (transferred > 0)
            {
                result.CombatChanged = true;
                // §10 — bump the version and publish an Actor|Capability invalidation so the next
                // bounded cycle re-checks this Raid's readiness in the SAME turn.
                V2StateVersion.Bump();
                StrategicInterruptRegistry.Mark(player, ctx.TurnNumber,
                    StrategicInvalidationReason.Actor | StrategicInvalidationReason.Capability,
                    actorIds: new[] { primary.Id, support.Id });
            }

            // §9/§10 — the roster is re-verified against the shared estimator, and ONLY a verified
            // roster returns the operation to Assault. Continuity owns that state transition.
            IReadOnlyList<WorthIt.DefenderProfile> defenders =
                AiMapMemoryDefenders(player, pm.RaidTargetArmyId);
            bool verified = GroundCombatFeasibility.Clears(
                primary.Members.Select(WorthIt.FromLiveUnit).ToList(), defenders,
                AiConfigV2.raidMinViableWinChance, out float win, out bool cover);
            MissionContinuityLayer.CompleteRaidReinforcement(player, primary.Id, verified,
                $"win={win.ToString("0.00", CultureInfo.InvariantCulture)} cover={(cover ? 1 : 0)} "
                + $"transferred={transferred}");

            // The operation advanced either because a transfer actually happened, OR because the
            // roster already verified without needing one (CompleteRaidReinforcement just flipped
            // ri.Phase back to Assault above in that case too) — basing this solely on handoffOk
            // under-reports a genuinely productive step (e.g. support had nothing to spare but the
            // primary already clears the target on its own) as an unproductive one.
            bool operationAdvanced = handoffOk || verified;
            result.ReachedGoal = operationAdvanced;
            // The RENDEZVOUS is satisfied, the RAID is not: the durable operation continues (back
            // into Assault once the roster re-cleared). DurableRoleContinues makes the ledger
            // classify this as a ProductiveStop instead of retiring the intent.
            result.DurableRoleContinues = operationAdvanced;
            result.StopReason = operationAdvanced
                ? ExecutionStopReason.ReachedGoal
                : ExecutionStopReason.MoveRejected;
        }

        // The concrete roster mutation: fill the primary's free slots first, then — if it is full —
        // swap its most critically wounded bodies for fresh ones. Executed through the SAME
        // authoritative ArmyActions primitives a human uses; the support container is never emptied.
        private static bool ApplyReinforcementHandoff(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm, ArmyData support, ArmyData primary,
            out int transferred, out string detail)
        {
            transferred = 0;
            detail = "";
            List<UnitData> sparable = RaidProvisioner.SparableSupportBodies(support);
            if (sparable.Count == 0)
            {
                detail = "support has no sparable body";
                return false;
            }

            int freeSlots = Mathf.Max(0,
                ArmyData.ComputeCapacity(primary.Members, primary.IsGarrison) - primary.Members.Count);
            if (freeSlots > 0)
            {
                List<UnitData> batch = sparable.Take(freeSlots).ToList();
                if (ArmyActions.TransferMembersAtomic(batch, support, primary, ctx.HexSelection,
                        out string why))
                {
                    transferred = batch.Count;
                    detail = $"transferred {batch.Count} into free slot(s)";
                    return true;
                }
                detail = $"atomic transfer rejected: {why}";
                return false;
            }

            // Primary is full — trade out its most critically wounded member for the best fresh
            // body the support can spare (a straight swap needs no free slot on either side).
            UnitData weakest = primary.Members
                .Where(u => u != null && !u.IsHero && !u.IsAviation)
                .OrderBy(u => u.HitPointsMax > 0 ? (float)u.HitPointsCurrent / u.HitPointsMax : 1f)
                .ThenBy(u => GroundCombatDonorPolicy.UnitCombatValue(u))
                .FirstOrDefault();
            if (weakest == null)
            {
                detail = "primary is full and has no swappable non-hero body";
                return false;
            }
            foreach (UnitData fresh in sparable)
            {
                if (GroundCombatDonorPolicy.UnitCombatValue(fresh)
                    <= GroundCombatDonorPolicy.UnitCombatValue(weakest))
                    continue;
                if (ArmyActions.SwapMembers(fresh, support, weakest, primary, ctx.HexSelection,
                        out string swapWhy))
                {
                    transferred = 1;
                    detail = $"swapped {weakest.Name} out for {fresh.Name}";
                    return true;
                }
                detail = $"swap rejected: {swapWhy}";
            }
            if (string.IsNullOrEmpty(detail))
                detail = "primary is full and no support body improves on its weakest member";
            return false;
        }

        private static IReadOnlyList<WorthIt.DefenderProfile> AiMapMemoryDefenders(
            PlayerSetupData player, int targetArmyId)
        {
            AiMapMemory.KnownEnemySighting? s = FindRaidSighting(player, targetArmyId);
            return s?.Defenders ?? (IReadOnlyList<WorthIt.DefenderProfile>)
                System.Array.Empty<WorthIt.DefenderProfile>();
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

        // 2026-09-14 review round 5 — the one real mutation site for a deferred Economy
        // garrison-extraction mission. Applies the EXACT GarrisonExtractionCandidate pinned onto
        // `pm` by Provisioning (pm.EconomyExtractionPlan — no re-resolve, see the plan field's own
        // comment on ProvisionedMission) for real (ProvisioningManager.ApplyGarrisonExtraction —
        // ArmyActions.CreateArmy/TransferMember, the one owner of this mutation, unchanged since
        // round 2), then runs the SAME FinishEconomyBuilder tail the direct-army path already uses,
        // now against the real live hero. `pm` is mutated in place — MoverArmyId flips from
        // synthetic to real — so every caller downstream of this one (ExecuteStep's own Resolve just
        // below, MissionContinuity, etc.) sees an ordinary, already-real mover from here on. Callers
        // must NOT fall through to movement in the same step afterwards (see ExecuteStep/Execute) —
        // this call alone is already CreateArmy + TransferMember + lightening/reinforcement, one
        // canonical batch; a move is a separate step.
        // 2026-09-14 review round 5 (P0 #2, P1 #6) — shared by both execution doors (the batch
        // Execute() and the incremental ExecuteStep()) so a deferred Economy garrison-extraction
        // mission is handled identically no matter which one runs it. Fully populates `result` and
        // returns true when there was something deferred to handle at all (materialization attempted
        // — success or failure, the caller must add `result` and stop this mission for this call);
        // returns false when nothing was deferred, so the caller proceeds with its normal
        // Resolve/mission-kind dispatch untouched.
        //
        // On success this is DELIBERATELY a terminal step of its own — it does not fall through to
        // movement in the same call. MaterializeEconomyGarrisonBuilder alone is already
        // CreateArmy + TransferMember + lightening/reinforcement (one canonical batch, mirroring the
        // granularity FinishEconomyBuilder already used for the pre-existing direct-army path);
        // bundling a MoveArmyRoutine on top of that in the same step would violate the one-mutation-
        // per-step contract. `pm.MoverArmyId` is flipped from synthetic to real in place, so the
        // NEXT admission pass finds this mission an ordinary, already-real direct-army mover — ready
        // to move on its own step, exactly like any in-progress Economy mission already is.
        private static bool TryHandleDeferredEconomyMaterialization(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result,
            int apBefore)
        {
            // 2026-09-14 review round 10 (P0) — this door now also covers a DIRECT-army Economy
            // mission (hero already real) whose composition change and/or donor-loan suspend
            // Provisioning left pinned but unapplied (pm.EconomyPreparationPending), not only a
            // garrison-extraction candidate — see ApplyEconomyPreparation's own comment.
            if (pm.Kind != MissionKind.Economy
                || (pm.EconomyExtractionGarrisonArmyId < 0 && !pm.EconomyPreparationPending))
                return false;

            bool materialized = ApplyEconomyPreparation(player, root, ctx, pm, result, apBefore);
            result.StartHex = pm.ExecutionHex;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            if (!materialized)
            {
                result.FinalHex = pm.ExecutionHex;
                if (!result.ActualActorArmyId.HasValue)
                    result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                return true;
            }

            result.FinalHex = Resolve(player, pm.MoverArmyId)?.Hex ?? pm.ExecutionHex;
            result.StopReason = ExecutionStopReason.StepCompleted;
            result.NeedsReplan = false;
            return true;
        }

        // 2026-09-14 review round 10 (P0) — this is now the SOLE apply site for
        // pm.EconomyExtractionPreparation, for BOTH shapes of pending Economy work: a
        // garrison-extraction candidate (hero not yet real — EconomyExtractionGarrisonArmyId >= 0)
        // AND a direct-army candidate whose hero was ALREADY real but whose composition change/
        // donor-loan suspend Provisioning still left pinned rather than applying itself (round 10 —
        // the direct-army path used to apply this synchronously inside Provisioning via the now-
        // deleted FinishEconomyBuilder; no Economy actor is ever mutated inside Provisioning any
        // more, extracted or not). Execution never re-plans here — Provisioning already computed and
        // pinned the FULL decision (ProvisioningManager.PlanEconomyCompletion, run against a
        // read-only preview for the extraction case, the real live hero for the direct case) — this
        // function only APPLIES the pinned hero extraction (if any) and cheaply re-validates the two
        // facts that can actually have shifted since Provisioning within the same batch pass (AP,
        // resource spendability — never composition/donor/route, which are not re-derived).
        private static bool ApplyEconomyPreparation(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            ProvisioningManager.EconomyCompletionPlan prep = pm.EconomyExtractionPreparation;
            if (!prep.Feasible)
                return false;   // defensive only — Provisioning only ever defers a feasible plan

            ArmyData materialized;
            bool extractionNeeded = pm.EconomyExtractionGarrisonArmyId >= 0;
            if (extractionNeeded)
            {
                ArmyData garrison = Resolve(player, pm.EconomyExtractionGarrisonArmyId);
                if (garrison == null)
                    return false;
                // 2026-09-14 review round 5 — materialize the EXACT plan Provisioning already chose
                // and funded (pm.EconomyExtractionPlan), never a fresh re-resolve: a re-resolve with
                // commitments:null/session:null runs under weaker constraints than the original
                // choice and can legally pick a different — or already-claimed — hero/container/tier.
                ProvisioningManager.GarrisonExtractionCandidate plan = pm.EconomyExtractionPlan;
                if (plan.Tier == ProvisioningManager.GarrisonExtractionTier.None)
                    return false;
                materialized = ProvisioningManager.ApplyGarrisonExtraction(
                    player, garrison, plan, ctx);
                if (materialized == null)
                    return false;

                // Extraction is one complete task step; preparation and movement are re-admitted.
                pm.MoverArmyId = materialized.Id;
                pm.EconomyExtractionGarrisonArmyId = -1;
                pm.EconomyPreparationPending = prep.Donor != null
                    || prep.Unload.Count > 0 || prep.Reinforcement.Count > 0;
                pm.ClaimedAp = prep.RealAp;
                result.ActualActorArmyId = materialized.Id;
                result.ActorMaterialized = true;
                AiDebugLog.Write($"[AI][V2][Economy] materialized builder #{materialized.Id} "
                    + $"for {pm.Key}; preparation deferred to next admission");
                return true;
            }
            else
            {
                // Direct-army case — the hero is already a real, live field army; nothing to extract.
                materialized = Resolve(player, pm.MoverArmyId);
                if (materialized == null)
                    return false;
            }

            float eps = AiConfigV2.allocatorSliceEpsilon;
            // 2026-09-14 review round 5/8 — the envelope is what Provisioning actually funded THIS
            // mission (pm.ClaimedAp, now the AUTHORITATIVE PlanEconomyCompletion figure, not a coarse
            // pre-composition estimate), minus whatever the hero extraction itself just spent — never
            // the player's entire current AP pool.
            float spentSoFar = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            float remainingEnvelope = Mathf.Max(0f, pm.ClaimedAp - spentSoFar);
            if (prep.RealAp > remainingEnvelope + eps
                || !root.CanSpendActionPoints(Mathf.CeilToInt(prep.RealAp)))
            {
                AiDebugLog.Write($"[AI][V2][Economy] materialization prep stale for {pm.Key}: AP no "
                    + "longer available — hero stays a real field mover, found again next admission pass");
                result.ActualActorArmyId = materialized.Id;
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                return false;
            }
            if (!StrategicSpendability.FitsSpendableResources(player, root, ctx, prep.StageCost, prep.OwnerKey))
            {
                AiDebugLog.Write($"[AI][V2][Economy] materialization prep stale for {pm.Key}: resources "
                    + "no longer spendable — hero stays a real field mover, found again next admission pass");
                result.ActualActorArmyId = materialized.Id;
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                return false;
            }

            int preparedMembers = ProvisioningManager.ApplyEconomyArmyLightening(
                materialized, prep.Garrison, prep.Unload, prep.Reinforcement, ctx);
            int plannedTransfers = prep.Unload.Count + prep.Reinforcement.Count;
            if (preparedMembers != plannedTransfers)
            {
                AiDebugLog.Write($"[AI][V2][Economy] materialization prep failed for {pm.Key}: "
                    + "composition transaction did not commit"
                    + " — hero stays a real field mover, found again next admission pass");
                result.ActualActorArmyId = materialized.Id;
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                return false;
            }
            // Immediate join AP is already spent; validate the live unspent remainder.
            float realAp = ProvisioningManager.EconomyMissionClaimedAp(materialized,
                pm.EconomyTarget.BuildApCost, pm.EconomyTarget.MinimumFollowupAp, null,
                added: null, unloadTarget: null,
                travelNeeded: prep.TravelNeeded,
                completionThisTurn: prep.CompletionThisTurn);
            float spentAfterComposition = Mathf.Max(0f,
                apBefore - (root != null ? root.ActionPoints : apBefore));
            float envelopeAfterComposition = Mathf.Max(0f, pm.ClaimedAp - spentAfterComposition);
            if (realAp > envelopeAfterComposition + eps)
            {
                AiDebugLog.Write($"[AI][V2][Economy] materialization prep stale for {pm.Key}: could "
                    + "not lighten within the funded AP envelope — hero stays a real field mover, "
                    + "found again next admission pass");
                result.ActualActorArmyId = materialized.Id;
                result.EconomyPrepared = true;
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                return false;
            }
            // 2026-09-14 review round 10 (P1) — no InfrastructureFulfillment.ReserveEconomyCost call
            // here any more: Provisioning already reserved it (see the deferred branch of
            // ProvisionEconomy) the moment this mission committed to being deferred, so the resource
            // pool is honestly reduced for any OTHER Economy mission provisioned later in the same
            // batch pass. Reserving again here would double-charge the ledger for one build.
            if (prep.Donor != null)
            {
                prep.Donor.Status = IntentStatus.Suspended;
                prep.Donor.Suspended = SuspendReason.EconomyLoan;
                AiDebugLog.Write($"[AI][V2][Economy][Loan] borrow actor=#{materialized.Id} "
                    + $"from={prep.Donor.IntentKey} to={pm.Key}");
            }

            pm.MoverArmyId = materialized.Id;
            pm.EconomyExtractionGarrisonArmyId = -1;
            pm.EconomyPreparationPending = false;
            pm.ReservationOwner = prep.OwnerKey;
            pm.EconomyLoanSource = prep.Donor?.IntentKey;
            pm.ClaimedAp = realAp;
            pm.ClaimedPhysical = ProvisioningManager.CostVector(prep.StageCost);
            result.ActualActorArmyId = materialized.Id;
            result.EconomyPrepared = true;
            AiDebugLog.Write($"[AI][V2][Economy] prepared builder #{materialized.Id} for {pm.Key} "
                + (extractionNeeded ? "(garrison extraction)" : "(composition/loan only)"));
            return true;
        }

        private static IEnumerator RunEconomyStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            ArmyData army = Resolve(player, pm.MoverArmyId);
            if (army == null || army.Owner != player)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                yield break;
            }
            // 2026-09-14 review round 4 — was left null for every Ground Economy mission (only
            // Air/Raid stamped it). WorldAnalysis.Observation.PublishStepObservationDelta's
            // EconomyDeliveryReady branch passes this straight through as the Actor-invalidation id
            // — without it, that mark carried no actor at all, wasting half its own signal.
            result.ActualActorArmyId = army.Id;
            EconomyMissionTarget target = pm.EconomyTarget;
            if (army.Hex.Equals(target.TargetHex))
            {
                result.ReachedGoal = target.Kind == EconomyTaskKind.ReturnBuilder;
                result.StopReason = result.ReachedGoal
                    ? ExecutionStopReason.ReachedGoal
                    : ExecutionStopReason.StepCompleted;
                result.NeedsReplan = false;
                result.FinalHex = army.Hex;
                // 2026-09-14 review round 4 — was hardcoded 0f, which was honest before this step
                // could ever spend AP without moving. A deferred mission that materializes directly
                // onto its target hex (garrison.Hex == target.TargetHex — e.g. an in-place base)
                // reaches this branch having already spent real AP (CreateArmy / an activation
                // charge / lightening) with zero StepsMoved; ApCheck's live-pool-delta invariant
                // (spec §2.1) would otherwise flag every one of those turns as a reporting mismatch.
                result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                if (result.ReachedGoal)
                    AiDebugLog.Write($"[AI][V2][Economy][Recovery] builder #{army.Id} protected at "
                        + $"({army.Hex.Q},{army.Hex.R})");
                else
                {
                    result.EconomyDeliveryReady = true;
                    AiDebugLog.Write($"[AI][V2][Economy] delivery ready {pm.Key}; request Phase-A build follow-up");
                }
                yield break;
            }
            if (army.CurrentMovement <= 0)
            {
                result.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }
            HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, target.TargetHex);
            if (!next.HasValue)
            {
                result.StopReason = ExecutionStopReason.NoSafeStep;
                result.NeedsReplan = true;
                yield break;
            }
            HexCoord before = army.Hex;
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player,
                AiDecision.Move(army, next.Value,
                    $"V2 economy — {target.Kind} at ({target.TargetHex.Q},{target.TargetHex.R})", 0f),
                ctx, trace);
            army = Resolve(player, pm.MoverArmyId);
            HexCoord after = army != null ? army.Hex : trace.EndHex;
            result.FinalHex = after;
            if (!after.Equals(before)) result.StepsMoved = 1;
            bool recoveryArrived = target.Kind == EconomyTaskKind.ReturnBuilder
                && after.Equals(target.TargetHex);
            result.ReachedGoal = recoveryArrived;
            result.StopReason = recoveryArrived ? ExecutionStopReason.ReachedGoal
                : result.StepsMoved > 0 ? ExecutionStopReason.StepCompleted
                : ExecutionStopReason.MoveRejected;
            result.NeedsReplan = result.StepsMoved == 0 && !recoveryArrived;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            AiDebugLog.Write($"[AI][V2][Economy] move {pm.Key} ({before.Q},{before.R})->({after.Q},{after.R})");
        }

        private static void ReleaseEconomyReservation(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm)
        {
            if (pm?.Kind == MissionKind.Economy && ctx != null)
                StrategicResourceReservationLedger.ReleaseByOwner(player, ctx.TurnNumber,
                    pm.ReservationOwner);
        }

        private static void FinishRaid(PlayerSetupData player, PlayerRoot root,
            ProvisionedMission pm, ExecutionResult result, int apBefore,
            ExecutionStopReason stop)
        {
            result.FinalHex = Resolve(player, pm?.MoverArmyId ?? -1)?.Hex ?? result.FinalHex;
            result.StopReason = stop;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm?.Mission)}] {pm?.Key} — raid "
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
            // 2026-09-14 review round 5 (P0 #3) — ActorMaterialized (a garrison-extraction
            // CreateArmy/TransferMember) is a real world mutation with no movement/stealth/
            // infrastructure/combat signal of its own to piggyback a bump on; it used to leave
            // V2StateVersion stale despite Outcome.StateChanged already reporting true for it.
            if (result.StepsMoved > 0 || result.EnteredStealth || result.StealthChanged
                || result.InfrastructureChanged || result.CombatChanged || result.ActorMaterialized
                || result.EconomyPrepared)
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
