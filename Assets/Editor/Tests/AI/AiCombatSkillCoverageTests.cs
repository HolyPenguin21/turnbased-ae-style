#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Every combat skill must reach the AI's power reading (own AND remembered enemy units on the
    // same scale) and the card scoring registry, not only the battle simulations.
    public class AiCombatSkillCoverageTests
    {
        private static readonly string[] CombatSkills =
        {
            UnitAbilities.CriticalDamage, UnitAbilities.CeramicArmor, UnitAbilities.Berserk,
            UnitAbilities.ShockAttack, UnitAbilities.Hyperkinetic, UnitAbilities.Pyrokinetic,
            UnitAbilities.Splash, UnitAbilities.Scorcher, UnitAbilities.RaiseTheRots, UnitAbilities.Heal,
        };

        private static float ProfilePower(params string[] abilities) =>
            AiPower.EffectiveArmyPowerFromProfiles(new[]
            {
                new WorthIt.DefenderProfile(2f, false, null, 3f, 4f, 1, abilities),
            });

        [Test]
        public void EveryCombatSkill_RaisesRememberedEnemyPower()
        {
            float plain = ProfilePower();
            foreach (string skill in CombatSkills)
                Assert.That(ProfilePower(skill), Is.GreaterThan(plain), skill);
        }

        [Test]
        public void Berserk_PowerBump()
        {
            Assert.That(ProfilePower(UnitAbilities.Berserk),
                Is.EqualTo(ProfilePower() * (1f + AiConfigV2.powerBumpBerserk)).Within(1e-3f));
        }

        [Test]
        public void EveryCombatSkill_HasStrategicEffectRow()
        {
            foreach (string skill in CombatSkills)
                Assert.That(StrategicEffectRegistry.Resolve(new[] { skill }, 0), Is.Not.Empty, skill);
        }

        // The kernel prices these exchanges for equipment; the registry rows must stay out of
        // AttachmentValue (primaryExchange) so nothing is counted twice.
        [Test]
        public void KernelPricedSkills_AreCombatBodyPrimaryExchangeRows()
        {
            foreach (string skill in new[]
                     {
                         UnitAbilities.Pyrokinetic, UnitAbilities.ShockAttack,
                         UnitAbilities.Berserk, UnitAbilities.CeramicArmor,
                     })
            {
                StrategicEffect effect = StrategicEffectRegistry.Resolve(new[] { skill }, 0).Single();
                Assert.That(effect.Role, Is.EqualTo(IntendedRole.CombatBody), skill);
                Assert.That(effect.PrimaryExchange, Is.True, skill);
            }
        }

        // Weakest-body / donor / pre-filter comparisons read SkilledCombatValue: a skilled body is
        // worth more than a plain one with the same stats.
        [Test]
        public void SkilledCombatValue_CountsEveryCombatSkill()
        {
            WorthIt.DefenderProfile P(params string[] abilities) =>
                new WorthIt.DefenderProfile(2f, false, null, 3f, 4f, 1, abilities);
            float plain = AiPower.SkilledCombatValue(P());
            Assert.That(plain, Is.EqualTo(WorthIt.CombatValue(3f, 2f, 4f, 1)).Within(1e-4f));
            foreach (string skill in CombatSkills)
                Assert.That(AiPower.SkilledCombatValue(P(skill)), Is.GreaterThan(plain), skill);
        }

        // The retreat witness keys on this fingerprint: a skill swap at equal stats is a new fight,
        // while the same skills listed in another order are not.
        [Test]
        public void CombatFingerprint_SeesWhichSkills()
        {
            int Fp(params string[] abilities) => AttackTacticalOpportunity.CombatFingerprint(new[]
            {
                new WorthIt.DefenderProfile(2f, false, null, 3f, 4f, 1, abilities),
            });
            Assert.That(Fp(UnitAbilities.Splash), Is.Not.EqualTo(Fp(UnitAbilities.Pyrokinetic)));
            Assert.That(Fp(UnitAbilities.Splash, UnitAbilities.Berserk),
                Is.EqualTo(Fp(UnitAbilities.Berserk, UnitAbilities.Splash)));
        }

        // EnemyKnownStrength / near-deploy enemy power read a sighting on the AiPower scale (the
        // scale of our own FieldPower), skills included — not the raw attack+defense sum.
        [Test]
        public void SightingPower_IsEffectiveArmyPowerOfItsRoster()
        {
            var roster = new[]
            {
                new WorthIt.DefenderProfile(2f, false, null, 3f, 4f, 1, new[] { UnitAbilities.Splash }),
                new WorthIt.DefenderProfile(1f, false, null, 2f, 3f, 1),
            };
            var sighting = new Game.Ai.AiMapMemory.KnownEnemySighting(
                new Game.HexGrid.HexCoord(0, 0), null, "enemy", roster.Length, 3f, 5f, roster);
            Assert.That(AiPower.SightingPower(sighting),
                Is.EqualTo(AiPower.EffectiveArmyPowerFromProfiles(roster)).Within(1e-4f));
            Assert.That(AiPower.SightingPower(sighting), Is.Not.EqualTo(8f).Within(1e-3f));
        }

        // A hero never attacks: attacker-side skills add no CombatBody fit to a hero card, while
        // CeramicArmor (it is still hit) keeps its value.
        [Test]
        public void AttackerSkills_AddNoRoleFitOnHero()
        {
            float Fit(CardType type, string skill)
            {
                var def = new CardDefinition { cardType = type, attack = 3, defenseRating = 2, hitPoints = 4 };
                var ctx = new EffectEvaluationContext(null,
                    new MaterializationPlan { BaseCardInHand = new CardData(def) });
                return StrategicEffectRegistry.Contributions(IntendedRole.CombatBody,
                    skill == null ? new string[0] : new[] { skill }, 0, ctx).RoleFit;
            }
            float heroPlain = Fit(CardType.Hero, null);
            foreach (string skill in new[]
                     {
                         UnitAbilities.ShockAttack, UnitAbilities.Berserk, UnitAbilities.CriticalDamage,
                     })
            {
                Assert.That(Fit(CardType.Hero, skill), Is.EqualTo(heroPlain).Within(1e-5f), skill);
                Assert.That(Fit(CardType.Unit, skill), Is.GreaterThan(Fit(CardType.Unit, null)), skill);
            }
            Assert.That(Fit(CardType.Hero, UnitAbilities.CeramicArmor), Is.GreaterThan(heroPlain));
        }

        [Test]
        public void FlatCombatSkills_AddCombatBodyRoleFit()
        {
            var ctx = new EffectEvaluationContext(null);
            float plain = StrategicEffectRegistry.Contributions(
                IntendedRole.CombatBody, new string[0], 0, ctx).RoleFit;
            foreach (string skill in new[]
                     {
                         UnitAbilities.ShockAttack, UnitAbilities.Berserk, UnitAbilities.CeramicArmor,
                     })
                Assert.That(StrategicEffectRegistry.Contributions(
                    IntendedRole.CombatBody, new[] { skill }, 0, ctx).RoleFit, Is.GreaterThan(plain), skill);
        }
    }
}
#endif
