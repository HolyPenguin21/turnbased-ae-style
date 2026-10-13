#if UNITY_INCLUDE_TESTS
using Game.Cards;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // A load rebuilds objects under their saved identities; anything created afterwards must
    // never reuse one of them (missions, reservations and memory key on these ids).
    public sealed class RuntimeIdentityRestoreTests
    {
        [Test]
        public void RestoredArmyKeepsItsIdAndNewArmiesAreNumberedAbove()
        {
            int high = new ArmyData().Id + 1000;
            Assert.That(ArmyData.WithIdentity(high).Id, Is.EqualTo(high));
            Assert.That(new ArmyData().Id, Is.EqualTo(high + 1));
            Assert.That(ArmyData.WithIdentity(3).Id, Is.EqualTo(3), "a lower restored id never moves the sequence back");
            Assert.That(new ArmyData().Id, Is.EqualTo(high + 2));
        }

        [Test]
        public void RestoredUnitKeepsItsIdAndNewUnitsAreNumberedAbove()
        {
            int high = new UnitData().RuntimeId + 1000;
            Assert.That(UnitData.WithRuntimeId(high).RuntimeId, Is.EqualTo(high));
            Assert.That(new UnitData().RuntimeId, Is.EqualTo(high + 1));
            Assert.That(UnitData.WithRuntimeId(1).RuntimeId, Is.EqualTo(1));
            Assert.That(new UnitData().RuntimeId, Is.EqualTo(high + 2));
        }

        [Test]
        public void RestoredCardKeepsItsIdAndNewCardsAreNumberedAbove()
        {
            int high = new CardData(null).RuntimeId + 1000;
            Assert.That(CardData.WithRuntimeId(null, high).RuntimeId, Is.EqualTo(high));
            Assert.That(new CardData(null).RuntimeId, Is.EqualTo(high + 1));
        }
    }
}
#endif
