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
        // Attack: why a funded step did not move although terrain allows the route (a contact it
        // will not fight and cannot go around). Distinct from impassable terrain; log/test fact.
        public string TacticalBlockReason;
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
        // Economy extraction changes the actor identity once: source garrison -> real builder.
        // Continuity consumes this factual receipt; other mover mismatches remain pinned.
        public int? EconomyMaterializationSourceArmyId;
        // A direct Economy actor's roster and/or donor intent was prepared.
        public bool EconomyPrepared;
        // A committed exchange may survive a later preparation refusal.
        public bool ResourceConverted;
        public bool NeedsReplan;
        // Stops this step for a reason that asks the next pass to plan again.
        internal void StopForReplan(ExecutionStopReason reason)
        {
            StopReason = reason;
            NeedsReplan = true;
        }
        public int PlannedAtStateVersion = -1;
        public int StateVersionBefore = -1;
        public int StateVersionAfter = -1;
        // Observation boundary supplies immutable receipts; the registry remains the sole
        // pending-fact owner. Canonical mutation freshness is StateVersionAfter above.
        internal IReadOnlyList<WorldDelta> WorldDeltas = System.Array.Empty<WorldDelta>();
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
                    || InfrastructureChanged || CombatChanged || ActorMaterialized || EconomyPrepared || ResourceConverted;
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
        // The short-circuits of a step before any command: stale plan, mover-resolve failure and
        // MissionRevalidator stale goal. Each stops the step for a replan.
        private static bool TryHandleStalePlan(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result,
            List<ExecutionResult> results, bool enforceFreshPlan)
        {
            if (!enforceFreshPlan || pm.PlannedAtStateVersion < 0
                || WorldDeltaLifecycle.IsCurrent(pm.PlannedAtStateVersion))
                return false;

            result.StartHex = pm.ExecutionHex;
            result.FinalHex = pm.ExecutionHex;
            result.StopForReplan(ExecutionStopReason.TargetInvalidated);
            result.ApSpent = 0f;
            WorldDeltaLifecycle.Publish(player, ctx.TurnNumber,
                StrategicInvalidationReason.External, actorIds: new[] { pm.MoverArmyId });
            CompleteResult(result, root);
            results.Add(result);
            EconomyExecutor.ReleaseReservation(player, ctx, pm);
            return true;
        }

        // Resolves the mover; on success stamps result.StartHex/FinalHex and returns the army via
        // `army` (caller proceeds). On failure fully populates+adds `result` and returns null.
        private static bool TryResolveMoverOrHandleGone(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result,
            List<ExecutionResult> results, int apBefore, out ArmyData army)
        {
            army = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
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
            result.NeedsReplan = true;
            ApCheck(pm, apBefore, root, result);
            CompleteResult(result, root);
            results.Add(result);
            EconomyExecutor.ReleaseReservation(player, ctx, pm);
            ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId, "mover gone before atomic execution");
            return true;
        }

        private static bool TryHandleStaleValidity(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ArmyData army, ExecutionResult result,
            List<ExecutionResult> results, int apBefore)
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
            result.StateVersionAfter = WorldDeltaLifecycle.Current;   // nothing mutated
            result.StopReason = validity == MissionValidity.StaleMoverLost
                ? ExecutionStopReason.MoverLost
                : validity == MissionValidity.StaleGoalMet
                    ? ExecutionStopReason.ReachedGoal
                    : ExecutionStopReason.TargetInvalidated;
            result.NeedsReplan = validity != MissionValidity.StaleGoalMet;
            ApCheck(pm, apBefore, root, result);
            CompleteResult(result, root);
            results.Add(result);
            EconomyExecutor.ReleaseReservation(player, ctx, pm);
            if (validity == MissionValidity.StaleMoverLost)
                ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId, "atomic mission revalidation lost mover");
            return true;
        }

        // The ONE per-mission step lifecycle: stale-plan check, deferred-Economy materialization,
        // mover resolve, stale-goal revalidation, kind dispatch (each lane's one atomic step),
        // AP/version/resource stamping, result recording, reservation release.
        private static IEnumerator ExecuteMissionCore(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result,
            List<ExecutionResult> results, bool enforceFreshPlan, WorldSnapshot snapshot)
        {
            if (TryHandleStalePlan(player, root, ctx, pm, result, results, enforceFreshPlan))
                yield break;

            int apBefore = root != null ? root.ActionPoints : 0;
            // Pending Economy / Development work (a garrison extraction behind a SYNTHETIC negative
            // MoverArmyId, ProvisioningManager.SyntheticGarrisonExtractionActorId, or a pinned
            // composition change) is applied first, before anything resolves the mover, and is a
            // terminal step of its own (one mutation per step).
            if (DevelopmentExecutor.TryApplyPendingExtraction(player, root, ctx, pm, result, apBefore)
                || EconomyExecutor.TryApplyPendingPreparation(player, root, ctx, pm, result, apBefore, snapshot))
            {
                FinishStep(pm, apBefore, root, result, results);
                if (result.StopReason != ExecutionStopReason.StepCompleted)
                    EconomyExecutor.ReleaseReservation(player, ctx, pm);
                yield break;
            }
            // T01 — an Attack preparation host step (possibly creating its own container) runs
            // before the mover is resolved, exactly like the deferred Economy extraction above.
            if (ActiveDefenceExecutor.TryMaterializeSortie(player, root, ctx, pm, result, snapshot)
                || AttackExecutor.TryMaterializeSortie(player, root, ctx, pm, result, snapshot)
                || AttackExecutor.TryRunPreparationStep(player, ctx, pm, result, snapshot))
            {
                result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                FinishStep(pm, apBefore, root, result, results);
                yield break;
            }
            if (TryResolveMoverOrHandleGone(player, root, ctx, pm, result, results, apBefore,
                    out ArmyData army))
                yield break;

            // ARCH-02 §35 — the executor does NOT synthesise a replacement mission for a
            // stale-goal Scout. It records the stale outcome; MissionContinuityLayer.Reconcile
            // + the mission planner re-target the durable ReconPatrolState on the next pass.
            if (TryHandleStaleValidity(player, root, ctx, pm, army, result, results, apBefore))
                yield break;

            if (pm.Kind == MissionKind.Scout)
            {
                yield return ReconGroundExecutor.RunStep(player, root, ctx, pm, result, apBefore,
                    snapshot, new ReconGroundExecutor.StepControl());
                FinishStep(pm, apBefore, root, result, results);
                yield break;
            }

            // One departure rule for every ground operation: a hero the own facility needs stays in
            // the local garrison when it is legally and freely possible (Housekeeping's twin rule).
            if (pm.Kind == MissionKind.Raid || pm.Kind == MissionKind.ActiveDefence
                || pm.Kind == MissionKind.Attack)
            {
                if (LocalOperatorRelease.ReleaseBeforeDeparture(player, ctx, army,
                        snapshot == null ? null : ActorCommitments.FromIntents(
                            WorldSnapshot.IntentsOf(snapshot), snapshot, null),
                        DevelopmentOpportunityEvaluator.LivePreparationSite(
                            player, AiHandRegistry.Peek(player), ctx, snapshot?.Intents)) > 0)
                    result.StateVersionAfter = WorldDeltaLifecycle.Current;
            }

            if (pm.Kind == MissionKind.Raid)
            {
                yield return RaidExecutor.RunRaidStep(player, root, ctx, pm, result, apBefore, snapshot);
                FinishStep(pm, apBefore, root, result, results);
                yield break;
            }

            if (pm.Kind == MissionKind.ActiveDefence)
            {
                yield return ActiveDefenceExecutor.RunActiveDefenceStep(player, root, ctx, pm, result, apBefore, snapshot);
                FinishStep(pm, apBefore, root, result, results);
                yield break;
            }

            // ATK §67 — Attack is one atomic step: a capture changes the map's topology so much that
            // the next step must be decided against a fresh snapshot, through the settled-step loop.
            if (pm.Kind == MissionKind.Attack)
            {
                yield return AttackExecutor.RunStep(player, root, ctx, pm, result, snapshot);
                // §2.1 — same physical turn-pool delta every other executor reports (activation
                // AP is spent inside the move routine; RunStep never reported it).
                result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                FinishStep(pm, apBefore, root, result, results);
                yield break;
            }

            if (pm.Kind == MissionKind.Economy)
            {
                yield return EconomyExecutor.RunStep(player, root, ctx, pm, result, apBefore, snapshot);
                FinishStep(pm, apBefore, root, result, results);
                if (result.StopReason != ExecutionStopReason.StepCompleted)
                    EconomyExecutor.ReleaseReservation(player, ctx, pm);
                yield break;
            }

            if (pm.Kind == MissionKind.Development)
            {
                yield return DevelopmentExecutor.RunStep(player, root, ctx, pm, result, apBefore);
                FinishStep(pm, apBefore, root, result, results);
                yield break;
            }

            // Future mission kinds must opt into an executor explicitly. Never silently treat
            // an unknown mission as Scout or let it mutate the world through a fallback path.
            result.StopReason = ExecutionStopReason.TargetInvalidated;
            result.NeedsReplan = true;
            result.ApSpent = 0f;
            ApCheck(pm, apBefore, root, result);
            CompleteResult(result, root);
            results.Add(result);
            EconomyExecutor.ReleaseReservation(player, ctx, pm);
        }

        // The orchestration door for exactly one already-provisioned task step. Selection,
        // allocation and provisioning stay outside this execution owner; validation, command
        // dispatch, AP invariants and version/resource stamping stay inside ExecuteMissionCore.
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
                StateVersionBefore = WorldDeltaLifecycle.Current,
                ResourcesBefore = AiV2Trace.Stamp(root),
            };
            yield return ExecuteMissionCore(player, root, ctx, pm, result, results,
                enforceFreshPlan, snapshot);
        }

        // §2.1 — the real AP the turn's pool lost while this mission executed must equal the AP the
        // ExecutionResult reports it spent. Both executors derive ApSpent from the same physical
        // turn-pool delta; this catches any action path that reports less/more than it really used.
        // The settled-step tail of every executor branch: AP audit, state version, completion,
        // and the result handed to the caller.
        private static void FinishStep(ProvisionedMission pm, int apBefore, PlayerRoot root,
            ExecutionResult result, List<ExecutionResult> results)
        {
            ApCheck(pm, apBefore, root, result);
            StampVersion(result);
            CompleteResult(result, root);
            results.Add(result);
        }

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
            // A child action's non-negative StateVersionAfter is its committed revision receipt.
            // Air execution already provides it; aggregate completion must not stamp it twice.
            // No extra counter or independent "changed" flag is introduced.
            // ActorMaterialized (a garrison-extraction CreateArmy/TransferMember) is a real world
            // mutation with no movement/stealth/infrastructure/combat signal of its own, so it
            // bumps WorldDeltaLifecycle explicitly.
            result.StateVersionAfter = WorldDeltaLifecycle.StampAction(
                result.StepsMoved > 0 || result.EnteredStealth || result.StealthChanged
                    || result.InfrastructureChanged || result.CombatChanged || result.ActorMaterialized
                    || result.EconomyPrepared || result.ResourceConverted,
                childAlreadyCommitted: result.StateVersionAfter >= 0);
        }

        private static void CompleteResult(ExecutionResult result, PlayerRoot root)
        {
            if (result == null) return;
            if (result.StateVersionAfter < 0)
                result.StateVersionAfter = WorldDeltaLifecycle.Current;
            result.ResourcesAfter = AiV2Trace.Stamp(root);
        }
    }
}

