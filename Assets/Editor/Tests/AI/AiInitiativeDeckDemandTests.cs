#if UNITY_INCLUDE_TESTS
using Game.Ai.V2.Initiative;
using Game.Cards;
using Game.Economy;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiInitiativeDeckDemandTests
    {
        [Test]
        public void PrepaidHandCopyHasNoDemandButOrdinaryCopyOfSameDefinitionDoes()
        {
            var definition = new CardDefinition
            {
                cardType = CardType.Unit,
                resourceCost = new ResourceCost(human: 3, energy: 2, materials: 4, tech: 1),
            };
            var prepaid = new CardData(definition) { ResearchProductionCreated = true };
            var ordinary = new CardData(definition);
            var demand = new int[InitiativeDeckDemand.Types.Length];

            InitiativeDeckDemand.AccumulateHand(new[] { prepaid, ordinary }, demand);

            Assert.That(demand, Is.EqualTo(new[] { 3, 2, 4, 1 }),
                "Only the ordinary copy needs payment; production already paid the other copy.");
            Assert.That(definition.resourceCost.human, Is.EqualTo(3),
                "Instance prepayment must never mutate the common card definition.");
        }

        [Test]
        public void UndrawnDeckRetainsFullCostAfterPrepaidHandCard()
        {
            var definition = new CardDefinition
            {
                cardType = CardType.Facility,
                resourceCost = new ResourceCost(energy: 5, materials: 2),
            };
            var demand = new int[InitiativeDeckDemand.Types.Length];

            InitiativeDeckDemand.AccumulateHand(new[]
                { new CardData(definition) { ResearchProductionCreated = true } }, demand);
            InitiativeDeckDemand.Accumulate(new[] { definition }, demand);

            Assert.That(demand, Is.EqualTo(new[] { 0, 5, 2, 0 }),
                "A prepaid instance does not make an undrawn definition free.");
        }

        [Test]
        public void NonPermanentCardsAndNullEntriesDoNotCreateBuildDemand()
        {
            var equipment = new CardDefinition
            {
                cardType = CardType.Equipment,
                resourceCost = new ResourceCost(human: 9),
            };
            var demand = new int[InitiativeDeckDemand.Types.Length];

            InitiativeDeckDemand.AccumulateHand(new CardData[] { null, new CardData(equipment) }, demand);
            InitiativeDeckDemand.Accumulate(new CardDefinition[] { null, equipment }, demand);

            Assert.That(demand, Is.EqualTo(new[] { 0, 0, 0, 0 }));
        }
        [Test]
        public void FundingPreservesEachResourceOfAnAcceptedBuild()
        {
            // An accepted Concord Base costs 3H/2E/1M/2T. Other resources may still fund
            // initiative; neither two affordable dice nor the greedy unit choice may eat the
            // exact vector needed to complete that build.
            var analysis = new PreTurnCapacityAnalysis();
            int[] available = { 4, 3, 3, 4 };
            int[] committed = { 3, 2, 1, 2 };
            for (int i = 0; i < available.Length; i++)
            {
                analysis.Available[i] = available[i];
                analysis.CommittedResourceFloor[i] = committed[i];
            }

            InitiativeFundingResult result = InitiativeFundingOptimizer.Plan(analysis, 0, 2);
            Assert.That(result.Feasible, Is.True);
            Assert.That(result.PaymentUnits.Count, Is.EqualTo(3));
            for (int i = 0; i < available.Length; i++)
            {
                int paid = 0;
                foreach (ResourceType unit in result.PaymentUnits)
                    if (unit == InitiativeDeckDemand.Types[i]) paid++;
                Assert.That(available[i] - paid, Is.GreaterThanOrEqualTo(committed[i]),
                    $"Committed resource {InitiativeDeckDemand.Types[i]} was spent.");
            }

            var noSurplus = new PreTurnCapacityAnalysis();
            for (int i = 0; i < available.Length; i++)
            {
                noSurplus.Available[i] = committed[i];
                noSurplus.CommittedResourceFloor[i] = committed[i];
            }
            Assert.That(InitiativeFundingOptimizer.Plan(noSurplus, 0, 1).Feasible, Is.False);
        }
    }
}
#endif
