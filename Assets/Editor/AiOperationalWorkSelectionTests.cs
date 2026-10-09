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
            Assert.AreEqual(2, StepTriggerOutcome.MissionTriggerPairs, "ordinary step: two pairs");
        }

        // ---- delayed strategic re-admission (ReenterStrategicAxes gate) ---------------------------

        [Test]
        public void TheLastObligationResolvingAdmitsTheWaitingAxesWithoutANewTrigger() =>
            Assert.AreEqual(DeferredAdmissionGate.Admit, DeferredStrategicAdmission.Gate(
                triggered: false, flush: true, force: false, hasDeferredAxes: true, obligationsPending: false));

        [Test]
        public void WhileAnObligationIsPendingTheWaitingAxesStayWaiting()
        {
            Assert.AreEqual(DeferredAdmissionGate.Skip, DeferredStrategicAdmission.Gate(
                false, flush: true, force: false, hasDeferredAxes: true, obligationsPending: true));
            Assert.AreEqual(DeferredAdmissionGate.Defer, DeferredStrategicAdmission.Gate(
                true, flush: false, force: false, hasDeferredAxes: false, obligationsPending: true));
        }

        [Test]
        public void TheForcedFlushAdmitsEvenWhilePendingAndLogsIt() =>
            Assert.AreEqual(DeferredAdmissionGate.AdmitDespitePending, DeferredStrategicAdmission.Gate(
                false, flush: false, force: true, hasDeferredAxes: true, obligationsPending: true));

        [Test]
        public void NothingToAdmitIsSkipped()
        {
            Assert.AreEqual(DeferredAdmissionGate.Skip, DeferredStrategicAdmission.Gate(
                false, flush: true, force: true, hasDeferredAxes: false, obligationsPending: false));
            Assert.AreEqual(DeferredAdmissionGate.Skip, DeferredStrategicAdmission.Gate(
                false, flush: false, force: false, hasDeferredAxes: true, obligationsPending: false));
        }

        [Test]
        public void ATriggerWithNothingPendingAdmitsNormally() =>
            Assert.AreEqual(DeferredAdmissionGate.Admit, DeferredStrategicAdmission.Gate(
                true, flush: false, force: false, hasDeferredAxes: false, obligationsPending: false));
    }
}
#endif
