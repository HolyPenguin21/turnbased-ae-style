using System;
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
    // flags only; the loop is its single transition owner (not yet the single writer of every field:
    // the work bodies write settled steps and deferred returns, and share no progress and the
    // residual window with the loop).
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

    // The existing work bodies of the turn, owned by Pipeline.RunTurn. The loop only orders them.
    internal struct TurnLoopWork
    {
        // Pass-scoped resets that live with the admission body (retry set, frame-pacing clock).
        internal Action OpenPass;
        // One admission iteration; reports true when the pass must stop.
        internal Func<Action<bool>, IEnumerator> Iteration;
        // The deferred-axes force admission that ends every pass.
        internal Func<IEnumerator> TerminalForce;
        // The settlement window before the first Phase B round (income cover, continuation window).
        internal Action FirstPhaseBSettle;
        // One Phase B round (its index, outcome).
        internal Func<int, Action<TempoRoundOutcome>, IEnumerator> TempoRound;
        // Number of zero-Radar axes (read when the cold stage is reached).
        internal Func<int> ColdAxisCount;
        // The cold residual body; reports whether it changed state.
        internal Func<Action<bool>, IEnumerator> Cold;
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

        internal static IEnumerator Run(TurnLoopState s, TurnLoopWork work)
        {
            OpenPass(s, work, AdmissionCause.Initial);
            while (true)
            {
                switch (Phase(s))
                {
                    case TurnPhase.Ordinary:
                    {
                        bool stop = false;
                        yield return work.Iteration(v => stop = v);
                        if (stop)
                            yield return ClosePass(s, work);
                        break;
                    }
                    case TurnPhase.CloseOrdinary:
                        yield return ClosePass(s, work);
                        break;
                    case TurnPhase.Tempo:
                    {
                        // First Phase B: continuing Hard operations and deferred builds had their
                        // chance in the first pass; settle what they keep before Phase B spends.
                        if (s.PhaseBRounds == 0)
                            work.FirstPhaseBSettle();
                        TempoRoundOutcome round = default;
                        yield return work.TempoRound(s.PhaseBRounds, o => round = o);
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
                            OpenPass(s, work, verdict.Cause);
                        if (verdict.Closed)
                            s.Stage = TurnStage.Cold;
                        break;
                    }
                    case TurnPhase.Cold:
                        if (ColdEligible(s, work.ColdAxisCount()))
                        {
                            bool changed = false;
                            yield return work.Cold(v => changed = v);
                            if (changed)
                                OpenPass(s, work, AdmissionCause.ColdChanged);
                        }
                        s.Stage = TurnStage.Done;
                        break;
                    default:
                        yield break;
                }
            }
        }

        private static void OpenPass(TurnLoopState s, TurnLoopWork work, AdmissionCause cause)
        {
            s.ResidualWindow = false;
            s.PassOpen = true;
            s.PassCause = cause;
            AiDebugLog.Write("[AI][V2][Loop] begin — typed operational admission");
            AiDebugLog.Write($"[AI][V2][Loop] admission pass opened — cause={cause}");
            work.OpenPass();
        }

        private static IEnumerator ClosePass(TurnLoopState s, TurnLoopWork work)
        {
            if (s.SettledSteps >= AiConfigV2.maxMidTurnStepsPerTurn)
                AiDebugLog.Write($"[AI][V2][Loop] bounded stop — max steps "
                    + $"{AiConfigV2.maxMidTurnStepsPerTurn}");
            if (s.NoProgressCycles >= AiConfigV2.maxMidTurnNoProgressCycles)
                AiDebugLog.Write($"[AI][V2][Loop] bounded stop — no progress cycles "
                    + $"{s.NoProgressCycles}");
            // Axes still waiting for aviation must not be lost when the pass ends first.
            yield return work.TerminalForce();
            s.PassOpen = false;
        }
    }
}
