using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        // 2026-09-14 review round 4 — set by RunEconomyStep's MaterializeEconomyGarrisonBuilder when
        // this step pulled a hero out of its garrison (ArmyActions.CreateArmy/TransferMember, or a
        // lightening/reinforcement roster change) — a real mutation with no movement/stealth/
        // infrastructure signal of its own to piggyback on, but real all the same. See
        // Docs/ai-economy-mover-materialization-decision-tree.md, review round 4.
        public bool ActorMaterialized;
        // 2026-09-14 review round 6 (P1) — split from ActorMaterialized: a garrison-extraction
        // CreateArmy that ran but whose immediately-following hero TransferMember failed leaves a
        // real, kept, but HERO-LESS empty army. That is a genuine world mutation (StateChanged must
        // see it) but it is NOT an Economy actor — ActorMaterialized/ActualActorArmyId must stay
        // reserved for "a real hero-led mover now exists", or Continuity would track an empty shell
        // as this mission's mover.
        public bool ContainerCreated;
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
                // contradiction. ContainerCreated alone (orphan shell, no hero) stays a state change
                // only — it is not, by itself, this mission succeeding at anything.
                bool succeeded = ReachedGoal || moved || InfrastructureChanged || CombatChanged
                    || ActorMaterialized;
                bool changed = moved || EnteredStealth || StealthChanged
                    || InfrastructureChanged || CombatChanged || ActorMaterialized || ContainerCreated;
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
                    ReleaseEconomyReservation(player, ctx, pm);
                    AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm.Mission)}] {pm.Key} — stale plan "
                        + $"planned@v{pm.PlannedAtStateVersion}, current=v{V2StateVersion.Current}; no command issued");
                    continue;
                }

                int apBefore = root != null ? root.ActionPoints : 0;
                // 2026-09-14 review round 5 (P1) — this batch loop used to fall straight into
                // Resolve(pm.MoverArmyId) below with a deferred Economy mission's SYNTHETIC negative
                // id and misreport MoverLost every time; ExecuteStep already had the real handling.
                // Same door both loops must use — see the helper's own comment.
                if (TryHandleDeferredEconomyMaterialization(player, root, ctx, pm, result, apBefore, snapshot))
                {
                    ApCheck(pm, apBefore, root, result);
                    StampVersion(result);
                    CompleteResult(result, root);
                    results.Add(result);
                    if (result.StopReason != ExecutionStopReason.StepCompleted)
                        ReleaseEconomyReservation(player, ctx, pm);
                    continue;
                }
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
                    ReleaseEconomyReservation(player, ctx, pm);
                    ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId, "mover gone before execution");
                    AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm.Mission)}] {pm.Key} — mover #{pm.MoverArmyId} gone before first step");
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
                    ReleaseEconomyReservation(player, ctx, pm);
                    if (validity == MissionValidity.StaleMoverLost)
                        ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId, "mission revalidation lost mover");
                    AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm.Mission)}] {pm.Key} — revalidation: {validity}; "
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

                if (pm.Kind == MissionKind.Economy)
                {
                    yield return RunEconomyStep(player, root, ctx, pm, result, apBefore);
                    ApCheck(pm, apBefore, root, result);
                    StampVersion(result);
                    CompleteResult(result, root);
                    results.Add(result);
                    if (result.StopReason != ExecutionStopReason.StepCompleted)
                        ReleaseEconomyReservation(player, ctx, pm);
                    continue;
                }

                // Future mission kinds must opt into an executor explicitly. Never silently treat
                // an unknown mission as Scout or let it mutate the world through a fallback path.
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.ApSpent = 0f;
                ApCheck(pm, apBefore, root, result);
                CompleteResult(result, root);
                results.Add(result);
                ReleaseEconomyReservation(player, ctx, pm);
                AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm.Mission)}] {pm.Key} — unsupported mission kind {pm.Kind}");
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
        // command dispatch, AP invariants and version/resource stamping stay inside it.
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
                if (result.StopReason != ExecutionStopReason.StepCompleted)
                    ReleaseEconomyReservation(player, ctx, pm);
                yield break;
            }

            int apBefore = root != null ? root.ActionPoints : 0;
            // 2026-09-14 review round 5 — a deferred Economy garrison-extraction mission carries a
            // SYNTHETIC negative MoverArmyId (ProvisioningManager.SyntheticGarrisonExtractionActorId)
            // — the same "actor does not exist yet" pattern ScoutExecutorKind.AirLaunch already uses
            // (there, ReconAirExecutor materializes it; the orchestrator routes those missions to it
            // instead of here). Economy stays on this one path, so materialization happens HERE,
            // first, before Resolve/MissionRevalidator/anything else below ever sees the synthetic
            // id. It is ALSO a terminal step in its own right — see the helper's own comment for why
            // this never falls through to movement in the same call.
            if (TryHandleDeferredEconomyMaterialization(player, root, ctx, pm, result, apBefore, snapshot))
            {
                ApCheck(pm, apBefore, root, result);
                StampVersion(result);
                CompleteResult(result, root);
                results.Add(result);
                if (result.StopReason != ExecutionStopReason.StepCompleted)
                    ReleaseEconomyReservation(player, ctx, pm);
                yield break;
            }
            ArmyData army = Resolve(player, pm.MoverArmyId);
            if (army == null)
            {
                result.StartHex = pm.ExecutionHex;
                result.FinalHex = pm.ExecutionHex;
                result.StopReason = ExecutionStopReason.MoverLost;
                result.ApSpent = 0f;
                result.NeedsReplan = true;
                ApCheck(pm, apBefore, root, result);
                CompleteResult(result, root);
                results.Add(result);
                ReleaseEconomyReservation(player, ctx, pm);
                ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId,
                    "mover gone before atomic execution");
                yield break;
            }

            result.StartHex = army.Hex;
            result.FinalHex = army.Hex;
            MissionValidity validity = MissionRevalidator.Validate(player, root, ctx, pm);
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
                result.StopReason = validity == MissionValidity.StaleMoverLost
                    ? ExecutionStopReason.MoverLost
                    : validity == MissionValidity.StaleGoalMet
                        ? ExecutionStopReason.ReachedGoal
                        : ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = validity != MissionValidity.StaleGoalMet;
                ApCheck(pm, apBefore, root, result);
                CompleteResult(result, root);
                results.Add(result);
                ReleaseEconomyReservation(player, ctx, pm);
                if (validity == MissionValidity.StaleMoverLost)
                    ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId,
                        "atomic mission revalidation lost mover");
                yield break;
            }

            if (pm.Kind == MissionKind.Scout)
            {
                var queue = new List<ProvisionedMission> { pm };
                var control = new ReconGroundExecutor.StepControl();
                yield return ReconGroundExecutor.RunStep(player, root, ctx, pm, result, apBefore,
                    queue, 0, snapshot, control);
                ApCheck(pm, apBefore, root, result);
                StampVersion(result);
                CompleteResult(result, root);
                results.Add(result);
                yield break;
            }

            if (pm.Kind == MissionKind.Raid)
            {
                yield return RunRaidStep(player, root, ctx, pm, result, apBefore);
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

            result.StopReason = ExecutionStopReason.TargetInvalidated;
            result.NeedsReplan = true;
            result.ApSpent = 0f;
            ApCheck(pm, apBefore, root, result);
            CompleteResult(result, root);
            results.Add(result);
            ReleaseEconomyReservation(player, ctx, pm);
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
            int apBefore, WorldSnapshot snapshot)
        {
            if (pm.Kind != MissionKind.Economy || pm.EconomyExtractionGarrisonArmyId < 0)
                return false;

            bool materialized = MaterializeEconomyGarrisonBuilder(
                player, root, ctx, pm, result, apBefore, snapshot);
            result.StartHex = pm.ExecutionHex;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            if (!materialized)
            {
                result.FinalHex = pm.ExecutionHex;
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                return true;
            }

            result.FinalHex = Resolve(player, pm.MoverArmyId)?.Hex ?? pm.ExecutionHex;
            result.StopReason = ExecutionStopReason.StepCompleted;
            result.NeedsReplan = false;
            return true;
        }

        private static bool MaterializeEconomyGarrisonBuilder(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore,
            WorldSnapshot snapshot)
        {
            ArmyData garrison = Resolve(player, pm.EconomyExtractionGarrisonArmyId);
            if (garrison == null || pm.EconomyPendingBuilderChoice == null)
                return false;

            // 2026-09-14 review round 5 — materialize the EXACT plan Provisioning already chose and
            // funded (pm.EconomyExtractionPlan), never a fresh re-resolve: a re-resolve with
            // commitments:null/session:null runs under weaker constraints than the original choice
            // and can legally pick a different — or already-claimed — hero/container/tier.
            ProvisioningManager.GarrisonExtractionCandidate plan = pm.EconomyExtractionPlan;
            if (plan.Tier == ProvisioningManager.GarrisonExtractionTier.None)
                return false;
            ArmyData materialized = ProvisioningManager.ApplyGarrisonExtraction(
                player, garrison, plan, ctx, out UnitData extractedHero, out bool containerCreated,
                out int createdContainerArmyId);
            if (materialized == null)
            {
                // 2026-09-14 review round 6 (P1) — an empty shell with no hero is NOT an Economy
                // actor: ActorMaterialized/ActualActorArmyId must stay strictly "a real hero-led
                // mover now exists", or Continuity will happily track a hero-less shell as this
                // mission's mover. The real, honest fact here is a separate one: the WORLD changed
                // (AP spent, a new empty army registered, kept — same "never rolled back" rule the
                // Create tier already documents) even though no actor for THIS mission exists yet.
                if (containerCreated)
                    result.ContainerCreated = true;
                return false;
            }

            List<MissionIntent> standingIntents = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Status == IntentStatus.Active).ToList();
            // 2026-09-14 review round 5 — the funding envelope for this call is what Provisioning
            // actually funded THIS mission (pm.ClaimedAp), minus whatever the extraction itself just
            // spent — never the player's entire current AP pool, which would let Economy silently
            // overrun the ECO-axis budget other demands were counting on this same pass. The raw pool
            // check stays live (root.ActionPoints): by Execution time each mission already mutates
            // AP for real, so there is no session-tracked cross-mission claim left to add back in.
            float spentSoFar = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            float remainingEnvelope = Mathf.Max(0f, pm.ClaimedAp - spentSoFar);
            // 2026-09-14 review round 6 (P1) — pass the REAL current snapshot, not null: a null
            // snapshot makes WorldAnalysis.KnownThreatsAffectingEconomyRoute (inside
            // PlanEconomyArmyLightening) see zero threats, which can silently starve a
            // ReinforceAtBase candidate's escort plan and turn an escort requirement into a bogus
            // AssemblyInfeasible right after the hero was for-real extracted.
            //
            // `bumpVersion: false` — StampVersion (the caller's caller, keyed off
            // ExecutionResult.ActorMaterialized) is the SOLE version-bump owner for this Execution
            // step; ProvisioningResult.Ok would otherwise also bump when preparedMembers > 0,
            // double-counting one mutation as two version bumps.
            ProvisioningResult finished = ProvisioningManager.FinishEconomyBuilder(player, root, ctx,
                snapshot, standingIntents, pm.Mission, pm.Key, pm.EconomyTarget,
                pm.EconomyPendingBuilderChoice, materialized,
                apEnvelope: remainingEnvelope, apPoolRemaining: root.ActionPoints, bumpVersion: false);
            if (!finished.Success || finished.Provisioned == null)
            {
                AiDebugLog.Write($"[AI][V2][Economy] materialization prep failed for {pm.Key}: "
                    + $"{finished.Failure.Kind} {finished.Failure.Detail}"
                    + " — hero stays a real field mover, found again next admission pass");
                result.ActualActorArmyId = materialized.Id;
                result.ActorMaterialized = true;
                return false;
            }

            pm.MoverArmyId = materialized.Id;
            pm.EconomyExtractionGarrisonArmyId = -1;
            pm.ReservationOwner = finished.Provisioned.ReservationOwner;
            pm.EconomyLoanSource = finished.Provisioned.EconomyLoanSource;
            pm.ClaimedAp = finished.Provisioned.ClaimedAp;
            pm.ClaimedPhysical = finished.Provisioned.ClaimedPhysical;
            result.ActualActorArmyId = materialized.Id;
            result.ActorMaterialized = true;
            AiDebugLog.Write($"[AI][V2][Economy] materialized builder #{materialized.Id} "
                + $"for {pm.Key} from garrison #{garrison.Id} ({plan.Tier})");
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
                || result.ContainerCreated)
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
