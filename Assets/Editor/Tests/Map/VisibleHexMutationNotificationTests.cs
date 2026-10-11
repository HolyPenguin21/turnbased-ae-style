#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // Regression for the memory-version optimization: a currently visible hex may change
    // without any change to the observer's vision radius or visited footprint.
    public sealed class VisibleHexMutationNotificationTests
    {
        private readonly HexCoord _hex = new HexCoord(3, 3);
        private PlayerSetupData _observer;
        private PlayerSetupData _owner;
        private PlayerSetupData _unrelated;
        private PlayerRoot _ownerRoot;

        [SetUp]
        public void SetUp()
        {
            VisionSystem.Clear();
            VisionSystem.Configure(null);
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            HexResourceBonusRegistry.Clear();
            PlayerRootRegistry.Clear();
            AiMapMemory.Clear();
            AiMapMemory.EnsureSubscribed();

            _observer = new PlayerSetupData { Nickname = "observer" };
            _owner = new PlayerSetupData { Nickname = "owner" };
            _unrelated = new PlayerSetupData { Nickname = "unrelated" };
            _ownerRoot = PlayerRoot.Create(_owner, "visible-hex-test-root");
            _ownerRoot.ActionPoints = 10;
            PlayerRootRegistry.Register(_owner, _ownerRoot);
            // An empty army is still a valid vision source; zero-radius vision on this
            // same hex is stable throughout each test.
            ArmyRegistry.Register(new ArmyData { Owner = _observer, Hex = _hex });
            Assert.That(VisionSystem.IsVisible(_observer, _hex), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            VisionSystem.Clear();
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            HexResourceBonusRegistry.Clear();
            PlayerRootRegistry.Clear();
            AiMapMemory.Clear();
            if (_ownerRoot != null)
                Object.DestroyImmediate(_ownerRoot.gameObject);
        }

        [Test]
        public void RepairOnVisibleHexRefreshesObservedHpWithoutRevealingOthers()
        {
            BuildingRegistry.Register(_hex, new BuildingData { Owner = _owner, Hex = _hex, IsBase = true });
            var wounded = new UnitData { Owner = _owner, Name = "wounded", HitPointsCurrent = 2, HitPointsMax = 5 };
            var enemy = new ArmyData { Owner = _owner, Hex = _hex };
            enemy.Members.Add(wounded);
            ArmyRegistry.Register(enemy);

            Assert.That(AiMapMemory.KnownEnemySightingAt(_observer, _hex).Value.Defenders[0].HitPoints,
                Is.EqualTo(2));
            int before = AiMapMemory.KnowledgeVersionFor(_observer);
            int unrelatedBefore = AiMapMemory.KnowledgeVersionFor(_unrelated);

            Assert.That(UnitRepair.TryRepair(wounded, _hex, _ownerRoot, out string failure), Is.True, failure);
            Assert.That(AiMapMemory.KnowledgeVersionFor(_observer), Is.EqualTo(before + 1));
            Assert.That(AiMapMemory.KnownEnemySightingAt(_observer, _hex).Value.Defenders[0].HitPoints,
                Is.EqualTo(5));
            Assert.That(AiMapMemory.KnowledgeVersionFor(_unrelated), Is.EqualTo(unrelatedBefore));
        }

        [Test]
        public void TransferWithinVisibleHexPublishesFinalRosterOnce()
        {
            var first = new UnitData { Owner = _owner, Name = "first", HitPointsCurrent = 4, HitPointsMax = 4 };
            var second = new UnitData { Owner = _owner, Name = "second", HitPointsCurrent = 4, HitPointsMax = 4 };
            var source = new ArmyData { Owner = _owner, Hex = _hex };
            source.Members.Add(first);
            source.Members.Add(second);
            var target = new ArmyData { Owner = _owner, Hex = _hex };
            ArmyRegistry.Register(source);
            ArmyRegistry.Register(target);
            Assert.That(AiMapMemory.AllKnownEnemySightings(_observer).Count(), Is.EqualTo(1));
            int before = AiMapMemory.KnowledgeVersionFor(_observer);

            Assert.That(ArmyActions.TransferMember(second, source, target, null, out string failure), Is.True, failure);
            Assert.That(AiMapMemory.KnowledgeVersionFor(_observer), Is.EqualTo(before + 1));
            var sightings = AiMapMemory.AllKnownEnemySightings(_observer).ToList();
            Assert.That(sightings.Single(s => s.ArmyId == source.Id).MemberCount, Is.EqualTo(1));
            Assert.That(sightings.Single(s => s.ArmyId == target.Id).MemberCount, Is.EqualTo(1));
        }

        [Test]
        public void FacilityPlacementRefreshesVisibleBuildingSlotsWithoutVisionChange()
        {
            var building = new BuildingData { Owner = _owner, Hex = _hex, IsBase = true };
            BuildingRegistry.Register(_hex, building);
            Assert.That(AiMapMemory.KnownBuildingAt(_observer, _hex).Value.FreeFacilitySlots, Is.EqualTo(2));
            int before = AiMapMemory.KnowledgeVersionFor(_observer);
            var facility = new CardDefinition { cardType = CardType.Facility, displayName = "collector" };
            facility.grantedAbilities.Add(UnitAbilities.CollectHuman);

            InfrastructureBuildOutcome outcome = InfrastructureActions.TryPlaceFacility(
                facility, _hex, _owner, 0, new ResourceCost());
            Assert.That(outcome.Ok, Is.True, outcome.FailReason);
            Assert.That(AiMapMemory.KnowledgeVersionFor(_observer), Is.EqualTo(before + 1));
            Assert.That(AiMapMemory.KnownBuildingAt(_observer, _hex).Value.FreeFacilitySlots, Is.EqualTo(1));
            Assert.That(AiMapMemory.KnownBuildingAt(_observer, _hex).Value.CollectedAmount(ResourceType.Human),
                Is.EqualTo(1));
        }
    }
}
#endif
