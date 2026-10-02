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
