#if UNITY_INCLUDE_TESTS
using System;
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

    }
}
#endif
