#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using NUnit.Framework;

namespace Game.EditorTests
{
    // A hero is the army's container, not a combat body: it adds no power of its own and shapes
    // an army only through its CommandRating slots, filled by bodies whose power sums.
    public class AiHeroPowerTests
    {
        private static CardDefinition Hero(int command, int fate = 5) => new CardDefinition
        {
            cardType = CardType.Hero, hitPoints = 6, initiative = 2, fate = fate,
            commandRating = command, unitTypeTags = new List<UnitTypeTag> { UnitTypeTag.Hero },
        };

        private static CardDefinition Infantry() => new CardDefinition
        {
            cardType = CardType.Unit, attack = 3, defenseRating = 1, hitPoints = 4, initiative = 1,
            resistanceRating = 0, range = 2, unitTypeTags = new List<UnitTypeTag> { UnitTypeTag.Bio, UnitTypeTag.Infantry },
        };

        [Test]
        public void HeroCard_HasNoCombatPower()
        {
            Assert.That(AiPower.ToPowerUnit(Hero(6)).BasePower, Is.EqualTo(0f));
            Assert.That(AiPower.ToPowerUnit(Hero(6)).CommandRating, Is.EqualTo(6));
            Assert.That(AiPower.ToPowerUnit(Infantry()).BasePower, Is.EqualTo(6.45f).Within(0.001f));
        }

        [Test]
        public void HeroDoesNotRaiseArmyPower_OnlyItsBodiesCount()
        {
            var infantryOnly = new List<AiPower.PowerUnit> { AiPower.ToPowerUnit(Infantry()) };
            var ledInfantry = new List<AiPower.PowerUnit>
                { AiPower.ToPowerUnit(Hero(6)), AiPower.ToPowerUnit(Infantry()) };
            Assert.That(AiPower.EffectiveArmyPower(ledInfantry),
                Is.EqualTo(AiPower.EffectiveArmyPower(infantryOnly)).Within(0.001f));
        }

        [Test]
        public void Peak_IsTheBestCommandersSlotsFilledWithBodies()
        {
            var pool = new List<AiPower.PowerUnit> { AiPower.ToPowerUnit(Hero(3, fate: 9)) };
            for (int i = 0; i < 5; i++)
                pool.Add(AiPower.ToPowerUnit(Infantry()));
            // Three slots: the hero (no power) + two bodies — never the hero's Fate.
            var twoBodies = new List<AiPower.PowerUnit>
                { AiPower.ToPowerUnit(Infantry()), AiPower.ToPowerUnit(Infantry()) };
            Assert.That(AiPower.TotalMilitaryPotential(pool),
                Is.EqualTo(AiPower.EffectiveArmyPower(twoBodies)).Within(0.001f));
        }

        [Test]
        public void EnemyHeroProfile_HasNoCombatPower()
        {
            var body = new WorthIt.DefenderProfile(1f, false, null, 3f, 4f, 1);
            var hero = new WorthIt.DefenderProfile(0f, false, null, 0f, 6f, 2,
                isGroundCombatant: false, isHero: true, fateMax: 5);
            Assert.That(AiPower.EffectiveArmyPowerFromProfiles(new[] { body, hero }),
                Is.EqualTo(AiPower.EffectiveArmyPowerFromProfiles(new[] { body })).Within(0.001f));
        }
    }
}
#endif
