using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using Game.Aviation;
using UnityEngine;

namespace Game.Ai.V2
{
    // Executes provisioned V2 missions through authoritative game paths. TaskExecutor now owns
    // the shared mission lifecycle/accounting shell and routes to mission-specific executors. Ground Scout
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
        public bool OperationStarted;
        // Immutable execution fact consumed by Continuity. A reinforcement handoff may change the
        // RaidIntent phase before the outcome ledger reconciles it, so actor-role ownership must
        // never be inferred from the intent's already-mutated current phase.
        public bool ReinforcementHandoffAttempted;
        // ATK §17 — this Attack step spent its one opportunistic side strike: the mover actually
        // made contact with the chosen weak enemy field army and a battle started. An execution
        // fact, like the handoff above: only Continuity may turn it into the durable intent's
        // turn-local marker, and nothing here re-derives it from the intent's mutated state.
        public bool AttackOpportunisticStrike;
        public bool AttackIntermediateCaptured;
        public bool AttackCaptureHadBattle;
        public bool AirSupportStrikeSucceeded;
        public RaidRefitAction RaidRefitAction;
        public bool RaidRefitSucceeded;
        public ResourceVector ResourcesSpent;

        // Set when an Economy builder reaches its BuildExtraction/FoundBase target this step but
        // the infrastructure itself is not up yet (that's Phase A's job next admission). Nothing in
        // WorldSnapshot changed yet, so without this explicit fact PublishStepObservationDelta sees
        // no typed invalidation and the typed loop stops before Phase A ever gets a chance to build.
        public bool EconomyDeliveryReady;
        // A MobileCollection collector standing on its site for the income tick: productive
        // work with no mutation of its own (same role as EconomyDeliveryReady for a build).
        public bool EconomyHolding;
        // A concrete Researcher/Assembler reached its exact Facility; Phase A must see the
        // newly executable source immediately, not wait for an unrelated resource mutation.
        public bool DevelopmentDeliveryReady;

        // Provisioned mission that produced this execution ledger row.
        public ProvisionedMission Source;

        // The real ArmyId after an actor is materialized or otherwise resolved during execution.
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
                // A successful hero extraction IS a genuine successful action, not merely a state
                // change (StateChanged=true/Succeeded=false would read as a contradiction to
                // telemetry/WasGenuineExecution).
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
        // Shared short-circuits of Execute() and ExecuteStep(): stale plan, mover-resolve failure
        // and MissionRevalidator stale goal. The two callers differ observably (Execute's batch
        // model never signals NeedsReplan on a stale mover/goal and logs a "mover
        // gone"/revalidation line; ExecuteStep does the opposite), so those differences are
        // explicit parameters.
        //
        // Scout dispatch itself (ReconGroundExecutor.Run vs RunStep) stays UNMERGED on purpose (see
        // docs/ai-economy-mover-materialization-decision-tree.md): Execute's multi-step lookahead
        // and ExecuteStep's single-atomic-step contract are two different behaviors, not two copies
        // of the same one.
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
            WorldDeltaLifecycle.Publish(player, ctx.TurnNumber,
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
                && pm.Kind == MissionKind.Scout
                && ScoutObjectiveEvaluator.RoleContinuesAtWaypoint(pm.ScoutKind,
                    pm.Mission?.FromDurableIntent == true, AiArmyRoles.IsSoloRecce(army),
                    actedThisTurn: false);
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

        // The ONE per-mission step lifecycle: stale-plan check, deferred-Economy materialization,
        // mover resolve, stale- goal revalidation, kind dispatch, AP/version/resource stamping,
        // result recording, reservation release. Both Execute() (batch adapter, `singleStepOnly:
        // false`) and ExecuteStep() (`singleStepOnly: true`) call this and NOTHING ELSE per mission
        // — neither re-implements any of it. `singleStepOnly` is an execution-STRATEGY switch, not
        // a second lifecycle: it picks which of Recon/Raid's own two execution modes applies
        // (continuous multi-step lookahead — Run/RunRaid — vs one atomic step —
        // RunStep/RunRaidStep, the difference those subsystems expose), and the two callers'
        // observable differences (Execute's batch model never sets NeedsReplan on a lost/stale
        // mover or an unsupported kind, and logs where ExecuteStep doesn't; ExecuteStep does the
        // reverse).
        private static IEnumerator ExecuteMissionCore(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result,
            List<ExecutionResult> results, bool enforceFreshPlan, WorldSnapshot snapshot,
            List<ProvisionedMission> queue, int missionIndex, bool singleStepOnly)
        {
            if (TryHandleStalePlan(player, root, ctx, pm, result, results,
                    enforceFreshPlan, logStalePlan: !singleStepOnly))
                yield break;

            int apBefore = root != null ? root.ActionPoints : 0;
            // A deferred Economy garrison-extraction mission
            // carries a SYNTHETIC negative MoverArmyId (ProvisioningManager.
            // SyntheticGarrisonExtractionActorId). Materialization happens HERE, first, before
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
            // T01 — an Attack preparation host step (possibly creating its own container) runs
            // before the mover is resolved, exactly like the deferred Economy extraction above.
            if (AttackExecutor.TryRunPreparationStep(player, ctx, pm, result, snapshot))
            {
                result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                ApCheck(pm, apBefore, root, result);
                StampVersion(result);
                CompleteResult(result, root);
                results.Add(result);
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
                    yield return RaidExecutor.RunRaidStep(player, root, ctx, pm, result, apBefore, snapshot);
                else
                    yield return RaidExecutor.RunRaid(player, root, ctx, pm, result, apBefore, snapshot);
                ApCheck(pm, apBefore, root, result);
                StampVersion(result);
                CompleteResult(result, root);
                results.Add(result);
                yield break;
            }

            if (pm.Kind == MissionKind.ActiveDefence)
            {
                if (singleStepOnly)
                    yield return ActiveDefenceExecutor.RunActiveDefenceStep(player, root, ctx, pm, result, apBefore);
                else
                    yield return ActiveDefenceExecutor.RunActiveDefence(player, root, ctx, pm, result, apBefore);
                ApCheck(pm, apBefore, root, result);
                StampVersion(result);
                CompleteResult(result, root);
                results.Add(result);
                yield break;
            }

            // ATK §67 — Attack is ALWAYS one atomic step, in both the single-step and the multi-step
            // caller. There is deliberately no multi-step Attack variant: a capture changes the map's
            // topology so much that the next step must be decided against a fresh snapshot, through
            // the ordinary settled-step loop, never inside one executor call.
            if (pm.Kind == MissionKind.Attack)
            {
                yield return AttackExecutor.RunStep(player, root, ctx, pm, result, snapshot);
                // §2.1 — same physical turn-pool delta every other executor reports (activation
                // AP is spent inside the move routine; RunStep never reported it).
                result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
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

            if (pm.Kind == MissionKind.Development)
            {
                yield return RunDevelopmentStep(player, root, ctx, pm, result, apBefore);
                ApCheck(pm, apBefore, root, result);
                StampVersion(result);
                CompleteResult(result, root);
                results.Add(result);
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
                ReconAcceptanceAudit.BeginTurn(player, ctx.TurnNumber);
                ReconAcceptanceAudit.Summarize(player, ctx.TurnNumber);
                yield break;
            }

            var queue = new List<ProvisionedMission>(provisioned);
            ReconAcceptanceAudit.BeginTurn(player, ctx.TurnNumber);

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

        // The one real mutation site for deferred pending work, shared by both execution doors
        // (batch Execute() and incremental ExecuteStep()) so it is handled identically either way.
        // Development: applies the pinned garrison extraction (pm.EconomyExtractionPlan) via
        // ProvisioningManager.ApplyGarrisonExtraction. Economy: ApplyEconomyPreparation applies the
        // pinned extraction (if any) and composition change — never a re-resolve. Fully populates
        // `result` and returns true when there was something deferred to handle (success or failure
        // — the caller must add `result` and stop this mission for this call); returns false when
        // nothing was deferred, so the caller proceeds with its normal Resolve/mission-kind
        // dispatch.
        //
        // On success this is DELIBERATELY a terminal step: CreateArmy + TransferMember +
        // lightening/reinforcement is already one canonical batch, and a move on top would violate
        // the one-mutation-per-step contract. `pm.MoverArmyId` is flipped from synthetic to real in
        // place, so the NEXT admission pass sees an ordinary, already-real mover.
        private static bool TryHandleDeferredEconomyMaterialization(PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result,
            int apBefore)
        {
            // This door also covers a DIRECT-army Economy mission (hero already real) whose
            // composition change and/or donor-loan suspend Provisioning left pinned but unapplied
            // (pm.EconomyPreparationPending) — see ApplyEconomyPreparation.
            if (pm.Kind != MissionKind.Economy && pm.Kind != MissionKind.Development)
                return false;
            if (pm.Kind == MissionKind.Development)
            {
                if (pm.EconomyExtractionGarrisonArmyId < 0)
                    return false;
                ArmyData garrison = Resolve(player, pm.EconomyExtractionGarrisonArmyId);
                ProvisioningManager.GarrisonExtractionCandidate pinned = pm.EconomyExtractionPlan;
                if (garrison == null || pinned.Tier == ProvisioningManager.GarrisonExtractionTier.None
                    || !ReferenceEquals(pinned.Hero, pm.DevelopmentTarget.Hero)
                    || !garrison.Members.Contains(pinned.Hero)
                    || !AiArmyRoles.CanSpareGarrisonMember(player, garrison, pinned.Hero)
                    || pinned.ApCost > pm.ClaimedAp + AiConfigV2.allocatorSliceEpsilon
                    || root == null || !root.CanSpendActionPoints(Mathf.CeilToInt(pinned.ApCost)))
                {
                    result.StopReason = ExecutionStopReason.TargetInvalidated;
                    result.NeedsReplan = true;
                    result.FinalHex = pm.ExecutionHex;
                    return true;
                }
                ArmyData extracted = ProvisioningManager.ApplyGarrisonExtraction(
                    player, garrison, pinned, ctx);
                result.StartHex = pm.ExecutionHex;
                result.FinalHex = extracted?.Hex ?? pm.ExecutionHex;
                result.ActualActorArmyId = extracted?.Id;
                result.ActorMaterialized = extracted != null;
                result.StopReason = extracted == null
                    ? ExecutionStopReason.TargetInvalidated : ExecutionStopReason.StepCompleted;
                result.NeedsReplan = extracted == null;
                result.ApSpent = Mathf.Max(0f, apBefore - root.ActionPoints);
                if (extracted != null)
                {
                    pm.MoverArmyId = extracted.Id;
                    pm.EconomyExtractionGarrisonArmyId = -1;
                }
                return true; // extraction is exactly one canonical mutation step
            }
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

        // The SOLE apply site for pm.EconomyExtractionPreparation, for BOTH shapes of pending
        // Economy work: a garrison-extraction candidate (hero not yet real —
        // EconomyExtractionGarrisonArmyId >= 0) AND a direct-army candidate whose hero is already
        // real but whose composition change/donor-loan suspend Provisioning left pinned. No Economy
        // actor is ever mutated inside Provisioning. Execution never re-plans here — Provisioning
        // already computed and pinned the FULL decision (ProvisioningManager.PlanEconomyCompletion,
        // against a read-only preview for the extraction case, the real live hero for the direct
        // case); this only APPLIES the pinned hero extraction (if any) and cheaply re-validates the
        // two facts that can shift within the same batch pass (AP, resource spendability — never
        // composition/donor/route).
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
                // Materialize the EXACT plan Provisioning already chose
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
            // The envelope is what Provisioning actually funded THIS mission (pm.ClaimedAp, the
            // AUTHORITATIVE PlanEconomyCompletion figure), minus whatever the hero extraction
            // itself just spent — never the player's entire current AP pool.
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
            if (!StrategicSpendability.FitsSpendableForEconomyCompletion(player, root, ctx, prep.StageCost, prep.OwnerKey))
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
            // No InfrastructureFulfillment.ReserveEconomyCost call here: Provisioning already
            // reserved it (see the deferred branch of ProvisionEconomy) the moment this mission
            // committed to being deferred, so the resource pool is honestly reduced for any OTHER
            // Economy mission provisioned later in the same batch pass. Reserving again here would
            // double-charge the ledger for one build.
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
            // WorldAnalysis.Observation.PublishStepObservationDelta's EconomyDeliveryReady branch
            // passes this straight through as the Actor-invalidation id.
            result.ActualActorArmyId = army.Id;
            EconomyMissionTarget target = pm.EconomyTarget;
            if (army.Hex.Equals(target.TargetHex))
            {
                result.ReachedGoal = target.Kind == EconomyTaskKind.ReturnBuilder
                    || target.Kind == EconomyTaskKind.ReturnCollector;
                result.StopReason = result.ReachedGoal
                    ? ExecutionStopReason.ReachedGoal
                    : ExecutionStopReason.StepCompleted;
                result.NeedsReplan = false;
                result.FinalHex = army.Hex;
                // A deferred mission that materializes directly
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
                    if (target.Kind == EconomyTaskKind.MobileCollection)
                    {
                        result.EconomyHolding = true;
                        AiDebugLog.Write($"[AI][V2][Economy][Mobile] collector #{army.Id} holding "
                            + $"@({army.Hex.Q},{army.Hex.R}) for global income tick");
                    }
                    else
                    {
                        result.EconomyDeliveryReady = true;
                        AiDebugLog.Write($"[AI][V2][Economy] delivery ready {pm.Key}; request Phase-A build follow-up");
                    }
                }
                yield break;
            }
            yield return RunGroundTransportStep(player, root, ctx, pm, result, apBefore,
                target.TargetHex, $"economy — {target.Kind}");
            HexCoord after = result.FinalHex;
            bool recoveryArrived = result.StopReason != ExecutionStopReason.MoverLost
                && (target.Kind == EconomyTaskKind.ReturnBuilder
                    || target.Kind == EconomyTaskKind.ReturnCollector)
                && after.Equals(target.TargetHex);
            result.ReachedGoal = recoveryArrived;
            if (recoveryArrived)
                result.StopReason = ExecutionStopReason.ReachedGoal;
            if (result.StopReason == ExecutionStopReason.MoveRejected)
                result.NeedsReplan = true;
        }

        private static IEnumerator RunDevelopmentStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore)
        {
            ArmyData army = Resolve(player, pm.MoverArmyId);
            DevelopmentMissionTarget target = pm.DevelopmentTarget;
            if (army == null || !army.Members.Contains(target.Hero))
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                yield break;
            }
            result.ActualActorArmyId = army.Id;
            if (!army.Hex.Equals(target.FacilityHex))
                yield return RunGroundTransportStep(player, root, ctx, pm, result, apBefore,
                    target.FacilityHex, $"development — {target.Mode} hero={target.HeroKey}");

            // A battle or event can interrupt an otherwise valid transport step; it must be
            // settled by its domain owner before this delivery can be marked ready.
            if (result.StopReason != ExecutionStopReason.MoverLost
                && result.StopReason != ExecutionStopReason.BattleStarted
                && result.StopReason != ExecutionStopReason.HexEventStarted
                && ResearchProductionSystem.ActorStillQualifies(player, target.Hero,
                    target.FacilityHex, target.Mode)
                && ResearchProductionSystem.IsEligible(player, target.FacilityHex,
                    target.Mode, out _))
            {
                result.ReachedGoal = true;
                result.DevelopmentDeliveryReady = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
                result.NeedsReplan = false;
                result.FinalHex = target.FacilityHex;
                AiDebugLog.Write($"[AI][V2][Development] arrived hero={target.HeroKey} "
                    + $"@({target.FacilityHex.Q},{target.FacilityHex.R}); production readmit");
            }
        }

        // Economy and Development share ONE safe-path movement command and AP/step accounting.
        // This helper only executes a bound adjacent step; it never selects actor or objective.
        private static IEnumerator RunGroundTransportStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore,
            HexCoord target, string label)
        {
            ArmyData army = Resolve(player, pm.MoverArmyId);
            if (army == null)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                yield break;
            }
            result.ActualActorArmyId = army.Id;
            result.FinalHex = army.Hex;
            if (army.CurrentMovement <= 0)
            {
                result.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }
            HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, target);
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
                    $"V2 {label} at ({target.Q},{target.R})", 0f, AiGroundMoveAuthority.Transit), ctx, trace);
            army = Resolve(player, pm.MoverArmyId);
            HexCoord after = army != null ? army.Hex : trace.EndHex;
            result.FinalHex = after;
            if (!after.Equals(before)) result.StepsMoved = 1;
            result.StopReason = army == null ? ExecutionStopReason.MoverLost
                : pm.Kind == MissionKind.Development && trace.BattleOccurred
                ? ExecutionStopReason.BattleStarted
                : pm.Kind == MissionKind.Development && trace.HexEventOccurred
                    ? ExecutionStopReason.HexEventStarted
                    : result.StepsMoved > 0
                            ? ExecutionStopReason.StepCompleted : ExecutionStopReason.MoveRejected;
            result.NeedsReplan = army == null || result.StepsMoved == 0;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            if (AiDebugLog.Verbose)
                AiDebugLog.Write($"[AI][V2] {label} move {pm.Key} "
                    + $"({before.Q},{before.R})->({after.Q},{after.R})");
        }

        private static void ReleaseEconomyReservation(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm)
        {
            if (pm?.Kind == MissionKind.Economy && ctx != null)
                StrategicResourceReservationLedger.ReleaseByOwner(player, ctx.TurnNumber,
                    pm.ReservationOwner);
        }

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            AiV2Util.ResolveArmy(player, armyId);

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
            // ActorMaterialized (a garrison-extraction CreateArmy/TransferMember) is a real world
            // mutation with no movement/stealth/infrastructure/combat signal of its own, so it
            // bumps V2StateVersion explicitly.
            if (result.StepsMoved > 0 || result.EnteredStealth || result.StealthChanged
                || result.InfrastructureChanged || result.CombatChanged || result.ActorMaterialized
                || result.EconomyPrepared)
                WorldDeltaLifecycle.CommitMutation();
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


