#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Regression coverage for the existing DevelopmentOpportunityEvaluator, not another scorer.
    public sealed class AiDevelopmentEquipmentMatchupTests
    {
        [Test]
        public void HandEquipmentMatchupRewardsARealCounterNotUnrelatedMobility()
        {
            var host = new CardData(new CardDefinition
            {
                cardType = CardType.Unit,
                attack = 2, defenseRating = 2, hitPoints = 8, initiative = 2,
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
                RecipientCard = host,
            };
            var enemy = new ArmySnapshot
            {
                Members = new[]
                {
                    new WorthIt.DefenderProfile(defense: 14, hasCeramicArmor: false,
                        attack: 5, hitPoints: 8, initiative: 2),
                },
            };
            var snap = new WorldSnapshot
            {
                TrueWorld = new TrueWorldSnapshot { EnemyArmies = new[] { enemy } },
            };

            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snap), Is.EqualTo(1f),
                "A weapon that makes an otherwise impenetrable enemy damageable must improve fit");

            var mobility = new EquipmentGrant();
            mobility.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.MoveMax, amount = 3,
            });
            opportunity.Card = new CardDefinition
            {
                cardType = CardType.Equipment, equipment = mobility,
            };
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snap), Is.Zero,
                "Movement alone cannot masquerade as an improvement in the battle roster");
            Assert.That(host.Equipment, Is.Null,
                "Valuation must not attach the preview to the actual card");
        }

        [Test]
        public void DeployedEquipmentMatchupUsesActualRosterWithoutMutatingItsRecipient()
        {
            var unit = new UnitData
            {
                Attack = 1, Defense = 2, Initiative = 2,
                HitPointsCurrent = 8, HitPointsMax = 8,
            };
            var army = new ArmyData();
            army.Members.Add(unit);
            var weapon = new EquipmentGrant();
            weapon.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.Attack, amount = 20,
            });
            var opportunity = new DevelopmentOpportunity
            {
                Card = new CardDefinition { cardType = CardType.Equipment, equipment = weapon },
                RecipientKind = DevRecipientKind.FieldUnit,
                RecipientUnit = unit,
            };
            var snap = new WorldSnapshot
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
                opportunity, army, snap), Is.EqualTo(1f),
                "An existing field unit should benefit when its real army gains a new counter");

            var mobility = new EquipmentGrant();
            mobility.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.MoveMax, amount = 3,
            });
            opportunity.Card = new CardDefinition
            {
                cardType = CardType.Equipment, equipment = mobility,
            };
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, army, snap), Is.Zero,
                "Movement must not be mistaken for a combat improvement in a deployed roster");
            Assert.That(unit.Attack, Is.EqualTo(1));
            Assert.That(unit.Equipment, Is.Null);
            Assert.That(army.Members.Count, Is.EqualTo(1));
        }

        [Test]
        public void RecipientStatusDoesNotCreateValueWithoutARealDifference()
        {
            var hand = new DevelopmentOpportunity
            {
                RecipientKind = DevRecipientKind.HandCard,
                ExpectedGain = 10f,
            };
            var garrison = new DevelopmentOpportunity
            {
                RecipientKind = DevRecipientKind.GarrisonUnit,
                ExpectedGain = 10f,
            };
            var field = new DevelopmentOpportunity
            {
                RecipientKind = DevRecipientKind.FieldUnit,
                ExpectedGain = 10f,
            };

            float handValue = DevelopmentOpportunityEvaluator.RecipientSelectionValue(hand, null, null);
            float garrisonValue = DevelopmentOpportunityEvaluator.RecipientSelectionValue(garrison, null, null);
            float fieldValue = DevelopmentOpportunityEvaluator.RecipientSelectionValue(field, null, null);

            Assert.That(garrisonValue, Is.EqualTo(handValue).Within(0.0001f));
            Assert.That(fieldValue, Is.EqualTo(handValue).Within(0.0001f),
                "Hand/garrison/field status must not recreate the removed fixed recipient multipliers");
        }

        [Test]
        public void HiddenEnemyCoordinatesAndIdentityCannotAffectCompositionOnlyFit()
        {
            var host = new CardData(new CardDefinition
            {
                cardType = CardType.Unit, attack = 1, defenseRating = 2, hitPoints = 8,
            });
            var weapon = new EquipmentGrant();
            weapon.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.Attack, amount = 20,
            });
            var opportunity = new DevelopmentOpportunity
            {
                Card = new CardDefinition { cardType = CardType.Equipment, equipment = weapon },
                RecipientKind = DevRecipientKind.HandCard, RecipientCard = host,
            };
            var enemy = new ArmySnapshot
            {
                ArmyId = 1, Hex = new HexCoord(3, 4),
                Members = new[]
                {
                    new WorthIt.DefenderProfile(defense: 14, hasCeramicArmor: false,
                        attack: 5, hitPoints: 8),
                },
            };
            var snap = new WorldSnapshot
            {
                TrueWorld = new TrueWorldSnapshot { EnemyArmies = new[] { enemy } },
            };
            float before = DevelopmentOpportunityEvaluator.EquipmentMatchupFit(opportunity, null, snap);
            enemy.ArmyId = 900;
            enemy.Hex = new HexCoord(-10, 11);
            float after = DevelopmentOpportunityEvaluator.EquipmentMatchupFit(opportunity, null, snap);
            Assert.That(after, Is.EqualTo(before).Within(0.0001f),
                "Only composition is permitted to reach equipment valuation, not a hidden target");

            snap.TrueWorld = null;
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snap), Is.Zero,
                "With no composition available the original intrinsic equipment score must stand");
        }
    }
}
#endif