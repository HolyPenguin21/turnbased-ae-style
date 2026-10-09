#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Level 4: the one main turn loop (TurnLoop.Run) against an INDEPENDENT reference — a
    // transcription of the baseline control flow of Pipeline.RunTurn @ c3cdde46 (L469-L834
    // RunTypedAdmissions, L846-L934 first pass + settle window + management `for`, L940-L990 cold
    // guard). The reference keeps the baseline's own statements (while, breaks, the three `if`, the
    // two exits, `lifecycleReturnsReleased`, `phaseBHandled`) instead of a derived formula, so a
    // mistake in the new verdict cannot be repeated by the reference. Both drive the same scripted
    // work bodies over many iterations; the traces (work order, counters at every boundary,
    // return-wait gate, pass resets, terminal force, admission cause) must be identical.
    public class AiTurnLoopTests
    {
        private const int MaxSteps = AiConfigV2.maxMidTurnStepsPerTurn;
        private const int MaxNoProgress = AiConfigV2.maxMidTurnNoProgressCycles;
        private const int MaxReruns = AiConfigV2.maxEndOfTurnTempoReruns;

        // ---- Scripted work bodies, shared by both drivers ------------------------------------

        private enum IterationKind { AviationStep, MissionStep, StopNoFunded, StopNoProvisioned, StopNoTrigger }

        private struct IterationScript
        {
            internal IterationKind Kind;
            internal bool Progress;
            internal bool Residual;
            internal bool DefersReturn;
        }

        private sealed class Scenario
        {
            internal int StartSettled;
            internal int StartNoProgress;
            internal Queue<IterationScript> Iterations = new Queue<IterationScript>();
            internal Queue<TempoRoundOutcome> Rounds = new Queue<TempoRoundOutcome>();
            internal int ColdAxes;
            internal bool ColdHasDemands;
            internal bool ColdChanged;

            internal Scenario Clone()
            {
                var c = (Scenario)MemberwiseClone();
                c.Iterations = new Queue<IterationScript>(Iterations);
                c.Rounds = new Queue<TempoRoundOutcome>(Rounds);
                return c;
            }
        }

        // The work bodies record what they observed. The baseline reference mutates the shared
        // counters itself (as the baseline bodies did: Iteration); the loop under test gets a
        // read-only view and reports an outcome (IterationOutcome) that only TurnLoop applies.
        // Both derive the same ending from the same script.
        private sealed class Works
        {
            private readonly Scenario _sc;
            private readonly TurnLoopState _st;
            private readonly Func<bool> _returnsMayWait;
            private readonly Func<AdmissionCause> _openCause;
            internal readonly List<string> Trace = new List<string>();
            private int _retrySet;

            internal Works(Scenario sc, TurnLoopState st, Func<bool> returnsMayWait, Func<AdmissionCause> openCause)
            {
                _sc = sc;
                _st = st;
                _returnsMayWait = returnsMayWait;
                _openCause = openCause;
            }

            private string Counters => $"s={_st.SettledSteps} np={_st.NoProgressCycles} zr={(_st.ResidualWindow ? 1 : 0)}";

            internal void OpenPass()
            {
                _retrySet++;
                Trace.Add($"begin cause={_openCause()} retrySet#{_retrySet} {Counters}");
            }

            // The loop under test (Level E1): no writes to the state, only an outcome.
            internal IEnumerator IterationOutcome(TurnLoopView view, AdmissionIterationOutcome outcome)
            {
                bool mayWait = view.ReturnsMayWait;
                IterationScript it = _sc.Iterations.Count > 0 ? _sc.Iterations.Dequeue()
                    : new IterationScript { Kind = IterationKind.StopNoFunded };
                if (it.DefersReturn && mayWait)
                    outcome.DeferReturns();
                switch (it.Kind)
                {
                    case IterationKind.AviationStep:
                    case IterationKind.MissionStep:
                        outcome.SettledStep(it.Progress);
                        break;
                    case IterationKind.StopNoFunded:
                        outcome.NoFundedMission();
                        break;
                    case IterationKind.StopNoProvisioned:
                        outcome.NoProvisionedTask(it.Residual);
                        break;
                    case IterationKind.StopNoTrigger:
                        outcome.SettledStep(it.Progress);
                        outcome.StopAfterSettledStep(it.Residual);
                        break;
                }
                // The line the baseline prints after its writes: computed from the view + outcome
                // (the residual window, which the view does not carry, is read from the state).
                int steps = view.SettledSteps + outcome.SettledStepDelta;
                int np = outcome.NoProgressAfter(view.NoProgressCycles);
                bool zr = outcome.ResidualVerdict ?? _st.ResidualWindow;
                bool deferred = _st.ReturnsDeferred || outcome.ReturnsDeferred;
                Trace.Add($"iter {it.Kind} mayWait={(mayWait ? 1 : 0)} deferred={(deferred ? 1 : 0)} "
                    + $"s={steps} np={np} zr={(zr ? 1 : 0)}");
                yield break;
            }

            // The baseline reference: the bodies write the shared counters themselves.
            internal IEnumerator Iteration(Action<bool> stop)
            {
                bool mayWait = _returnsMayWait();
                // An exhausted script ends the pass the way an empty portfolio does.
                IterationScript it = _sc.Iterations.Count > 0 ? _sc.Iterations.Dequeue()
                    : new IterationScript { Kind = IterationKind.StopNoFunded };
                if (it.DefersReturn && mayWait)
                    _st.ReturnsDeferred = true;
                bool halt = false;
                switch (it.Kind)
                {
                    case IterationKind.AviationStep:
                    case IterationKind.MissionStep:
                        _st.SettledSteps++;
                        _st.NoProgressCycles = StepTriggerOutcome.NextNoProgress(_st.NoProgressCycles, it.Progress);
                        break;
                    case IterationKind.StopNoFunded:
                        _st.ResidualWindow = true;
                        halt = true;
                        break;
                    case IterationKind.StopNoProvisioned:
                        _st.NoProgressCycles++;
                        _st.ResidualWindow = it.Residual;
                        halt = true;
                        break;
                    case IterationKind.StopNoTrigger:
                        _st.SettledSteps++;
                        _st.NoProgressCycles = StepTriggerOutcome.NextNoProgress(_st.NoProgressCycles, it.Progress);
                        _st.ResidualWindow = it.Residual;
                        halt = true;
                        break;
                }
                Trace.Add($"iter {it.Kind} mayWait={(mayWait ? 1 : 0)} deferred={(_st.ReturnsDeferred ? 1 : 0)} {Counters}");
                stop(halt);
                yield break;
            }

            internal IEnumerator TerminalForce()
            {
                Trace.Add($"terminalForce {Counters}"
                    + (_st.SettledSteps >= MaxSteps ? " boundedMax" : "")
                    + (_st.NoProgressCycles >= MaxNoProgress ? " boundedNoProgress" : ""));
                yield break;
            }

            internal void FirstPhaseBSettle() => Trace.Add($"settleWindow {Counters}");

            internal IEnumerator TempoRound(int index, Action<TempoRoundOutcome> done)
            {
                TempoRoundOutcome r = _sc.Rounds.Count > 0 ? _sc.Rounds.Dequeue() : default;
                Trace.Add($"round#{index + 1} op={B(r.OperationalDirty)} sd={B(r.StrategicDirty)} "
                    + $"sc={B(r.StrategicChanged)} st={B(r.StateChanged)} {Counters}");
                done(r);
                yield break;
            }

            internal int ColdAxisCount() => _sc.ColdAxes;

            internal IEnumerator Cold(Action<bool> changed)
            {
                Trace.Add($"cold demands={B(_sc.ColdHasDemands)} {Counters}");
                changed(_sc.ColdHasDemands && _sc.ColdChanged);
                yield break;
            }

            internal void End() => Trace.Add($"end {Counters} deferred={(_st.ReturnsDeferred ? 1 : 0)}");

            private static int B(bool v) => v ? 1 : 0;
        }

        // ---- The new loop -------------------------------------------------------------------

        private static List<string> RunNew(Scenario sc)
        {
            var st = new TurnLoopState { SettledSteps = sc.StartSettled, NoProgressCycles = sc.StartNoProgress };
            var w = new Works(sc, st, () => st.ReturnsMayWait, () => st.PassCause);
            Drain(TurnLoop.Run(st, new TurnLoopWork
            {
                OpenPass = w.OpenPass,
                Iteration = w.IterationOutcome,
                TerminalForce = w.TerminalForce,
                FirstPhaseBSettle = w.FirstPhaseBSettle,
                TempoRound = w.TempoRound,
                ColdAxisCount = w.ColdAxisCount,
                Cold = w.Cold,
            }));
            w.End();
            return w.Trace;
        }

        // ---- The baseline reference: Pipeline.RunTurn @ c3cdde46, control statements only ----

        private sealed class Baseline
        {
            private readonly TurnLoopState _st;   // used only as storage of the four shared counters
            private readonly Works _w;
            private bool _lifecycleReturnsReleased;   // L299
            private AdmissionCause _callSite;          // which of the five call sites (diagnostic)

            internal Baseline(Scenario sc, TurnLoopState st)
            {
                _st = st;
                _w = new Works(sc, st, () => !_lifecycleReturnsReleased, () => _callSite);
            }

            internal List<string> Trace => _w.Trace;

            // L469-L834
            private IEnumerator RunTypedAdmissions(AdmissionCause callSite)
            {
                _callSite = callSite;
                _st.ResidualWindow = false;                                        // L471
                _w.OpenPass();                                                     // L472, L491, L502
                while (_st.SettledSteps < AiConfigV2.maxMidTurnStepsPerTurn
                    && _st.NoProgressCycles < AiConfigV2.maxMidTurnNoProgressCycles)   // L504
                {
                    bool stop = false;
                    yield return _w.Iteration(v => stop = v);                    // L507-L822
                    if (stop)
                        break;                                                     // L610, L756, L821
                }
                yield return _w.TerminalForce();                                   // L825-L832
            }

            internal IEnumerator RunTurnControl(Scenario sc)
            {
                yield return RunTypedAdmissions(AdmissionCause.Initial);           // L846
                _w.FirstPhaseBSettle();                                            // L849-L856
                for (int managementRound = 0;
                     managementRound <= AiConfigV2.maxEndOfTurnTempoReruns;
                     managementRound++)                                            // L863
                {
                    TempoRoundOutcome r = default;
                    yield return _w.TempoRound(managementRound, o => r = o);      // L867-L888, L895
                    bool releaseReturnsNow = !_lifecycleReturnsReleased && _st.ReturnsDeferred;   // L890
                    _lifecycleReturnsReleased = true;                              // L891
                    bool operationalDirty = r.OperationalDirty;                    // L898
                    bool strategicDirty = r.StrategicDirty;                        // L899
                    bool strategicChanged = r.StrategicChanged;                    // L900
                    if (operationalDirty || strategicChanged)
                        _st.NoProgressCycles = 0;                                  // L901-L902
                    if (operationalDirty)                                          // L909
                    {
                        _st.NoProgressCycles = 0;
                        yield return RunTypedAdmissions(AdmissionCause.PhaseBTrigger);
                    }
                    if (r.StateChanged && !operationalDirty)                       // L918
                    {
                        _st.NoProgressCycles = 0;
                        yield return RunTypedAdmissions(AdmissionCause.PhaseBStateChanged);
                    }
                    if (releaseReturnsNow && !operationalDirty && !r.StateChanged) // L923
                    {
                        _st.NoProgressCycles = 0;
                        yield return RunTypedAdmissions(AdmissionCause.ReturnsReleased);
                    }
                    if (!r.StateChanged && !strategicChanged)                      // L929
                        break;
                    if (!operationalDirty && !strategicDirty)                      // L931
                        break;
                }
                // phaseBHandled = true (L934): the `if (!phaseBHandled)` branch below it is unreachable.
                int coldAxes = _w.ColdAxisCount();                                 // L940
                if (_st.ResidualWindow && coldAxes > 0
                    && _st.SettledSteps < AiConfigV2.maxMidTurnStepsPerTurn
                    && _st.NoProgressCycles < AiConfigV2.maxMidTurnNoProgressCycles)   // L942-L944
                {
                    bool changed = false;
                    yield return _w.Cold(v => changed = v);                        // L946-L988
                    if (changed)
                        yield return RunTypedAdmissions(AdmissionCause.ColdChanged);  // L983
                }
                _w.End();
            }
        }

        private static List<string> RunBaseline(Scenario sc)
        {
            var st = new TurnLoopState { SettledSteps = sc.StartSettled, NoProgressCycles = sc.StartNoProgress };
            var b = new Baseline(sc, st);
            Drain(b.RunTurnControl(sc));
            return b.Trace;
        }

        // ---- Scenario generation ---------------------------------------------------------------

        private static Scenario Generate(Random rng)
        {
            int[] starts = { 0, 0, 0, 1, 50, MaxSteps - 3, MaxSteps - 1, MaxSteps };
            var sc = new Scenario
            {
                StartSettled = starts[rng.Next(starts.Length)],
                StartNoProgress = rng.Next(MaxNoProgress + 1),
                ColdAxes = rng.Next(2),
                ColdHasDemands = rng.Next(4) != 0,
                ColdChanged = rng.Next(2) == 0,
            };
            int iterations = rng.Next(0, 40);
            for (int i = 0; i < iterations; i++)
            {
                int k = rng.Next(10);
                sc.Iterations.Enqueue(new IterationScript
                {
                    Kind = k < 3 ? IterationKind.MissionStep : k < 5 ? IterationKind.AviationStep
                        : k < 7 ? IterationKind.StopNoTrigger : k < 8 ? IterationKind.StopNoProvisioned
                        : IterationKind.StopNoFunded,
                    Progress = rng.Next(3) != 0,
                    Residual = rng.Next(2) == 0,
                    DefersReturn = rng.Next(5) == 0,
                });
            }
            for (int i = 0; i < MaxReruns + 1; i++)
                sc.Rounds.Enqueue(new TempoRoundOutcome(rng.Next(2) == 0, rng.Next(2) == 0,
                    rng.Next(2) == 0, rng.Next(2) == 0));
            return sc;
        }

        private static string FirstDivergence(List<string> expected, List<string> actual)
        {
            for (int i = 0; i < Math.Max(expected.Count, actual.Count); i++)
            {
                string e = i < expected.Count ? expected[i] : "<none>";
                string a = i < actual.Count ? actual[i] : "<none>";
                if (e != a)
                    return $"event {i}: baseline [{e}] vs loop [{a}]\nbaseline:\n  "
                        + string.Join("\n  ", expected) + "\nloop:\n  " + string.Join("\n  ", actual);
            }
            return null;
        }

        // ---- Tests -----------------------------------------------------------------------------

        [Test]
        public void TheLoopReproducesTheBaselineControlFlowOverManyMultiIterationTurns()
        {
            var rng = new Random(20261009);
            var kinds = new HashSet<string>();
            for (int n = 0; n < 20000; n++)
            {
                Scenario sc = Generate(rng);
                List<string> baseline = RunBaseline(sc.Clone());
                List<string> loop = RunNew(sc.Clone());
                string divergence = FirstDivergence(baseline, loop);
                Assert.IsNull(divergence, $"scenario {n}: {divergence}");
                foreach (string e in baseline)
                    kinds.Add(e.Split(' ')[0] + (e.Contains("boundedMax") ? "+max" : "")
                        + (e.Contains("boundedNoProgress") ? "+np" : "")
                        + (e.StartsWith("begin") ? e.Split(' ')[1] : ""));
            }
            // The generator must have reached every transition, including both bounds and every cause.
            foreach (string required in new[]
            {
                "begincause=Initial", "begincause=PhaseBTrigger", "begincause=PhaseBStateChanged",
                "begincause=ReturnsReleased", "begincause=ColdChanged", "terminalForce",
                "terminalForce+max", "terminalForce+np", "settleWindow", "round#1", "round#2", "cold",
                "iter", "end",
            })
                Assert.IsTrue(kinds.Contains(required), $"scenario generator never reached {required}");
        }

        // Every round-outcome combination, each through the transcribed baseline statements
        // (one round), against the verdict: the admission cause, the no-progress reset and the exit.
        [Test]
        public void TheTempoRoundVerdictMatchesTheBaselineBranchesForEveryInput()
        {
            for (int mask = 0; mask < 32; mask++)
            for (int roundsDone = 1; roundsDone <= MaxReruns + 1; roundsDone++)
            {
                bool op = (mask & 1) != 0, sd = (mask & 2) != 0, sc = (mask & 4) != 0,
                    st = (mask & 8) != 0, release = (mask & 16) != 0;

                // Transcription of L898-L932 for one round; noProgress starts at 1 to see the reset.
                int noProgress = 1;
                var admissions = new List<AdmissionCause>();
                if (op || sc) noProgress = 0;
                if (op) { noProgress = 0; admissions.Add(AdmissionCause.PhaseBTrigger); }
                if (st && !op) { noProgress = 0; admissions.Add(AdmissionCause.PhaseBStateChanged); }
                if (release && !op && !st) { noProgress = 0; admissions.Add(AdmissionCause.ReturnsReleased); }
                bool exits = (!st && !sc) || (!op && !sd);
                // `for (managementRound = 0; managementRound <= maxReruns; …)`: after roundsDone rounds the
                // loop continues only while roundsDone <= maxReruns.
                bool loopEnds = roundsDone > MaxReruns;

                TempoRoundVerdict v = TempoRoundVerdict.Decide(new TempoRoundOutcome(op, sd, sc, st),
                    release, roundsDone, MaxReruns);
                string label = $"op={op} sd={sd} sc={sc} st={st} release={release} rounds={roundsDone}";
                Assert.LessOrEqual(admissions.Count, 1, "baseline branches are mutually exclusive: " + label);
                Assert.AreEqual(admissions.Count == 0 ? AdmissionCause.None : admissions[0], v.Cause, label);
                Assert.AreEqual(noProgress == 0, v.ResetNoProgress, label);
                Assert.AreEqual(exits || loopEnds, v.Closed, label);
            }
        }

        // Hand-fixed traces, independent of both drivers: written from the baseline source.
        [Test]
        public void AQuietTurnRunsOnePassTwoAtMostRoundsAndNoCold()
        {
            var sc = new Scenario { ColdAxes = 1, ColdHasDemands = true, ColdChanged = true };
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.MissionStep, Progress = true });
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.StopNoTrigger, Progress = true, Residual = false });
            // Round 1 changed nothing: the tempo stage closes after one round.
            sc.Rounds.Enqueue(new TempoRoundOutcome(false, false, false, false));
            CollectionAssert.AreEqual(new[]
            {
                "begin cause=Initial retrySet#1 s=0 np=0 zr=0",
                "iter MissionStep mayWait=1 deferred=0 s=1 np=0 zr=0",
                "iter StopNoTrigger mayWait=1 deferred=0 s=2 np=0 zr=0",
                "terminalForce s=2 np=0 zr=0",
                "settleWindow s=2 np=0 zr=0",
                "round#1 op=0 sd=0 sc=0 st=0 s=2 np=0 zr=0",
                "end s=2 np=0 zr=0 deferred=0",
            }, RunNew(sc.Clone()));
        }

        [Test]
        public void AWaitedReturnIsReleasedByTheFirstRoundOnlyAndThenNeverWaitsAgain()
        {
            var sc = new Scenario();
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.StopNoFunded, DefersReturn = true });
            // The released pass sees the gate closed; a second round must not release again.
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.StopNoFunded, DefersReturn = true });
            sc.Rounds.Enqueue(new TempoRoundOutcome(false, true, true, false));
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.StopNoFunded, DefersReturn = true });
            sc.Rounds.Enqueue(new TempoRoundOutcome(false, false, false, false));
            CollectionAssert.AreEqual(new[]
            {
                "begin cause=Initial retrySet#1 s=0 np=0 zr=0",
                "iter StopNoFunded mayWait=1 deferred=1 s=0 np=0 zr=1",
                "terminalForce s=0 np=0 zr=1",
                "settleWindow s=0 np=0 zr=1",
                "round#1 op=0 sd=1 sc=1 st=0 s=0 np=0 zr=1",
                "begin cause=ReturnsReleased retrySet#2 s=0 np=0 zr=0",
                "iter StopNoFunded mayWait=0 deferred=1 s=0 np=0 zr=1",
                "terminalForce s=0 np=0 zr=1",
                "round#2 op=0 sd=0 sc=0 st=0 s=0 np=0 zr=1",
                "end s=0 np=0 zr=1 deferred=1",
            }, RunNew(sc.Clone()));
        }

        [Test]
        public void APhaseBHandChangeWithoutATriggerReopensAdmissionBeforeCold()
        {
            var sc = new Scenario { ColdAxes = 1, ColdHasDemands = true, ColdChanged = true };
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.StopNoFunded });
            // Hand changed, no trigger, nothing strategic: one admission, then the stage closes.
            sc.Rounds.Enqueue(new TempoRoundOutcome(false, false, false, true));
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.StopNoFunded });
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.StopNoFunded });
            CollectionAssert.AreEqual(new[]
            {
                "begin cause=Initial retrySet#1 s=0 np=0 zr=0",
                "iter StopNoFunded mayWait=1 deferred=0 s=0 np=0 zr=1",
                "terminalForce s=0 np=0 zr=1",
                "settleWindow s=0 np=0 zr=1",
                "round#1 op=0 sd=0 sc=0 st=1 s=0 np=0 zr=1",
                "begin cause=PhaseBStateChanged retrySet#2 s=0 np=0 zr=0",
                "iter StopNoFunded mayWait=0 deferred=0 s=0 np=0 zr=1",
                "terminalForce s=0 np=0 zr=1",
                "cold demands=1 s=0 np=0 zr=1",
                "begin cause=ColdChanged retrySet#3 s=0 np=0 zr=0",
                "iter StopNoFunded mayWait=0 deferred=0 s=0 np=0 zr=1",
                "terminalForce s=0 np=0 zr=1",
                "end s=0 np=0 zr=1 deferred=0",
            }, RunNew(sc.Clone()));
        }

        [Test]
        public void TheGlobalStepCapEndsEveryPassAndBlocksCold()
        {
            var sc = new Scenario { StartSettled = MaxSteps - 1, ColdAxes = 1, ColdHasDemands = true, ColdChanged = true };
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.MissionStep, Progress = true });
            sc.Rounds.Enqueue(new TempoRoundOutcome(true, true, true, true));
            sc.Rounds.Enqueue(new TempoRoundOutcome(true, true, true, true));
            CollectionAssert.AreEqual(new[]
            {
                $"begin cause=Initial retrySet#1 s={MaxSteps - 1} np=0 zr=0",
                $"iter MissionStep mayWait=1 deferred=0 s={MaxSteps} np=0 zr=0",
                $"terminalForce s={MaxSteps} np=0 zr=0 boundedMax",
                $"settleWindow s={MaxSteps} np=0 zr=0",
                $"round#1 op=1 sd=1 sc=1 st=1 s={MaxSteps} np=0 zr=0",
                // The pass opens (cause logged, retry set reset) but cannot iterate: bounded at once.
                $"begin cause=PhaseBTrigger retrySet#2 s={MaxSteps} np=0 zr=0",
                $"terminalForce s={MaxSteps} np=0 zr=0 boundedMax",
                $"round#2 op=1 sd=1 sc=1 st=1 s={MaxSteps} np=0 zr=0",
                $"begin cause=PhaseBTrigger retrySet#3 s={MaxSteps} np=0 zr=0",
                $"terminalForce s={MaxSteps} np=0 zr=0 boundedMax",
                // Round cap reached; the zero window and the bound keep cold closed.
                $"end s={MaxSteps} np=0 zr=0 deferred=0",
            }, RunNew(sc.Clone()));
        }

        [Test]
        public void TheNoProgressBoundEndsThePassButARoundThatReadmitsResetsIt()
        {
            var sc = new Scenario();
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.AviationStep, Progress = false });
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.AviationStep, Progress = false });
            sc.Rounds.Enqueue(new TempoRoundOutcome(true, false, false, false));
            sc.Iterations.Enqueue(new IterationScript { Kind = IterationKind.StopNoProvisioned, Residual = true });
            CollectionAssert.AreEqual(new[]
            {
                "begin cause=Initial retrySet#1 s=0 np=0 zr=0",
                "iter AviationStep mayWait=1 deferred=0 s=1 np=1 zr=0",
                "iter AviationStep mayWait=1 deferred=0 s=2 np=2 zr=0",
                "terminalForce s=2 np=2 zr=0 boundedNoProgress",
                "settleWindow s=2 np=2 zr=0",
                "round#1 op=1 sd=0 sc=0 st=0 s=2 np=2 zr=0",
                "begin cause=PhaseBTrigger retrySet#2 s=2 np=0 zr=0",
                "iter StopNoProvisioned mayWait=0 deferred=0 s=2 np=1 zr=1",
                "terminalForce s=2 np=1 zr=1",
                // op dirty but nothing strategic and no state change: the stage closes after the pass.
                "end s=2 np=1 zr=1 deferred=0",
            }, RunNew(sc.Clone()));
        }

        [Test]
        public void ReturnsMayWaitOnlyUntilTheFirstPhaseBRoundSettles()
        {
            var st = new TurnLoopState();
            Assert.IsTrue(st.ReturnsMayWait);
            st.PhaseBRounds = 1;
            Assert.IsFalse(st.ReturnsMayWait);
            st.PhaseBRounds = 2;
            Assert.IsFalse(st.ReturnsMayWait);
        }

        [Test]
        public void AnOpenPassAlwaysRunsBeforeTheNextRoundOrCold()
        {
            foreach (TurnStage stage in Enum.GetValues(typeof(TurnStage)))
            {
                var st = new TurnLoopState { PassOpen = true, Stage = stage };
                Assert.AreEqual(TurnPhase.Ordinary, TurnLoop.Phase(st), stage.ToString());
                st.SettledSteps = MaxSteps;
                Assert.AreEqual(TurnPhase.CloseOrdinary, TurnLoop.Phase(st), stage.ToString());
                st.SettledSteps = 0;
                st.NoProgressCycles = MaxNoProgress;
                Assert.AreEqual(TurnPhase.CloseOrdinary, TurnLoop.Phase(st), stage.ToString());
            }
            Assert.AreEqual(TurnPhase.Tempo, TurnLoop.Phase(new TurnLoopState { Stage = TurnStage.Tempo }));
            Assert.AreEqual(TurnPhase.Cold, TurnLoop.Phase(new TurnLoopState { Stage = TurnStage.Cold }));
            Assert.AreEqual(TurnPhase.Stop, TurnLoop.Phase(new TurnLoopState { Stage = TurnStage.Done }));
        }

        [Test]
        public void ColdNeedsTheLastPassWindowAColdAxisAndOpenBounds()
        {
            Assert.IsTrue(TurnLoop.ColdEligible(new TurnLoopState { ResidualWindow = true }, 1));
            Assert.IsFalse(TurnLoop.ColdEligible(new TurnLoopState { ResidualWindow = false }, 1));
            Assert.IsFalse(TurnLoop.ColdEligible(new TurnLoopState { ResidualWindow = true }, 0));
            Assert.IsFalse(TurnLoop.ColdEligible(
                new TurnLoopState { ResidualWindow = true, SettledSteps = MaxSteps }, 1));
            Assert.IsFalse(TurnLoop.ColdEligible(
                new TurnLoopState { ResidualWindow = true, NoProgressCycles = MaxNoProgress }, 1));
        }

        // ---- Level E1: one writer of the control state ----------------------------------------

        // The table of iteration endings, written from the baseline source (L506-L846 of the
        // pre-decoupling Pipeline) with literal expectations: what each ending does to the counters.
        // columns: ending, steps delta, no-progress before -> after, residual verdict, stop, deferred
        [Test]
        public void TheIterationEndingsChangeTheCountersExactlyAsTheBaselineBodiesDid()
        {
            TurnLoopState Apply(Action<AdmissionIterationOutcome> body, int steps, int np, bool zr,
                bool deferred = false)
            {
                var s = new TurnLoopState
                { SettledSteps = steps, NoProgressCycles = np, ResidualWindow = zr, ReturnsDeferred = deferred };
                var o = new AdmissionIterationOutcome();
                body(o);
                TurnLoop.ApplyIterationOutcome(s, o);
                return s;
            }

            // mandatory aviation action / mission step with progress: +1 step, no progress reset to 0
            TurnLoopState a = Apply(o => o.SettledStep(true), 4, 1, true);
            Assert.AreEqual((5, 0, true), (a.SettledSteps, a.NoProgressCycles, a.ResidualWindow));
            // a settled step without progress: +1 step, +1 cycle; the residual window is untouched
            TurnLoopState b = Apply(o => o.SettledStep(false), 4, 0, false);
            Assert.AreEqual((5, 1, false), (b.SettledSteps, b.NoProgressCycles, b.ResidualWindow));
            // no funded mission: window opens, nothing else changes
            TurnLoopState c = Apply(o => o.NoFundedMission(), 4, 1, false);
            Assert.AreEqual((4, 1, true), (c.SettledSteps, c.NoProgressCycles, c.ResidualWindow));
            // no provisioned task: no step, +1 cycle, window = verdict (both values)
            TurnLoopState d1 = Apply(o => o.NoProvisionedTask(true), 4, 0, false);
            Assert.AreEqual((4, 1, true), (d1.SettledSteps, d1.NoProgressCycles, d1.ResidualWindow));
            TurnLoopState d2 = Apply(o => o.NoProvisionedTask(false), 4, 0, true);
            Assert.AreEqual((4, 1, false), (d2.SettledSteps, d2.NoProgressCycles, d2.ResidualWindow));
            // settled mission without typed invalidation: step counted + window = verdict
            TurnLoopState e = Apply(o => { o.SettledStep(true); o.StopAfterSettledStep(true); }, 7, 1, false);
            Assert.AreEqual((8, 0, true), (e.SettledSteps, e.NoProgressCycles, e.ResidualWindow));
            // a return leg waited: only the flag; it accumulates and is never cleared by an iteration
            TurnLoopState f = Apply(o => o.DeferReturns(), 4, 1, true);
            Assert.AreEqual((4, 1, true, true), (f.SettledSteps, f.NoProgressCycles, f.ResidualWindow, f.ReturnsDeferred));
            TurnLoopState g = Apply(o => o.SettledStep(true), 4, 1, true, deferred: true);
            Assert.IsTrue(g.ReturnsDeferred);

            // stop flag: only the three stopping endings
            var stops = new[]
            {
                (new Action<AdmissionIterationOutcome>(o => o.SettledStep(true)), false),
                (o => o.NoFundedMission(), true),
                (o => o.NoProvisionedTask(false), true),
                (o => { o.SettledStep(false); o.StopAfterSettledStep(false); }, true),
                (o => o.DeferReturns(), false),
            };
            foreach (var (body, stop) in stops)
            {
                var o = new AdmissionIterationOutcome();
                body(o);
                Assert.AreEqual(stop, o.StopPass);
            }
            // the projection used by log lines equals what Apply stores (one place for the arithmetic)
            foreach (int np in new[] { 0, 1, 2 })
            {
                var o = new AdmissionIterationOutcome();
                o.SettledStep(false);
                var s = new TurnLoopState { NoProgressCycles = np };
                TurnLoop.ApplyIterationOutcome(s, o);
                Assert.AreEqual(s.NoProgressCycles, o.NoProgressAfter(np));
            }
        }

        // The view is a copy taken at the start of the iteration: later writes by TurnLoop do not
        // change it, and it carries no reference to the writable state.
        [Test]
        public void TheViewIsAValueCopyAndHoldsNoStateReference()
        {
            var s = new TurnLoopState { SettledSteps = 3, NoProgressCycles = 1 };
            var view = new TurnLoopView(s);
            s.SettledSteps = 9;
            Assert.AreEqual(3, view.SettledSteps);
            Assert.AreEqual(1, view.NoProgressCycles);
            Assert.IsFalse(typeof(TurnLoopView).GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .Any(f => f.FieldType == typeof(TurnLoopState)));
        }

        // Source check (task E1): the fields of TurnLoopState are written only in TurnLoop.cs.
        [Test]
        public void OnlyTurnLoopWritesTheTurnLoopState()
        {
            string root = FindScriptsRoot();
            if (root == null)
                Assert.Ignore("Assets/Scripts not found from the working directory");
            var write = new System.Text.RegularExpressions.Regex(
                @"(?<![\w.])(?:loop|state|st|s)\.(?:SettledSteps|NoProgressCycles|ResidualWindow|ReturnsDeferred|PhaseBRounds|Stage|PassOpen|PassCause)\s*(?:=(?!=)|\+\+|--|\+=|-=)",
                System.Text.RegularExpressions.RegexOptions.Compiled);
            var offenders = new List<string>();
            foreach (string file in System.IO.Directory.GetFiles(root, "*.cs", System.IO.SearchOption.AllDirectories))
            {
                if (System.IO.Path.GetFileName(file) == "TurnLoop.cs")
                    continue;
                int n = 0;
                foreach (string line in System.IO.File.ReadLines(file))
                {
                    n++;
                    string code = line.Split(new[] { "//" }, 2, StringSplitOptions.None)[0];
                    if (write.IsMatch(code))
                        offenders.Add($"{System.IO.Path.GetFileName(file)}:{n}: {line.Trim()}");
                }
            }
            Assert.IsEmpty(offenders, "TurnLoopState is written outside TurnLoop.cs:" + System.Environment.NewLine + string.Join(System.Environment.NewLine, offenders));
        }

        private static string FindScriptsRoot()
        {
            foreach (string start in new[] { System.IO.Directory.GetCurrentDirectory(), AppDomain.CurrentDomain.BaseDirectory })
            {
                var dir = new System.IO.DirectoryInfo(start);
                for (int depth = 0; dir != null && depth < 8; depth++, dir = dir.Parent)
                {
                    string a = System.IO.Path.Combine(dir.FullName, "Assets", "Scripts");
                    if (System.IO.Directory.Exists(a)) return a;
                    string b = System.IO.Path.Combine(dir.FullName, "src", "Assets", "Scripts");
                    if (System.IO.Directory.Exists(b)) return b;
                }
            }
            return null;
        }

        private static void Drain(IEnumerator root)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                if (stack.Peek().Current is IEnumerator nested)
                    stack.Push(nested);
            }
        }
    }
}
#endif
