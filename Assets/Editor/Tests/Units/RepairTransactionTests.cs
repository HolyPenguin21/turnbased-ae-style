#if UNITY_INCLUDE_TESTS
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // Native Unity witnesses for payment, modal ownership and Phase-B reservation revalidation.
    public sealed class RepairTransactionTests
    {
        private PlayerSetupData _owner;
        private PlayerRoot _root;
        private ArmyData _source, _army;
        private UnitData _unit;
        private GameObject _ui;
        private static readonly HexCoord Field = new HexCoord(4, 2);
        [SetUp] public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear();
            StrategicResourceReservationLedger.ClearAll(); VisionSystem.Clear(); VisionSystem.Configure(null);
            _owner = new PlayerSetupData(); _root = PlayerRoot.Create(_owner, "repair-test");
            PlayerRootRegistry.Register(_owner, _root); _root.ActionPoints = 5;
            _root.AddResource(ResourceType.Materials, 5);
            var mechanic = new UnitData { Owner = _owner, HitPointsCurrent = 3, HitPointsMax = 3 };
            mechanic.Abilities.Add(UnitAbilities.Repair);
            _source = new ArmyData { Owner = _owner, Hex = Field }; _source.Members.Add(mechanic);
            ArmyRegistry.Register(_source);
            _unit = new UnitData { Owner = _owner, Name = "wounded", HitPointsCurrent = 1,
                HitPointsMax = 6, MoveMax = 3, MoveCurrent = 1, Fate = 1, FateMax = 3,
                Attack = 4, Defense = 2, RepairResourceCost = new ResourceCost { materials = 2 } };
            _army = new ArmyData { Owner = _owner, Hex = Field }; _army.Members.Add(_unit);
            ArmyRegistry.Register(_army);
        }
        [TearDown] public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear(); VisionSystem.Clear();
            StrategicResourceReservationLedger.ClearAll();
            if (_ui != null) Object.DestroyImmediate(_ui);
            Object.DestroyImmediate(_root.gameObject);
        }
        [Test] public void RepairPaysOnceRestoresOnlyHpAndCannotRepeat()
        {
            var price = _unit.RepairResourceCost;
            Assert.That(UnitRepair.TryRepair(_unit, Field, _root, out var reason), Is.True, reason);
            Assert.That(_root.ActionPoints, Is.EqualTo(4));
            Assert.That(_root.GetResource(ResourceType.Materials), Is.EqualTo(3));
            Assert.That(_unit.HitPointsCurrent, Is.EqualTo(6));
            Assert.That(_unit.MoveCurrent, Is.EqualTo(1)); Assert.That(_unit.Fate, Is.EqualTo(1));
            Assert.That(_unit.RepairResourceCost, Is.SameAs(price));
            Assert.That(UnitRepair.TryRepair(_unit, Field, _root, out _), Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(4));
        }
        [TestCase("sourceMoved")][TestCase("sourceDead")][TestCase("targetMoved")]
        [TestCase("targetRemoved")][TestCase("targetDead")][TestCase("prisoner")]
        [TestCase("ap")][TestCase("resources")]
        public void RejectedRepairNeverSpends(string reason)
        {
            switch (reason) {
                case "sourceMoved": ArmyRegistry.MoveArmy(_source, new HexCoord(9, 0)); break;
                case "sourceDead": _source.Members[0].HitPointsCurrent = 0; break;
                case "targetMoved": ArmyRegistry.MoveArmy(_army, new HexCoord(9, 0)); break;
                case "targetRemoved": _army.Members.Clear(); break;
                case "targetDead": _unit.HitPointsCurrent = 0; break;
                case "prisoner": _unit.IsPrisoner = true; break;
                case "ap": _root.ActionPoints = 0; break;
                case "resources": _root.AddResource(ResourceType.Materials, -5); break;
            }
            int ap = _root.ActionPoints, materials = _root.GetResource(ResourceType.Materials);
            int hp = _unit.HitPointsCurrent;
            Assert.That(UnitRepair.TryRepair(_unit, Field, _root, out _), Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(ap));
            Assert.That(_root.GetResource(ResourceType.Materials), Is.EqualTo(materials));
            Assert.That(_unit.HitPointsCurrent, Is.EqualTo(hp));
        }
        [Test] public void AnotherPlayersAccountCannotPayForRepair()
        {
            var foreign = PlayerRoot.Create(new PlayerSetupData(), "foreign-repair-test");
            try {
                foreign.ActionPoints = 10; foreign.AddResource(ResourceType.Materials, 10);
                Assert.That(UnitRepair.TryRepair(_unit, Field, foreign, out _), Is.False);
                Assert.That(foreign.ActionPoints, Is.EqualTo(10));
                Assert.That(foreign.GetResource(ResourceType.Materials), Is.EqualTo(10));
                Assert.That(_unit.HitPointsCurrent, Is.EqualTo(1));
            } finally { Object.DestroyImmediate(foreign.gameObject); }
        }
        [Test] public void ReadOnlyModalCannotExecuteRepairEvenWhenTheHexQualifies()
        {
            _ui = new GameObject("repair-modal"); _ui.SetActive(false);
            var modal = _ui.AddComponent<ArmyViewerModalUI>();
            typeof(ArmyViewerModalUI).GetField("_currentArmy", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(modal, _army);
            Assert.That(modal.CanRepairUnit(_unit), Is.True);
            typeof(ArmyViewerModalUI).GetField("_readOnly", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(modal, true);
            Assert.That(modal.CanRepairUnit(_unit), Is.False); modal.RepairUnit(_unit);
            Assert.That(_root.ActionPoints, Is.EqualTo(5)); Assert.That(_unit.HitPointsCurrent, Is.EqualTo(1));
        }
        [TestCase(true)][TestCase(false)]
        public void SelectedPhaseBRepairRechecksTheResourceAndApBank(bool reserveAp)
        {
            var ctx = new AiTurnContext { TurnNumber = 3 };
            var hand = new AiHandData(null, _owner.Faction, 0);
            var snap = new WorldSnapshot { Observer = _owner, Self = new SelfSnapshot() };
            var candidate = StrategicMaintenancePolicy.EnumerateCandidates(_owner, _root, hand, ctx, snap)
                .Single(c => c.StableKey == "repair:" + _unit.RuntimeId);
            StrategicResourceReservationLedger.Upsert(_owner, 3, new StrategicResourceReservation {
                Owner = "other-work", Reason = StrategicReservationReason.StrategicReactionPass,
                Resource = reserveAp ? StrategicReservedResource.ActionPoints : StrategicReservedResource.Materials,
                Amount = 5, ExpirationStage = StrategicReservationExpiry.EndOfTurn });
            Assert.That(candidate.Execute(_owner, _root, ctx, out bool changed, out bool progressed), Is.False);
            Assert.That(changed || progressed, Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(5)); Assert.That(_unit.HitPointsCurrent, Is.EqualTo(1));
            StrategicResourceReservationLedger.ReleaseByOwner(_owner, 3, "other-work");
            Assert.That(candidate.Execute(_owner, _root, ctx, out changed, out progressed), Is.True);
            Assert.That(changed && progressed, Is.True); Assert.That(_root.ActionPoints, Is.EqualTo(4));
            Assert.That(_root.GetResource(ResourceType.Materials), Is.EqualTo(3));
        }
    }
}
#endif
