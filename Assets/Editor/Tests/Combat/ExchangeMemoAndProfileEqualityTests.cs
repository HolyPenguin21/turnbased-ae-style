#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Cards;
using Game.Combat;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // 2026-10-17 — the analytic exchange is memoised by (attack, defense, hp cap, modifier profile):
    // every combination of the modifier inputs must still equal the exhaustive tokens, on the first
    // call and on the cached one. DefenderProfile equality keeps the default struct semantics.
    public sealed class ExchangeMemoAndProfileEqualityTests
    {
        private static double Exhaustive(int a, int d, IList<string> attacker, IList<UnitTypeTag> tags,
            IList<string> defender, int cap, bool secondary, IList<string> side, out double hitShare)
        {
            double sum = 0, hits = 0;
            int possibilities = 1 << (a + d);
            for (int mask = 0; mask < possibilities; mask++)
            {
                int raw = 0;
                for (int i = 0; i < a; i++) if ((mask & (1 << i)) != 0) raw++;
                for (int i = 0; i < d; i++) if ((mask & (1 << (a + i))) != 0) raw--;
                int damage = ChallengeResult.ApplyAbilityModifiers(System.Math.Max(0, raw), (IEnumerable<string>)attacker,
                    (IReadOnlyCollection<UnitTypeTag>)tags, defender, AbilityMagnitudes.Default);
                if (damage > 0) hits++;
                if (secondary) damage = BattleSimulationKernel.SecondaryDamage(damage, side, AbilityMagnitudes.Default);
                sum += System.Math.Min(damage, cap);
            }
            hitShare = hits / possibilities;
            return sum / possibilities;
        }

        [Test]
        public void EveryModifierCombination_FirstAndCachedCall_EqualTheExhaustiveTokens()
        {
            for (int flags = 0; flags < 32; flags++)
            for (int secondary = 0; secondary < 2; secondary++)
            {
                var attacker = new List<string>();
                if ((flags & 1) != 0) attacker.Add(UnitAbilities.CriticalDamage);
                if ((flags & 2) != 0) attacker.Add(UnitAbilities.Hyperkinetic);
                if ((flags & 4) != 0) attacker.Add(UnitAbilities.Pyrokinetic);
                if ((flags & 8) != 0) attacker.Add(UnitAbilities.Ionic);
                var defender = (flags & 16) != 0 ? new List<string> { UnitAbilities.CeramicArmor } : new List<string>();
                var tags = new List<UnitTypeTag> { UnitTypeTag.Armored, UnitTypeTag.Bio, UnitTypeTag.Mechanical };
                var side = new List<string> { UnitAbilities.CeramicArmor };
                for (int a = 0; a <= 5; a++) for (int d = 0; d <= 4; d++)
                {
                    double expected = Exhaustive(a, d, attacker, tags, defender, 5, secondary == 1, side, out double hitShare);
                    for (int call = 0; call < 2; call++)
                    {
                        float value = BattleSimulationKernel.ExpectedExchangeDamage(a, d, attacker, tags, defender, 5,
                            out float hit, secondary == 1, side);
                        Assert.That(value, Is.EqualTo(expected).Within(0.00001), $"flags={flags} sec={secondary} a={a} d={d} call={call}");
                        Assert.That(hit, Is.EqualTo(hitShare).Within(0.00001));
                    }
                }
            }
        }

        [Test]
        public void ATagWithoutItsAbility_OrAnAbilityWithoutItsTag_ChangesNothing()
        {
            var plain = BattleSimulationKernel.ExpectedExchangeDamage(4, 1, new List<string>(), new List<UnitTypeTag>(),
                new List<string>(), 9, out _);
            Assert.That(BattleSimulationKernel.ExpectedExchangeDamage(4, 1, new List<string> { UnitAbilities.Hyperkinetic },
                new List<UnitTypeTag> { UnitTypeTag.Bio }, new List<string>(), 9, out _), Is.EqualTo(plain));
            Assert.That(BattleSimulationKernel.ExpectedExchangeDamage(4, 1, new List<string>(),
                new List<UnitTypeTag> { UnitTypeTag.Armored }, new List<string>(), 9, out _), Is.EqualTo(plain));
        }

        [Test]
        public void DefenderProfileEquality_IsFieldwise_WithItsOwnedListsByIdentity()
        {
            var a = new WorthIt.DefenderProfile(2, false, new[] { UnitTypeTag.Bio }, 3, 5, 1, new[] { UnitAbilities.Berserk });
            var copy = a;
            Assert.That(copy.Equals(a), Is.True);
            Assert.That(copy.GetHashCode(), Is.EqualTo(a.GetHashCode()));
            Assert.That(((object)copy).Equals(a), Is.True);
            // Same arguments build new owned lists: as with the default struct equality, not equal.
            var rebuilt = new WorthIt.DefenderProfile(2, false, new[] { UnitTypeTag.Bio }, 3, 5, 1, new[] { UnitAbilities.Berserk });
            Assert.That(rebuilt.Equals(a), Is.False);
        }
    }
}
#endif
