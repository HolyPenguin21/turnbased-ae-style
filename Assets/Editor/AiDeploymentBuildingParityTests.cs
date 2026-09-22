#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiDeploymentBuildingParityTests
    {
        private static readonly HexCoord Site = new HexCoord(71, -23);

        [SetUp]
        public void SetUp() => BuildingRegistry.Clear();

        [TearDown]
        public void TearDown() => BuildingRegistry.Clear();

        [Test]
        public void MissingOrEmptyAbilityNeverAuthorizesGroundDeployment()
        {
            var owner = new PlayerSetupData();
            var building = new BuildingData { Hex = Site, Owner = owner };
            building.Abilities.Add(UnitAbilities.Barracks);
            BuildingRegistry.Register(Site, building);

            var missingRequirement = new CardDefinition
            {
                cardType = CardType.Unit,
                requiredBuildingAbility = null,
            };
            Assert.That(PlacementRules.HasRequiredBuilding(owner, Site, missingRequirement), Is.False);
            missingRequirement.requiredBuildingAbility = string.Empty;
            Assert.That(PlacementRules.HasRequiredBuilding(owner, Site, missingRequirement), Is.False);
        }

        [Test]
        public void RequiredAbilityMustBeOnOwnBuildingAtExactDestination()
        {
            var owner = new PlayerSetupData();
            var other = new PlayerSetupData();
            var building = new BuildingData { Hex = Site, Owner = owner };
            building.Abilities.Add(UnitAbilities.Barracks);
            BuildingRegistry.Register(Site, building);
            var unit = new CardDefinition
            {
                cardType = CardType.Unit,
                requiredBuildingAbility = UnitAbilities.Barracks,
            };

            Assert.That(PlacementRules.HasRequiredBuilding(owner, Site, unit), Is.True);
            Assert.That(PlacementRules.HasRequiredBuilding(other, Site, unit), Is.False);
            Assert.That(PlacementRules.HasRequiredBuilding(owner, new HexCoord(72, -23), unit), Is.False);
            unit.requiredBuildingAbility = UnitAbilities.Research;
            Assert.That(PlacementRules.HasRequiredBuilding(owner, Site, unit), Is.False);
        }
    }
}
#endif
