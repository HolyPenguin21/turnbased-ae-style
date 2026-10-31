#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class RememberedMapVisualStateTests
    {
        private GameObject root;
        private PlayerSetupData viewer;
        private PlayerSetupData enemy;
        private HexSelectionController selection;
        private Texture2D texture;
        private Sprite observedFlag;
        private Sprite changedFlag;
        private readonly Dictionary<string, MapObjectVisual> encoded = new Dictionary<string, MapObjectVisual>();
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        private static object Call(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, Private).Invoke(target, arguments);

        [SetUp]
        public void SetUp()
        {
            HumanVisualMemory.Clear(); VisionSystem.Clear();
            root = new GameObject("remembered-visual-state-tests"); root.SetActive(false);
            selection = root.AddComponent<HexSelectionController>();
            viewer = new PlayerSetupData { PlayerId = "viewer", IsHuman = true };
            enemy = new PlayerSetupData { PlayerId = "enemy" };
            VisionSystem.CurrentViewer = viewer;
            texture = new Texture2D(2, 1);
            observedFlag = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
            changedFlag = Sprite.Create(texture, new Rect(1, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }
        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(observedFlag); Object.DestroyImmediate(changedFlag); Object.DestroyImmediate(texture);
            HumanVisualMemory.Clear(); VisionSystem.Clear(); encoded.Clear();
        }
        private MapObjectVisual Marker(string name, Color color)
        {
            var go = new GameObject(name); go.transform.SetParent(root.transform);
            var visual = go.AddComponent<MapObjectVisual>();
            var circle = go.AddComponent<SpriteRenderer>(); circle.sprite = observedFlag; Set(visual, "innerCircle", circle);
            var flag = new GameObject("flag"); flag.transform.SetParent(go.transform);
            var accent = flag.AddComponent<SpriteRenderer>(); accent.sprite = observedFlag; Set(visual, "factionAccent", accent);
            var emblem = new GameObject("emblem"); emblem.transform.SetParent(go.transform);
            var logo = emblem.AddComponent<SpriteRenderer>(); logo.sprite = observedFlag;
            Set(visual, "auxiliaryRenderers", new[] { logo });
            go.AddComponent<MapFlagAnimator>();
            visual.SetColor(color);
            return visual;
        }
        private MapVisualSnapshotReference Encode(MapObjectVisual visual)
        {
            string key = "frozen-" + encoded.Count;
            encoded.Add(key, visual.CreateFrozenSnapshot(root.transform));
            return new MapVisualSnapshotReference { TypeKey = "test.layered-marker", SnapshotKey = key };
        }
        private MapObjectVisual Decode(MapVisualSnapshotReference reference)
        {
            Assert.That(reference.TypeKey, Is.EqualTo("test.layered-marker"));
            return encoded[reference.SnapshotKey];
        }
        private PlayerSetupData Player(string id) => id == viewer.PlayerId ? viewer : id == enemy.PlayerId ? enemy : null;
        private HexSelectionController NewSelection()
        {
            var go = new GameObject("restored-selection"); go.transform.SetParent(root.transform);
            return go.AddComponent<HexSelectionController>();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HiddenBuildingRestoresHistoricalColorAndFlagAfterUnseenChangeOrDestruction(bool destroyLive)
        {
            var hex = new HexCoord(2, 1);
            var live = Marker("building", Color.red);
            HumanVisualMemory.ObserveBuilding(viewer, hex, true);
            Call(selection, "UpdateRememberedBuildingVisual", viewer, hex, live);
            var memory = HumanVisualMemory.CaptureState(army => "unused");
            var state = selection.CaptureRememberedVisuals(Encode);
            live.SetColor(Color.blue);
            live.transform.Find("flag").GetComponent<SpriteRenderer>().sprite = changedFlag;
            live.transform.Find("emblem").GetComponent<SpriteRenderer>().sprite = changedFlag;
            if (destroyLive) Object.DestroyImmediate(live.gameObject);
            HumanVisualMemory.Clear(); HumanVisualMemory.RestoreState(memory, Player, key => null);
            var restored = NewSelection();
            restored.RestoreRememberedVisuals(state, Player, Decode);
            Call(restored, "RefreshRememberedBuildingVisual", hex, false);
            MapObjectVisual ghost = null;
            restored.CaptureRememberedVisuals(visual => { ghost = visual; return Encode(visual); });
            Assert.That(ghost.IsVisible, Is.True);
            Assert.That(ghost.GetComponent<SpriteRenderer>().color, Is.EqualTo(Color.red));
            Assert.That(ghost.transform.Find("flag").GetComponent<SpriteRenderer>().color, Is.EqualTo(Color.red));
            Assert.That(ghost.transform.Find("flag").GetComponent<SpriteRenderer>().sprite, Is.SameAs(observedFlag));
            Assert.That(ghost.transform.Find("emblem").GetComponent<SpriteRenderer>().sprite, Is.SameAs(observedFlag));
            Assert.That(ghost.GetComponent<MapFlagAnimator>().enabled, Is.False);
            Assert.That(ghost, Is.Not.SameAs(encoded[state.Buildings[0].Appearance.SnapshotKey]));
        }

        [Test]
        public void DestroyedHiddenArmyRestoresItsFrozenRosterAndPerHexMarkerIndex()
        {
            var hex = new HexCoord(3, 0);
            var liveVisual = Marker("army", Color.yellow);
            var controller = liveVisual.gameObject.AddComponent<ArmyController>(); Call(controller, "Awake");
            var army = new ArmyData { Owner = enemy, Hex = hex, Controller = controller };
            army.Members.Add(new UnitData { Owner = enemy, Name = "Observed scout", HitPointsCurrent = 3 });
            controller.SetData(army);
            HumanVisualMemory.ObserveArmy(viewer, army, hex);
            Call(selection, "UpdateRememberedArmyVisual", viewer, army);
            ArmyData frozenRoster = null;
            var memory = HumanVisualMemory.CaptureState(snapshot => { frozenRoster = snapshot; return "roster"; });
            var state = selection.CaptureRememberedVisuals(Encode);
            army.Members.Clear(); Object.DestroyImmediate(liveVisual.gameObject);
            HumanVisualMemory.Clear(); HumanVisualMemory.RestoreState(memory, Player, key => frozenRoster);
            var restored = NewSelection(); restored.RestoreRememberedVisuals(state, Player, Decode);
            Call(restored, "RefreshRememberedArmyVisualsAt", hex);
            MapObjectVisual ghost = null;
            restored.CaptureRememberedVisuals(visual => { ghost = visual; return Encode(visual); });
            Assert.That(ghost.IsVisible, Is.True, "The restored per-hex army index must reach the marker.");
            Assert.That(ghost.GetComponent<SpriteRenderer>().color, Is.EqualTo(Color.yellow));
            Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, army.Id, out var sighting), Is.True);
            Assert.That(sighting.Army.Members[0].Name, Is.EqualTo("Observed scout"));
            Assert.That(sighting.Army.Members[0].HitPointsCurrent, Is.EqualTo(3));
        }

        [Test]
        public void HiddenDepartureProjectsFrozenAppearanceWithoutReadingChangedLiveMarker()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Configure(null);
            try
            {
                var from = new HexCoord(0, 0); var to = new HexCoord(1, 0);
                var watcher = new ArmyData { Owner = viewer, Hex = from };
                watcher.Members.Add(new UnitData { Owner = viewer }); ArmyRegistry.Register(watcher);
                var marker = Marker("departing", Color.yellow);
                var controller = marker.gameObject.AddComponent<ArmyController>(); Call(controller, "Awake");
                var army = new ArmyData { Owner = enemy, Hex = from, Controller = controller };
                army.Members.Add(new UnitData { Owner = enemy, Name = "observed", HitPointsCurrent = 3 });
                controller.SetData(army); ArmyRegistry.Register(army);
                Call(selection, "UpdateRememberedArmyVisual", viewer, army);
                ArmyRegistry.MoveArmy(army, to);
                marker.SetColor(Color.blue); marker.transform.Find("flag").GetComponent<SpriteRenderer>().sprite = changedFlag;
                Call(selection, "ProjectObservedHex", viewer, to);
                MapObjectVisual ghost = null;
                var saved = selection.CaptureRememberedVisuals(visual => { ghost = visual; return Encode(visual); });
                Assert.That(saved.Armies.Count, Is.EqualTo(1)); Assert.That(saved.Armies[0].Hex, Is.EqualTo(to));
                Assert.That(ghost.GetComponent<SpriteRenderer>().color, Is.EqualTo(Color.yellow));
                Assert.That(ghost.transform.Find("flag").GetComponent<SpriteRenderer>().sprite, Is.SameAs(observedFlag));
            }
            finally { ArmyRegistry.Clear(); BuildingRegistry.Clear(); }
        }

        [Test]
        public void MissingOrLiveAppearanceIsRejectedWithoutInventingKnowledge()
        {
            var hex = new HexCoord(1, 0);
            HumanVisualMemory.ObserveBuilding(viewer, hex, true);
            Assert.Throws<InvalidOperationException>(() => selection.CaptureRememberedVisuals(Encode));
            Assert.Throws<ArgumentException>(() => selection.RestoreRememberedVisuals(new RememberedMapVisualState(), Player, Decode));
            var state = new RememberedMapVisualState();
            state.Buildings.Add(new RememberedBuildingVisualState { ViewerId = viewer.PlayerId, Hex = hex,
                Appearance = new MapVisualSnapshotReference { TypeKey = "building", SnapshotKey = "missing" } });
            var live = Marker("live", Color.blue);
            Assert.Throws<ArgumentException>(() => selection.RestoreRememberedVisuals(state, Player, key => live));
            Assert.That(HumanVisualMemory.IsBuildingKnown(viewer, hex), Is.True);
        }
    }
}
#endif
