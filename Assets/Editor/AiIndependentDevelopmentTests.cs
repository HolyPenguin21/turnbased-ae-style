#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
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
        public void GeneratedOperatorClaimProtectsOnlyItsExactPhysicalCard()
        {
            var reservation = new MaterializationReservation();
            var def = new CardDefinition { cardType = CardType.Hero };
            var operatorCard = new CardData(def) { ResearchProductionCreated = true };
            var unrelatedCard = new CardData(def) { ResearchProductionCreated = true };
            Assert.That(reservation.ClaimsDevelopmentOperatorCard(operatorCard), Is.False);
            reservation.ClaimDevelopmentOperatorCard(operatorCard);
            Assert.That(reservation.ClaimsDevelopmentOperatorCard(operatorCard), Is.True);
            Assert.That(reservation.ClaimsDevelopmentOperatorCard(unrelatedCard), Is.False,
                "Same-definition Hero copies must remain available for ordinary missions");
            Assert.That(reservation.ClaimsDevelopmentOperatorCard(null), Is.False);
        }

        [Test]
        public void MintedOperatorSurvivesTurnBoundaryAndOnlyItsPhysicalCardIsClaimed()
        {
            var persistent = new MissionIntentState();
            var factory = new HexCoord(3, -2);
            var definition = new CardDefinition { cardType = CardType.Hero };
            var minted = new CardData(definition) { ResearchProductionCreated = true };
            var otherCopy = new CardData(definition) { ResearchProductionCreated = true };
            persistent.RememberGeneratedDevelopmentOperator(minted, factory,
                ResearchProductionMode.Production, 2);
            var nextTurn = new MaterializationReservation(); // not the T2 instance
            foreach (CardData card in persistent.ReconcileGeneratedDevelopmentOperators(3,
                (c, site, mode) => site.Equals(factory)
                    && mode == ResearchProductionMode.Production))
                nextTurn.ClaimDevelopmentOperatorCard(card);
            Assert.That(nextTurn.ClaimsDevelopmentOperatorCard(minted), Is.True);
            Assert.That(nextTurn.ClaimsDevelopmentOperatorCard(otherCopy), Is.False);
        }

        [Test]
        public void MintedOperatorClaimReleasesOnInvalidDestinationAndFiniteExpiry()
        {
            var persistent = new MissionIntentState();
            var minted = new CardData(new CardDefinition { cardType = CardType.Hero });
            var factory = new HexCoord(3, -2);
            persistent.RememberGeneratedDevelopmentOperator(minted, factory,
                ResearchProductionMode.Production, 2);
            var reservation = new MaterializationReservation();
            reservation.ClaimDevelopmentOperatorCard(minted);
            reservation.ReconcileDevelopmentOperatorCards(
                persistent.ReconcileGeneratedDevelopmentOperators(3, (c, site, mode) => false));
            Assert.That(reservation.ClaimsDevelopmentOperatorCard(minted), Is.False,
                "Lost, staffed, contested or full factory must release its Hero");

            persistent.RememberGeneratedDevelopmentOperator(minted, factory,
                ResearchProductionMode.Production, 2);
            int expiredTurn = 2 + System.Math.Max(1, AiConfigV2.commitmentStallTurns) + 1;
            Assert.That(persistent.ReconcileGeneratedDevelopmentOperators(expiredTurn,
                (c, site, mode) => true), Is.Empty,
                "No permanent reservation when AP or structural availability never recovers");
        }

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

    }
}
#endif
