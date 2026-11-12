#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Cards;
using Game.Combat;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Playtest 2026-10-02: a lone Flamer + hero was rated 0.88 against two neutral bodies and lost
    // four of four. The roster simulation spent half of the enemy's attacks on the hero, who stands
    // in the back row and never ends the fight — a hero must not make an army look stronger.
    public sealed class WorthItHeroTargetingTests
    {
        private static WorthIt.DefenderProfile Body(float attack, float defense, float hp) =>
            new WorthIt.DefenderProfile(defense, false, new List<UnitTypeTag>(), attack, hp, 2,
                new List<string>(), hp);

        private static WorthIt.DefenderProfile Hero(float hp) =>
            new WorthIt.DefenderProfile(2, false, new List<UnitTypeTag>(), 0, hp, 2,
                new List<string>(), hp, true, true, 0);

        [Test]
        public void EmptyHexControl_DoesNotInventARequiredFight()
        {
            var empty = new[] { new WorthIt.DefendingArmy(new WorthIt.DefenderProfile[0], default) };
            Assert.That(WorthIt.EstimateHexControl(new[] { Hero(4) }, default, empty, 0).WinChance,
                Is.EqualTo(1f));
        }

        [Test]
        public void HeroAlone_CannotDisplaceAnotherHero()
        {
            var defenders = new[] { new WorthIt.DefendingArmy(new[] { Hero(4) }, default) };
            Assert.That(WorthIt.EstimateHexControl(new[] { Hero(4) }, default, defenders, 0).WinChance,
                Is.Zero);
        }

        [TestCase(1)]
        [TestCase(2)]
        public void HexControlReadsStormFromOpposition_AndMatchesExplicitWeather(int armyCount)
        {
            var attackers = new[] { new WorthIt.DefenderProfile(2, false, attack: 3,
                hitPoints: 6, initiative: 1, range: 3),
                new WorthIt.DefenderProfile(2, false, attack: 3, hitPoints: 6, initiative: 1, range: 2) };
            var body = new[] { new WorthIt.DefenderProfile(2, false, attack: 3,
                hitPoints: 6, initiative: 1, range: 2) };
            var storm = new List<WorthIt.DefendingArmy>();
            var clear = new List<WorthIt.DefendingArmy>();
            for (int i = 0; i < armyCount; i++)
            {
                storm.Add(new WorthIt.DefendingArmy(body, default, 4f, i, 2f,
                    environment: BattleEnvironment.Sandstorm));
                clear.Add(new WorthIt.DefendingArmy(body, default, 4f, i, 2f));
            }
            var expected = WorthIt.EstimateHexControl(attackers, default, clear, 4f,
                environment: BattleEnvironment.Sandstorm);
            var inferred = WorthIt.EstimateHexControl(attackers, default, storm, 4f);
            Assert.That(inferred.WinChance, Is.EqualTo(expected.WinChance));
            Assert.That(inferred.ExpectedSurvivingHpRatioOnWin,
                Is.EqualTo(expected.ExpectedSurvivingHpRatioOnWin));
            Assert.That(WorthIt.EstimateSequential(attackers, default, storm, 4f).WinChance,
                Is.EqualTo(expected.WinChance), "capture-free fights share the same weather boundary");
        }

        [Test]
        public void ABackRowHeroIsNotADecoyForTheEnemyAttacks()
        {
            var enemy = new List<WorthIt.DefenderProfile> { Body(3, 2, 4), Body(3, 1, 4) };
            float alone = WorthIt.WinChance(new List<WorthIt.DefenderProfile> { Body(3, 2, 4) }, enemy);
            float withHero = WorthIt.WinChance(
                new List<WorthIt.DefenderProfile> { Body(3, 2, 4), Hero(10) }, enemy);
            Assert.That(withHero, Is.LessThanOrEqualTo(alone + 0.05f),
                "a fate-less hero standing behind one body must not raise the army's win chance");
            Assert.That(withHero, Is.LessThan(0.5f), "one body against two equal ones is not a favourite");
        }
    }
}
#endif
