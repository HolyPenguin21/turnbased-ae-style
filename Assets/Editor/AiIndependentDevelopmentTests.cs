#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Regression coverage alongside AiProductionScoreAlignmentTests; no separate test harness.
    public sealed class AiIndependentDevelopmentTests
    {
        [Test]
        public void UsefulEquipmentOpportunityDoesNotRequireAnotherAxesDemand()
        {
            MethodInfo method = typeof(DemandLayer).GetMethod("DevelopmentDemands",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var opportunity = new DevelopmentOpportunity
            {
                Card = new CardDefinition { cardType = CardType.Equipment },
                BaseValue = 8f,
                ExpectedGain = 2f,
                RecipientKind = DevRecipientKind.HandCard,
            };
            var snapshot = new WorldSnapshot { Self = new SelfSnapshot() };
            var demands = (IEnumerable<AxisDemand>)method.Invoke(null, new object[]
            {
                snapshot, null, new[] { opportunity }, Array.Empty<AxisDemand>(),
                Array.Empty<MissionIntent>(), null, null, null,
            });

            AxisDemand upgrade = demands.Single(d => d.Capability == CapabilityKind.CardUpgrade);
            Assert.That(upgrade.RequestingAxis, Is.EqualTo(DesireAxis.Development));
            Assert.That(upgrade.DevOpportunity, Is.SameAs(opportunity));
            Assert.That(upgrade.Value, Is.EqualTo(8f),
                "Development's intrinsic value must not absorb another axis or radar weight");
        }

        [Test]
        public void ResourceCostIgnoresStockpileOfResourceAbsentFromChain()
        {
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Stockpile = new ResourceBundle { Human = 12f, Energy = 12f, Materials = 8f },
                    PerTurnIncome = new ResourceBundle { Energy = 1f, Materials = 1f },
                    Hand = Array.Empty<CardData>(),
                    Deck = Array.Empty<CardDefinition>(),
                },
            };
            var cost = new ResourceCost { energy = 4, materials = 3, tech = 0 };
            float noTech = StrategicCardEvaluator.StrategicResourceCostValue(cost, snapshot);
            snapshot.Self.Stockpile = new ResourceBundle
            {
                Human = 12f, Energy = 12f, Materials = 8f, Tech = 100f,
            };
            float abundantTech = StrategicCardEvaluator.StrategicResourceCostValue(cost, snapshot);
            Assert.That(noTech, Is.EqualTo(abundantTech).Within(0.0001f),
                "An irrelevant Tech stockpile must not affect the price of an Energy/Materials chain");
        }
    }
}
#endif
