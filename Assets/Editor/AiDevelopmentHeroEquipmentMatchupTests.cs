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

            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snapshot), Is.Zero,
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
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, army, snapshot), Is.Zero,
                "A deployed Hero is excluded from the same canonical combat roster");
            Assert.That(hero.Attack, Is.EqualTo(1), "Valuation must not modify a live Hero");
            Assert.That(hero.Equipment, Is.Null);
            Assert.That(heroCard.Equipment, Is.Null);
        }
    }
}
#endif
