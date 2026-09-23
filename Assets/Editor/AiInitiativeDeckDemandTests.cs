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
    }
}
#endif
