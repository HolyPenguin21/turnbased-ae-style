#if UNITY_INCLUDE_TESTS
using System;
using System.Reflection;
using System.Linq;
using Game.Combat;
using Game.Units;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public class AiProductionScoreAlignmentTests
    {
        [Test]
        public void MaterializationValue_ConvertsPowerToCardUnitsAndChargesCanonicalPlanCosts()
        {
            float powerUnit = Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate);
            var opportunity = new DevelopmentOpportunity
            {
                SuccessChance = 0.75f,
                ExpectedGain = powerUnit * 2f,
                AlternativeValue = powerUnit * 0.4f,
                ProductionSupport = 0.7f,
            };
            var plan = new MaterializationPlan
            {
                Kind = MaterializationChainKind.GenerateAttachUpgrade,
                ApCost = 3f,
                ResCost = new ResourceCost { energy = 4 },
            };
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Stockpile = new ResourceBundle { Energy = 20f },
                    PerTurnIncome = new ResourceBundle { Energy = 1f },
                    Hand = new[]
                    {
                        new CardData(new CardDefinition
                        {
                            resourceCost = new ResourceCost { energy = 10 },
                        }),
                    },
                    Deck = Array.Empty<CardDefinition>(),
                },
            };
            var equipment = new CardDefinition { cardType = CardType.Equipment };
            opportunity.Card = equipment;
            plan.Generation = new GenerationStep { CardDef = equipment, ProducesEquipment = true };
            plan.GeneratedEquipmentDef = equipment;
            float resourceCost = StrategicCardEvaluator.StrategicResourceCostValue(plan.ResCost, snapshot);
            float expected = (0.75f * 2f * 0.7f - 0.4f)
                - resourceCost - 3f * AiConfigV2.stratCardApCostWeight
                - AiConfigV2.stratChainGenerationStepPenalty
                - AiConfigV2.stratChainAttachStepPenalty;
            float actual = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, snapshot, null, null, null);
            Assert.That(actual, Is.EqualTo(expected).Within(0.0001f));
            Assert.That(actual, Is.LessThan(expected + 0.0001f),
                "Development's lifetime persistence multiplier must not be applied a second time in card competition");
        }

        [Test]
        public void MaterializationValue_RewardsOnlyMarginalStrengthAndRemainsSensitiveToEconomy()
        {
            float powerUnit = Mathf.Max(1f, AiConfigV2.combatPowerPerBodyEstimate);
            var opportunity = new DevelopmentOpportunity
            {
                SuccessChance = 0.8f, ExpectedGain = powerUnit,
                ProductionSupport = AiConfigV2.productionSupportMin,
            };
            var plan = new MaterializationPlan
            {
                Kind = MaterializationChainKind.GenerateAttachUpgrade,
                ApCost = 2f,
            };
            var equipment = new CardDefinition { cardType = CardType.Equipment };
            opportunity.Card = equipment;
            plan.Generation = new GenerationStep { CardDef = equipment, ProducesEquipment = true };
            plan.GeneratedEquipmentDef = equipment;
            float weak = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, null, null, null, null);
            opportunity.ProductionSupport = AiConfigV2.productionSupportMax;
            float strong = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, null, null, null, null);
            Assert.That(strong - weak,
                Is.EqualTo(0.8f * (AiConfigV2.productionSupportMax - AiConfigV2.productionSupportMin))
                    .Within(0.0001f));
            opportunity.ExpectedGain = 0f;
            float zeroGain = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, null, null, null, null);
            Assert.That(zeroGain, Is.LessThan(0f),
                "Production cannot turn a zero-benefit equipment attachment into a useful action");
        }

        [Test]
        public void ConcreteChainResourceCostIgnoresAResourceTheChainDoesNotConsume()
        {
            var chainCost = new ResourceCost { energy = 4, materials = 3, tech = 0 };
            WorldSnapshot Make(float tech) => new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Stockpile = new ResourceBundle
                    {
                        Human = 12f, Energy = 12f, Materials = 8f, Tech = tech,
                    },
                    PerTurnIncome = new ResourceBundle
                    {
                        Human = 1f, Energy = 1f, Materials = 1f, Tech = 0f,
                    },
                    Hand = new[]
                    {
                        new CardData(new CardDefinition
                        {
                            resourceCost = new ResourceCost { energy = 6, materials = 5, tech = 20 },
                        }),
                    },
                    Deck = Array.Empty<CardDefinition>(),
                },
            };

            float noTech = StrategicCardEvaluator.StrategicResourceCostValue(chainCost, Make(0f));
            float abundantTech = StrategicCardEvaluator.StrategicResourceCostValue(chainCost, Make(100f));

            Assert.That(noTech, Is.EqualTo(abundantTech).Within(0.0001f),
                "A resource with zero cost in the concrete production chain must not create a false operational penalty");
        }

        [Test]
        public void ConcreteChainResourceCostRespondsToScarcityOfAResourceItActuallyConsumes()
        {
            var chainCost = new ResourceCost { energy = 4 };
            WorldSnapshot Make(float energy) => new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Stockpile = new ResourceBundle { Energy = energy, Tech = 100f },
                    PerTurnIncome = new ResourceBundle { Energy = 0f, Tech = 10f },
                    Hand = new[]
                    {
                        new CardData(new CardDefinition
                        {
                            resourceCost = new ResourceCost { energy = 20 },
                        }),
                    },
                    Deck = Array.Empty<CardDefinition>(),
                },
            };

            float scarceEnergy = StrategicCardEvaluator.StrategicResourceCostValue(chainCost, Make(1f));
            float abundantEnergy = StrategicCardEvaluator.StrategicResourceCostValue(chainCost, Make(100f));

            Assert.That(scarceEnergy, Is.GreaterThan(abundantEnergy),
                "Scarcity of a resource actually consumed by the chain must raise its canonical opportunity cost");
        }

        [Test]
        public void GeneratedUnitCannotBeMistakenForAnExistingCardUpgrade()
        {
            var unit = new CardDefinition { cardType = CardType.Unit };
            var generation = new GenerationStep
            {
                CardDef = unit, ProducesEquipment = false, CardKey = "unit",
            };
            var opportunity = new DevelopmentOpportunity
            {
                Card = unit, Generation = generation,
                RecipientCard = new CardData(new CardDefinition { cardType = CardType.Unit }),
                SuccessChance = 1f, ExpectedGain = 99f, ProductionSupport = 1f,
            };
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Development,
                Capability = CapabilityKind.CardUpgrade,
                DevOpportunity = opportunity,
            };
            Assert.That(MaterializationPlanFactory.MakeDevelopmentUpgradePlan(demand), Is.Null,
                "A new combat body must use GenerateDeploy, not attach-to-existing-host valuation");
            var invalid = new MaterializationPlan
            {
                Kind = MaterializationChainKind.GenerateAttachUpgrade,
                GeneratedEquipmentDef = unit, Generation = generation,
            };
            Assert.That(StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, invalid, null, null, null, null), Is.EqualTo(float.NegativeInfinity));

            var equipment = new CardDefinition { cardType = CardType.Equipment, activationApCost = 1 };
            opportunity.Card = equipment;
            generation.CardDef = equipment;
            generation.ProducesEquipment = true;
            MaterializationPlan valid = MaterializationPlanFactory.MakeDevelopmentUpgradePlan(demand);
            Assert.That(valid, Is.Not.Null);
            Assert.That(valid.GeneratedEquipmentDef, Is.SameAs(equipment));
            Assert.That(valid.GeneratedBaseDef, Is.Null);
        }

        [Test]
        public void GenerationSupportDependsOnUnitOrEquipmentOutputNotFactoryVsLab()
        {
            var snap = new WorldSnapshot
            {
                Development = new DevelopmentReadiness
                {
                    ProductionSupport = AiConfigV2.productionSupportMin,
                },
            };
            MethodInfo support = typeof(StrategicCardEvaluator)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(m => m.Name == "ProductionSupportAdjustment"
                    && m.GetParameters().Length == 4
                    && m.GetParameters()[1].ParameterType == typeof(GenerationStep));
            float Evaluate(CardType type, ResearchProductionMode mode)
            {
                var score = new StrategicUseScoreBreakdown { RoleFit = 2f };
                var generation = new GenerationStep
                {
                    Mode = mode, CardDef = new CardDefinition { cardType = type },
                };
                return (float)support.Invoke(null, new object[] { score, generation, snap, 0f });
            }
            Assert.That(Evaluate(CardType.Unit, ResearchProductionMode.Research),
                Is.EqualTo(Evaluate(CardType.Unit, ResearchProductionMode.Production)).Within(0.0001f));
            Assert.That(Evaluate(CardType.Equipment, ResearchProductionMode.Research),
                Is.EqualTo(Evaluate(CardType.Unit, ResearchProductionMode.Production)).Within(0.0001f));
            Assert.That(Evaluate(CardType.Facility, ResearchProductionMode.Production), Is.Zero);
            Assert.That(Evaluate(CardType.Unit, ResearchProductionMode.Production), Is.LessThan(0f));
        }

        [Test]
        public void RaidEquipmentWitnessRequiresActualWorthItImprovement()
        {
            var primary = new UnitData
            {
                Attack = 1, Defense = 2, Initiative = 2,
                HitPointsCurrent = 8, HitPointsMax = 8,
            };
            var guards = new[]
            {
                new WorthIt.DefenderProfile(defense: 4, hasCeramicArmor: false,
                    attack: 6, hitPoints: 8, initiative: 2),
            };
            var moveOnly = new EquipmentGrant();
            moveOnly.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.MoveMax, amount = 3,
            });
            Assert.That(DemandLayer.ImprovesRaidCombatOutcome(
                primary, new[] { primary }, moveOnly, guards), Is.False,
                "Mobility alone cannot claim a WorthIt combat improvement against known guards");
            var weapon = new EquipmentGrant();
            weapon.statChanges.Add(new EquipmentStatChange
            {
                stat = EquipmentStat.Attack, amount = 20,
            });
            Assert.That(DemandLayer.ImprovesRaidCombatOutcome(
                primary, new[] { primary }, weapon, guards), Is.True,
                "A proven improvement in the primary's combat outcome can support its Raid");
            Assert.That(DemandLayer.ImprovesRaidCombatOutcome(
                primary, new[] { primary }, weapon, Array.Empty<WorthIt.DefenderProfile>()), Is.False,
                "An unobserved enemy cannot justify speculative Raid equipment");
            Assert.That(primary.Attack, Is.EqualTo(1),
                "Projection must never mutate the living army before generation/attachment");
            Assert.That(primary.Equipment, Is.Null);
        }

        [Test]
        public void EquipmentSupport_UsesTheProducedCardNotResearchOrProductionMode()
        {
            var snapshot = new WorldSnapshot
            {
                Development = new DevelopmentReadiness
                {
                    ProductionSupport = AiConfigV2.productionSupportMin,
                    SurplusFraction = 0.25f,
                },
            };
            MethodInfo score = typeof(DevelopmentOpportunityEvaluator).GetMethod(
                "Score", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(score, Is.Not.Null);
            DevelopmentOpportunity Make(ResearchProductionMode mode) => new DevelopmentOpportunity
            {
                Mode = mode,
                Card = new CardDefinition
                {
                    cardType = CardType.Equipment,
                    resourceCost = new ResourceCost(),
                },
                SuccessChance = 0.8f,
                ExpectedGain = 6f,
            };
            DevelopmentOpportunity research = Make(ResearchProductionMode.Research);
            DevelopmentOpportunity production = Make(ResearchProductionMode.Production);
            score.Invoke(null, new object[] { research, snapshot, null, null });
            score.Invoke(null, new object[] { production, snapshot, null, null });
            Assert.That(research.ProductionSupport,
                Is.EqualTo(AiConfigV2.productionSupportMin).Within(0.0001f));
            Assert.That(production.ProductionSupport,
                Is.EqualTo(research.ProductionSupport).Within(0.0001f));
            Assert.That(production.Ev, Is.EqualTo(research.Ev).Within(0.0001f));
        }
    }
}
#endif
