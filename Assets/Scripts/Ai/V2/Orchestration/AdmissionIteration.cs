using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  The operational admission of the AI turn: what runs inside an open admission pass (one
    //  iteration = settled world -> missions -> pack -> exactly one work step) and the terminal
    //  force admission that ends every pass. TurnLoop decides WHEN; this component decides WHAT.
    //  Dependencies are explicit; the pass-scoped state (the parking of rejected jobs, the frame
    //  pacing clock) lives here and is reset by OpenPass. The work step keeps its own order of
    //  observation, ledger, settlement and trigger resolution (they differ by kind of work).
    // ===========================================================================================
    internal sealed class AdmissionIteration : IAdmissionWork
    {
        // Perf: an AI turn can run dozens of settled steps back-to-back with no other yield in
        // between, so the whole turn could land in one single-frame hitch. Give a real frame back to
        // the engine whenever the wall-clock budget since the last frame is exceeded.
        private const float YieldBudgetSeconds = 0.008f;

        private readonly DecisionFrame _frame;
        private readonly AiTurnSession _session;
        private readonly StrategicReadmissionRunner _runner;
        private readonly TurnTelemetry _telemetry;
        private readonly DesireBreakdown _breakdown;
        private readonly Radar _radar;
        private readonly V2TraceScope _trace;
        private readonly PlayerSetupData _player;
        private readonly PlayerRoot _root;
        private readonly AiHandData _hand;
        private readonly AiTurnContext _ctx;

        // Jobs the provisioning of this pass found out of the running for the rest of the turn.
        private PassParking _passParking;
        private float _lastYieldTime;

        internal AdmissionIteration(DecisionFrame frame, AiTurnSession session,
            StrategicReadmissionRunner runner, TurnTelemetry telemetry, DesireBreakdown breakdown,
            Radar radar, V2TraceScope trace, PlayerSetupData player, PlayerRoot root, AiHandData hand,
            AiTurnContext ctx)
        {
            _frame = frame;
            _session = session;
            _runner = runner;
            _telemetry = telemetry;
            _breakdown = breakdown;
            _radar = radar;
            _trace = trace;
            _player = player;
            _root = root;
            _hand = hand;
            _ctx = ctx;
        }

        // The pass-scoped resets of an admission pass (TurnLoop opens every pass).
        public void OpenPass()
        {
            _passParking = new PassParking();
            _lastYieldTime = UnityEngine.Time.realtimeSinceStartup;
        }

        // Every admission pass ends with this: axes still waiting for aviation must not be lost
        // when the pass ends first.
        public IEnumerator TerminalForce() =>
            _runner.Run(ReadmissionCause.TerminalForce, StrategicInvalidationReason.None, null);

        // A mandatory aviation obligation as a work step. Execution stays with the existing
        // owners (MandatoryAviationStep). No ledger, Settle or observer boundary: the obligation
        // was paid when the sortie launched.
        private IEnumerator RunMandatoryAviationStep(MandatoryAviationKind kind, ArmyData actor,
            TurnLoopView view, AdmissionIterationOutcome outcome)
        {
            string label = MandatoryAviationOrder.Label(kind);
            WorldAnalysis.StepObservationStamp beforeAviation =
                WorldAnalysis.CaptureStepObservation(_root, _hand, _frame.Snapshot);
            bool actionChanged = false;
            yield return MandatoryAviationStep.Execute(kind, actor, _player, _root, _ctx,
                _frame.Snapshot, v => actionChanged = v);
            _frame.ObserveSettled(beforeAviation, null);
            // The step is counted by TurnLoop when this iteration returns; its number is
            // already known from the view.
            int stepNumber = view.SettledSteps + 1;
            ReservationInvariants.CheckBoundary(_player, _root, _ctx,
                $"step {stepNumber} {label} #{actor.Id}");
            var triggers = new StepTriggerSink();
            yield return StepTriggerSequence.Run(MandatoryAviationOrder.TriggerPairs(kind), _session, _frame, _runner, triggers);
            StepTriggerOutcome stepTriggers = triggers.Outcome;
            bool progress = stepTriggers.Progressed(actionChanged);
            outcome.SettledStep(progress);
            AiDebugLog.Write($"[AI][V2][Loop] step={stepNumber} {label} actor=#{actor.Id} "
                + $"progress={(progress ? 1 : 0)} "
                + $"operationalTriggers={stepTriggers.Operational} "
                + $"strategicTriggers={stepTriggers.Strategic}");
            // Recon audit B1 — an obligation that did not progress is skipped for the rest of
            // this turn (the owner of the obligations records it); it must not stop every
            // mission's admission with it.
            if (AviationObligations.RecordSettledStep(_player, _ctx, actor.Id, progress))
                AiDebugLog.Write("[AI][V2][Loop] "
                    + MandatoryAviationOrder.StallMessage(kind, actor.Id));
        }



        // One admission iteration of the open pass: settled world -> missions -> pack -> one work
        // step. Reports its ending in the outcome (TurnLoop applies it): a settled step, or a stop
        // (no funded mission, no provisioned task, or a settled task without a typed invalidation).
        public IEnumerator Iteration(TurnLoopView view, AdmissionIterationOutcome outcome)
        {
            List<MissionProposal> missions;
            TentativeAllocation allocation;
            if (UnityEngine.Time.realtimeSinceStartup - _lastYieldTime >= YieldBudgetSeconds)
            {
                yield return null;
                _lastYieldTime = UnityEngine.Time.realtimeSinceStartup;
            }
            // The last aviation obligation may have settled (or stalled) without a typed
            // trigger: admit the axes that waited for it before this admission.
            yield return _runner.Run(ReadmissionCause.DeferredFlush, StrategicInvalidationReason.None, null);
            // Every admission reads a settled world. Strategic observations are refreshed
            // here. The radar frame stays stable for this turn; typed Development facts
            // re-enter the existing manager immediately after the settled task boundary.
            yield return _frame.PrepareAdmission();
            // Demand families persist across settled admissions. Only
            // ReenterStrategicAxes replaces dirty families after a factual invalidation.

            // The proposals of this admission, valued (Missions).
            MissionPortfolioResult portfolio = MissionPortfolio.Build(_frame.Snapshot,
                _breakdown, _frame.Intents, _frame.Recon, _frame.Aggression,
                _radar, _frame.Demands, _trace, _ctx);
            missions = portfolio.Missions;
            Dictionary<MissionIntentKey, string> missionDeferrals = portfolio.Deferrals;
            // Return legs wait for the first Phase B round (Continuity decides which, records
            // the wait and protects it from the stall counter); the loop only says whether the
            // wait is still allowed.
            if (view.ReturnsMayWait)
            {
                ReturnDeferral returns = MissionContinuityLayer.DeferReturnsBeforeTempo(
                    _frame.Snapshot, _player, _ctx.TurnNumber, missions, _frame.Intents);
                if (returns.Waiting.Count > 0)
                {
                    outcome.DeferReturns();
                    foreach (KeyValuePair<MissionIntentKey, string> wait in returns.Deferrals)
                        missionDeferrals[wait.Key] = wait.Value;
                    missions = returns.Retained;
                }
            }
            PassParkingResult parked = _passParking.Filter(missions, _frame.Snapshot, _player);
            foreach (KeyValuePair<MissionIntentKey, string> parkedLeg in parked.Deferrals)
                missionDeferrals[parkedLeg.Key] = parkedLeg.Value;
            missions = parked.Retained;
            _telemetry.Missions = missions;
            List<Commitment> cycleCommitments =
                MissionContinuityLayer.BindFunding(_frame.Intents, missions, _frame.Snapshot,
                    missionDeferrals);
            var cycleLedger = new MissionOutcomeLedger();
            cycleLedger.RegisterProposals(missions);
            cycleLedger.RegisterCommitments(cycleCommitments);

            AllocationSession cycleSession = ResourceAllocator.BeginTurn(_frame.Snapshot, _radar,
                missions, cycleCommitments, _player);
            using var cycleProvisioning = new ProvisioningSession(_frame.Snapshot, _session);
            allocation = cycleSession.Pack();
            _telemetry.Allocation = allocation;
            _telemetry.RecordFunded(allocation);

            // Airborne obligations are settled before discretionary mission progress: a
            // multi-turn rebase already committed to landing, and a recovery whose wing
            // must return. Both are already paid (no card was played while one was pending,
            // see AviationObligations), so they are chosen here, not funded. Exactly one
            // action is executed, observed and re-admitted per iteration.
            (MandatoryAviationKind Kind, ArmyData Actor) mandatory = MandatoryAviationOrder.Next(
                AviationRebasePlanner.FindMandatoryContinuations(_player, _ctx.TurnNumber),
                ReconAirExecutor.FindMandatoryRecoveryActors(_player, _ctx));
            OperationalWorkKind work = OperationalWorkSelection.Select(
                mandatory.Kind, allocation.Funded.Count);
            if (work == OperationalWorkKind.MandatoryAviation)
            {
                yield return RunMandatoryAviationStep(mandatory.Kind, mandatory.Actor, view, outcome);
                yield break;
            }
            if (work == OperationalWorkKind.None)
            {
                outcome.NoFundedMission();
                AiDebugLog.Write("[AI][V2][Loop] stop — no funded typed mission");
                yield break;
            }

            // Which funded mission becomes executable: the retry / reprice protocol and the
            // parking of rejected jobs belong to Provisioning (ProvisionNext).
            ProvisioningSelectionOutcome pick = ProvisioningManager.ProvisionNext(_player, _root,
                _hand, _ctx, _frame.Snapshot, _frame.Commitments, cycleSession, cycleProvisioning,
                _passParking, allocation);
            allocation = pick.FinalAllocation;
            _telemetry.Allocation = allocation;
            foreach (StableMissionKey fundedKey in pick.FundedKeysAcrossPacks)
                _telemetry.FundedKeys.Add(fundedKey);
            // The attempts reach the mission ledger and the turn telemetry in the order they
            // happened, before the step executes (nothing in between reads the ledger).
            foreach (ProvisionEvent attempt in pick.Events)
            {
                cycleLedger.RecordProvisionAttempt(attempt);
                if (attempt.Kind == ProvisionEventKind.Success)
                {
                    _telemetry.Provisioned.Add(attempt.Provisioned);
                    continue;
                }
                _telemetry.ProvisioningFailures.TryGetValue(attempt.Failure.Kind, out int failureCount);
                _telemetry.ProvisioningFailures[attempt.Failure.Kind] = failureCount + 1;
            }
            ProvisionedMission selected = pick.Selected;
            bool selectedIsCommitment = pick.SelectedIsCommitment;
            StableMissionKey selectedKey = pick.SelectedKey;
            HashSet<StableMissionKey> attemptedKeys = pick.AttemptedKeys;

            if (selected == null)
            {
                cycleLedger.RecordDeferrals(allocation.Deferred);
                _session.SettleStep(cycleLedger.FinalizeSteps(), attemptedKeys,
                    _frame.Snapshot, _frame.Recon);
                // A rejected positive or durable mission must not be mistaken for
                // an exhausted portfolio; zero-only rejections leave a residual window.
                outcome.NoProvisionedTask(
                    ResidualWindowPolicy.AfterNoProvisionedTask(allocation.Funded));
                AiDebugLog.Write($"[AI][V2][Loop] admission stopped — no provisioned task; "
                    + $"noProgress={outcome.NoProgressAfter(view.NoProgressCycles)}");
                // No task command ran and no observation can differ. Repeating the same
                // admission under a fresh session only reproduces the same rejection; stop
                // this family without consuming the real bounded task-step budget.
                yield break;
            }

            WorldAnalysis.StepObservationStamp beforeStep =
                WorldAnalysis.CaptureStepObservation(_root, _hand, _frame.Snapshot);
            var stepResults = new List<ExecutionResult>();
            if (OperationalWorkSelection.RouteFor(selected.Kind, selected.ExecutorKind)
                == MissionRoute.AirRecon)
            {
                AirReconPlan plan = AirReconPlanner.Plan(_player, _root, _ctx,
                    _frame.Snapshot, new[] { selected });
                var airStepResult = new AirReconExecutionResult();
                yield return ReconAirExecutor.ExecutePlanStep(plan, _player, _root, _ctx,
                    _frame.Snapshot, airStepResult, stepResults);
            }
            else
            {
                yield return TaskExecutor.ExecuteStep(_player, _root, _ctx,
                    selected, stepResults, _frame.Snapshot, enforceFreshPlan: true);
            }

            ExecutionResult settled = stepResults.FirstOrDefault();
            _frame.ObserveSettled(beforeStep, settled);

            foreach (ExecutionResult er in stepResults)
            {
                cycleLedger.RecordExecution(er);
                _telemetry.AllExecuted.Add(er);
                ApBudgetTelemetry.RecordStep(_player, _ctx.TurnNumber, selected.Mission,
                    selectedIsCommitment, er.ApSpent);
            }
            cycleLedger.RecordDeferrals(allocation.Deferred);
            cycleLedger.RefreshObjectiveStatesLive(_player);
            _session.SettleStep(cycleLedger.FinalizeSteps(), attemptedKeys,
                _frame.Snapshot, _frame.Recon);
            // A single atomic move may consume the last MP after Provisioning had
            // legitimately reserved this owner's completion AP: the bank settles its stage.
            StrategicManager.AfterMissionSettlement(_player, _root, _hand, _ctx);

            int taskStepNumber = view.SettledSteps + 1;
            ReservationInvariants.CheckBoundary(_player, _root, _ctx,
                $"step {taskStepNumber} task={selectedKey}");
            // Snapshot, mission ledger and reservation reconciliation now all describe
            // the completed command; inspection never sees a half-settled action.
            yield return _ctx.WaitAtObserverActionBoundary();
            var triggers = new StepTriggerSink();
            yield return StepTriggerSequence.Run(StepTriggerSequence.StandardPairs, _session, _frame, _runner, triggers);
            StepTriggerOutcome stepTriggers = triggers.Outcome;
            StrategicInvalidationReason operationalReasons = stepTriggers.Operational;
            StrategicInvalidationReason strategicReasons = stepTriggers.Strategic;
            bool strategicChanged = stepTriggers.StrategicChanged;
            bool progressed = stepTriggers.Progressed(stepResults.Any(er =>
                er != null && er.Outcome.StateChanged));
            outcome.SettledStep(progressed);
            AiDebugLog.Write($"[AI][V2][Loop] step={taskStepNumber} task={selectedKey} "
                + $"progress={(progressed ? 1 : 0)} stop={settled?.StopReason} "
                + $"operationalTriggers={operationalReasons} strategicTriggers={strategicReasons} "
                + $"noProgress={outcome.NoProgressAfter(view.NoProgressCycles)}");
            if (operationalReasons == StrategicInvalidationReason.None && !strategicChanged)
            {
                // Ignore the task that JUST executed: only unfinished positive
                // allocations should prevent residual admission.
                outcome.StopAfterSettledStep(ResidualWindowPolicy.AfterSettledTask(
                    allocation.Funded, selectedKey));
                AiDebugLog.Write("[AI][V2][Loop] stop — settled task produced no typed invalidation");
            }
        }


    }
}
