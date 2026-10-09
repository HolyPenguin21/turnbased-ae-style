using System.Collections;

namespace Game.Ai.V2
{
    // Which work the turn loop runs next (TurnLoop.Phase). Ordinary is one admission iteration of an
    // open pass; CloseOrdinary ends that pass; Tempo is one Phase B round; Cold the zero-Radar
    // residual; Stop leaves the loop for the final safety net and turn end.
    public enum TurnPhase { Ordinary, CloseOrdinary, Tempo, Cold, Stop }

    // The turn's phase order after the first admission pass: Phase B rounds, then the cold residual
    // once, then the end. Only moves forward.
    public enum TurnStage { Tempo, Cold, Done }

    // Why an operational admission pass was opened. Diagnostics and tests only: no decision reads it.
    public enum AdmissionCause { None, Initial, PhaseBTrigger, PhaseBStateChanged, ReturnsReleased, ColdChanged }

    // What one Phase B round reported after its own trigger resolution.
    internal readonly struct TempoRoundOutcome
    {
        internal readonly bool OperationalDirty;
        internal readonly bool StrategicDirty;
        internal readonly bool StrategicChanged;
        internal readonly bool StateChanged;

        internal TempoRoundOutcome(bool operationalDirty, bool strategicDirty, bool strategicChanged,
            bool stateChanged)
        {
            OperationalDirty = operationalDirty;
            StrategicDirty = strategicDirty;
            StrategicChanged = strategicChanged;
            StateChanged = stateChanged;
        }
    }

    // What follows a Phase B round. Replaces the management loop's three mutually exclusive
    // re-admission branches, their no-progress resets and its two exits:
    //   operational trigger                       -> admission (PhaseBTrigger)
    //   else the round changed hand/world         -> admission (PhaseBStateChanged)
    //   else return legs waited for this round    -> admission (ReturnsReleased)
    //   no-progress resets on any admission or a strategic change;
    //   the tempo stage closes when the round changed nothing strategic, published nothing new,
    //   or the round cap (maxEndOfTurnTempoReruns + 1 rounds) is reached.
    internal readonly struct TempoRoundVerdict
    {
        internal readonly AdmissionCause Cause;
        internal readonly bool ResetNoProgress;
        internal readonly bool Closed;

        private TempoRoundVerdict(AdmissionCause cause, bool resetNoProgress, bool closed)
        {
            Cause = cause;
            ResetNoProgress = resetNoProgress;
            Closed = closed;
        }

        internal static TempoRoundVerdict Decide(TempoRoundOutcome round, bool releaseReturnsNow,
            int roundsDone, int maxReruns)
        {
            AdmissionCause cause = round.OperationalDirty ? AdmissionCause.PhaseBTrigger
                : round.StateChanged ? AdmissionCause.PhaseBStateChanged
                : releaseReturnsNow ? AdmissionCause.ReturnsReleased
                : AdmissionCause.None;
            bool reset = round.OperationalDirty || round.StrategicChanged || cause != AdmissionCause.None;
            bool closed = (!round.StateChanged && !round.StrategicChanged)
                || (!round.OperationalDirty && !round.StrategicDirty)
                || roundsDone > maxReruns;
            return new TempoRoundVerdict(cause, reset, closed);
        }
    }

    // The turn loop's control state (previously closure locals of Pipeline.RunTurn). Counters and
    // flags only. TurnLoop is its ONLY writer: the work bodies get a read-only TurnLoopView and
    // report what happened in an AdmissionIterationOutcome, which TurnLoop applies after the
    // iteration (AiTurnLoopTests also scans the sources for writes outside this file).
    internal sealed class TurnLoopState
    {
        internal int SettledSteps;
        internal int NoProgressCycles;
        // The zero-Radar residual window: the verdict of the LAST pass; reset when a pass opens.
        internal bool ResidualWindow;
        // A return leg waited for the first Phase B round in this turn.
        internal bool ReturnsDeferred;
        // Phase B rounds settled so far this turn.
        internal int PhaseBRounds;
        internal TurnStage Stage = TurnStage.Tempo;
        internal bool PassOpen;
        internal AdmissionCause PassCause;

        // Return legs may still wait (LifecycleReturnPolicy): only before the first Phase B round
        // has settled. Replaces the former `lifecycleReturnsReleased` flag (it was set exactly when
        // the first round settled and never reset).
        internal bool ReturnsMayWait => PhaseBRounds == 0;

        internal bool WithinStepBounds =>
            SettledSteps < AiConfigV2.maxMidTurnStepsPerTurn
            && NoProgressCycles < AiConfigV2.maxMidTurnNoProgressCycles;
    }

    // The read-only view of the control state an admission iteration is given. A value copy taken
    // when the iteration starts: counters cannot change under the iteration (only TurnLoop writes
    // them, after the iteration returns), so a body that needs "the step number" or "no-progress
    // after this step" for a boundary or a log line computes it from the view and the outcome.
    internal readonly struct TurnLoopView
    {
        internal readonly int SettledSteps;
        internal readonly int NoProgressCycles;
        internal readonly bool ReturnsMayWait;
        internal readonly bool WithinStepBounds;

        internal TurnLoopView(TurnLoopState s)
        {
            SettledSteps = s.SettledSteps;
            NoProgressCycles = s.NoProgressCycles;
            ReturnsMayWait = s.ReturnsMayWait;
            WithinStepBounds = s.WithinStepBounds;
        }
    }

    internal enum ProgressUpdate
    {
        Unchanged,   // no work step ran and none is counted (no funded mission)
        Increment,   // no task could be provisioned: one cycle without progress
        FromStep,    // a settled step: StepTriggerOutcome.NextNoProgress(current, Progressed)
    }

    // The result of ONE admission iteration, filled by its body and applied by TurnLoop once the
    // iteration returned. The variants are the iteration's possible endings, not modes of a generic
    // completion: settled step (aviation or mission), no funded mission, no provisioned task,
    // settled task without a typed invalidation; plus "a return leg waited".
    internal sealed class AdmissionIterationOutcome
    {
        internal int SettledStepDelta { get; private set; }
        internal ProgressUpdate Progress { get; private set; }
        internal bool Progressed { get; private set; }
        internal bool StopPass { get; private set; }
        internal bool? ResidualVerdict { get; private set; }
        internal bool ReturnsDeferred { get; private set; }

        // A return leg waited for the first Phase B round in this iteration.
        internal void DeferReturns() => ReturnsDeferred = true;

        // A settled work step (mandatory aviation or mission): counts as one step.
        internal void SettledStep(bool progressed)
        {
            SettledStepDelta = 1;
            Progress = ProgressUpdate.FromStep;
            Progressed = progressed;
        }

        // The portfolio has no funded mission: stop, the residual window opens.
        internal void NoFundedMission()
        {
            ResidualVerdict = true;
            StopPass = true;
        }

        // No task could be provisioned: no step, one cycle without progress, stop.
        internal void NoProvisionedTask(bool residualWindow)
        {
            Progress = ProgressUpdate.Increment;
            ResidualVerdict = residualWindow;
            StopPass = true;
        }

        // The settled mission produced no typed invalidation: stop (the step itself is recorded
        // with SettledStep).
        internal void StopAfterSettledStep(bool residualWindow)
        {
            ResidualVerdict = residualWindow;
            StopPass = true;
        }

        // The no-progress counter after this iteration, from the counter before it. The only place
        // this arithmetic lives: TurnLoop applies it and bodies use it for their log lines.
        internal int NoProgressAfter(int current) =>
            Progress == ProgressUpdate.Increment ? current + 1
            : Progress == ProgressUpdate.FromStep ? StepTriggerOutcome.NextNoProgress(current, Progressed)
            : current;
    }

    // The works the loop orders. Each is a component with explicit dependencies (AdmissionIteration,
    // TempoRound, ColdResidual); the loop only decides which one runs next and applies their outcomes.
    // The interfaces exist so the loop can be driven by scripted works in tests.
    internal interface IAdmissionWork
    {
        // Pass-scoped resets (the parking of rejected jobs, the frame-pacing clock).
        void OpenPass();
        // One admission iteration: reads the view, reports its ending in the outcome.
        IEnumerator Iteration(TurnLoopView view, AdmissionIterationOutcome outcome);
        // The deferred-axes force admission that ends every pass.
        IEnumerator TerminalForce();
    }

    internal interface ITempoWork
    {
        // The settlement window before the first Phase B round (income cover, continuation window).
        void SettleBeforeFirstRound();
        // One Phase B round: its index; the outcome goes to the sink.
        IEnumerator Round(int index, TempoRoundSink sink);
    }

    internal interface IColdWork
    {
        // Number of zero-Radar axes (read when the cold stage is reached).
        int AxisCount();
        // The cold residual body; the sink says whether it changed state.
        IEnumerator Run(ColdSink sink);
    }

    internal sealed class TempoRoundSink
    {
        internal TempoRoundOutcome Outcome;
    }

    internal sealed class ColdSink
    {
        internal bool Changed;
    }

    // ===========================================================================================
    //  The ONE main loop of the AI turn (level 4). It replaces the operational `while` inside the
    //  former RunTypedAdmissions, the management `for` around Phase B and the cold branch's own
    //  entry. Every operational admission pass is opened here — at the start, after a Phase B
    //  round that asks for it, after a cold residual that changed state — and an open pass always
    //  runs before the next Phase B round or the cold stage, exactly where the former nested call
    //  ran. Work bodies, their observation/settle order and their trigger resolution are unchanged
    //  and stay with Pipeline.RunTurn; this type never scores, prices, funds or executes.
    // ===========================================================================================
    internal static class TurnLoop
    {
        internal static TurnPhase Phase(TurnLoopState s) =>
            s.PassOpen ? (s.WithinStepBounds ? TurnPhase.Ordinary : TurnPhase.CloseOrdinary)
            : s.Stage == TurnStage.Tempo ? TurnPhase.Tempo
            : s.Stage == TurnStage.Cold ? TurnPhase.Cold
            : TurnPhase.Stop;

        // A zero Radar is not a prohibition: cold preparation may use what ordinary work and the
        // tempo rounds left, once, if the last pass left the residual window open and the bounds hold.
        internal static bool ColdEligible(TurnLoopState s, int coldAxisCount) =>
            s.ResidualWindow && coldAxisCount > 0 && s.WithinStepBounds;

        internal static IEnumerator Run(TurnLoopState s, IAdmissionWork admission, ITempoWork tempo, IColdWork cold)
        {
            OpenPass(s, admission, AdmissionCause.Initial);
            while (true)
            {
                switch (Phase(s))
                {
                    case TurnPhase.Ordinary:
                    {
                        var outcome = new AdmissionIterationOutcome();
                        yield return admission.Iteration(new TurnLoopView(s), outcome);
                        ApplyIterationOutcome(s, outcome);
                        if (outcome.StopPass)
                            yield return ClosePass(s, admission);
                        break;
                    }
                    case TurnPhase.CloseOrdinary:
                        yield return ClosePass(s, admission);
                        break;
                    case TurnPhase.Tempo:
                    {
                        // First Phase B: continuing Hard operations and deferred builds had their
                        // chance in the first pass; settle what they keep before Phase B spends.
                        if (s.PhaseBRounds == 0)
                            tempo.SettleBeforeFirstRound();
                        var roundSink = new TempoRoundSink();
                        yield return tempo.Round(s.PhaseBRounds, roundSink);
                        TempoRoundOutcome round = roundSink.Outcome;
                        // Phase B has spent first; return legs now take what is left.
                        bool releaseReturnsNow = s.PhaseBRounds == 0 && s.ReturnsDeferred;
                        s.PhaseBRounds++;
                        TempoRoundVerdict verdict = TempoRoundVerdict.Decide(round, releaseReturnsNow,
                            s.PhaseBRounds, AiConfigV2.maxEndOfTurnTempoReruns);
                        if (verdict.ResetNoProgress)
                            s.NoProgressCycles = 0;
                        if (verdict.Cause == AdmissionCause.ReturnsReleased)
                            AiDebugLog.Write("[AI][V2][Loop] lifecycle returns released after the tempo pass");
                        if (verdict.Cause != AdmissionCause.None)
                            OpenPass(s, admission, verdict.Cause);
                        if (verdict.Closed)
                            s.Stage = TurnStage.Cold;
                        break;
                    }
                    case TurnPhase.Cold:
                        if (ColdEligible(s, cold.AxisCount()))
                        {
                            var coldSink = new ColdSink();
                            yield return cold.Run(coldSink);
                            if (coldSink.Changed)
                                OpenPass(s, admission, AdmissionCause.ColdChanged);
                        }
                        s.Stage = TurnStage.Done;
                        break;
                    default:
                        yield break;
                }
            }
        }

        // The only write of the iteration's counters. Independent fields, so the order is free.
        internal static void ApplyIterationOutcome(TurnLoopState s, AdmissionIterationOutcome o)
        {
            s.SettledSteps += o.SettledStepDelta;
            s.NoProgressCycles = o.NoProgressAfter(s.NoProgressCycles);
            if (o.ResidualVerdict.HasValue)
                s.ResidualWindow = o.ResidualVerdict.Value;
            if (o.ReturnsDeferred)
                s.ReturnsDeferred = true;
        }

        private static void OpenPass(TurnLoopState s, IAdmissionWork admission, AdmissionCause cause)
        {
            s.ResidualWindow = false;
            s.PassOpen = true;
            s.PassCause = cause;
            AiDebugLog.Write("[AI][V2][Loop] begin — typed operational admission");
            AiDebugLog.Write($"[AI][V2][Loop] admission pass opened — cause={cause}");
            admission.OpenPass();
        }

        private static IEnumerator ClosePass(TurnLoopState s, IAdmissionWork admission)
        {
            if (s.SettledSteps >= AiConfigV2.maxMidTurnStepsPerTurn)
                AiDebugLog.Write($"[AI][V2][Loop] bounded stop — max steps "
                    + $"{AiConfigV2.maxMidTurnStepsPerTurn}");
            if (s.NoProgressCycles >= AiConfigV2.maxMidTurnNoProgressCycles)
                AiDebugLog.Write($"[AI][V2][Loop] bounded stop — no progress cycles "
                    + $"{s.NoProgressCycles}");
            // Axes still waiting for aviation must not be lost when the pass ends first.
            yield return admission.TerminalForce();
            s.PassOpen = false;
        }
    }
}
