#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Playtest 2026-11-05 (Ashen T13-T14): the Citadel garrison stood 6/7 (four non-combat heroes,
    // two bodies) and the reserved last slot refused every one of five combat cards in hand, so the
    // garrison-floor demand had no deliverable chain and the Citadel fell. Owner decision: no slot
    // is held back; a garrison takes a card while it has a free slot.
    public class AiGarrisonDepositRoomTests
    {
        private static ArmyData Garrison(int commandRating, int bodies)
        {
            var g = new ArmyData { IsGarrison = true };
            g.Members.Add(new UnitData { IsHero = true, CommandRating = commandRating });
            for (int i = 0; i < bodies; i++)
                g.Members.Add(new UnitData());
            return g;
        }

        [Test]
        public void GarrisonWithOneFreeSlot_TakesADeposit()
        {
            ArmyData g = Garrison(commandRating: 7, bodies: 5); // 6/7
            Assert.That(g.Capacity, Is.EqualTo(7));
            Assert.That(PlacementRules.CanDepositIntoGarrison(g), Is.True);
        }

        [Test]
        public void FullGarrison_RefusesADeposit()
        {
            ArmyData g = Garrison(commandRating: 7, bodies: 6); // 7/7
            Assert.That(PlacementRules.CanDepositIntoGarrison(g), Is.False);
        }

        [Test]
        public void FieldArmy_IsNoGarrisonDeposit()
        {
            var field = new ArmyData();
            Assert.That(PlacementRules.CanDepositIntoGarrison(field), Is.False);
        }
    }
}
#endif
