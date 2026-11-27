using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

using Game.Combat;

namespace Game.Ai.V2
{
    // What one air-recon step did. Air recon is a coroutine (it cannot `return` a value), so the
    // caller passes one of these in to read whether the world moved.
    internal sealed class AirReconExecutionResult
    {
        public bool AnyMoved;
        public bool AnyStruck;
        public bool ResourceConverted;

        public void RecordMove() { AnyMoved = true; }
        public void RecordStrike() { AnyStruck = true; }

        public bool Mutated => AnyMoved || AnyStruck || ResourceConverted;
    }

    // EXECUTION ONLY. It receives an AirReconPlan (actor/airfield/subset WHO is
    // picked by ReconAssignmentPlanner/ProvisioningManager, the same single owner Ground has;
    // AirReconPlanner only turns that binding, plus continuing wings, into this plan's shape — mode,
    // first-step gate and energy policy re-derived fresh as execution-input assembly, never a second
    // actor selection) and, for each airborne actor, asks AirReconStepDirector for the next tactical
    // decision and issues exactly the canonical Move / Strike / assignment-bookkeeping call it names.
    // It never chooses a mode, a step, a landing, a phase transition or whether a strike is
    // worthwhile — that all lives in the director, which is free to replan live on every call.
    internal static class ReconAirExecutor
    {
        internal sealed class ActorStepControl
        {
            public bool MovedAny;
            public bool CommandAttempted;
            public ExecutionStopReason StopReason = ExecutionStopReason.StepCompleted;

            public void ResetStep()
            {
                CommandAttempted = false;
                StopReason = ExecutionStopReason.StepCompleted;
            }
        }

        private static bool PrepareStoredMissions(AirReconPlan plan, PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snapshot)
        {
            bool changed = false;
            foreach (ProvisionedMission pm in plan.StoredMissions.ToList())
            {
                ArmyData source = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
                var group = pm.AircraftRuntimeIds == null ? new List<UnitData>()
                    : pm.AircraftRuntimeIds.Select(id => source?.Members.FirstOrDefault(u => u.RuntimeId == id)).ToList();
                bool valid = AviationRules.IsAirfield(source) && source.Owner == player
                    && ReconAirCapacityPolicy.CanFlyAirSweep(group)
                    && group.All(u => AviationRules.EffectiveMoveCurrent(u) > 0);
                float ap = valid ? group.Sum(u => Math.Max(0, u.ActivationApCost)) : float.PositiveInfinity;
                float energy = valid ? group.Sum(u => Math.Max(0, u.LaunchEnergyCost)) : float.PositiveInfinity;
                var pick = valid ? ReconAirStepPlanner.PickFromStorage(player, ctx, source.Hex, group, snapshot,
                    AirReconModePolicy.RequestedMode(player, snapshot), ctx.TurnNumber,
                    missionFocusHex: pm.FocusHex) : null;
                bool affordable = ap <= pm.ClaimedAp + AiConfigV2.allocatorSliceEpsilon
                    && energy <= pm.ClaimedEnergy + AiConfigV2.allocatorSliceEpsilon
                    && ap <= StrategicSpendability.SpendableAp(player, root, ctx)
                    && TradeFunding.CanFund(player, root, ctx, new ResourceCost(energy: (int)energy));
                ActorCommitments commitments = ActorCommitments.FromIntents(
                    WorldSnapshot.IntentsOf(snapshot), snapshot, null);
                if (!valid || !affordable || !pick.HasValue
                    || !AviationWingPreparation.TryForm(player, ctx, source, group, commitments, out ArmyData wing, out _))
                {
                    plan.SkippedMissions.Add(new AirReconSkippedMission
                        { Mission = pm, Reason = ExecutionStopReason.NoSafeStep });
                    continue;
                }
                pm.MoverArmyId = wing.Id;
                pm.ExecutorKind = ScoutExecutorKind.AirExisting;
                pm.AircraftRuntimeIds = null;
                plan.MaterializedActorIds.Add(wing.Id);
                plan.ReadyActorIds.Add(wing.Id);
                plan.ReadyMissionByActorId[wing.Id] = pm;
                changed = true;
                WorldDeltaLifecycle.CommitMutation();
            }
            plan.StoredMissions.Clear();
            return changed;
        }

        // Single owner of the safety exception to normal Mission -> Allocation -> Provisioning:
        // an airborne Recon actor already in Return phase must be allowed to recover even without
        // a fresh strategic-progress entitlement; the lifecycle projection below is the unique
        // must-recover rule.
        internal static List<ArmyData> FindMandatoryRecoveryActors(PlayerSetupData player,
            AiTurnContext ctx)
        {
            return ArmyRegistry.AllForOwner(player)
                .Where(a => ReconAirCapacityPolicy.IsAirborneReconWing(player, a)
                    // Recon audit B1 — a recovery that could not progress this turn is not
                    // re-admitted (nor protected by StrategicSpendability) until the next turn.
                    && !AviationObligationStallRegistry.IsStalled(player, ctx?.TurnNumber ?? -1, a.Id))
                .Where(a =>
                {
                    ReconAirSortieState projected =
                        ReconAirReservationPrepass.ProjectScoringSortie(player, ctx, a);
                    return projected != null && projected.Phase == ReconAirPhase.Return;
                })
                .OrderBy(a => a.Id)
                .ToList();
        }

        // End-of-turn safety net, the recon counterpart of GroundCombatAirSupport.RecallUnsafeStrikes.
        // An airborne recon wing that Assignment no longer drives (NO_CANDIDATE: no useful or safe
        // forward step) keeps its Outbound phase, so FindMandatoryRecoveryActors never sees it and it
        // takes the unlanded-end damage twice — a 0-endurance plane is lost aloft. Once the turn's
        // loop is over, every such wing that cannot safely end the turn here is turned home through
        // the sortie lifecycle; the wings now owed a recovery come from the one must-recover rule.
        // Every outbound step leaves this executor's own Recon AirSortie record on the wing
        // (EnsureAirReconReservation), so that record does not mean "driven by someone else" —
        // only a Strike/Rebase record does (RecallUnsafeStrikes / AviationRebaseExecutor own it).
        internal static List<ArmyData> RecallUnsafeReconWings(PlayerSetupData player, AiTurnContext ctx)
        {
            if (player == null || ctx?.Map == null)
                return new List<ArmyData>();
            foreach (ArmyData wing in ArmyRegistry.AllForOwner(player)
                .Where(a => ReconAirCapacityPolicy.IsAirborneReconWing(player, a)
                    && (AirSortieRegistry.ForArmy(player, a)?.Kind ?? AirSortieKind.Recon) == AirSortieKind.Recon
                    && !AiAirSortiePlanner.CanEndTurnHereAndRecover(a, ctx.Map, player))
                .OrderBy(a => a.Id).ToList())
            {
                ReconAirSortieState sortie = ReconAirSortieRegistry.GetOrCreate(player, wing.Id, wing.Hex);
                if (sortie.Phase == ReconAirPhase.Return)
                    continue;
                ReconAirSortieLifecycle.RecallForTurnEnd(sortie, "turn_end_recall: cannot safely end the turn aloft");
                AiDebugLog.Write($"[AI][V2][Recon][Air][Recall] actor=#{wing.Id} at ({wing.Hex.Q},{wing.Hex.R}) "
                    + $"mp={wing.CurrentMovement} - not driven this turn and cannot end it aloft; turning home");
            }
            return FindMandatoryRecoveryActors(player, ctx);
        }

        // Execute at most one action from an already assembled air-recon plan. Planning stays
        // in AirReconPlanner; launch/actor validation and result construction stay in this executor.
        internal static IEnumerator ExecutePlanStep(AirReconPlan plan, PlayerSetupData player,
            PlayerRoot root, AiTurnContext ctx, WorldSnapshot snapshot,
            List<ExecutionResult> perMissionResults)
        {
            if (plan == null || player == null || root == null || ctx?.Map == null
                || snapshot?.Self == null || perMissionResults == null)
                yield break;

            if (PrepareStoredMissions(plan, player, root, ctx, snapshot))
                snapshot = WorldAnalysis.RefreshStrategicKnowledge(snapshot, player, root, AiHandRegistry.Peek(player), ctx);
            AirReconSkippedMission skipped = plan.SkippedMissions.FirstOrDefault();
            if (skipped?.Mission != null)
            {
                ProvisionedMission pm = skipped.Mission;
                ExecutionResult er = NewPerMissionResult(pm, pm.ExecutionHex, -1);
                if (skipped.Reason != ExecutionStopReason.MoverLost && ObjectiveSatisfied(player, pm))
                    MarkSatisfiedNoOp(pm, er);
                else
                    er.StopReason = skipped.Reason;
                er.StateVersionAfter = WorldDeltaLifecycle.Current;
                perMissionResults.Add(er);
                yield break;
            }

            if (plan.ReadyActorIds.Count > 0)
            {
                int id = plan.ReadyActorIds[0];
                ArmyData air = AiV2Util.ResolveArmy(player, id);
                plan.ReadyMissionByActorId.TryGetValue(id, out ProvisionedMission pm);
                if (pm == null)
                    yield break;

                if (air != null && ObjectiveSatisfied(player, pm))
                {
                    ExecutionResult stale = NewPerMissionResult(pm, air.Hex, air.Id);
                    MarkSatisfiedNoOp(pm, stale);
                    stale.StateVersionAfter = WorldDeltaLifecycle.Current;
                    perMissionResults.Add(stale);
                    yield break;
                }

                ExecutionResult perMission = NewPerMissionResult(pm,
                    air?.Hex ?? pm.ExecutionHex, air?.Id ?? -1);
                perMission.ActorMaterialized = plan.MaterializedActorIds.Contains(id);
                int apBefore = root.ActionPoints;
                yield return RunActorStep(player, root, ctx, snapshot, air, null, apBefore,
                    pm.FocusHex, perMission);
                perMissionResults.Add(perMission);
                yield break;
            }

        }

        // Shared per-mission ExecutionResult scaffold, mirroring how
        // TaskExecutor/ReconGroundExecutor seed one: Key/Source/StartHex from the ProvisionedMission,
        // ActualActorArmyId is the already-existing real aviation army id (or null if it vanished).
        private static ExecutionResult NewPerMissionResult(ProvisionedMission pm, HexCoord startHex, int actorArmyId) =>
            new ExecutionResult
            {
                Key = pm.Key,
                Source = pm,
                StartHex = startHex,
                FinalHex = startHex,
                ActualActorArmyId = actorArmyId >= 0 ? actorArmyId : (int?)null,
                PlannedAtStateVersion = pm.PlannedAtStateVersion,
            };

        // The same "is the bound objective satisfied" question Ground's
        // RefreshObjectiveSatisfied asks, reused verbatim here so Air's per-mission result carries
        // the SAME ReachedGoal/ObjectiveSatisfied semantics Ground's does.
        private static void FinalizePerMissionResult(PlayerSetupData player, ProvisionedMission pm, ExecutionResult er)
        {
            if (er == null)
                return;
            er.StateVersionAfter = WorldDeltaLifecycle.Current;
            if (player == null || pm == null || er.ReachedGoal || er.StopReason == ExecutionStopReason.MoverLost)
                return;
            bool satisfied = ObjectiveSatisfied(player, pm);
            if (satisfied)
            {
                er.ReachedGoal = true;
                er.DurableRoleContinues = ScoutObjectiveEvaluator.RoleContinuesAtWaypoint(
                    pm.ScoutKind, pm.Mission?.FromDurableIntent == true, actorStillScout: true,
                    actedThisTurn: false);
            }
        }

        // An AirSweep is never "met" by seeing its anchor: the sortie ends by its own refuel
        // endurance (AirReconStepDirector outbound cap -> Return), not by an observation check.
        private static bool ObjectiveSatisfied(PlayerSetupData player, ProvisionedMission pm) =>
            pm != null && ObjectiveLiveStatus.ScoutSatisfied(player, pm.ScoutKind, pm.FocusHex);

        private static void MarkSatisfiedNoOp(ProvisionedMission pm, ExecutionResult er)
        {
            er.ReachedGoal = true;
            er.StaleNoOp = true;
            er.StopReason = ExecutionStopReason.ReachedGoal;
            er.DurableRoleContinues = pm != null && ScoutObjectiveEvaluator.RoleContinuesAtWaypoint(
                pm.ScoutKind, pm.Mission?.FromDurableIntent == true, actorStillScout: true,
                actedThisTurn: false);
        }

        // One admitted airborne Recon task step. It executes at most one canonical stationary
        // strike OR one adjacent MoveArmyRoutine command. Local Hold/phase bookkeeping may also be
        // resolved, but this method never selects another mission or actor.
        internal static IEnumerator RunActorStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snapshot, ArmyData initial,
            AirReconExecutionResult result, int apBefore, HexCoord? missionFocusHex = null,
            ExecutionResult perMissionResult = null, ActorStepControl control = null)
        {
            control ??= new ActorStepControl();
            control.ResetStep();
            if (initial == null)
            {
                control.StopReason = ExecutionStopReason.MoverLost;
                FinishActorStep(player, -1, perMissionResult, control, control.StopReason);
                yield break;
            }

            var stepResult = result ?? new AirReconExecutionResult();
            int rateBefore = root?.TradeRate ?? 2;

            yield return RunActorStepCore(player, root, ctx, snapshot, initial.Id,
                stepResult, missionFocusHex, perMissionResult, control);
            bool converted = root != null && root.TradeRate != rateBefore;
            stepResult.ResourceConverted |= converted;
            if (perMissionResult != null) perMissionResult.ResourceConverted |= converted;
            FinishActorStep(player, initial.Id, perMissionResult, control, control.StopReason);

            if (perMissionResult != null)
            {
                perMissionResult.SettleAp(apBefore, root);
                FinalizePerMissionResult(player, perMissionResult.Source, perMissionResult);
            }
        }

        private static IEnumerator RunActorStepCore(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, WorldSnapshot snapshot, int armyId, AirReconExecutionResult result,
            HexCoord? missionFocusHex, ExecutionResult perMissionResult, ActorStepControl control)
        {
            ArmyData air = AiV2Util.ResolveArmy(player, armyId);
            if (air == null || !AviationRules.IsValidAirArmy(air) || air.Controller == null)
            {
                ReconPatrolStateRegistry.Retire(player, armyId, "air mover lost / invalid");
                ReconAirSortieRegistry.Retire(player, armyId);
                if (air != null) RemoveAirReconReservation(player, air);
                control.StopReason = ExecutionStopReason.MoverLost;
                yield break;
            }

            if (ctx?.Map == null)
            {
                control.StopReason = ExecutionStopReason.TargetInvalidated;
                yield break;
            }
            if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
            {
                control.StopReason = ExecutionStopReason.BattleStarted;
                yield break;
            }

            ReconAirSortieState sortie = ReconAirSortieRegistry.GetOrCreate(
                player, armyId, air.Hex);
            bool atAirfield = AviationRules.IsOwnedAirfieldAt(air.Hex, player);
            bool hasDeparted = control.MovedAny || sortie.Trail.Count > 1;
            if (ReconAirSortieLifecycle.CompletesAtAirfield(sortie, atAirfield, hasDeparted))
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Landing at "
                    + $"({air.Hex.Q},{air.Hex.R}); sortie complete");
                ReconPatrolStateRegistry.Retire(player, armyId, "air recon landed");
                ReconAirSortieRegistry.Retire(player, armyId);
                RemoveAirReconReservation(player, air);
                control.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }
            if (air.CurrentMovement <= 0)
            {
                if (!atAirfield)
                {
                    // Hold is an actual end-of-turn state, never a side effect of attacking.
                    // Record it only when the wing really has no movement left and can legally
                    // spend this end-turn aloft; next turn reopens from live TurnsWithoutRefuel.
                    if (AiAirSortiePlanner.CanEndTurnHereAndRecover(air, ctx.Map, player))
                    {
                        sortie.Phase = ReconAirPhase.Hold;
                        sortie.LastDecisionReason = "end_turn_aloft: movement exhausted";
                    }
                    else
                    {
                        sortie.Phase = ReconAirPhase.Return;
                        sortie.LastDecisionReason = "movement exhausted: return required";
                    }
                }
                control.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }

            ReconAirSortieLifecycle.Observe(sortie, air);
            bool newTurn = ReconAirSortieLifecycle.BeginTurn(sortie, ctx.TurnNumber);
            AirReconStepDirector.StepDecision d = AirReconStepDirector.PlanStep(
                player, root, ctx, snapshot, air, sortie, newTurn, missionFocusHex);

            if (d.Kind == AirReconStepDirector.StepKind.Stop)
            {
                if (d.RetireAssignment)
                    ReconPatrolStateRegistry.Retire(player, armyId, d.Reason);
                if (d.RemoveReservation)
                    RemoveAirReconReservation(player, air);
                control.StopReason = ExecutionStopReason.NoSafeStep;
                yield break;
            }
            if (d.Kind == AirReconStepDirector.StepKind.HoldEndTurn)
            {
                control.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }

            if (d.Kind == AirReconStepDirector.StepKind.HoldReopen)
            {
                // Re-open the physical flight BEFORE checking the stationary strike. A successful
                // strike may put the sortie back into Hold (when endurance remains) or Return (last
                // safe airborne end consumed). Doing this first preserves that post-strike decision
                // instead of overwriting it with the generic resume phase afterwards.
                sortie.Phase = d.ResumePhase;
                control.CommandAttempted = true;
                yield return ExecuteOpportunisticStrike(
                    player, ctx, air, sortie, result, perMissionResult);
                ArmyData afterHoldStrike = AiV2Util.ResolveArmy(player, armyId);
                if (afterHoldStrike == null || !AviationRules.IsValidAirArmy(afterHoldStrike)
                    || afterHoldStrike.Controller == null)
                {
                    ReconPatrolStateRegistry.Retire(player, armyId, "air mover lost / invalid");
                    ReconAirSortieRegistry.Retire(player, armyId);
                    control.StopReason = ExecutionStopReason.MoverLost;
                    yield break;
                }
                ReconAirSortieLifecycle.Apply(sortie, d);
                ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                control.StopReason = ExecutionStopReason.StepCompleted;
                yield break;
            }

            if (d.Kind == AirReconStepDirector.StepKind.Strike)
            {
                control.CommandAttempted = true;
                bool attacked = false;
                yield return ExecuteOpportunisticStrike(
                    player, ctx, air, sortie, result, perMissionResult,
                    didAttack => attacked = didAttack);
                if (attacked)
                    ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                control.StopReason = AiV2Util.ResolveArmy(player, armyId) == null
                    ? ExecutionStopReason.MoverLost
                    : attacked
                    ? ExecutionStopReason.StepCompleted
                    : ExecutionStopReason.NoSafeStep;
                yield break;
            }

            if (d.Kind == AirReconStepDirector.StepKind.ReturnStep)
            {
                AirSortie reservation = EnsureAirReconReservation(
                    player, air, d.LandingHex, outbound: false);
                if (reservation == null)
                {
                    AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} return blocked - another task owns aircraft");
                    control.StopReason = ExecutionStopReason.NoSafeStep;
                    yield break;
                }
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Return "
                    + $"({air.Hex.Q},{air.Hex.R})->({d.Step.Q},{d.Step.R}) "
                    + $"landing=({d.LandingHex.Q},{d.LandingHex.R}) informative={(d.AlsoInformative ? 1 : 0)} {d.Reason}");
                bool moved = false;
                control.CommandAttempted = true;
                yield return MoveOne(player, ctx, air, d.Step,
                    "V2 Air Recon - safe return", () => moved = true, perMissionResult);
                if (!moved)
                {
                    control.StopReason = ExecutionStopReason.MoveRejected;
                    yield break;
                }
                WorldDeltaLifecycle.CommitMutation();
                result.RecordMove();
                control.MovedAny = true;
                if (perMissionResult != null) perMissionResult.StepsMoved++;
                ReconAirSortieLifecycle.Apply(sortie, d);
                ArmyData afterReturn = AiV2Util.ResolveArmy(player, armyId);
                if (afterReturn != null) sortie.RecordStep(afterReturn.Hex);
                ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
                control.StopReason = ExecutionStopReason.StepCompleted;
                yield break;
            }

            ReconPatrolState assignment = ReconPatrolStateRegistry.GetOrCreate(
                player, armyId, air.Hex, missionFocusHex ?? d.Step, d.Mode, ctx.TurnNumber);
            AirSortie reservationTask = EnsureAirReconReservation(
                player, air, d.LandingHex, outbound: true, target: d.Step);
            if (reservationTask == null)
            {
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} step blocked - another task owns aircraft");
                control.StopReason = ExecutionStopReason.NoSafeStep;
                yield break;
            }

            bool stepMoved = false;
            int movementBeforeStep = air.CurrentMovement;
            control.CommandAttempted = true;
            yield return MoveOne(player, ctx, air, d.Step,
                $"V2 Air Recon - {assignment.Mode} {sortie.Phase} one-step live replan",
                () => stepMoved = true, perMissionResult);
            if (!stepMoved)
            {
                control.StopReason = ExecutionStopReason.MoveRejected;
                yield break;
            }

            WorldDeltaLifecycle.CommitMutation();
            result.RecordMove();
            control.MovedAny = true;
            if (perMissionResult != null) perMissionResult.StepsMoved++;
            ArmyData afterStep = AiV2Util.ResolveArmy(player, armyId);
            if (afterStep != null)
                sortie.RecordStep(afterStep.Hex);
            ReconAirSortieLifecycle.Apply(sortie, d);
            if (d.PivotToReturnAfterMove)
                AiDebugLog.Write($"[AI][V2][Recon][Air] actor=#{armyId} phase=Turning->Return pivot step taken");
            ReconPatrolStateRegistry.MarkProgress(player, armyId, ctx.TurnNumber);
            control.StopReason = ExecutionStopReason.StepCompleted;
        }

        private static void FinishActorStep(PlayerSetupData player, int armyId,
            ExecutionResult perMissionResult, ActorStepControl control,
            ExecutionStopReason stop)
        {
            ArmyData settled = AiV2Util.ResolveArmy(player, armyId);
            if (settled == null)
            {
                ReconPatrolStateRegistry.Retire(player, armyId, "air mover lost / invalid");
                ReconAirSortieRegistry.Retire(player, armyId);
                AirSortieRegistry.Remove(player, armyId);
                stop = ExecutionStopReason.MoverLost;
                control.StopReason = stop;
            }
            if (!control.MovedAny && settled != null
                && AviationRules.IsOwnedAirfieldAt(settled.Hex, player))
                ReconAirSortieRegistry.Retire(player, armyId);

            if (perMissionResult != null)
            {
                perMissionResult.FinalHex = settled?.Hex ?? perMissionResult.FinalHex;
                perMissionResult.StopReason = stop;
                perMissionResult.StateVersionAfter = WorldDeltaLifecycle.Current;
            }
        }

        // §46 — EXECUTION of an opportunistic air strike the director already judged legal and
        // safe. The executor re-guards CanStrikeAtCurrentHex (live), resolves the AviationActions
        // call, refreshes intel, then hands the post-strike phase decision back to the director.
        private static IEnumerator ExecuteOpportunisticStrike(PlayerSetupData player, AiTurnContext ctx,
            ArmyData air, ReconAirSortieState sortie, AirReconExecutionResult passResult,
            ExecutionResult perMissionResult = null, Action<bool> onResolved = null)
        {
            if (air == null || ctx == null || !AviationRules.IsValidAirArmy(air))
                yield break;

            if (!AirReconStepDirector.CanOpportunisticallyStrike(player, ctx, air))
                yield break;

            AviationCombatPresenter presenter = ctx.HexSelection?.AviationCombatPresenter;
            if (presenter == null)
                yield break;

            AiDebugLog.Write($"[AI][V2][Recon][Air][Opportunity] actor=#{air.Id} hex=({air.Hex.Q},{air.Hex.R}) "
                + "decision=STRIKE reason=legal_and_recoverable");
            HashSet<int> enemyBefore = AiV2Util.KnownArmyIds(AiMapMemory.AllKnownEnemySightings(player));
            HashSet<int> neutralBefore = AiV2Util.KnownArmyIds(AiMapMemory.AllKnownNeutralSightings(player));
            var strike = new AviationCombatPresenter.AirStrikeResult();
            yield return AviationActions.ResolveStationaryStrike(presenter, air, strike);
            onResolved?.Invoke(strike.Attacked);

            if (strike.Attacked)
            {
                WorldDeltaLifecycle.CommitMutation();
                passResult.RecordStrike();
                if (perMissionResult != null)
                    perMissionResult.CombatChanged = true;
            }

            ArmyData afterStrike = AiV2Util.ResolveArmy(player, air.Id);
            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            RecordDiscoveries(player, ctx.TurnNumber, air.Id, enemyBefore, neutralBefore);
            AirReconStepDirector.ResolveAfterStrike(player, ctx, afterStrike, sortie, strike.Attacked);
        }

        private static IEnumerator MoveOne(PlayerSetupData player, AiTurnContext ctx, ArmyData air,
            HexCoord next, string reason, Action onMoved, ExecutionResult perMissionResult = null)
        {
            HexCoord before = air.Hex;
            bool visitedBefore = VisionSystem.IsVisited(player, next);
            HashSet<int> enemyBefore = AiV2Util.KnownArmyIds(AiMapMemory.AllKnownEnemySightings(player));
            HashSet<int> neutralBefore = AiV2Util.KnownArmyIds(AiMapMemory.AllKnownNeutralSightings(player));
            var decision = AiDecision.Move(air, next, reason, 0f);
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

            ArmyData live = AiV2Util.ResolveArmy(player, air.Id);
            HexCoord after = live != null ? live.Hex : trace.EndHex;
            if (perMissionResult != null) perMissionResult.FinalHex = after;
            if (!after.Equals(before))
                onMoved?.Invoke();

            AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
            RecordDiscoveries(player, ctx.TurnNumber, air.Id, enemyBefore, neutralBefore);
            LogVisitedInvariant(player, next, visitedBefore, "live-step");
            if (!after.Equals(before))
                StampObservedFootprint(player, ctx, air, after);
            if (AiDebugLog.IsVerbose(AiVerboseArea.Aviation))
                AiDebugLog.Write($"[AI][V2][Recon][Air][Observe] actor=#{air.Id} "
                    + $"({before.Q},{before.R})->({after.Q},{after.R}) intel refreshed; groundVisitedWrite=0");
        }

        private static AirSortie EnsureAirReconReservation(PlayerSetupData player, ArmyData air,
            HexCoord landing, bool outbound, HexCoord? target = null)
        {
            AirSortie task = AirSortieRegistry.ForArmy(player, air);
            if (task == null)
            {
                task = new AirSortie { Kind = AirSortieKind.Recon, Army = air };
                AirSortieRegistry.Add(player, task);
            }
            if (task.Kind != AirSortieKind.Recon)
                return null;

            task.Outbound = outbound;
            task.LandingHex = landing;
            task.TargetHex = target ?? landing;
            return task;
        }

        private static void RemoveAirReconReservation(PlayerSetupData player, ArmyData air)
        {
            AirSortie task = air != null ? AirSortieRegistry.ForArmy(player, air) : null;
            if (task == null || task.Kind != AirSortieKind.Recon)
                return;
            AirSortieRegistry.Remove(player, task);
        }

        private static void LogVisitedInvariant(PlayerSetupData player, HexCoord hex, bool visitedBefore, string phase)
        {
            bool visitedAfter = VisionSystem.IsVisited(player, hex);
            if (!visitedBefore && visitedAfter)
                AiDebugLog.Write($"[AI][V2][Recon][Air][INVARIANT-FAIL] phase={phase} aircraft unexpectedly "
                    + $"marked ground Visited at ({hex.Q},{hex.R})");
        }

        // AI-AIR-01 §5 — record the whole on-map vision footprint of a completed air step as
        // recently-air-observed, tagged with this sortie.
        private static void StampObservedFootprint(PlayerSetupData player, AiTurnContext ctx,
            ArmyData air, HexCoord center)
        {
            int sortieId = ReconAirSortieRegistry.TryGet(player, air.Id, out ReconAirSortieState st)
                ? st.SortieId : -1;
            int vision = (ctx?.GameConfig != null ? ctx.GameConfig.armyVisionRadius : 0)
                + AbilityParams.GetBestRecceRadius(air);
            foreach (HexCoord h in HexGridMath.HexesInRange(center, Math.Max(0, vision)))
                if (ctx?.Map != null && ctx.Map.TryGetTerrainAt(h, out _))
                    AirReconCoverageRegistry.Record(player, h, ctx.TurnNumber, sortieId);
        }

        private static void RecordDiscoveries(PlayerSetupData player, int turn, int actorId,
            HashSet<int> enemyBefore, HashSet<int> neutralBefore)
        {
            int[] enemies = AiV2Util.KnownArmyIds(AiMapMemory.AllKnownEnemySightings(player))
                .Where(id => enemyBefore == null || !enemyBefore.Contains(id)).ToArray();
            int[] neutrals = AiV2Util.KnownArmyIds(AiMapMemory.AllKnownNeutralSightings(player))
                .Where(id => neutralBefore == null || !neutralBefore.Contains(id)).ToArray();
            if (enemies.Length > 0)
            {
                StrategicInterruptRegistry.MarkDiscovery(player, turn, enemies);
                AiDebugLog.Write($"[AI][V2][Recon][Air][Discovery] actor=#{actorId} "
                    + $"enemy=[{string.Join(",", enemies)}]");
            }
            if (neutrals.Length > 0)
            {
                StrategicInterruptRegistry.MarkDiscovery(player, turn, neutrals);
                AiDebugLog.Write($"[AI][V2][Recon][Air][Discovery] actor=#{actorId} "
                    + $"neutral=[{string.Join(",", neutrals)}]");
            }
        }
    }
}


