#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Regression coverage alongside AiProductionScoreAlignmentTests; no separate test harness.
    public sealed class AiIndependentDevelopmentTests
    {
        [Test]
        public void GeneratedOperatorRequiresAuthenticQualifiedHeroAndPositiveChallengeChance()
        {
            var hero = new CardDefinition
            {
                cardType = CardType.Hero, authoredKey = "generated-assembler",
                grantedAbilities = new List<string> { UnitAbilities.Assembler },
            };
            var source = new GenerationStep { CardDef = hero, SuccessChance = 0.5f };
            Assert.That(DevelopmentOpportunityEvaluator.IsGeneratedOperatorCandidate(
                source, ResearchProductionMode.Production), Is.True);
            Assert.That(DevelopmentOpportunityEvaluator.IsGeneratedOperatorCandidate(
                source, ResearchProductionMode.Research), Is.False);

            source.SuccessChance = 0f;
            Assert.That(DevelopmentOpportunityEvaluator.IsGeneratedOperatorCandidate(
                source, ResearchProductionMode.Production), Is.False);
            source.SuccessChance = 0.5f;
            hero.authoredKey = null;
            Assert.That(DevelopmentOpportunityEvaluator.IsGeneratedOperatorCandidate(
                source, ResearchProductionMode.Production), Is.False);
            hero.authoredKey = "generated-assembler";
            hero.cardType = CardType.Unit;
            Assert.That(DevelopmentOpportunityEvaluator.IsGeneratedOperatorCandidate(
                source, ResearchProductionMode.Production), Is.False);
        }

        [Test]
        public void ProspectiveAviationStaysInCanonicalNonCombatLaneAndRequiresRealPlacement()
        {
            var plane = new CardDefinition
            {
                cardType = CardType.Unit, isAviation = true, authoredKey = "candidate-plane",
            };
            Assert.That(NonCombatCardPlayer.LaneFor(plane),
                Is.EqualTo(NonCombatCardPlayer.PlayKind.Aviation));
            float withoutRealFacility = NonCombatCardPlayer.ProjectedAviationInvestmentValue(
                plane, ResearchProductionMode.Production, new HexCoord(0, 0),
                null, null, null, null, null, null);
            Assert.That(withoutRealFacility, Is.EqualTo(float.NegativeInfinity),
                "An aviation-only catalog may justify investment only with a real airfield and operator");
        }

        [TestCase(CardType.Unit, CapabilityKind.FieldCombatPower)]
        [TestCase(CardType.Hero, CapabilityKind.Hero)]
        public void ProspectiveGeneratedBodyUsesCanonicalPlanAndCorrectSurplusCapability(
            CardType type, CapabilityKind expectedCapability)
        {
            var definition = new CardDefinition
            {
                cardType = type, authoredKey = "prospective-asset",
                resourceCost = new ResourceCost { energy = 4, materials = 3, tech = 0 },
            };
            var generation = new GenerationStep
            {
                CardDef = definition, SuccessChance = 0.8f,
                CardKey = "investment-preview:prospective-asset",
            };
            var option = new PlacementOption(new HexCoord(0, 0), DeploymentKind.NewArmy, null);
            var abilities = MaterializationChainMatching.EffectiveAbilities(definition, null);
            var plan = MaterializationPlanFactory.MakeGeneratedPlan(
                MaterializationChainKind.GenerateDeploy, null, generation,
                baseInHand: null, baseIdx: -1, generatedIsEquipment: false,
                opt: option, projected: abilities);
            plan.FinalCapability = MaterializationChainEnumerator.SurplusCapability(
                definition, abilities, option);

            Assert.That(plan.GeneratedBaseDef, Is.SameAs(definition));
            Assert.That(plan.Generation, Is.SameAs(generation));
            Assert.That(plan.FinalCapability, Is.EqualTo(expectedCapability));
            Assert.That(plan.ResCost.energy, Is.EqualTo(4));
            Assert.That(plan.ResCost.materials, Is.EqualTo(3));
            Assert.That(plan.ResCost.tech, Is.Zero,
                "An irrelevant zero-Tech requirement cannot enter the prospective chain");
            Assert.That(plan.ApCost, Is.GreaterThanOrEqualTo(
                ResearchProductionSystem.AttemptApCost(definition)),
                "Prospective Unit/Hero valuation must include the real Challenge AP");
        }

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

        [Test]
        public void ReadyEquipmentIgnoresFourResourceSupportWhileInvestmentRetainsIt()
        {
            var equipment = new CardDefinition
            {
                cardType = CardType.Equipment,
                resourceCost = new ResourceCost { energy = 4, materials = 3 },
            };
            var source = new GenerationStep { CardDef = equipment, ProducesEquipment = true };
            var snapshot = new WorldSnapshot
            {
                Development = new DevelopmentReadiness
                {
                    ProductionSupport = AiConfigV2.productionSupportMin,
                    SurplusFraction = 0f,
                },
                Self = new SelfSnapshot
                {
                    Stockpile = new ResourceBundle { Human = 12f, Energy = 12f, Materials = 8f },
                    PerTurnIncome = new ResourceBundle { Energy = 1f, Materials = 1f },
                    Hand = Array.Empty<CardData>(), Deck = Array.Empty<CardDefinition>(),
                },
            };
            MethodInfo score = typeof(DevelopmentOpportunityEvaluator).GetMethod(
                "Score", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(score, Is.Not.Null);
            var ready = new DevelopmentOpportunity
            {
                Card = equipment, Generation = source, SuccessChance = 0.8f, ExpectedGain = 8f,
            };
            score.Invoke(null, new object[] { ready, snapshot, null, null });
            float initially = ready.Ev;
            Assert.That(ready.ProductionSupport, Is.EqualTo(1f).Within(0.0001f));

            // Tech is absent from this chain. Neither unrelated Tech inventory nor a changed
            // four-resource investment signal may change its operational valuation.
            snapshot.Self.Stockpile = new ResourceBundle
            {
                Human = 12f, Energy = 12f, Materials = 8f, Tech = 100f,
            };
            snapshot.Development.ProductionSupport = AiConfigV2.productionSupportMax;
            snapshot.Development.SurplusFraction = 1f;
            score.Invoke(null, new object[] { ready, snapshot, null, null });
            Assert.That(ready.ProductionSupport, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(ready.Ev, Is.EqualTo(initially).Within(0.0001f));

            // The same card before a factory exists remains a different, investment decision.
            var preparation = new DevelopmentOpportunity
            {
                Card = equipment, SuccessChance = 0.8f, ExpectedGain = 8f,
            };
            score.Invoke(null, new object[] { preparation, snapshot, null, null });
            Assert.That(preparation.ProductionSupport,
                Is.EqualTo(AiConfigV2.productionSupportMax).Within(0.0001f));
        }
    }
}
#endif
