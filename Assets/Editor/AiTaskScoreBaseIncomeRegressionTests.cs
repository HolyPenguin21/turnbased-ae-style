#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiTaskScoreBaseIncomeRegressionTests
    {
        [Test]
        public void BaseDoesNotClaimAlreadyHarvestedArmyIncome()
        {
            var hex = new HexCoord(2, -1);
            var card = new CardDefinition { cardType = CardType.Base,
                grantedAbilities = new List<string>
                { UnitAbilities.CollectAbilityFor(ResourceType.Materials) } };
            var site = new EconomyBaseOpportunity { Hex = hex,
                HexYield = new ResourceBundle { Materials = 1 } };
            var snap = new WorldSnapshot { Self = new SelfSnapshot
            {
                Armies = new List<ArmySnapshot> { new ArmySnapshot
                {
                    Hex = hex, CollectionCapacity = new ResourceBundle { Materials = 1 }
                } }
            } };
            Assert.That(StrategicCardEvaluator.BaseCardMarginalGain(snap, site,
                card, ResourceType.Materials), Is.Zero);
            Assert.That(StrategicCardEvaluator.BaseCardMarginalYield(snap, site, card), Is.Zero);
            snap.Self.Armies = new List<ArmySnapshot>();
            Assert.That(StrategicCardEvaluator.BaseCardMarginalGain(snap, site,
                card, ResourceType.Materials), Is.EqualTo(1f));
            site.HexYield = new ResourceBundle { Materials = 2 };
            snap.Self.Armies = new List<ArmySnapshot> { new ArmySnapshot
            {
                Hex = hex, CollectionCapacity = new ResourceBundle { Materials = 1 }
            } };
            Assert.That(StrategicCardEvaluator.BaseCardMarginalGain(snap, site,
                card, ResourceType.Materials), Is.EqualTo(1f));
        }

        [Test]
        public void MultiResourceBaseUsesIndependentNetCollection()
        {
            var hex = new HexCoord(3, 0);
            var card = new CardDefinition { cardType = CardType.Base,
                grantedAbilities = new List<string>
                {
                    UnitAbilities.CollectAbilityFor(ResourceType.Materials),
                    UnitAbilities.CollectAbilityFor(ResourceType.Energy)
                } };
            var site = new EconomyBaseOpportunity { Hex = hex,
                HexYield = new ResourceBundle { Materials = 1, Energy = 2 } };
            var snap = new WorldSnapshot { Self = new SelfSnapshot
            {
                Armies = new List<ArmySnapshot> { new ArmySnapshot
                {
                    Hex = hex, CollectionCapacity = new ResourceBundle { Materials = 1 }
                } }
            } };
            Assert.That(StrategicCardEvaluator.BaseCardMarginalGain(snap, site,
                card, ResourceType.Materials), Is.Zero);
            Assert.That(StrategicCardEvaluator.BaseCardMarginalGain(snap, site,
                card, ResourceType.Energy), Is.EqualTo(1f));
            Assert.That(StrategicCardEvaluator.BaseCardMarginalYield(snap, site, card),
                Is.EqualTo(1f));
            Assert.That(IncomeProjection.MarginalOwnerCollectionAtHex(1, 0, 1, 1, true), Is.Zero);
        }

        [Test]
        public void ExtractionFacilitiesAreEligibleForLosslessBaseMerge()
        {
            var site = new BuildingData { HasTieredUnlock = false };
            var facility = new CardDefinition { cardType = CardType.Facility,
                grantedAbilities = new List<string>
                { UnitAbilities.CollectAbilityFor(ResourceType.Materials) } };
            site.FacilitySlots[0] = FacilityData.FromDefinition(facility);
            Assert.That(site.CollectedAmount(ResourceType.Materials), Is.EqualTo(1));
            Assert.That(InfrastructureActions.CanMergeIntoResourceSite(site), Is.True);
        }
    }
}
#endif
