#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Level 3 of the pipeline simplification: the single re-admission protocol. The arithmetic
    // (which axes run now) is StrategicReadmission; the keys are computed by the domain owners.
    // The expected values are what the inline ReenterStrategicAxes / fingerprint closure produced.
    public class AiStrategicReadmissionTests
    {
        private static Dictionary<DesireAxis, string> Keys(params (DesireAxis Axis, string Key)[] keys) =>
            keys.ToDictionary(k => k.Axis, k => k.Key);

        private static DeferredAdmissionGate Decide(StrategicReadmission r, ReadmissionCause cause,
            StrategicInvalidationReason reasons, HashSet<DesireAxis> dirty, bool pending,
            Dictionary<DesireAxis, string> keys, out HashSet<DesireAxis> axes, List<DesireAxis> unchanged = null) =>
            r.Decide(cause, reasons, dirty, () => pending, a => keys[a],
                (a, k) => unchanged?.Add(a), out axes);

        // ---- unchanged fingerprint ---------------------------------------------------------------

        [Test]
        public void AnUnchangedKeyDoesNotStartAPass()
        {
            var r = new StrategicReadmission();
            r.Seed(DesireAxis.Development, "k");
            var dropped = new List<DesireAxis>();
            var gate = Decide(r, ReadmissionCause.Trigger, StrategicInvalidationReason.Hand,
                new HashSet<DesireAxis> { DesireAxis.Development }, false,
                Keys((DesireAxis.Development, "k")), out var axes, dropped);
            Assert.AreEqual(DeferredAdmissionGate.Admit, gate);
            Assert.IsEmpty(axes, "nothing to run: the pipeline stops before the frame refresh");
            Assert.AreEqual(new[] { DesireAxis.Development }, dropped);
        }

        [Test]
        public void OnlyTheAxesWhoseKeyChangedRunAndAnUnseenAxisAlwaysRuns()
        {
            var r = new StrategicReadmission();
            r.Seed(DesireAxis.Development, "d");
            r.Seed(DesireAxis.Economy, "e");
            Decide(r, ReadmissionCause.Trigger, StrategicInvalidationReason.Hand | StrategicInvalidationReason.Actor,
                new HashSet<DesireAxis> { DesireAxis.Development, DesireAxis.Economy, DesireAxis.Aggression },
                false, Keys((DesireAxis.Development, "d2"), (DesireAxis.Economy, "e"),
                    (DesireAxis.Aggression, "a")), out var axes);
            Assert.That(axes, Is.EquivalentTo(new[] { DesireAxis.Development, DesireAxis.Aggression }));
        }

        [Test]
        public void ACommittedKeyMakesTheSameKeySettled()
        {
            var r = new StrategicReadmission();
            r.Seed(DesireAxis.Economy, "e");
            r.Commit(new[] { DesireAxis.Economy }, Keys((DesireAxis.Economy, "e2")));
            Assert.AreEqual("e2", r.LastAdmitted(DesireAxis.Economy));
            Decide(r, ReadmissionCause.Trigger, StrategicInvalidationReason.Resources,
                new HashSet<DesireAxis> { DesireAxis.Economy }, false,
                Keys((DesireAxis.Economy, "e2")), out var axes);
            Assert.IsEmpty(axes);
        }

        // ---- aviation: defer, flush without a trigger, terminal force ----------------------------

        [Test]
        public void AxesWaitWhileAnObligationIsPendingAndTheLastOneReleasesThemWithoutANewTrigger()
        {
            var r = new StrategicReadmission();
            var keys = Keys((DesireAxis.Economy, "e2"), (DesireAxis.Development, "d2"));
            r.Seed(DesireAxis.Economy, "e");
            r.Seed(DesireAxis.Development, "d");
            var gate = Decide(r, ReadmissionCause.Trigger, StrategicInvalidationReason.Actor,
                new HashSet<DesireAxis> { DesireAxis.Economy }, true, keys, out var axes);
            Assert.AreEqual(DeferredAdmissionGate.Defer, gate);
            Assert.IsNull(axes);
            Assert.That(r.Deferred.Axes, Is.EquivalentTo(new[] { DesireAxis.Economy }));

            gate = Decide(r, ReadmissionCause.DeferredFlush, StrategicInvalidationReason.None, null,
                true, keys, out axes);
            Assert.AreEqual(DeferredAdmissionGate.Skip, gate, "still pending: keep waiting");
            Assert.IsTrue(r.Deferred.HasAxes);

            gate = Decide(r, ReadmissionCause.DeferredFlush, StrategicInvalidationReason.None, null,
                false, keys, out axes);
            Assert.AreEqual(DeferredAdmissionGate.Admit, gate);
            Assert.That(axes, Is.EquivalentTo(new[] { DesireAxis.Economy }));
            Assert.IsFalse(r.Deferred.HasAxes, "the deferral is consumed exactly once");
        }

        [Test]
        public void AFreshTriggerAfterTheLastObligationAdmitsItsOwnAxesTogetherWithTheWaitingOnes()
        {
            var r = new StrategicReadmission();
            r.Deferred.Defer(new[] { DesireAxis.Economy });
            Decide(r, ReadmissionCause.Trigger, StrategicInvalidationReason.Hand,
                new HashSet<DesireAxis> { DesireAxis.Development }, false,
                Keys((DesireAxis.Economy, "e"), (DesireAxis.Development, "d")), out var axes);
            Assert.That(axes, Is.EquivalentTo(new[] { DesireAxis.Economy, DesireAxis.Development }));
        }

        [Test]
        public void TheTerminalForceAdmitsWaitingAxesEvenWhilePending()
        {
            var r = new StrategicReadmission();
            r.Deferred.Defer(new[] { DesireAxis.Aggression });
            var gate = Decide(r, ReadmissionCause.TerminalForce, StrategicInvalidationReason.None, null,
                true, Keys((DesireAxis.Aggression, "a")), out var axes);
            Assert.AreEqual(DeferredAdmissionGate.AdmitDespitePending, gate);
            Assert.That(axes, Is.EquivalentTo(new[] { DesireAxis.Aggression }));
        }

        [Test]
        public void PendingAviationIsNotReadWhenThereIsNothingToAdmit()
        {
            var r = new StrategicReadmission();
            int reads = 0;
            var gate = r.Decide(ReadmissionCause.DeferredFlush, StrategicInvalidationReason.None, null,
                () => { reads++; return true; }, a => "k", null, out _);
            Assert.AreEqual(DeferredAdmissionGate.Skip, gate);
            Assert.AreEqual(0, reads);
        }

        // ---- capacity unlock ---------------------------------------------------------------------

        [Test]
        public void ACapacityUnlockForgetsOnlyTheDevelopmentBaseline()
        {
            var r = new StrategicReadmission();
            r.Seed(DesireAxis.Development, "k");
            r.Seed(DesireAxis.Economy, "e");
            Decide(r, ReadmissionCause.CapacityUnlock,
                StrategicInvalidationReason.Infrastructure | StrategicInvalidationReason.Capability,
                new HashSet<DesireAxis> { DesireAxis.Development, DesireAxis.Economy }, false,
                Keys((DesireAxis.Development, "k"), (DesireAxis.Economy, "e")), out var axes);
            Assert.That(axes, Is.EquivalentTo(new[] { DesireAxis.Development }),
                "an equal Development key is admitted once; the other baselines stay");
        }

        // ---- compound facts reach every consumer; later facts survive the consume -----------------

        [Test]
        public void FactsPublishedByAReentryAreNotErasedByTheConsumeOfTheOlderSnapshot()
        {
            var p = new PlayerSetupData { Nickname = "readmission" };
            StrategicInterruptRegistry.ClearAll();
            try
            {
                StrategicInterruptRegistry.Record(p, 7,
                    StrategicInvalidationReason.Hand | StrategicInvalidationReason.Infrastructure);
                TypedTriggerSplit first = TypedTriggerFanOut.Split(
                    StrategicInterruptRegistry.Peek(p, 7).Reasons, () => false);
                Assert.IsTrue(first.DirtyAxes.Contains(DesireAxis.Development));
                // The re-entry itself publishes another compound fact before the first consume.
                StrategicInterruptRegistry.Record(p, 7,
                    StrategicInvalidationReason.Resources | StrategicInvalidationReason.Actor);
                StrategicInterruptRegistry.Consume(p, 7, first.Consumed);
                StrategicInvalidationReason left = StrategicInterruptRegistry.Peek(p, 7).Reasons;
                Assert.AreEqual(StrategicInvalidationReason.Resources | StrategicInvalidationReason.Actor,
                    left & (StrategicInvalidationReason.Resources | StrategicInvalidationReason.Actor),
                    "the next bounded pair still sees them");
                TypedTriggerSplit second = TypedTriggerFanOut.Split(left, () => true);
                Assert.IsTrue(second.DirtyAxes.Contains(DesireAxis.Economy), "builder arrival: same-turn completion");
            }
            finally { StrategicInterruptRegistry.ClearAll(); }
        }
    }
}
#endif
