#if UNITY_INCLUDE_TESTS
using Game.Cards;
using Game.Combat;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class CombatBerserkMutationTests
    {
        [Test]
        public void CanonicalHitMutationStacksAttackAndFloorsDefenseAtOne()
        {
            var unit = new UnitData
            {
                Attack = 2,
                Defense = 2,
            };
            unit.Abilities.Add(UnitAbilities.Berserk);
            var magnitudes = new AbilityMagnitudes(
                criticalDamageMultiplier: 2f,
                hyperkineticBonusDamage: 2,
                ceramicArmorReduction: 1,
                pyrokineticBonusDamage: 2,
                berserkAttackGain: 1,
                berserkDefenseLoss: 1);

            Assert.That(ChallengeResult.ApplyBerserkOnHit(unit, magnitudes), Is.True);
            Assert.That(unit.Attack, Is.EqualTo(3));
            Assert.That(unit.Defense, Is.EqualTo(1));
            Assert.That(unit.BerserkStacks, Is.EqualTo(1));
            Assert.That(unit.BerserkDefenseLost, Is.EqualTo(1));

            Assert.That(ChallengeResult.ApplyBerserkOnHit(unit, magnitudes), Is.True);
            Assert.That(unit.Attack, Is.EqualTo(4));
            Assert.That(unit.Defense, Is.EqualTo(1),
                "Further Berserk hits must never reduce Defense below 1.");
            Assert.That(unit.BerserkStacks, Is.EqualTo(2));
            Assert.That(unit.BerserkDefenseLost, Is.EqualTo(1),
                "Only Defense that was actually lost may be restored after battle.");
        }

        [Test]
        public void NonBerserkVictimIsUntouched()
        {
            var unit = new UnitData { Attack = 4, Defense = 3 };

            Assert.That(ChallengeResult.ApplyBerserkOnHit(unit, AbilityMagnitudes.Default), Is.False);
            Assert.That(unit.Attack, Is.EqualTo(4));
            Assert.That(unit.Defense, Is.EqualTo(3));
            Assert.That(unit.BerserkStacks, Is.Zero);
            Assert.That(unit.BerserkDefenseLost, Is.Zero);
        }
    }
}
#endif
