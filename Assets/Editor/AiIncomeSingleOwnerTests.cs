#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Reflection;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Turns;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class AiIncomeSingleOwnerTests
    {
        private static readonly HexCoord Site = new HexCoord(85, -17);
        private PlayerSetupData _player;
        private PlayerRoot _root;
        private HexMap _map;
        private GameConfig _config;
        private GameObject _turnObject;
        private GameTurnController _turn;
        private List<PlayerSetupData> _previousPlayers;

        [SetUp]
        public void SetUp()
        {
            _previousPlayers = GameSession.Players;
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            HexResourceBonusRegistry.Clear();
            PlayerRootRegistry.Clear();
            _player = new PlayerSetupData();
            GameSession.Players = new List<PlayerSetupData> { _player };
            _root = PlayerRoot.Create(_player, "actual income parity account");
            PlayerRootRegistry.Register(_player, _root);
            _map = new GameObject("actual income parity map").AddComponent<HexMap>();
            _map.SetData(2, 1f, new Dictionary<HexCoord, TerrainTypeEntry>
            {
                { Site, new TerrainTypeEntry
                    { resourceYields = new ResourceYields { materials = 3, energy = 1 } } },
            });
            _config = ScriptableObject.CreateInstance<GameConfig>();
            _turnObject = new GameObject("inactive income parity turn controller");
            _turnObject.SetActive(false);
            _turn = _turnObject.AddComponent<GameTurnController>();
            typeof(GameTurnController).GetField("map",
                BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_turn, _map);
            typeof(GameTurnController).GetField("gameConfig",
                BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_turn, _config);
        }

        [TearDown]
        public void TearDown()
        {
            GameSession.Players = _previousPlayers;
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            HexResourceBonusRegistry.Clear();
            PlayerRootRegistry.Clear();
            if (_turnObject != null) Object.DestroyImmediate(_turnObject);
            if (_config != null) Object.DestroyImmediate(_config);
            if (_map != null) Object.DestroyImmediate(_map.gameObject);
            if (_root != null) Object.DestroyImmediate(_root.gameObject);
        }

        private void CollectPhysical()
        {
            MethodInfo collect = typeof(GameTurnController).GetMethod("CollectResourceIncome",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(collect, Is.Not.Null);
            collect.Invoke(_turn, null);
        }

        private void GrantProduce()
        {
            MethodInfo grant = typeof(GameTurnController).GetMethod("GrantProduceResourceIncome",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(grant, Is.Not.Null);
            grant.Invoke(_turn, null);
        }

        [Test]
        public void BuildingTakesFirstSliceThenOwnArmyTakesOnlyRemainder()
        {
            var building = new BuildingData { Hex = Site, Owner = _player };
            building.Abilities.Add(UnitAbilities.CollectAbilityFor(ResourceType.Materials));
            var facility = new FacilityData();
            facility.Abilities.Add(UnitAbilities.CollectAbilityFor(ResourceType.Materials));
            building.FacilitySlots[0] = facility;
            BuildingRegistry.Register(Site, building);

            var army = new ArmyData { Owner = _player, Hex = Site };
            for (int i = 0; i < 2; i++)
            {
                var collector = new UnitData { Owner = _player };
                collector.Abilities.Add(UnitAbilities.CollectAbilityFor(ResourceType.Materials));
                army.Members.Add(collector);
            }
            ArmyRegistry.Register(army);

            var slices = new List<int>();
            IncomeProjection.ForEachHexCollectionGrant(_map, (account, type, amount) =>
            {
                if (account == _root && type == ResourceType.Materials)
                    slices.Add(amount);
            }, ResourceType.Materials);
            Assert.That(slices, Is.EqualTo(new[] { 2, 1 }),
                "The facility adds one building capacity, leaving only one unit of finite yield for the army.");
            Assert.That(IncomeProjection.IncomeFor(_player, ResourceType.Materials, _map), Is.EqualTo(3));
            Assert.That(_root.GetResource(ResourceType.Materials), Is.Zero,
                "A projection must not pay income.");

            CollectPhysical();
            Assert.That(_root.GetResource(ResourceType.Materials), Is.EqualTo(3));
            Assert.That(_root.GetResource(ResourceType.Energy), Is.Zero);
        }

        [Test]
        public void UnregisteredBuildingCannotConsumeRegisteredCollectorsLastUnit()
        {
            var ghostOwner = new PlayerSetupData();
            var building = new BuildingData { Hex = Site, Owner = ghostOwner };
            building.Abilities.Add(UnitAbilities.CollectAbilityFor(ResourceType.Energy));
            BuildingRegistry.Register(Site, building);
            var army = new ArmyData { Owner = _player, Hex = Site };
            var collector = new UnitData { Owner = _player };
            collector.Abilities.Add(UnitAbilities.CollectAbilityFor(ResourceType.Energy));
            army.Members.Add(collector);
            ArmyRegistry.Register(army);

            Assert.That(IncomeProjection.IncomeFor(_player, ResourceType.Energy, _map), Is.EqualTo(1));
            CollectPhysical();
            Assert.That(_root.GetResource(ResourceType.Energy), Is.EqualTo(1));
            Assert.That(PlayerRootRegistry.FindFor(ghostOwner), Is.Null);
        }

        [Test]
        public void ProduceProjectionMatchesRoundGrantAndExcludesPrisoners()
        {
            var building = new BuildingData { Owner = _player, Hex = Site };
            building.Abilities.Add(UnitAbilities.ProduceAbilityFor(ResourceType.Tech));
            var facility = new FacilityData();
            facility.Abilities.Add(UnitAbilities.ProduceAbilityFor(ResourceType.Tech));
            building.FacilitySlots[0] = facility;
            BuildingRegistry.Register(Site, building);
            var army = new ArmyData { Owner = _player, Hex = Site };
            var producer = new UnitData { Owner = _player };
            producer.Abilities.Add(UnitAbilities.ProduceAbilityFor(ResourceType.Tech));
            army.Members.Add(producer);
            ArmyRegistry.Register(army);
            var prison = new ArmyData { Owner = _player, Hex = Site, IsPrison = true };
            var prisonerProducer = new UnitData { Owner = _player };
            prisonerProducer.Abilities.Add(UnitAbilities.ProduceAbilityFor(ResourceType.Tech));
            prison.Members.Add(prisonerProducer);
            ArmyRegistry.Register(prison);

            Assert.That(IncomeProjection.CountInPlayAbilitySources(_player,
                UnitAbilities.ProduceAbilityFor(ResourceType.Tech)), Is.EqualTo(3));
            Assert.That(IncomeProjection.IncomeFor(_player, ResourceType.Tech, _map), Is.EqualTo(3));
            Assert.That(_root.GetResource(ResourceType.Tech), Is.Zero);
            CollectPhysical();
            Assert.That(_root.GetResource(ResourceType.Tech), Is.Zero,
                "Hex collection must not pay a flat Produce ability before its separate phase.");
            GrantProduce();
            Assert.That(_root.GetResource(ResourceType.Tech), Is.EqualTo(3),
                "The actual turn's Produce grant must match the read-only forecast exactly.");
        }
    }
}
#endif
