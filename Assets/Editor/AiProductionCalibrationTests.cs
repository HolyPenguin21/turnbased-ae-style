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
    public class AiProductionCalibrationTests
    {
        [TestCase(DesireAxis.Aggression)]
        [TestCase(DesireAxis.Recon)]
        [TestCase(DesireAxis.Economy)]
        [TestCase(DesireAxis.Development)]
        public void WorldTaskUrgency_UsesOneSharedBand(DesireAxis axis)
        {
            var demand = new AxisDemand { RequestingAxis = axis };
            demand.Value = AiConfigV2.taskScoreUrgencyRampLo;
            Assert.That(DemandUrgencyPolicy.Normalized(demand), Is.Zero);
            demand.Value = (AiConfigV2.taskScoreUrgencyRampLo + AiConfigV2.taskScoreUrgencyRampHi) * 0.5f;
            Assert.That(DemandUrgencyPolicy.Normalized(demand), Is.EqualTo(0.5f).Within(0.0001f));
            demand.Value = AiConfigV2.taskScoreUrgencyRampHi;
            Assert.That(DemandUrgencyPolicy.Normalized(demand), Is.EqualTo(1f).Within(0.0001f));
            Assert.That(DemandUrgencyPolicy.NormalizedWorldValue(demand.Value), Is.EqualTo(1f));
        }

        [Test]
        public void ResourceOpportunityCost_UsesSpendableInsteadOfGrossStockpile()
        {
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Stockpile = new ResourceBundle { Energy = 20f },
                    PerTurnIncome = new ResourceBundle(),
                    Hand = new[]
                    {
                        new CardData(new CardDefinition
                        {
                            cardType = CardType.Unit,
                            resourceCost = new ResourceCost { energy = 30 },
                        }),
                    },
                    Deck = Array.Empty<CardDefinition>(),
                },
            };
            var cost = new ResourceCost { energy = 4 };
            float gross = StrategicCardEvaluator.StrategicResourceCostValue(cost, snapshot);
            float spendable = StrategicCardEvaluator.StrategicResourceCostValue(cost, snapshot,
                type => type == ResourceType.Energy ? 6f : 0f);
            Assert.That(spendable, Is.GreaterThan(gross),
                "Reserved energy must be priced consistently in Phase A and Phase B");
        }

        [Test]
        public void ConcreteProductionCannotReceiveAnAdditionalGlobalSupportMultiplier()
        {
            var breakdown = new StrategicUseScoreBreakdown { RoleFit = 4f };
            var plan = new MaterializationPlan
            {
                Generation = new GenerationStep
                {
                    CardDef = new CardDefinition { cardType = CardType.Unit },
                    SuccessChance = 1f,
                },
            };
            var snapshot = new WorldSnapshot
            {
                Development = new DevelopmentReadiness
                {
                    ProductionSupport = AiConfigV2.productionSupportMin,
                },
            };
            MethodInfo method = typeof(StrategicCardEvaluator).GetMethod(
                "ProductionSupportAdjustment", BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { typeof(StrategicUseScoreBreakdown), typeof(MaterializationPlan),
                    typeof(WorldSnapshot), typeof(float) }, null);
            Assert.That(method, Is.Not.Null);
            float Adjust(float floor) => (float)method.Invoke(null,
                new object[] { breakdown, plan, snapshot, floor });
            Assert.That(Adjust(AiConfigV2.productionSupportEmergencyFloor), Is.Zero,
                "Only the actual production-chain cost may affect an offered card");
            snapshot.Development.ProductionSupport = AiConfigV2.productionSupportMax;
            Assert.That(Adjust(AiConfigV2.productionSupportEmergencyFloor), Is.Zero,
                "A global economic multiplier must not bypass chain-specific pricing");
            plan.Generation = null;
            Assert.That(Adjust(AiConfigV2.productionSupportEmergencyFloor), Is.Zero,
                "Direct hand card must not inherit a production-only multiplier");
        }
    }
}
#endif
