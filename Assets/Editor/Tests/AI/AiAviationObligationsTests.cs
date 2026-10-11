#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Strategic re-admission waits while aviation obligations are pending and is admitted once,
    // with every axis collected meanwhile, when they are done.
    public class AiAviationObligationsTests
    {
        [Test]
        public void DeferredAxes_AccumulateAcrossTriggers()
        {
            var deferred = new DeferredStrategicAdmission();

            deferred.Defer(new[] { DesireAxis.Economy });
            deferred.Defer(new[] { DesireAxis.Recon, DesireAxis.Economy });

            Assert.That(deferred.HasAxes, Is.True);
            Assert.That(deferred.Axes, Is.EquivalentTo(new[] { DesireAxis.Economy, DesireAxis.Recon }));
        }

        [Test]
        public void TakeWith_AdmitsDeferredTogetherWithTheCallersAxes_AndClears()
        {
            var deferred = new DeferredStrategicAdmission();
            deferred.Defer(new[] { DesireAxis.Economy });

            var admitted = deferred.TakeWith(new[] { DesireAxis.Development });

            Assert.That(admitted, Is.EquivalentTo(new[] { DesireAxis.Economy, DesireAxis.Development }));
            Assert.That(deferred.HasAxes, Is.False);
        }

        [Test]
        public void TakeWith_NoTrigger_AdmitsOnlyTheDeferredAxes()
        {
            var deferred = new DeferredStrategicAdmission();
            deferred.Defer(new[] { DesireAxis.Recon });

            Assert.That(deferred.TakeWith(null), Is.EquivalentTo(new[] { DesireAxis.Recon }));
            Assert.That(deferred.TakeWith(null), Is.Empty);
        }
    }
}
#endif
