#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Stage E6 of the decoupling task: the work bodies of the turn are components with explicit
    // dependencies (AdmissionIteration, TempoRound, ColdResidual, StrategicReadmissionRunner) and the
    // trigger sequence talks to the session, the frame and the readmission directly.
    public class AiTurnWorkIntegrationTests
    {
        private const int Turn = 11;

        [TearDown]
        public void TearDown() => StrategicInterruptRegistry.ClearAll();

        // ---- the production trigger sequence on a real session ----

        private sealed class ScriptedReadmission : IStrategicReadmission
        {
            internal readonly List<(ReadmissionCause Cause, StrategicInvalidationReason Reasons, HashSet<DesireAxis> Axes)> Calls
                = new List<(ReadmissionCause, StrategicInvalidationReason, HashSet<DesireAxis>)>();
            internal Action<int> OnCall;
            internal bool Changed = true;

            public IEnumerator Run(ReadmissionCause cause, StrategicInvalidationReason reasons,
                HashSet<DesireAxis> dirtyAxes, ReadmissionOutcome outcome)
            {
                Calls.Add((cause, reasons, dirtyAxes == null ? new HashSet<DesireAxis>() : new HashSet<DesireAxis>(dirtyAxes)));
                OnCall?.Invoke(Calls.Count);
                outcome.StateChanged = Changed && Calls.Count == 1;
                yield break;
            }
        }

        private static DecisionFrame FrameWith(List<MissionIntent> intents, WorldSnapshot snapshot)
        {
            var services = new FrameServices
            {
                RefreshKnowledge = s => s,
                ObserveSettled = (s, st, r) => s,
                WarmEstimates = s => Nothing(),
                EnumerateRecon = s => new List<ReconObjective>(),
                RefreshAggressionFacts = s => { },
                EnumerateAggression = s => new List<RaidObjective>(),
                ResolveActive = (s, recon, aggr, withCtx) => intents,
                RefreshActors = (i, s, recon) => null,
                RefreshPersistentActors = (s, recon) => null,
                GenerateDemands = (s, recon, aggr, i, c, axes) => new List<AxisDemand>(),
            };
            var frame = new DecisionFrame(snapshot, services);
            frame.EnumerateObjectives();
            frame.ResolveInitialOwnership();
            return frame;
        }

        private static IEnumerator Nothing() { yield break; }

        private static void Drain(IEnumerator root)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                if (stack.Peek().Current is IEnumerator nested) stack.Push(nested);
            }
        }

        [TestCase(StepTriggerSequence.StandardPairs, 2, true)]
        [TestCase(StepTriggerSequence.RebasePairs, 1, false)]
        public void TheSequenceFansOutOneSnapshotConsumesOnlyItAndTheSecondPairSeesTheCompoundFact(
            int pairs, int expectedReentries, bool compoundTaken)
        {
            var player = new PlayerSetupData { Nickname = "work" };
            using var session = AiTurnSession.Begin(player, null, null, null, Turn);
            session.Apply(new WorldDelta(false, StrategicInvalidationReason.Hand | StrategicInvalidationReason.Infrastructure));
            var readmission = new ScriptedReadmission
            {
                // the first re-entry itself publishes a compound fact (a follow-up Phase A action)
                OnCall = n => { if (n == 1) session.Apply(new WorldDelta(false,
                    StrategicInvalidationReason.Resources | StrategicInvalidationReason.Actor)); },
            };
            DecisionFrame frame = FrameWith(new List<MissionIntent>(), new WorldSnapshot());
            var sink = new StepTriggerSink();

            Drain(StepTriggerSequence.Run(pairs, session, frame, readmission, sink));

            Assert.That(readmission.Calls.Count, Is.EqualTo(expectedReentries));
            Assert.That(readmission.Calls.All(c => c.Cause == ReadmissionCause.Trigger));
            Assert.That(readmission.Calls[0].Reasons & (StrategicInvalidationReason.Hand | StrategicInvalidationReason.Infrastructure),
                Is.Not.EqualTo(StrategicInvalidationReason.None));
            StrategicInvalidationReason pending = session.PendingInvalidations.Reasons;
            Assert.That(pending.HasFlag(StrategicInvalidationReason.Resources), Is.EqualTo(!compoundTaken),
                "the fact the first re-entry published is consumed only when a second pair took it");
            Assert.That(sink.Outcome.Strategic.HasFlag(StrategicInvalidationReason.Resources), Is.EqualTo(compoundTaken));
            Assert.That(sink.Outcome.StrategicChanged, Is.True, "the first re-entry changed the world");
        }

        [Test]
        public void TheBuilderReadinessIsAskedOfContinuityOnTheCurrentFrameAndOnlyForAnActorOnlyEconomyFact()
        {
            var player = new PlayerSetupData { Nickname = "ready" };
            using var session = AiTurnSession.Begin(player, null, null, null, Turn);
            var target = new HexCoord(3, 2);
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy, Status = IntentStatus.Active, PreferredMoverArmyId = 9,
                Objective = new EconomyIntent { Kind = EconomyTaskKind.FoundBase, TargetHex = target, BuilderArmyId = 9 },
            };
            WorldSnapshot Where(HexCoord hex) => new WorldSnapshot
            {
                Self = new SelfSnapshot { Armies = new List<ArmySnapshot> { new ArmySnapshot { ArmyId = 9, Hex = hex } } },
            };

            foreach ((HexCoord at, bool economyDirty) in new[] { (target, true), (new HexCoord(0, 0), false) })
            {
                session.Apply(new WorldDelta(false, StrategicInvalidationReason.Actor, new[] { 9 }));
                var readmission = new ScriptedReadmission();
                DecisionFrame frame = FrameWith(new List<MissionIntent> { intent }, Where(at));
                Drain(StepTriggerSequence.Run(1, session, frame, readmission, new StepTriggerSink()));
                Assert.That(readmission.Calls[0].Axes.Contains(DesireAxis.Economy), Is.EqualTo(economyDirty),
                    "builder at " + at);
                session.ConsumeInvalidations(StrategicInvalidationReason.Actor);
            }
        }

        // ---- shared state of the components ----

        [Test]
        public void TheCarriedReservationIsReadAtEveryUseNeverCached()
        {
            var a = new MaterializationReservation();
            var b = new MaterializationReservation();
            var phaseA = new StrategicPhaseResult { Reservation = a };
            var phaseB = new StrategicPhaseResult();
            var phases = new PhaseResults(phaseA, phaseB);
            Assert.That(phases.Carried, Is.SameAs(a), "Phase B has none yet");
            phaseB.Reservation = b;
            Assert.That(phases.Carried, Is.SameAs(b), "once a round produced one it wins, read at the moment of use");
            phaseB.Reservation = null;
            phaseA.Reservation = a;
            Assert.That(phases.Carried, Is.SameAs(a));
        }

        [Test]
        public void TheColdAxesAreTheZeroRadarAxesOfTheTurn()
        {
            Radar radar = Radar.Even();
            radar.Weight[DesireAxis.Aggression] = 0f;
            radar.Weight[DesireAxis.Recon] = 0f;
            var cold = new ColdResidual(null, null, null, radar, new HashSet<DesireAxis>(DesireAxes.All),
                null, null, null, null);
            Assert.That(cold.AxisCount(), Is.EqualTo(2));
            radar.Weight[DesireAxis.Recon] = 0.2f;
            Assert.That(cold.AxisCount(), Is.EqualTo(1), "recomputed from the Radar at the moment the cold stage is reached");
        }

        [Test]
        public void TheTelemetryCountsAKeyFundedByManyPacksOnce()
        {
            var telemetry = new TurnTelemetry();
            var job = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = new HexCoord(1, 1) },
            };
            var pack = new TentativeAllocation();
            pack.Funded.Add(new FundedEntry { Mission = job });
            telemetry.RecordFunded(pack);
            telemetry.RecordFunded(pack);
            Assert.That(telemetry.FundedKeys.Count, Is.EqualTo(1));
        }

        // ---- the shape of the components ----

        private static readonly Type[] Components =
        {
            typeof(AdmissionIteration), typeof(TempoRound), typeof(ColdResidual), typeof(StrategicReadmissionRunner),
        };

        [Test]
        public void TheComponentsTakeTheirDependenciesAsPlainConstructorParametersNotCallbacks()
        {
            foreach (Type type in Components)
            {
                var constructor = type.GetConstructors(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).Single();
                foreach (System.Reflection.ParameterInfo parameter in constructor.GetParameters())
                    Assert.That(typeof(Delegate).IsAssignableFrom(parameter.ParameterType), Is.False,
                        type.Name + "(" + parameter.Name + ")");
                foreach (System.Reflection.FieldInfo field in type.GetFields(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                    Assert.That(typeof(Delegate).IsAssignableFrom(field.FieldType), Is.False,
                        type.Name + "." + field.Name);
            }
        }

        [Test]
        public void TheComponentsDoNotReachBackIntoRunTurnAndRunTurnHoldsNoWorkBodies()
        {
            string root = AiTurnLoopTests.FindScriptsRoot();
            if (root == null) Assert.Ignore("Assets/Scripts not found from the working directory");
            string Code(string fileName) => string.Join(Environment.NewLine, System.IO.File
                .ReadLines(System.IO.Directory.GetFiles(root, fileName, System.IO.SearchOption.AllDirectories).Single())
                .Select(l => l.Split(new[] { "//" }, 2, StringSplitOptions.None)[0]));
            foreach (string name in new[] { "AdmissionIteration.cs", "TempoRound.cs", "ColdResidual.cs", "StrategicReadmissionRunner.cs" })
            {
                string code = Code(name);
                Assert.That(code, Does.Not.Contain("Pipeline."), name);
                Assert.That(code, Does.Not.Contain("TurnLoopState"), name + ": the loop's control state is the loop's");
                Assert.That(code, Does.Not.Contain("TurnLoopWork"), name);
            }
            // the lifetimes the bodies relied on are kept: the provisioning session lives for the whole
            // iteration (using), and the parking of rejected jobs is renewed by OpenPass, not per iteration
            string iteration = Code("AdmissionIteration.cs");
            Assert.That(iteration, Does.Contain("using var cycleProvisioning = new ProvisioningSession("));
            int open = iteration.IndexOf("public void OpenPass()", StringComparison.Ordinal);
            int created = iteration.IndexOf("_passParking = new PassParking();", StringComparison.Ordinal);
            Assert.That(created, Is.GreaterThan(open));
            Assert.That(iteration.IndexOf("_passParking = new PassParking();", created + 1, StringComparison.Ordinal), Is.EqualTo(-1));
            // RunTurn declares no local function: every work body is a component
            string pipeline = Code("AiStrategyV2Pipeline.cs");
            var localFunction = new System.Text.RegularExpressions.Regex(
                @"^\s{12,}(IEnumerator|void|bool|string|int|TypedTriggerSplit|MaterializationReservation)\s+\w+\([^;]*\)\s*(\{|=>)?\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            Assert.That(localFunction.Matches(pipeline).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value.Trim()),
                Is.Empty, "local functions left in RunTurn");
        }

        // The order of calls inside each work, and of the turn's tail, written from the baseline (the
        // protocol table of the task): reads and writes of the frame, the ledger, the bank and the
        // observer boundary keep their places. The bodies differ by kind of work on purpose.
        [Test]
        public void EachWorkAndTheTailOfTheTurnKeepTheirOrderOfCalls()
        {
            string root = AiTurnLoopTests.FindScriptsRoot();
            if (root == null) Assert.Ignore("Assets/Scripts not found from the working directory");
            string Code(string fileName) => string.Join(Environment.NewLine, System.IO.File
                .ReadLines(System.IO.Directory.GetFiles(root, fileName, System.IO.SearchOption.AllDirectories).Single())
                .Select(l => l.Split(new[] { "//" }, 2, StringSplitOptions.None)[0]));
            void InOrder(string code, string what, params string[] steps)
            {
                int at = -1;
                foreach (string step in steps)
                {
                    int next = code.IndexOf(step, at + 1, StringComparison.Ordinal);
                    Assert.That(next, Is.GreaterThan(at), what + ": '" + step + "' is missing or out of order");
                    at = next;
                }
            }

            string admission = Code("AdmissionIteration.cs");
            int mandatoryAt = admission.IndexOf("RunMandatoryAviationStep(MandatoryAviationKind", StringComparison.Ordinal);
            int iterationAt = admission.IndexOf("public IEnumerator Iteration(", StringComparison.Ordinal);
            string mandatory = admission.Substring(mandatoryAt, iterationAt - mandatoryAt);
            string iteration = admission.Substring(iterationAt);

            // mandatory aviation: no ledger, no SettleStep, no observer boundary
            InOrder(mandatory, "mandatory aviation", "CaptureStepObservation(", "MandatoryAviationStep.Execute(",
                "_frame.ObserveSettled(", "CheckBoundary(", "StepTriggerSequence.Run(", "outcome.SettledStep(",
                "AviationObligations.RecordSettledStep(");
            foreach (string absent in new[] { "RecordExecution(", "SettleStep(", "WaitAtObserverActionBoundary" })
                Assert.That(mandatory, Does.Not.Contain(absent), "mandatory aviation must not call " + absent);

            // a mission step: observe -> ledger -> settle -> bank event -> boundary -> observer -> pairs -> outcome
            InOrder(iteration, "mission step", "ProvisionNext(", "CaptureStepObservation(", "TaskExecutor.ExecuteStep(",
                "_frame.ObserveSettled(beforeStep", "RecordExecution(", "RecordDeferrals(", "RefreshObjectiveStatesLive(",
                "SettleStep(", "AfterMissionSettlement(", "CheckBoundary(_player, _root, _ctx,",
                "WaitAtObserverActionBoundary()", "StepTriggerSequence.Run(StepTriggerSequence.StandardPairs",
                "outcome.SettledStep(progressed)", "outcome.StopAfterSettledStep(");

            // the iteration itself: frame pacing -> deferred flush -> frame -> portfolio -> returns -> parking -> funding -> pack
            InOrder(iteration, "iteration start", "realtimeSinceStartup", "ReadmissionCause.DeferredFlush",
                "_frame.PrepareAdmission()", "MissionPortfolio.Build(", "DeferReturnsBeforeTempo(", "_passParking.Filter(",
                "BindFunding(", "ResourceAllocator.BeginTurn(", "new ProvisioningSession(", ".Pack()",
                "MandatoryAviationOrder.Next(", "OperationalWorkSelection.Select(");

            // a Phase B round
            InOrder(Code("TempoRound.cs"), "tempo round", "PrepareTempoOwnership(", "CaptureStepObservation(",
                "BeforeTempoSpend(", "StrategicManager.UseSurplus(", "CheckBoundary(", "_frame.ObserveSettled(",
                "PhaseB.Accumulate(", "WaitAtObserverActionBoundary()", "StepTriggerSequence.Run(", "sink.Outcome =");

            // the cold residual
            InOrder(Code("ColdResidual.cs"), "cold residual", "PrepareColdResidual(", "GenerateDemands(",
                "CaptureStepObservation(", "StrategicManager.FulfillDemands(", "PhaseA.Accumulate(",
                "UnresolvedDemands.AddRange(", "AcceptChangedCold(", "WaitAtObserverActionBoundary()", "sink.Changed = true");

            // re-admission
            InOrder(Code("StrategicReadmissionRunner.cs"), "re-admission", "_readmission.Decide(",
                "_frame.RefreshOperationalDecision()", "admittedFingerprints", "_frame.GenerateDemands(",
                "ReplaceDemandFamilies(", "CaptureStepObservation(", "StrategicManager.FulfillDemands(",
                "ReservationInvariants.CheckBoundary(", "AcceptChangedReentry()", "PublishStepObservationDelta(",
                "_readmission.Commit(");

            // the tail of the turn: loop -> recall -> final ownership -> settle -> Housekeeping (Reaction inside)
            // -> audit -> release
            InOrder(Code("AiStrategyV2Pipeline.cs"), "end of the turn", "TurnLoop.Run(", "RecallUnsafeStrikes(",
                "RefreshFinalOwnership()", "SettleAfterTurn(", "RunHousekeeping(", "AcceptHousekeeping()",
                "AuditTurnEnd(", "CompleteReservations()");
        }

        [Test]
        public void TheLoopOrdersThreeWorksThroughNarrowInterfaces()
        {
            Assert.That(typeof(IAdmissionWork).GetMethods().Select(m => m.Name),
                Is.EquivalentTo(new[] { "OpenPass", "Iteration", "TerminalForce" }));
            Assert.That(typeof(ITempoWork).GetMethods().Select(m => m.Name),
                Is.EquivalentTo(new[] { "SettleBeforeFirstRound", "Round" }));
            Assert.That(typeof(IColdWork).GetMethods().Select(m => m.Name),
                Is.EquivalentTo(new[] { "AxisCount", "Run" }));
            Assert.That(typeof(AdmissionIteration).GetInterfaces(), Does.Contain(typeof(IAdmissionWork)));
            Assert.That(typeof(TempoRound).GetInterfaces(), Does.Contain(typeof(ITempoWork)));
            Assert.That(typeof(ColdResidual).GetInterfaces(), Does.Contain(typeof(IColdWork)));
        }
    }
}
#endif
