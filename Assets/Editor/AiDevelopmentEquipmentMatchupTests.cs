#if UNITY_INCLUDE_TESTS
using Game.Ai;
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
        public void HandDefensiveEquipmentCanImproveMatchupAfterPenetrationAlreadyExists()
        {
            var host = new CardData(new CardDefinition
            {
                cardType = CardType.Unit,
                attack = 20, defenseRating = 1, hitPoints = 8, initiative = 2,
            });
            var armor = new EquipmentGrant();
            armor.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.Defense, amount = 20,
            });
            var opportunity = new DevelopmentOpportunity
            {
                Card = new CardDefinition { cardType = CardType.Equipment, equipment = armor },
                RecipientKind = DevRecipientKind.HandCard,
                RecipientCard = host,
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
                                new WorthIt.DefenderProfile(defense: 5, hasCeramicArmor: false,
                                    attack: 20, hitPoints: 8, initiative: 2),
                            },
                        },
                    },
                },
            };

            Assert.That(WorthIt.CanDamageAll(
                new[] { new WorthIt.DefenderProfile(1, false, attack: 20, hitPoints: 8, initiative: 2) },
                snap.TrueWorld.EnemyArmies[0].Members), Is.True,
                "The host must already have penetration so this regression exercises defensive value");
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snap), Is.EqualTo(1f),
                "A large defensive improvement must be visible through canonical WorthIt outcomes");
            Assert.That(host.Equipment, Is.Null,
                "Projected defensive valuation must not mutate the hand card");
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

            float handValue = DevelopmentOpportunityEvaluator.RecipientSelectionValue(hand);
            float garrisonValue = DevelopmentOpportunityEvaluator.RecipientSelectionValue(garrison);
            float fieldValue = DevelopmentOpportunityEvaluator.RecipientSelectionValue(field);

            Assert.That(garrisonValue, Is.EqualTo(handValue).Within(0.0001f));
            Assert.That(fieldValue, Is.EqualTo(handValue).Within(0.0001f),
                "Hand/garrison/field status must not recreate the removed fixed recipient multipliers");
        }

        [Test]
        public void KnownNeutralCompositionAffectsFitButUnknownNeutralAndHiddenHexDoNot()
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
            var defenders = new[]
            {
                new WorthIt.DefenderProfile(defense: 14, hasCeramicArmor: false,
                    attack: 5, hitPoints: 8),
            };
            var neutral = new ArmySnapshot
            {
                ArmyId = 77,
                Hex = new HexCoord(8, -3),
                Members = defenders,
            };
            var snap = new WorldSnapshot
            {
                Known = new KnownSnapshot
                {
                    NeutralSightings = new[]
                    {
                        new AiMapMemory.KnownEnemySighting(
                            new HexCoord(1, 1), null, "known neutral", 1, 14f, 5f,
                            defenders, armyId: 77),
                    },
                },
                TrueWorld = new TrueWorldSnapshot
                {
                    EnemyArmies = System.Array.Empty<ArmySnapshot>(),
                    NeutralArmies = new[] { neutral },
                },
            };

            float known = DevelopmentOpportunityEvaluator.EquipmentMatchupFit(opportunity, null, snap);
            Assert.That(known, Is.EqualTo(1f),
                "A legitimately sighted neutral may contribute its current composition to Production valuation");

            neutral.Hex = new HexCoord(-20, 19);
            float movedBehindFog = DevelopmentOpportunityEvaluator.EquipmentMatchupFit(opportunity, null, snap);
            Assert.That(movedBehindFog, Is.EqualTo(known).Within(0.0001f),
                "The neutral's hidden live Hex must not enter Production valuation once identity is known");

            snap.Known.NeutralSightings = System.Array.Empty<AiMapMemory.KnownEnemySighting>();
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snap), Is.Zero,
                "An unknown neutral must not become a Production threat merely because TrueWorld can see it");
        }

        [Test]
        public void KnownEventGuardImprovesEquipmentFitWithoutExposingAnUnknownEvent()
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
            var defenders = new[]
            {
                new WorthIt.DefenderProfile(defense: 14, hasCeramicArmor: false,
                    attack: 5, hitPoints: 8),
            };
            var guard = new AiMapMemory.GuardStrength(14f, 5f, defenders, "event guard");
            var snap = new WorldSnapshot
            {
                Known = new KnownSnapshot
                {
                    EventGuards = new[]
                    {
                        new KnownEventGuardSnapshot(new HexCoord(3, 4), guard, "event guard", 1),
                    },
                },
            };

            float visible = DevelopmentOpportunityEvaluator.EquipmentMatchupFit(opportunity, null, snap);
            Assert.That(visible, Is.EqualTo(1f),
                "An honestly observed event guard is a legitimate neutral composition witness");
            snap.Known.EventGuards = new[]
            {
                new KnownEventGuardSnapshot(new HexCoord(-15, 12), guard, "event guard", 1),
            };
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snap), Is.EqualTo(visible).Within(0.0001f),
                "Location must not leak from an event witness into production valuation");

            snap.Known.EventGuards = System.Array.Empty<KnownEventGuardSnapshot>();
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snap), Is.Zero,
                "An undiscovered event must not be fabricated as a Production target");
        }

        [Test]
        public void AirEnemyCompositionParticipatesInEquipmentMatchupValuation()
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
            var airArmy = new ArmySnapshot
            {
                IsAir = true,
                ArmyId = 501,
                Hex = new HexCoord(12, -7),
                Members = new[]
                {
                    new WorthIt.DefenderProfile(defense: 14, hasCeramicArmor: false,
                        attack: 5, hitPoints: 8),
                },
            };
            var snap = new WorldSnapshot
            {
                TrueWorld = new TrueWorldSnapshot { EnemyArmies = new[] { airArmy } },
            };

            float withAir = DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snap);
            Assert.That(withAir, Is.EqualTo(1f),
                "Enemy aviation composition must reach the same canonical WorthIt valuation as ground composition");

            airArmy.ArmyId = 999;
            airArmy.Hex = new HexCoord(-30, 22);
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snap), Is.EqualTo(withAir).Within(0.0001f),
                "Aviation composition may affect valuation, but its hidden identity/position must not");

            snap.TrueWorld.EnemyArmies = System.Array.Empty<ArmySnapshot>();
            Assert.That(DevelopmentOpportunityEvaluator.EquipmentMatchupFit(
                opportunity, null, snap), Is.Zero,
                "Without the aviation composition witness the matchup bonus must disappear");
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
