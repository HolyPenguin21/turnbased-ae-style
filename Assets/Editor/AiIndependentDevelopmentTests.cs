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

        [Test]
        public void UpgradePlanPricesTheWholeChallengeAndAttachmentExactlyOnce()
        {
            var equipment = new CardDefinition
            {
                cardType = CardType.Equipment,
                activationApCost = 2,
                resourceCost = new ResourceCost { energy = 4, materials = 3 },
            };
            var generation = new GenerationStep
            {
                CardDef = equipment, ProducesEquipment = true, CardKey = "factory:equipment",
            };
            var host = new CardData(new CardDefinition { cardType = CardType.Unit });
            var opportunity = new DevelopmentOpportunity
            {
                Card = equipment, Generation = generation, RecipientCard = host,
                RecipientKind = DevRecipientKind.HandCard, RecipientLabel = "hand:host",
            };
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Development,
                Capability = CapabilityKind.CardUpgrade,
                DevOpportunity = opportunity,
            };

            MaterializationPlan plan = MaterializationPlanFactory.MakeDevelopmentUpgradePlan(demand);
            Assert.That(plan, Is.Not.Null);
            Assert.That(plan.ApCost,
                Is.EqualTo(ResearchProductionSystem.AttemptApCost(equipment) + 2));
            Assert.That(plan.ResCost, Is.Not.Null);
            Assert.That(plan.ResCost.energy, Is.EqualTo(4));
            Assert.That(plan.ResCost.materials, Is.EqualTo(3));
            Assert.That(plan.ResCost.human, Is.Zero);
            Assert.That(plan.ResCost.tech, Is.Zero);
            Assert.That(plan.UpgradeTargetCard, Is.SameAs(host));
            Assert.That(plan.Generation, Is.SameAs(generation));
        }

        [Test]
        public void ReadyUpgradeDemandIgnoresSunkInvestmentEvButRequiresActualExpectedGain()
        {
            var op = new DevelopmentOpportunity
            {
                ExpectedGain = 12f, SuccessChance = 0.8f,
                Ev = -100f, AlternativeValue = 1000f,
                ResourceCostValue = 1000f,
            };
            float intrinsic = DevelopmentOpportunityEvaluator.ReadyOpportunityValue(op);
            Assert.That(intrinsic, Is.GreaterThan(0f),
                "A functioning factory's candidate must reach canonical card competition even if investment EV is negative");

            op.Ev = 100f;
            Assert.That(DevelopmentOpportunityEvaluator.ReadyOpportunityValue(op),
                Is.EqualTo(intrinsic).Within(0.0001f),
                "Prerequisite investment EV must not leak into operational demand value");
            op.SuccessChance = 0f;
            Assert.That(DevelopmentOpportunityEvaluator.ReadyOpportunityValue(op), Is.Zero,
                "A Challenge with no chance of producing equipment must raise no upgrade demand");
            op.SuccessChance = 1f;
            op.ExpectedGain = 0f;
            Assert.That(DevelopmentOpportunityEvaluator.ReadyOpportunityValue(op), Is.Zero,
                "No net improvement means no operational demand");
        }
    }
}
#endif
