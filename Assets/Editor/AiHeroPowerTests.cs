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

        private static AiPower.PowerUnit Body(float power, UnitTypeTag tag = UnitTypeTag.Infantry) =>
            new AiPower.PowerUnit(power, new List<UnitTypeTag> { tag }, 1, false);

        private static AiPower.PowerUnit Leader(int command) =>
            new AiPower.PowerUnit(0f, new List<UnitTypeTag> { UnitTypeTag.Hero }, 1, true, command);

        private static void AssertNested(AiPower.ForcePotentials p)
        {
            Assert.That(p.UnitsReserve, Is.GreaterThanOrEqualTo(0f));
            Assert.That(p.HeroReserve, Is.GreaterThanOrEqualTo(0f));
            Assert.That(p.Field + p.UnitsReserve + p.HeroReserve, Is.EqualTo(p.Total).Within(1e-4f));
        }

        // ATK-F01 control case: Cassia T20 — empty hand and deck, one commander, more bodies
        // than its slots. The units ceiling once filled the commander's slot with a body.
        [Test]
        public void NestedPotentials_EmptyCards_NoReserve()
        {
            var map = new List<AiPower.PowerUnit> { Leader(3) };
            for (int i = 0; i < 5; i++) map.Add(Body(8f + i));
            AiPower.ForcePotentials p = AiPower.NestedPotentials(map,
                new List<AiPower.PowerUnit>(), new List<AiPower.PowerUnit>());
            AssertNested(p);
            Assert.That(p.UnitsReserve, Is.EqualTo(0f));
            Assert.That(p.HeroReserve, Is.EqualTo(0f));
            Assert.That(p.Total, Is.EqualTo(AiPower.TotalMilitaryPotential(map)));
        }

        [Test]
        public void NestedPotentials_CommandEight_TakesSevenBodies()
        {
            var map = new List<AiPower.PowerUnit> { Leader(8) };
            for (int i = 0; i < 8; i++) map.Add(Body(10f));
            var sevenLed = new List<AiPower.PowerUnit> { Leader(8) };
            for (int i = 0; i < 7; i++) sevenLed.Add(Body(10f));
            AiPower.ForcePotentials p = AiPower.NestedPotentials(map,
                new List<AiPower.PowerUnit> { Body(10f) }, new List<AiPower.PowerUnit>());
            AssertNested(p);
            Assert.That(p.Field, Is.EqualTo(AiPower.EffectiveArmyPower(sevenLed)).Within(1e-4f));
            Assert.That(p.UnitsReserve, Is.EqualTo(0f), "an equal body cannot join a full stack");
        }

        [Test]
        public void NestedPotentials_StrongBodyCard_IsUnitsReserve_WeakOneIsNot()
        {
            var map = new List<AiPower.PowerUnit> { Leader(3), Body(10f), Body(10f) };
            AiPower.ForcePotentials weak = AiPower.NestedPotentials(map,
                new List<AiPower.PowerUnit> { Body(1f) }, new List<AiPower.PowerUnit>());
            AssertNested(weak);
            Assert.That(weak.UnitsReserve, Is.EqualTo(0f));
            AiPower.ForcePotentials strong = AiPower.NestedPotentials(map,
                new List<AiPower.PowerUnit> { Body(30f) }, new List<AiPower.PowerUnit>());
            AssertNested(strong);
            Assert.That(strong.UnitsReserve, Is.GreaterThan(0f));
            Assert.That(strong.HeroReserve, Is.EqualTo(0f));
        }

        [Test]
        public void NestedPotentials_HeroCard_AddsOnlyThroughItsSlots()
        {
            var map = new List<AiPower.PowerUnit> { Leader(3) };
            for (int i = 0; i < 5; i++) map.Add(Body(10f));
            AiPower.ForcePotentials same = AiPower.NestedPotentials(map,
                new List<AiPower.PowerUnit>(), new List<AiPower.PowerUnit> { Leader(3) });
            AssertNested(same);
            Assert.That(same.HeroReserve, Is.EqualTo(0f), "an equal commander opens no slot");
            AiPower.ForcePotentials bigger = AiPower.NestedPotentials(map,
                new List<AiPower.PowerUnit>(), new List<AiPower.PowerUnit> { Leader(6) });
            AssertNested(bigger);
            Assert.That(bigger.HeroReserve, Is.GreaterThan(0f), "three more slots for map bodies");
        }

        [Test]
        public void NestedPotentials_NoHero_TwoBodyStack()
        {
            var map = new List<AiPower.PowerUnit> { Body(10f), Body(10f), Body(10f) };
            AiPower.ForcePotentials p = AiPower.NestedPotentials(map,
                new List<AiPower.PowerUnit> { Body(5f) }, new List<AiPower.PowerUnit>());
            AssertNested(p);
            Assert.That(p.Field, Is.EqualTo(AiPower.EffectiveArmyPower(
                new List<AiPower.PowerUnit> { Body(10f), Body(10f) })).Within(1e-4f));
            Assert.That(p.UnitsReserve, Is.EqualTo(0f));
        }

        [Test]
        public void HeroTags_AreNoTypeCoverage()
        {
            var taggedHero = new AiPower.PowerUnit(0f,
                new List<UnitTypeTag> { UnitTypeTag.Hero, UnitTypeTag.Bio }, 1, true, 4);
            var bodies = new List<AiPower.PowerUnit> { Body(10f), Body(10f) };
            var led = new List<AiPower.PowerUnit>(bodies) { taggedHero };
            Assert.That(AiPower.EffectiveArmyPower(led),
                Is.EqualTo(AiPower.EffectiveArmyPower(bodies)).Within(1e-4f));
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
