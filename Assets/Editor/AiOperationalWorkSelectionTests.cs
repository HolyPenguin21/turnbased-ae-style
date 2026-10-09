#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Level 2 of the pipeline simplification: the one selection boundary of the operational loop,
    // the executor route of a selected mission, the shared outcome of a settled step and the
    // delayed re-admission gate. Expected values are what the inline code produced before.
    public class AiOperationalWorkSelectionTests
    {
        [TestCase(MandatoryAviationKind.Rebase, 0, OperationalWorkKind.MandatoryAviation)]
        [TestCase(MandatoryAviationKind.Recovery, 3, OperationalWorkKind.MandatoryAviation)]
        [TestCase(MandatoryAviationKind.None, 2, OperationalWorkKind.Mission)]
        [TestCase(MandatoryAviationKind.None, 0, OperationalWorkKind.None)]
        public void PaidAviationGoesBeforeFundedMissionsAndNothingStopsTheAdmission(
            MandatoryAviationKind mandatory, int funded, OperationalWorkKind expected) =>
            Assert.AreEqual(expected, OperationalWorkSelection.Select(mandatory, funded));

        [Test]
        public void AStalledObligationLeavesTheFundedMissionsSelectable()
        {
            // FindMandatory* drop a stalled wing, so the loop sees Kind None again.
            var next = MandatoryAviationOrder.Next(new List<ArmyData>(), new List<ArmyData>());
            Assert.AreEqual(OperationalWorkKind.Mission, OperationalWorkSelection.Select(next.Kind, 1));
        }

        [TestCase(MissionKind.Scout, ScoutExecutorKind.Ground, MissionRoute.Task)]
        [TestCase(MissionKind.Scout, ScoutExecutorKind.AirExisting, MissionRoute.AirRecon)]
        [TestCase(MissionKind.Scout, ScoutExecutorKind.AirStored, MissionRoute.AirRecon)]
        [TestCase(MissionKind.Raid, ScoutExecutorKind.Ground, MissionRoute.Task)]
        [TestCase(MissionKind.Attack, ScoutExecutorKind.AirExisting, MissionRoute.Task)]
        [TestCase(MissionKind.Economy, ScoutExecutorKind.Ground, MissionRoute.Task)]
        public void OnlyAnAirScoutTakesTheAirReconRoute(
            MissionKind kind, ScoutExecutorKind executor, MissionRoute expected) =>
            Assert.AreEqual(expected, OperationalWorkSelection.RouteFor(kind, executor));

        [Test]
        public void StepProgressIsTheActionOrAReadmissionThatChangedState()
        {
            var none = new StepTriggerOutcome(StrategicInvalidationReason.None,
                StrategicInvalidationReason.None, strategicChanged: false);
            var changed = new StepTriggerOutcome(StrategicInvalidationReason.Actor,
                StrategicInvalidationReason.Hand, strategicChanged: true);
            Assert.IsFalse(none.Progressed(false));
            Assert.IsTrue(none.Progressed(true));
            Assert.IsTrue(changed.Progressed(false));
            Assert.AreEqual(0, StepTriggerOutcome.NextNoProgress(1, true));
            Assert.AreEqual(2, StepTriggerOutcome.NextNoProgress(1, false));
            Assert.AreEqual(2, StepTriggerSequence.StandardPairs, "ordinary step: two pairs");
        }

        // ---- delayed strategic re-admission (StrategicReadmission.Gate) ---------------------------
        // Level 5: the gate reads the ReadmissionCause itself (was: flush / force bools). Each
        // expectation below is the one the bool gate had for the same request: flush -> DeferredFlush,
        // force -> TerminalForce, neither -> Trigger. The expected values did not change.

        private static System.Func<bool> Pending(bool pending) => () => pending;

        [Test]
        public void TheLastObligationResolvingAdmitsTheWaitingAxesWithoutANewTrigger() =>
            Assert.AreEqual(DeferredAdmissionGate.Admit, StrategicReadmission.Gate(
                ReadmissionCause.DeferredFlush, triggered: false, hasDeferredAxes: true, Pending(false)));

        [Test]
        public void WhileAnObligationIsPendingTheWaitingAxesStayWaiting()
        {
            Assert.AreEqual(DeferredAdmissionGate.Skip, StrategicReadmission.Gate(
                ReadmissionCause.DeferredFlush, false, hasDeferredAxes: true, Pending(true)));
            Assert.AreEqual(DeferredAdmissionGate.Defer, StrategicReadmission.Gate(
                ReadmissionCause.Trigger, true, hasDeferredAxes: false, Pending(true)));
        }

        [Test]
        public void TheForcedFlushAdmitsEvenWhilePendingAndLogsIt() =>
            Assert.AreEqual(DeferredAdmissionGate.AdmitDespitePending, StrategicReadmission.Gate(
                ReadmissionCause.TerminalForce, false, hasDeferredAxes: true, Pending(true)));

        [Test]
        public void NothingToAdmitIsSkipped()
        {
            // The bool gate's (flush: true, force: true) request is either cause now; both skip.
            Assert.AreEqual(DeferredAdmissionGate.Skip, StrategicReadmission.Gate(
                ReadmissionCause.DeferredFlush, false, hasDeferredAxes: false, Pending(false)));
            Assert.AreEqual(DeferredAdmissionGate.Skip, StrategicReadmission.Gate(
                ReadmissionCause.TerminalForce, false, hasDeferredAxes: false, Pending(false)));
            Assert.AreEqual(DeferredAdmissionGate.Skip, StrategicReadmission.Gate(
                ReadmissionCause.Trigger, false, hasDeferredAxes: true, Pending(false)));
        }

        [Test]
        public void ATriggerWithNothingPendingAdmitsNormally() =>
            Assert.AreEqual(DeferredAdmissionGate.Admit, StrategicReadmission.Gate(
                ReadmissionCause.Trigger, true, hasDeferredAxes: false, Pending(false)));

        // The bool gate @ c3cdde46 (Recon/AviationObligations.cs), transcribed, together with the
        // caller's mapping and lazy read (StrategicReadmission.Decide: flush/force from the cause,
        // `wanted && obligationsPending()`).
        private static DeferredAdmissionGate BaselineGate(ReadmissionCause cause, bool triggered,
            bool hasDeferredAxes, System.Func<bool> obligationsPending)
        {
            bool flush = cause == ReadmissionCause.DeferredFlush;
            bool force = cause == ReadmissionCause.TerminalForce;
            bool wanted = triggered || ((flush || force) && hasDeferredAxes);
            bool pending = wanted && obligationsPending();
            if (!triggered && !((flush || force) && hasDeferredAxes))
                return DeferredAdmissionGate.Skip;
            if (!pending)
                return DeferredAdmissionGate.Admit;
            if (force)
                return DeferredAdmissionGate.AdmitDespitePending;
            return triggered ? DeferredAdmissionGate.Defer : DeferredAdmissionGate.Skip;
        }

        [Test]
        public void TheCauseGateMatchesTheBoolGateForEveryRequestAndReadsPendingAsLazily()
        {
            foreach (ReadmissionCause cause in System.Enum.GetValues(typeof(ReadmissionCause)))
            foreach (bool triggered in new[] { false, true })
            foreach (bool deferred in new[] { false, true })
            foreach (bool pending in new[] { false, true })
            {
                int baselineReads = 0, reads = 0;
                DeferredAdmissionGate expected = BaselineGate(cause, triggered, deferred,
                    () => { baselineReads++; return pending; });
                DeferredAdmissionGate actual = StrategicReadmission.Gate(cause, triggered, deferred,
                    () => { reads++; return pending; });
                string label = $"{cause} triggered={triggered} deferred={deferred} pending={pending}";
                Assert.AreEqual(expected, actual, label);
                Assert.AreEqual(baselineReads, reads, "pending read count: " + label);
            }
        }
    }
}
#endif
