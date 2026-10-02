#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiCollectorSpendAuthorityTests
    {
        [Test]
        public void EconomyCollectorMayDrawOnOtherBuildsDeferredHolds()
        {
            var collector = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy, Capability = CapabilityKind.CollectorCapability,
                EconomyResourceScarce = true,
            };
            SpendAuthority a = InfrastructureFulfillment.SpendAuthorityFor(collector);
            Assert.That(a.EconomyCompletesNow, Is.True);
            Assert.That(a.Owner, Is.Null);
        }

        [Test]
        public void SurplusResourceCollectorKeepsRespectingDeferredHolds()
        {
            Assert.That(InfrastructureFulfillment.SpendAuthorityFor(new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy, Capability = CapabilityKind.CollectorCapability,
                EconomyResourceScarce = false,
            }).IsNone, Is.True);
        }

        [Test]
        public void OtherDemandsKeepRespectingDeferredHolds()
        {
            Assert.That(InfrastructureFulfillment.SpendAuthorityFor(new AxisDemand
                { RequestingAxis = DesireAxis.Aggression, Capability = CapabilityKind.CollectorCapability, EconomyResourceScarce = true }).IsNone, Is.True);
            Assert.That(InfrastructureFulfillment.SpendAuthorityFor(new AxisDemand
                { RequestingAxis = DesireAxis.Economy, Capability = CapabilityKind.Hero }).EconomyCompletesNow, Is.False);
            Assert.That(InfrastructureFulfillment.SpendAuthorityFor(null).IsNone, Is.True);
        }
    }
}
#endif
