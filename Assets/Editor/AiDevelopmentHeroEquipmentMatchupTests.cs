#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Heroes may receive Equipment, but WorthIt's combat roster excludes Hero members.
    // EquipmentUpgradeUtilityFor evaluates their actual marginal benefits separately;
    // a Hero's Attack increase must not fabricate an additional damage-dealing body.
    public sealed class AiDevelopmentHeroEquipmentMatchupTests
    {
        [Test]
        public void StealthOnlyHeroMutatorAddsTheExistingStrategicTraitValue()
        {
            var host = new CardData(AttachmentSlotTests.Host(hero: true));
            var genome = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator);
            genome.equipment.statChanges.Clear();
            genome.equipment.addAbilities.Add("Stealth4");
            var delta = StrategicCardEvaluator.EquipmentDeltaParts(genome, host);
            Assert.That(delta.Combat, Is.Zero);
            Assert.That(delta.Tactical, Is.EqualTo(AiConfigV2.stratTraitMatchBonus * 0.5f).Within(0.0001f));
            Assert.That(host.Mutator, Is.Null);
            var deploy = new MaterializationPlan { BaseCardInHand = host, GeneratedEquipmentDef = genome };
            Assert.That(StrategicCardEvaluator.EquipmentUpgradeValue(deploy), Is.Zero,
                "A deploy chain prices final Stealth in SynergyValue, so its attachment delta must not price it again");
            host.Definition.grantedAbilities.Add("Stealth4");
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(genome, host).Total, Is.Zero,
                "Receiving an already-present trait is not a marginal upgrade");
        }

        [Test]
        public void LosingStealthIsASignedTacticalLossForHeroesAndBodies()
        {
            var strip = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator);
            strip.equipment.statChanges.Clear();
            strip.equipment.clearAbilityFamilies.Add(AbilityFamily.Stealth);
            foreach (bool hero in new[] { false, true })
            {
                var definition = AttachmentSlotTests.Host(hero: hero);
                definition.grantedAbilities.Add("Stealth4");
                var delta = StrategicCardEvaluator.EquipmentDeltaParts(strip, new CardData(definition));
                Assert.That(delta.Combat, Is.Zero,
                    "Stealth loss must not also be counted as a lost combat ability");
                Assert.That(delta.Tactical, Is.LessThan(0f));
            }
        }

        [Test]
        public void HeroRecipientDoesNotGetInventedCombatMatchupFromAttackEquipment()
        {
            var heroCard = new CardData(new CardDefinition
            {
                cardType = CardType.Hero,
                attack = 1, defenseRating = 2, hitPoints = 8,
            });
            var weapon = new EquipmentGrant();
            weapon.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.Attack, amount = 20,
            });
            var opportunity = new DevelopmentOpportunity
            {
                Card = new CardDefinition { cardType = CardType.Equipment, equipment = weapon },
                RecipientKind = DevRecipientKind.HandCard,
                RecipientCard = heroCard,
            };
            var snapshot = new WorldSnapshot
            {
                TrueWorld = new TrueWorldSnapshot
                {
                    EnemyArmies = new[]
                    {
                        new ArmySnapshot
                        {
                            Members = new[]
                            {
                                new WorthIt.DefenderProfile(defense: 14, hasCeramicArmor: false,
                                    attack: 5, hitPoints: 8, initiative: 2),
                            },
                        },
                    },
                },
            };

            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snapshot).Combat, Is.Zero,
                "Hand Heroes cannot be treated as new WorthIt combat bodies");

            var hero = new UnitData
            {
                IsHero = true, Attack = 1, Defense = 2,
                HitPointsCurrent = 8, HitPointsMax = 8,
            };
            var army = new ArmyData();
            army.Members.Add(hero);
            opportunity.RecipientKind = DevRecipientKind.FieldUnit;
            opportunity.RecipientCard = null;
            opportunity.RecipientUnit = hero;
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientUnit, snapshot).Combat, Is.Zero,
                "A deployed Hero is excluded from the same canonical combat roster");
            Assert.That(hero.Attack, Is.EqualTo(1), "Valuation must not modify a live Hero");
            Assert.That(hero.Equipment, Is.Null);
            Assert.That(heroCard.Equipment, Is.Null);
        }
    }
}
#endif
