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
        public void EconomyBaseAdmission_RejectsPlacementOnlyStrategicValue()
        {
            var placementOnly = new TaskScore(
                airfield: 8f,
                frontProgress: 2f,
                corridorAlignment: 8f,
                ownTerritoryProximity: 1.5f,
                terrainDefense: 3f);

            Assert.That(DemandLayer.HasMeaningfulBaseBenefit(placementOnly), Is.False,
                "airfield/front/corridor/defense may rank a site but cannot originate an Economy Base project");
        }

        [Test]
        public void EconomyBaseAdmission_AllowsEconomyNativeValue()
        {
            Assert.That(DemandLayer.HasMeaningfulBaseBenefit(
                new TaskScore(economicHexBenefit: 0.1f)), Is.True);
            Assert.That(DemandLayer.HasMeaningfulBaseBenefit(
                new TaskScore(payback: 0.1f)), Is.True);
            Assert.That(DemandLayer.HasMeaningfulBaseBenefit(
                new TaskScore(globalCardEffect: 0.1f)), Is.True,
                "GlobalCardEffect here is evaluated explicitly for IntendedRole.Economy");
        }

        [Test]
        public void EconomyBaseAdmission_StrategicPlacementCannotRescueNegativeEconomics()
        {
            var strategicallyExcellentButEconomicallyNegative = new TaskScore(
                economicHexBenefit: 1f,
                airfield: 8f,
                frontProgress: 8f,
                corridorAlignment: 8f,
                terrainDefense: 8f,
                cardPrice: 2f);

            Assert.That(strategicallyExcellentButEconomicallyNegative.Value, Is.GreaterThan(0f),
                "sanity: the full placement score is intentionally attractive");
            Assert.That(DemandLayer.EconomyBaseAdmissionValue(
                strategicallyExcellentButEconomicallyNegative), Is.LessThan(0f));
            Assert.That(DemandLayer.HasMeaningfulBaseBenefit(
                strategicallyExcellentButEconomicallyNegative), Is.False,
                "placement quality may rank admitted sites, but it cannot pay for the Economy project itself");
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
