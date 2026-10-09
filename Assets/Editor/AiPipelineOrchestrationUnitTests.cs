#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Characterization of the orchestration units extracted from Pipeline.RunTurn (level 0 of the
    // pipeline simplification): the values below are what the inline code produced before.
    public class AiPipelineOrchestrationUnitTests
    {
        [TestCase(null, null, false)]
        [TestCase(5, null, true)]
        [TestCase(null, 5, false)]
        [TestCase(3, 7, true)]
        [TestCase(7, 7, true)]   // a tie resumes the rebase
        [TestCase(9, 7, false)]
        public void RebaseIsResumedBeforeRecoveryUnlessRecoveryHasTheLowerId(
            int? rebaseId, int? recoveryId, bool expectedRebaseFirst) =>
            Assert.AreEqual(expectedRebaseFirst, MandatoryAviationOrder.RebaseFirst(rebaseId, recoveryId));

        [Test]
        public void NoPendingReasonsDirtyNothing()
        {
            TypedTriggerSplit s = TypedTriggerFanOut.Split(StrategicInvalidationReason.None, () => true);
            Assert.AreEqual(StrategicInvalidationReason.None, s.Consumed);
            Assert.AreEqual(0, s.DirtyAxes.Count);
        }

        [Test]
        public void ActorAloneReadmitsEconomyOnlyWhenTheBuilderArrived()
        {
            TypedTriggerSplit idle = TypedTriggerFanOut.Split(StrategicInvalidationReason.Actor, () => false);
            Assert.IsFalse(idle.DirtyAxes.Contains(DesireAxis.Economy));
            TypedTriggerSplit arrived = TypedTriggerFanOut.Split(StrategicInvalidationReason.Actor, () => true);
            Assert.IsTrue(arrived.DirtyAxes.Contains(DesireAxis.Economy));
        }

        [Test]
        public void BuilderArrivalIsNotAskedWhenEconomyHasAnotherReason()
        {
            int asked = 0;
            TypedTriggerSplit s = TypedTriggerFanOut.Split(
                StrategicInvalidationReason.Actor | StrategicInvalidationReason.Resources,
                () => { asked++; return false; });
            Assert.AreEqual(0, asked);
            Assert.IsTrue(s.DirtyAxes.Contains(DesireAxis.Economy));
        }

        [Test]
        public void ACompoundFactReachesEveryConsumerFamilyAndIsConsumedWhole()
        {
            const StrategicInvalidationReason compound =
                StrategicInvalidationReason.Hand | StrategicInvalidationReason.Infrastructure
                | StrategicInvalidationReason.Contact;
            TypedTriggerSplit s = TypedTriggerFanOut.Split(compound, () => false);
            foreach (DesireAxis axis in new[] { DesireAxis.Development, DesireAxis.Aggression })
                if ((compound & DesireAxes.InvalidationMaskFor(axis)) != StrategicInvalidationReason.None)
                    Assert.IsTrue(s.DirtyAxes.Contains(axis), axis.ToString());
            Assert.AreEqual(compound & (s.Operational | s.Strategic), s.Consumed & compound);
            Assert.AreEqual(s.Operational | s.Strategic, s.Consumed);
        }
    }
}
#endif
