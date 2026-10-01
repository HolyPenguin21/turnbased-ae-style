#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Combat;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Playtest 2026-10-01 #7: army power jumped up in Panel_Data while a battle ran. Berserk
    // stacks and summoned units are battle-only (reverted / stripped when it ends) and are never
    // part of an army's strength between battles.
    public class AiPowerBattleTransientTests
    {
        private static UnitData Body(int attack, int defense) => new UnitData
        {
            Name = "Body", Attack = attack, Defense = defense, HitPointsMax = 6, HitPointsCurrent = 6,
            Initiative = 2, MoveMax = 3, MoveCurrent = 3,
        };

        [Test]
        public void BerserkStacks_AreReadOutOfTheStatLine()
        {
            UnitData calm = Body(4, 3);
            UnitData raging = Body(4 + 2 * AbilityMagnitudes.Default.BerserkAttackGain, 1);
            raging.BerserkStacks = 2;
            raging.BerserkDefenseLost = 2;
            Assert.That(AiPower.StatLinePower(raging), Is.EqualTo(AiPower.StatLinePower(calm)).Within(1e-4f));
        }

        [Test]
        public void SummonedUnits_AreNotPartOfTheArmyStrength()
        {
            UnitData a = Body(4, 3), b = Body(5, 2);
            UnitData summoned = Body(6, 2);
            summoned.IsSummoned = true;
            Assert.That(AiPower.EffectiveArmyPower(new[] { a, b, summoned }),
                Is.EqualTo(AiPower.EffectiveArmyPower(new[] { a, b })).Within(1e-4f));
            Assert.That(AiPower.MilitaryPool(new[] { a, summoned }, null, null), Has.Count.EqualTo(1));
        }
    }
}
#endif
