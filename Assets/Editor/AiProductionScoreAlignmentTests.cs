#if UNITY_INCLUDE_TESTS
using System;
using System.Reflection;
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
            float resourceCost = StrategicCardEvaluator.StrategicResourceCostValue(plan.ResCost, snapshot);
            float expected = (0.75f * 2f * 0.7f - 0.4f)
                - resourceCost - 3f * AiConfigV2.stratCardApCostWeight
                - AiConfigV2.stratChainGenerationStepPenalty
                - AiConfigV2.stratChainAttachStepPenalty;
            float actual = DevelopmentOpportunityEvaluator.MaterializationValue(
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
            float weak = DevelopmentOpportunityEvaluator.MaterializationValue(
                opportunity, plan, null, null, null, null);
            opportunity.ProductionSupport = AiConfigV2.productionSupportMax;
            float strong = DevelopmentOpportunityEvaluator.MaterializationValue(
                opportunity, plan, null, null, null, null);
            Assert.That(strong - weak,
                Is.EqualTo(0.8f * (AiConfigV2.productionSupportMax - AiConfigV2.productionSupportMin))
                    .Within(0.0001f));
            opportunity.ExpectedGain = 0f;
            float zeroGain = DevelopmentOpportunityEvaluator.MaterializationValue(
                opportunity, plan, null, null, null, null);
            Assert.That(zeroGain, Is.LessThan(0f),
                "Production cannot turn a zero-benefit equipment attachment into a useful action");
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
