#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using System.Collections.Generic;
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

            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat, Is.GreaterThan(0f),
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
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat, Is.Zero,
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
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat, Is.GreaterThan(0f),
                "A large defensive improvement must be visible through the active contextual combat delta");
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
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientUnit, snap).Combat, Is.GreaterThan(0f),
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
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientUnit, snap).Combat, Is.Zero,
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

            float handValue = StrategicCardEvaluator.EquipmentUpgradeValue(hand);
            float garrisonValue = StrategicCardEvaluator.EquipmentUpgradeValue(garrison);
            float fieldValue = StrategicCardEvaluator.EquipmentUpgradeValue(field);

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

            float known = StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat;
            Assert.That(known, Is.GreaterThan(0f),
                "A legitimately sighted neutral may contribute its current composition to Production valuation");

            neutral.Hex = new HexCoord(-20, 19);
            float movedBehindFog = StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat;
            Assert.That(movedBehindFog, Is.EqualTo(known).Within(0.0001f),
                "The neutral's hidden live Hex must not enter Production valuation once identity is known");

            snap.Known.NeutralSightings = System.Array.Empty<AiMapMemory.KnownEnemySighting>();
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat, Is.EqualTo(AiEquipmentTestMath.IntrinsicAttack(20)).Within(0.0001f),
                "An unknown neutral must not become a Production threat merely because TrueWorld can see it (the efficiency value stands, no fabricated threat)");
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

            float visible = StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat;
            Assert.That(visible, Is.GreaterThan(0f),
                "An honestly observed event guard is a legitimate neutral composition witness");
            snap.Known.EventGuards = new[]
            {
                new KnownEventGuardSnapshot(new HexCoord(-15, 12), guard, "event guard", 1),
            };
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat, Is.EqualTo(visible).Within(0.0001f),
                "Location must not leak from an event witness into production valuation");

            snap.Known.EventGuards = System.Array.Empty<KnownEventGuardSnapshot>();
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat, Is.EqualTo(AiEquipmentTestMath.IntrinsicAttack(20)).Within(0.0001f),
                "An undiscovered event must not be fabricated as a Production target (the efficiency value stands, no fabricated threat)");
        }

        [Test]
        public void AirEnemyCompositionParticipatesInEquipmentMatchupValuation()
        {
            var host = new CardData(new CardDefinition
            {
                cardType = CardType.Unit, attack = 1, defenseRating = 2, hitPoints = 8,
                grantedAbilities = new List<string> { UnitAbilities.AntiAir },
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
                        typeTags: new[] { UnitTypeTag.Aircraft }, attack: 5, hitPoints: 8),
                },
            };
            var snap = new WorldSnapshot
            {
                TrueWorld = new TrueWorldSnapshot { EnemyArmies = new[] { airArmy } },
            };

            float withAir = StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat;
            Assert.That(withAir, Is.GreaterThan(0f),
                "Enemy aviation composition must reach the same contextual combat valuation as ground composition");

            airArmy.ArmyId = 999;
            airArmy.Hex = new HexCoord(-30, 22);
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat, Is.EqualTo(withAir).Within(0.0001f),
                "Aviation composition may affect valuation, but its hidden identity/position must not");

            snap.TrueWorld.EnemyArmies = System.Array.Empty<ArmySnapshot>();
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat, Is.LessThan(withAir),
                "Without the aviation witness AA falls back to the default share of air enemies");
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
            float before = StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat;
            enemy.ArmyId = 900;
            enemy.Hex = new HexCoord(-10, 11);
            float after = StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat;
            Assert.That(after, Is.EqualTo(before).Within(0.0001f),
                "Only composition is permitted to reach equipment valuation, not a hidden target");

            snap.TrueWorld = null;
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(opportunity.Card, opportunity.RecipientCard, snap).Combat, Is.EqualTo(AiEquipmentTestMath.IntrinsicAttack(20)).Within(0.0001f),
                "With no composition available the original intrinsic equipment score must stand (the efficiency value stands, no fabricated threat)");
        }

        private static CardDefinition PolicyGear(EquipmentStat stat, int amount, bool flat = false)
        {
            var grant = new EquipmentGrant();
            grant.statChanges.Add(new EquipmentStatChange { stat = stat, amount = amount, isOverride = flat });
            return new CardDefinition { cardType = CardType.Equipment, equipment = grant };
        }

        private static CardDefinition PolicySkill(string ability)
        {
            var grant = new EquipmentGrant(); grant.addAbilities.Add(ability);
            return new CardDefinition { cardType = CardType.Equipment, equipment = grant };
        }

        private static WorldSnapshot PolicyWorld(params WorthIt.DefenderProfile[] opposition) =>
            new WorldSnapshot
            {
                Self = new SelfSnapshot { Deck = new[] { new CardDefinition
                    { cardType = CardType.Unit, attack = 6, defenseRating = 6, hitPoints = 6 } } },
                TrueWorld = new TrueWorldSnapshot { EnemyArmies = opposition.Length == 0
                    ? System.Array.Empty<ArmySnapshot>() : new[] { new ArmySnapshot { Members = opposition } } },
            };

        [Test]
        public void EquipmentWithoutFateChangeIsScoredWithoutProjectedFateKey()
        {
            var host = new CardData(new CardDefinition { cardType = CardType.Unit,
                attack = 3, defenseRating = 3, hitPoints = 5 });
            Assert.DoesNotThrow(() =>
                StrategicCardEvaluator.EquipmentDeltaParts(PolicyGear(EquipmentStat.Attack, 2), host));
        }

        [TestCase(AttachmentSlot.Equipment, false)]
        [TestCase(AttachmentSlot.Mutator, false)]
        [TestCase(AttachmentSlot.Equipment, true)]
        [TestCase(AttachmentSlot.Mutator, true)]
        public void FirstLiveAttachmentHasFullStatsAndIsScoredAgainstOpposition(AttachmentSlot slot, bool hero)
        {
            var unit = AttachmentSlotTests.Body(AttachmentSlotTests.Host(hero: hero));
            unit.HitPointsCurrent = 4; unit.MoveCurrent = 1; unit.Fate = 1;
            var attachment = AttachmentSlotTests.Attachment(slot,
                hero ? EquipmentStat.Fate : EquipmentStat.Attack, 2);
            var predicted = EquipmentSystem.PredictAttachment(attachment, unit);
            Assert.That(predicted.Stats.Keys, Is.EquivalentTo(
                EquipmentSystem.DefinitionStats(unit.OriginatingCard).Keys));
            Assert.That(predicted.Stats[EquipmentStat.HitPoints], Is.EqualTo(unit.HitPointsMax));
            Assert.That(predicted.Stats[EquipmentStat.Defense], Is.EqualTo(unit.Defense));
            var snap = PolicyWorld(new WorthIt.DefenderProfile(3, false, attack: 3, hitPoints: 5));
            var delta = StrategicCardEvaluator.EquipmentDeltaParts(attachment, unit, snap);
            Assert.That(delta.Total, Is.GreaterThan(0f));
            Assert.That(unit.Equipment, Is.Null);
            Assert.That(unit.Mutator, Is.Null);
            Assert.That(unit.HitPointsCurrent, Is.EqualTo(4));
            Assert.That(unit.MoveCurrent, Is.EqualTo(1));
            Assert.That(unit.Fate, Is.EqualTo(1));
            EquipmentSystem.ApplyAttachments(unit,
                slot == AttachmentSlot.Equipment ? attachment : null,
                slot == AttachmentSlot.Mutator ? attachment : null);
            Assert.That(unit.Attack, Is.EqualTo(predicted.Stats[EquipmentStat.Attack]));
            Assert.That(unit.FateMax, Is.EqualTo(predicted.Stats[EquipmentStat.Fate]));
            Assert.That(unit.Fate, Is.EqualTo(hero ? 3 : 1));
            Assert.That(unit.HitPointsCurrent, Is.EqualTo(4));
            Assert.That(unit.MoveCurrent, Is.EqualTo(1));
        }

        [Test]
        public void AbsolutePenetrationBeatsSmallPercentageGainOnWeakScout()
        {
            var scout = new CardData(new CardDefinition { cardType = CardType.Unit,
                attack = 1, defenseRating = 1, hitPoints = 4 });
            var snap = PolicyWorld(new WorthIt.DefenderProfile(6, false, attack: 5, hitPoints: 6));
            float small = StrategicCardEvaluator.EquipmentDeltaParts(PolicyGear(EquipmentStat.Attack, 1), scout, snap).Total;
            float flat = StrategicCardEvaluator.EquipmentDeltaParts(PolicyGear(EquipmentStat.Attack, 8, true), scout, snap).Total;
            Assert.That(flat, Is.GreaterThan(small * 4f));
        }

        [Test]
        public void AlreadyArmouredBodyPrefersPenetrationOverSurplusArmour()
        {
            var body = new CardData(new CardDefinition { cardType = CardType.Unit,
                attack = 2, defenseRating = 12, hitPoints = 6 });
            var snap = PolicyWorld(new WorthIt.DefenderProfile(5, false, attack: 5, hitPoints: 6));
            float armour = StrategicCardEvaluator.EquipmentDeltaParts(PolicyGear(EquipmentStat.Defense, 3), body, snap).Total;
            float weapon = StrategicCardEvaluator.EquipmentDeltaParts(PolicyGear(EquipmentStat.Attack, 6), body, snap).Total;
            Assert.That(weapon, Is.GreaterThan(armour * 4f));
        }

        [Test]
        public void StrongAttackRealisesSplashButSeparateSingleBodyArmiesDoNot()
        {
            var body = new CardData(new CardDefinition { cardType = CardType.Unit,
                attack = 12, defenseRating = 4, hitPoints = 6 });
            var enemy = new WorthIt.DefenderProfile(3, false, attack: 4, hitPoints: 8);
            var snap = PolicyWorld(enemy, enemy, enemy);
            float splash = StrategicCardEvaluator.EquipmentDeltaParts(PolicySkill(UnitAbilities.Splash), body, snap).Total;
            float attack = StrategicCardEvaluator.EquipmentDeltaParts(PolicyGear(EquipmentStat.Attack, 1), body, snap).Total;
            Assert.That(splash, Is.GreaterThan(attack));
            snap.TrueWorld.EnemyArmies = new[] { new ArmySnapshot { Members = new[] { enemy } },
                new ArmySnapshot { Members = new[] { enemy } }, new ArmySnapshot { Members = new[] { enemy } } };
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(PolicySkill(UnitAbilities.Splash), body, snap).Total, Is.Zero);
        }

        [Test]
        public void PyrokineticNeedsBioAndDuplicateSkillHasNoMarginalValue()
        {
            var body = new CardData(new CardDefinition { cardType = CardType.Unit,
                attack = 8, defenseRating = 4, hitPoints = 6 });
            var pyro = PolicySkill(UnitAbilities.Pyrokinetic);
            var bio = PolicyWorld(new WorthIt.DefenderProfile(4, false, new[] { UnitTypeTag.Bio }, 4, 8));
            var machine = PolicyWorld(new WorthIt.DefenderProfile(4, false, new[] { UnitTypeTag.Mechanical }, 4, 8));
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(pyro, body, bio).Total, Is.GreaterThan(0));
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(pyro, body, machine).Total, Is.Zero);
            body.Definition.grantedAbilities.Add(UnitAbilities.Pyrokinetic);
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(pyro, body, bio).Total, Is.Zero);
        }

        [Test]
        public void UsefulReserveUpgradeDoesNotNeedKnownEnemyButStockDoesNotValueEmptyGear()
        {
            var body = new CardData(new CardDefinition { cardType = CardType.Unit,
                attack = 2, defenseRating = 4, hitPoints = 6 });
            var snap = PolicyWorld();
            var gain = StrategicCardEvaluator.EquipmentDeltaParts(PolicyGear(EquipmentStat.Attack, 8, true), body, snap);
            Assert.That(StrategicCardEvaluator.EquipmentUpgradeValue(gain), Is.GreaterThan(0));
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(new CardDefinition
                { cardType = CardType.Equipment, equipment = new EquipmentGrant() }, body, snap).Total, Is.Zero);
            snap.Development = new DevelopmentReadiness { UpgradeTargetCount = 1, SurplusFraction = 1 };
            Assert.That(ForceNeedModel.DevelopmentNeed(snap), Is.GreaterThan(0));
            Assert.That(ForceNeedModel.JustifiedForceNeed(snap).Witnessed, Is.False);
        }

        [Test]
        public void HeroPrefersFateMoveAndRecceToOrdinaryAttackOrCommand()
        {
            var hero = new CardData(new CardDefinition { cardType = CardType.Hero, fate = 2, moveMax = 2 });
            var snap = PolicyWorld();
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(PolicyGear(EquipmentStat.Attack, 10), hero, snap).Total, Is.Zero);
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(PolicyGear(EquipmentStat.CommandRating, 5), hero, snap).Total, Is.Zero);
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(PolicyGear(EquipmentStat.Fate, 1), hero, snap).Total, Is.GreaterThan(0));
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(PolicyGear(EquipmentStat.MoveMax, 1), hero, snap).Total, Is.GreaterThan(0));
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(PolicySkill("r1s2"), hero, snap).Total, Is.GreaterThan(0));
        }

        [Test]
        public void ExchangeExpectationMatchesExhaustiveDiceIncludingSecondaryArmour()
        {
            for (int a = 0; a <= 5; a++) for (int d = 0; d <= 5; d++)
            {
                var attackAbilities = new[] { UnitAbilities.CriticalDamage, UnitAbilities.Pyrokinetic };
                var tags = new[] { UnitTypeTag.Bio };
                var armour = new[] { UnitAbilities.CeramicArmor };
                double sum = 0, side = 0, hits = 0;
                int possibilities = 1 << (a + d);
                for (int mask = 0; mask < possibilities; mask++)
                {
                    int raw = 0;
                    for (int i = 0; i < a; i++) if ((mask & (1 << i)) != 0) raw++;
                    for (int i = 0; i < d; i++) if ((mask & (1 << (a + i))) != 0) raw--;
                    int damage = ChallengeResult.ApplyAbilityModifiers(System.Math.Max(0, raw), attackAbilities, tags, armour, AbilityMagnitudes.Default);
                    sum += System.Math.Min(damage, 6); if (damage > 0) hits++;
                    side += System.Math.Min(BattleSimulationKernel.SecondaryDamage(damage, armour, AbilityMagnitudes.Default), 6);
                }
                float expectation = BattleSimulationKernel.ExpectedExchangeDamage(a, d, attackAbilities, tags, armour, 6, out float chance);
                Assert.That(expectation, Is.EqualTo(sum / possibilities).Within(0.00001));
                Assert.That(chance, Is.EqualTo(hits / possibilities).Within(0.00001));
                float secondary = BattleSimulationKernel.ExpectedExchangeDamage(a, d, attackAbilities, tags, armour, 6, out _, true, armour);
                Assert.That(secondary, Is.EqualTo(side / possibilities).Within(0.00001));
            }
        }

        [Test]
        public void PreparationUsesItsKnownTargetAndChangingItInvalidatesAdmission()
        {
            var player = new PlayerSetupData();
            var body = new CardData(new CardDefinition { authoredKey = "scout", cardType = CardType.Unit,
                attack = 8, defenseRating = 4, hitPoints = 6 });
            var firstHex = new HexCoord(0, 0); var secondHex = new HexCoord(2, 0);
            var bio = new WorthIt.DefenderProfile(4, false, new[] { UnitTypeTag.Bio }, 4, 8);
            var mechanical = new WorthIt.DefenderProfile(4, false, new[] { UnitTypeTag.Mechanical }, 4, 8);
            var intent = new MissionIntent { Kind = MissionKind.Attack, Status = IntentStatus.Active,
                Objective = new AttackIntent { Preparation = true,
                    Target = AttackTargetRef.For(firstHex, new PlayerSetupData(), AttackTargetKind.Base),
                    TargetRoster = new List<StrikeRosterSlot> { new StrikeRosterSlot("scout", false, 1, ForceSource.Hand) } } };
            intent.IntentKey = MissionIntentKey.For(intent);
            try
            {
                MissionIntentRegistry.GetOrCreate(player).Put(intent);
                var snap = PolicyWorld(); snap.Observer = player;
                snap.Known = new KnownSnapshot { EventGuards = new[] {
                    new KnownEventGuardSnapshot(firstHex, new AiMapMemory.GuardStrength(4, 4, new[] { bio }), "bio", 1),
                    new KnownEventGuardSnapshot(secondHex, new AiMapMemory.GuardStrength(4, 4, new[] { mechanical }), "mechanical", 1) } };
                var pyro = PolicySkill(UnitAbilities.Pyrokinetic);
                string before = Pipeline.DevelopmentAdmissionFacts(snap, new[] { intent });
                Assert.That(StrategicCardEvaluator.EquipmentPurposeLabel(snap, body, null), Is.EqualTo("Attack"));
                Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(pyro, body, snap).Total, Is.GreaterThan(0));
                intent.Attack.Target = AttackTargetRef.For(secondHex, new PlayerSetupData(), AttackTargetKind.Base);
                Assert.That(Pipeline.DevelopmentAdmissionFacts(snap, new[] { intent }), Is.Not.EqualTo(before));
                Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(pyro, body, snap).Total, Is.Zero);
                intent.Attack.Target = AttackTargetRef.None;
                float noTarget = StrategicCardEvaluator.EquipmentDeltaParts(pyro, body, snap).Total;
                float expected = AiConfigV2.equipPyrokineticFactor * 8 * AiConfigV2.equipDefaultBioShare
                    * AiConfigV2.equipAttackOffenseMult * AiConfigV2.equipCardValuePerE
                    / AiConfigV2.equipmentUpgradePersistence;
                Assert.That(noTarget, Is.EqualTo(expected).Within(1e-4f),
                    "An absent target is not a real objective at hex 0,0: only the Attack mission multiplier applies, no hex defence");
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void DeckBenchmarkAndScoutRequirementAreExactAdmissionInputs()
        {
            var player = new PlayerSetupData(); var snap = PolicyWorld(); snap.Observer = player;
            var intent = new MissionIntent { Kind = MissionKind.Scout, Status = IntentStatus.Active,
                PreferredMoverArmyId = 1, Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore } };
            try
            {
                MissionIntentRegistry.GetOrCreate(player).Put(intent);
                string original = Pipeline.DevelopmentAdmissionFacts(snap, new[] { intent });
                snap.Self.Deck[0].defenseRating++;
                string deckChanged = Pipeline.DevelopmentAdmissionFacts(snap, new[] { intent });
                Assert.That(deckChanged, Is.Not.EqualTo(original));
                intent.Scout.RequiresStealth = true;
                Assert.That(Pipeline.DevelopmentAdmissionFacts(snap, new[] { intent }), Is.Not.EqualTo(deckChanged));
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        [Test]
        public void SecondaryEffectsCoordinateBothSlotsAndRespectRemainingTargets()
        {
            var body = new CardData(new CardDefinition { cardType = CardType.Unit,
                attack = 12, defenseRating = 4, hitPoints = 6 });
            body.Equipment = PolicySkill(UnitAbilities.Splash);
            var scorcher = PolicySkill(UnitAbilities.Scorcher); scorcher.attachmentSlot = AttachmentSlot.Mutator;
            var bio = new WorthIt.DefenderProfile(3, false, new[] { UnitTypeTag.Bio }, 4, 8);
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(scorcher, body, PolicyWorld(bio, bio, bio)).Total, Is.Zero,
                "Splash already occupies both available secondary targets");
            Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(scorcher, body, PolicyWorld(bio, bio, bio, bio)).Total,
                Is.GreaterThan(0), "The remaining target makes Scorcher useful");
        }

        [Test]
        public void UsefulFateCannotCompensateForDisablingAssignedChallengeOperator()
        {
            var player = new PlayerSetupData();
            var definition = new CardDefinition { cardType = CardType.Hero, fate = 2, moveMax = 2 };
            definition.grantedAbilities.Add(UnitAbilities.Researcher);
            var hero = new UnitData { IsHero = true, Fate = 2, FateMax = 2, MoveMax = 2, OriginatingCard = definition };
            hero.Abilities.Add(UnitAbilities.Researcher);
            var intent = new MissionIntent { Kind = MissionKind.Development, Status = IntentStatus.Active,
                Objective = new DevelopmentIntent { Hero = hero } };
            try
            {
                MissionIntentRegistry.GetOrCreate(player).Put(intent);
                var snap = PolicyWorld(); snap.Observer = player;
                var gear = PolicyGear(EquipmentStat.Fate, 5);
                Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(gear, hero, snap).Total, Is.GreaterThan(0));
                gear.equipment.removeAbilities.Add(UnitAbilities.Researcher);
                Assert.That(StrategicCardEvaluator.EquipmentDeltaParts(gear, hero, snap).Total, Is.LessThan(0));
            }
            finally { MissionIntentRegistry.Clear(); }
        }

        // 2026-10-07 (user decision) — Regeneration restores 1 HP at the end of its owner's turn, between
        // battles: it is worth 0.5 x (Defense + HP) of its carrier, not a function of the enemy.
        [Test]
        public void RegenerationIsPricedFromTheCarriersDurabilityNotFromTheEnemy()
        {
            var body = new CardData(new CardDefinition { cardType = CardType.Unit,
                attack = 5, defenseRating = 3, hitPoints = 6 });
            var regen = PolicySkill(UnitAbilities.Regeneration);
            float weak = StrategicCardEvaluator.EquipmentDeltaParts(regen, body,
                PolicyWorld(new WorthIt.DefenderProfile(3, false, attack: 6, hitPoints: 6))).Total;
            float lethal = StrategicCardEvaluator.EquipmentDeltaParts(regen, body,
                PolicyWorld(new WorthIt.DefenderProfile(3, false, attack: 40, hitPoints: 6))).Total;
            Assert.That(weak, Is.GreaterThan(0f), "A wounded survivor heals at the end of its owner's turn");
            Assert.That(lethal, Is.LessThan(weak),
                "Healing pays only while the carrier lives to the tick: a lethal enemy leaves little to heal");
        }
    }
}
#endif
