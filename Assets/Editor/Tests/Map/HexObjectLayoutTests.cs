#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Map;
using Game.Players;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public class HexObjectLayoutTests
    {
        private GameConfig config;
        [SetUp] public void SetUp()
        {
            config = ScriptableObject.CreateInstance<GameConfig>();
            VisionSystem.Clear();
            VisionSystem.DebugRevealAll = false;
            BuildingRegistry.Clear();
            ArmyRegistry.Clear();
            StealthSystem.Clear();
            VisionSystem.Configure(null);
        }
        [TearDown] public void TearDown() { Object.DestroyImmediate(config); VisionSystem.Clear(); BuildingRegistry.Clear(); ArmyRegistry.Clear(); StealthSystem.Clear(); }

        [TestCase(false, 0f, 0f)]
        [TestCase(true, 0.25f, -0.25f)]
        public void SingleOwnerPreservesExistingPosition(bool building, float x, float y)
        {
            var layout = HexObjectLayout.Resolve(config, building, new[] { new PlayerSetupData() });
            Assert.That(layout.ArmyOffsets[0], Is.EqualTo(new Vector2(x, y)));
            Assert.That(layout.BuildingOffset, Is.EqualTo(Vector2.zero));
        }

        [TestCase(2)] [TestCase(3)] [TestCase(4)]
        public void BuildingKeepsCentreAndEveryOwnerHasADistinctLowerSlot(int count)
        {
            var owners = new List<PlayerSetupData>();
            for (int i = 0; i < count; i++) owners.Add(new PlayerSetupData { ColorIndex = i });
            var layout = HexObjectLayout.Resolve(config, true, owners);
            Assert.That(layout.BuildingOffset, Is.EqualTo(Vector2.zero));
            Assert.That(new HashSet<Vector2>(layout.ArmyOffsets).Count, Is.EqualTo(count));
            foreach (var offset in layout.ArmyOffsets) Assert.That(offset.y, Is.LessThan(0f));
        }

        [Test]
        public void ReorderingRegistryEntriesDoesNotSwapOwnerSlots()
        {
            var a = new PlayerSetupData { ColorIndex = 0 };
            var b = new PlayerSetupData { ColorIndex = 1 };
            var first = HexObjectLayout.Resolve(config, true, new[] { a, b });
            var second = HexObjectLayout.Resolve(config, true, new[] { b, a });
            Assert.That(first.ArmyOffsets[0], Is.EqualTo(second.ArmyOffsets[1]));
            Assert.That(first.ArmyOffsets[1], Is.EqualTo(second.ArmyOffsets[0]));
        }

        [Test]
        public void EmptyHexUsesADifferentLayoutFromBuildingHex()
        {
            var owners = new[] { new PlayerSetupData { ColorIndex = 0 }, new PlayerSetupData { ColorIndex = 1 } };
            var empty = HexObjectLayout.Resolve(config, false, owners);
            var building = HexObjectLayout.Resolve(config, true, owners);
            Assert.That(empty.ArmyOffsets[0], Is.Not.EqualTo(building.ArmyOffsets[0]));
            Assert.That(empty.ArmyOffsets[0].y, Is.EqualTo(0f));
            Assert.That(empty.ArmyOffsets[1].y, Is.EqualTo(0f));
        }

        [Test]
        public void UnconfirmedBuildingDoesNotOffsetAnArmy()
        {
            var own = new PlayerSetupData { IsHuman = true };
            VisionSystem.CurrentViewer = own;
            var hex = new Game.HexGrid.HexCoord(8, 8);
            BuildingRegistry.Register(hex, new BuildingData { Owner = new PlayerSetupData() });
            bool known = (bool)typeof(HexSelectionController)
                .GetMethod("BuildingKnownToViewer", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { hex });
            Assert.That(known, Is.False);
            var layout = HexObjectLayout.Resolve(config, known, new[] { own });
            Assert.That(layout.ArmyOffsets[0], Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void FullyHiddenEnemyOnAVisibleHexDoesNotMoveTheOwnMarker()
        {
            var own = new PlayerSetupData { IsHuman = true, ColorIndex = 0 };
            var enemy = new PlayerSetupData { ColorIndex = 1 };
            var hex = new Game.HexGrid.HexCoord(0, 0);
            var ownArmy = new ArmyData { Owner = own, Hex = hex };
            ownArmy.Members.Add(new Game.Units.UnitData { Owner = own });
            var enemyArmy = new ArmyData { Owner = enemy, Hex = hex };
            enemyArmy.Members.Add(new Game.Units.UnitData { Owner = enemy, IsHidden = true });
            ArmyRegistry.Register(ownArmy);
            ArmyRegistry.Register(enemyArmy);
            VisionSystem.CurrentViewer = own;
            VisionSystem.RecomputeFor(own);
            Assert.That(VisionSystem.IsVisible(own, hex), Is.True);
            var visible = (List<ArmyData>)typeof(HexSelectionController)
                .GetMethod("VisibleForLayout", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { new List<ArmyData> { ownArmy, enemyArmy }, hex });
            Assert.That(visible, Is.EqualTo(new[] { ownArmy }));
            var layout = HexObjectLayout.Resolve(config, true, new[] { visible[0].Owner });
            Assert.That(layout.ArmyOffsets[0], Is.EqualTo(new Vector2(0.25f, -0.25f)));
        }

        [Test]
        public void UnseenEnemyDoesNotContributeALayoutSlot()
        {
            var own = new PlayerSetupData { IsHuman = true, ColorIndex = 0 };
            var enemy = new PlayerSetupData { ColorIndex = 1 };
            VisionSystem.CurrentViewer = own;
            var armies = new List<ArmyData> { new ArmyData { Owner = own }, new ArmyData { Owner = enemy } };
            var visible = (List<ArmyData>)typeof(HexSelectionController)
                .GetMethod("VisibleForLayout", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { armies, default(Game.HexGrid.HexCoord) });
            Assert.That(visible.Count, Is.EqualTo(1));
            var layout = HexObjectLayout.Resolve(config, false, new[] { visible[0].Owner });
            Assert.That(layout.ArmyOffsets[0], Is.EqualTo(Vector2.zero));
        }
    }
}
#endif
