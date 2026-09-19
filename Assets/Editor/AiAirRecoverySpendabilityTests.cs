#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiAirRecoverySpendabilityTests
    {
        [Test]
        public void T11_MandatoryReturnProtectsEnergyBeforePhaseA()
        {
            // Rust Bite has not activated this turn: 3 Energy total, 2 owed to its
            // mandatory return. A discretionary 2-Energy card must not spend that fuel.
            float spendable = StrategicSpendability.SpendableWithRecovery(
                ownerAwareSpendable: 3f, legacySpendable: 3f, unpaidRecoveryCost: 2f);
            Assert.That(spendable, Is.EqualTo(1f));
            Assert.That(spendable, Is.LessThan(2f));
        }

        [Test]
        public void IndependentEconomyAndReturnHoldsBothReducePhysicalSpendability()
        {
            float spendable = StrategicSpendability.SpendableWithRecovery(
                ownerAwareSpendable: 2f, legacySpendable: 3f, unpaidRecoveryCost: 2f);
            Assert.That(spendable, Is.Zero,
                "a separate Economy reservation cannot silently consume safety fuel");
        }

        [Test]
        public void LegacyProtectionIsNotSubtractedTwiceAndRecoveryReleasesItsOwnHold()
        {
            Assert.That(StrategicSpendability.SpendableWithRecovery(
                ownerAwareSpendable: 3f, legacySpendable: 1f, unpaidRecoveryCost: 2f),
                Is.EqualTo(1f), "legacy and recovery views of one reserve cannot stack twice");
            Assert.That(StrategicSpendability.SpendableWithRecovery(
                ownerAwareSpendable: 3f, legacySpendable: 3f, unpaidRecoveryCost: 0f),
                Is.EqualTo(3f), "a landed or already activated wing owes no further activation");
        }
    }
}
#endif
