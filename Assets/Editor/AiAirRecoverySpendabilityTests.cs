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
                ownerAwareSpendable: 3f, unpaidRecoveryCost: 2f);
            Assert.That(spendable, Is.EqualTo(1f));
            Assert.That(spendable, Is.LessThan(2f));
        }

        [Test]
        public void IndependentEconomyAndReturnHoldsBothReducePhysicalSpendability()
        {
            float spendable = StrategicSpendability.SpendableWithRecovery(
                ownerAwareSpendable: 2f, unpaidRecoveryCost: 2f);
            Assert.That(spendable, Is.Zero,
                "a separate Economy reservation cannot silently consume safety fuel");
        }

        [Test]
        public void RecoveryReleasesItsOwnHold()
        {
            Assert.That(StrategicSpendability.SpendableWithRecovery(
                ownerAwareSpendable: 3f, unpaidRecoveryCost: 0f),
                Is.EqualTo(3f), "a landed or already activated wing owes no further activation");
        }

        [Test]
        public void RecoveryProtectionStopsAtThePhysicallyAffordablePrefix()
        {
            Assert.That(StrategicSpendability.CanFundRecoveryPrefix(
                availableAp: 12f, availableEnergy: 3,
                alreadyCommittedAp: 0f, alreadyCommittedEnergy: 0,
                nextActivationAp: 1f, nextActivationEnergy: 2), Is.True);
            Assert.That(StrategicSpendability.CanFundRecoveryPrefix(
                availableAp: 12f, availableEnergy: 3,
                alreadyCommittedAp: 1f, alreadyCommittedEnergy: 2,
                nextActivationAp: 1f, nextActivationEnergy: 2), Is.False,
                "a second wing that cannot be activated must not protect another 2 Energy");
            Assert.That(StrategicSpendability.CanFundRecoveryPrefix(
                availableAp: 12f, availableEnergy: 1,
                alreadyCommittedAp: 0f, alreadyCommittedEnergy: 0,
                nextActivationAp: 1f, nextActivationEnergy: 2), Is.False,
                "a return already unaffordable at turn start must not freeze the last Energy");
            Assert.That(StrategicSpendability.CanFundRecoveryPrefix(
                availableAp: 0f, availableEnergy: 3,
                alreadyCommittedAp: 0f, alreadyCommittedEnergy: 0,
                nextActivationAp: 1f, nextActivationEnergy: 2), Is.False,
                "Energy should remain spendable when recovery has no physical AP");
        }
    }
}
#endif
