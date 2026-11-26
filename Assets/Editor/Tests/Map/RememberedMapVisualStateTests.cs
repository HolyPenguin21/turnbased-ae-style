#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.UI;
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
        private static object Call(object target, string name, params object[] arguments) => TestReflection.FindMethod(target.GetType(), name, Private, arguments).Invoke(target, arguments);

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
            army.ReplaceRoster(null); Object.DestroyImmediate(liveVisual.gameObject);
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

        // Content changes refresh only the changed hex plus the hexes whose remembered markers
        // changed (OnVisibleContentChanged), and both ends of a relocation (OnArmyRelocated) —
        // never the whole map. These drive the real event chain and check the hexes no content
        // event of the viewer names.
        private void RunWithLiveSelection(Action body, params HexCoord[] hexes)
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); StealthSystem.Clear(); HexEventRegistry.Clear();
            VisionSystem.Configure(null);
            var terrain = new Dictionary<HexCoord, TerrainTypeEntry>();
            foreach (HexCoord hex in hexes) terrain[hex] = new TerrainTypeEntry();
            var map = root.AddComponent<HexMap>(); map.SetData(3, 1f, terrain);
            var config = ScriptableObject.CreateInstance<GameConfig>();
            Set(selection, "map", map); Set(selection, "gameConfig", config);
            // Edit Mode never runs a runtime component's lifecycle: drive it as Play Mode would.
            Call(selection, "Awake"); Call(selection, "OnEnable");
            try { body(); }
            finally
            {
                Call(selection, "OnDisable"); Call(selection, "OnDestroy");
                ArmyRegistry.Clear(); BuildingRegistry.Clear(); StealthSystem.Clear(); HexEventRegistry.Clear();
                Object.DestroyImmediate(config);
            }
        }
        private void SetVisible(PlayerSetupData who, params HexCoord[] hexes) =>
            VisionSystem.RestoreState(new VisionState { CurrentViewerId = VisionSystem.CurrentViewer?.PlayerId,
                Players = new List<PlayerVisionState> { new PlayerVisionState { PlayerId = who.PlayerId, Visible = new List<HexCoord>(hexes) } } }, Player);
        private ArmyData LiveEnemy(string name, HexCoord hex)
        {
            var marker = Marker(name, Color.yellow);
            var controller = marker.gameObject.AddComponent<ArmyController>(); Call(controller, "Awake");
            var army = new ArmyData { Owner = enemy, Hex = hex, Controller = controller };
            army.Members.Add(new UnitData { Owner = enemy, Name = name, HitPointsCurrent = 3 });
            controller.SetData(army); ArmyRegistry.Register(army);
            return army;
        }
        private MapObjectVisual GhostOf(ArmyData army)
        {
            var byViewer = (System.Collections.IDictionary)selection.GetType().GetField("_rememberedArmyVisuals", Private).GetValue(selection);
            var byId = (System.Collections.IDictionary)byViewer?[viewer];
            object remembered = byId?[army.Id];
            return (MapObjectVisual)remembered?.GetType().GetField("Visual").GetValue(remembered);
        }

        [Test]
        public void EnemyWalkingIntoFogLeavesAVisibleGhostAndHidesItsLiveMarker()
        {
            HexCoord from = new HexCoord(0, 0), to = new HexCoord(1, 0);
            RunWithLiveSelection(() =>
            {
                SetVisible(viewer, from);
                ArmyData army = LiveEnemy("walker", from);
                Assert.That(army.Controller.Visual.IsVisible, Is.True);
                ArmyRegistry.MoveArmy(army, to);
                Assert.That(army.Controller.Visual.IsVisible, Is.False, "The live marker must not show inside fog.");
                Assert.That(GhostOf(army), Is.Not.Null);
                Assert.That(GhostOf(army).IsVisible, Is.True, "The departure ghost on the fogged destination must show.");
            }, from, to);
        }

        [Test]
        public void UntrackedViewerStillHidesAnEnemyThatWalksIntoFog()
        {
            HexCoord from = new HexCoord(0, 0), to = new HexCoord(1, 0);
            viewer.IsHuman = false; VisionSystem.CurrentViewer = viewer;
            RunWithLiveSelection(() =>
            {
                Assert.That(HumanVisualMemory.Tracks(viewer), Is.False);
                SetVisible(viewer, from);
                ArmyData army = LiveEnemy("walker", from);
                Assert.That(army.Controller.Visual.IsVisible, Is.True);
                ArmyRegistry.MoveArmy(army, to);
                Assert.That(GhostOf(army), Is.Null);
                Assert.That(army.Controller.Visual.IsVisible, Is.False,
                    "No ghost names the fogged destination; the relocation itself must reconcile it.");
            }, from, to);
        }

        [Test]
        public void GhostLeavingAFoggedHexHandsItsSlotToTheSameOwnersOtherGhost()
        {
            HexCoord seen = new HexCoord(0, 0), fogged = new HexCoord(1, 0), unseen = new HexCoord(2, 0);
            RunWithLiveSelection(() =>
            {
                SetVisible(viewer, fogged);
                ArmyData first = LiveEnemy("first", fogged), second = LiveEnemy("second", fogged);
                SetVisible(viewer, seen);
                Call(selection, "RefreshAllVisibility");
                Assert.That(GhostOf(first).IsVisible, Is.True, "One ghost per owner: the first remembered one shows.");
                Assert.That(GhostOf(second).IsVisible, Is.False);
                // Leaves through fog, then appears in sight: the content event names only `seen`,
                // the relocations only unseen/seen — `fogged` is reached through the changed ghost.
                ArmyRegistry.MoveArmy(first, unseen);
                ArmyRegistry.MoveArmy(first, seen);
                Assert.That(first.Controller.Visual.IsVisible, Is.True);
                Assert.That(GhostOf(first).IsVisible, Is.False);
                Assert.That(GhostOf(second).IsVisible, Is.True, "The remaining ghost must take over the fogged hex.");
            }, seen, fogged, unseen);
        }

        [Test]
        public void ArmyObservedBeforeItsMarkerExistsGetsItsGhostWhenTheMarkerIsCreated()
        {
            var hex = new HexCoord(0, 0);
            var decks = ScriptableObject.CreateInstance<StartingDeckCatalog>();
            var catalog = ScriptableObject.CreateInstance<FactionCardCatalog>();
            ArmyController created = null;
            try
            {
                catalog.faction = enemy.Faction; catalog.armyPrefab = Marker("enemy-army-prefab", Color.green);
                decks.catalogs.Add(catalog);
                var handObject = new GameObject("inactive-hand"); handObject.SetActive(false); handObject.transform.SetParent(root.transform);
                var hand = handObject.AddComponent<CardHandUI>(); Set(hand, "startingDeckCatalog", decks);
                Set(selection, "cardHandUI", hand);
                RunWithLiveSelection(() =>
                {
                    SetVisible(viewer, hex);
                    // Map content spawn order: the roster is registered (and observed) first,
                    // the marker is created afterwards.
                    var army = new ArmyData { Owner = enemy, Hex = hex };
                    army.Members.Add(new UnitData { Owner = enemy, Name = "spawned", HitPointsCurrent = 3 });
                    ArmyRegistry.Register(army);
                    Assert.That(HumanVisualMemory.TryGetArmySighting(viewer, army.Id, out _), Is.True);
                    Assert.That(GhostOf(army), Is.Null, "Nothing to clone yet.");
                    created = selection.CreateArmyMarker(army);
                    Assert.That(created, Is.Not.Null);
                    // Edit Mode skips the Awake that binds Visual inside AddComponent in play;
                    // run it and the projection CreateArmyMarker performs right after it.
                    Call(created, "Awake"); Call(selection, "ProjectSightingsOfNewMarker", army);
                    selection.RestackArmiesOn(hex, null);
                    Assert.That(GhostOf(army), Is.Not.Null);
                    Assert.DoesNotThrow(() => selection.ValidateRememberedVisualCoverage(),
                        "A save right after the spawn must find the remembered marker.");
                    Assert.That(GhostOf(army).IsVisible, Is.False, "The hex is in sight: the live marker shows instead.");
                    Assert.That(created.Visual.IsVisible, Is.True);
                }, hex);
            }
            finally
            {
                if (created != null) Object.DestroyImmediate(created.gameObject);
                Object.DestroyImmediate(decks); Object.DestroyImmediate(catalog);
            }
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
