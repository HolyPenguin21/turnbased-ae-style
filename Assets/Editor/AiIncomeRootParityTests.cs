#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class AiIncomeRootParityTests
    {
        private static readonly HexCoord Site = new HexCoord(84, -17);
        private HexMap _map;
        private PlayerRoot _collectorRoot;
        private PlayerRoot _buildingRoot;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            HexResourceBonusRegistry.Clear();
            PlayerRootRegistry.Clear();
            _map = new GameObject("income projection parity map").AddComponent<HexMap>();
            _map.SetData(2, 1f, new Dictionary<HexCoord, TerrainTypeEntry>
            {
                { Site, new TerrainTypeEntry
                    { resourceYields = new ResourceYields { materials = 1 } } },
            });
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            HexResourceBonusRegistry.Clear();
            PlayerRootRegistry.Clear();
            if (_collectorRoot != null) Object.DestroyImmediate(_collectorRoot.gameObject);
            if (_buildingRoot != null) Object.DestroyImmediate(_buildingRoot.gameObject);
            if (_map != null) Object.DestroyImmediate(_map.gameObject);
        }

        [Test]
        public void BuildingWithoutOwnerRootDoesNotConsumeYieldBeforeAnotherCollectorsArmy()
        {
            var collector = new PlayerSetupData();
            var buildingOwner = new PlayerSetupData();
            _collectorRoot = PlayerRoot.Create(collector, "registered collector");
            PlayerRootRegistry.Register(collector, _collectorRoot);

            var building = new BuildingData { Hex = Site, Owner = buildingOwner };
            building.Abilities.Add(UnitAbilities.CollectAbilityFor(ResourceType.Materials));
            BuildingRegistry.Register(Site, building);
            var army = new ArmyData { Hex = Site, Owner = collector };
            var unit = new UnitData { Owner = collector };
            unit.Abilities.Add(UnitAbilities.CollectAbilityFor(ResourceType.Materials));
            army.Members.Add(unit);
            ArmyRegistry.Register(army);

            Assert.That(IncomeProjection.IncomeFor(collector, ResourceType.Materials, _map), Is.EqualTo(1),
                "The turn collector skips the unregistered buildingRoot without consuming the hex.");
            Assert.That(IncomeProjection.IncomeFor(buildingOwner, ResourceType.Materials, _map), Is.Zero,
                "No PlayerRoot means no real income, even with a physical building.");
            Assert.That(_collectorRoot.GetResource(ResourceType.Materials), Is.Zero,
                "IncomeFor must remain a read-only projection.");

            _buildingRoot = PlayerRoot.Create(buildingOwner, "registered building owner");
            PlayerRootRegistry.Register(buildingOwner, _buildingRoot);
            Assert.That(IncomeProjection.IncomeFor(collector, ResourceType.Materials, _map), Is.Zero,
                "A registered building takes the same first cut as the actual turn collector.");
            Assert.That(IncomeProjection.IncomeFor(buildingOwner, ResourceType.Materials, _map), Is.EqualTo(1));
            Assert.That(_buildingRoot.GetResource(ResourceType.Materials), Is.Zero);
        }

        [Test]
        public void PlayerWithoutRootCannotReceiveProduceIncome()
        {
            var owner = new PlayerSetupData();
            var army = new ArmyData { Hex = Site, Owner = owner };
            var producer = new UnitData { Owner = owner };
            producer.Abilities.Add(UnitAbilities.ProduceAbilityFor(ResourceType.Tech));
            army.Members.Add(producer);
            ArmyRegistry.Register(army);

            Assert.That(IncomeProjection.IncomeFor(owner, ResourceType.Tech, _map), Is.Zero);
            _collectorRoot = PlayerRoot.Create(owner, "producer owner");
            PlayerRootRegistry.Register(owner, _collectorRoot);
            Assert.That(IncomeProjection.IncomeFor(owner, ResourceType.Tech, _map), Is.EqualTo(1));
        }
    }
}
#endif
