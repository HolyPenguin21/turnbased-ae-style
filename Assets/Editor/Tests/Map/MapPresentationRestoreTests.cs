#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class MapPresentationRestoreTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject root;
        private GameConfig config;
        private HexMap map;
        private MapEventDisplay events;
        private MapResourceDisplay resources;
        private PlayerSetupData human, ai;
        private static readonly HexCoord Hex = new HexCoord(0, 0), BonusOnly = new HexCoord(1, 0);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        private static IDictionary Cache(object target, string name) => (IDictionary)target.GetType().GetField(name, Private).GetValue(target);
        private void RefreshEvents()
        {
            MethodInfo refresh = typeof(MapEventDisplay).GetMethod("RefreshAll", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(refresh, Is.Not.Null, "Restored event flags need a silent presentation rebuild.");
            refresh.Invoke(events, null);
        }
        [SetUp]
        public void SetUp()
        {
            VisionSystem.Clear(); HexEventRegistry.Clear(); HexResourceBonusRegistry.Clear();
            human = new PlayerSetupData { PlayerId = "human", IsHuman = true };
            ai = new PlayerSetupData { PlayerId = "ai" };
            VisionSystem.RestoreState(new VisionState { CurrentViewerId = "human", Players = new List<PlayerVisionState> {
                new PlayerVisionState { PlayerId = "human", Visible = new List<HexCoord> { Hex, BonusOnly }, Visited = new List<HexCoord> { Hex, BonusOnly }, EverSeen = new List<HexCoord> { Hex, BonusOnly } },
                new PlayerVisionState { PlayerId = "ai", Visible = new List<HexCoord> { Hex }, EverSeen = new List<HexCoord> { Hex } } } },
                id => id == "human" ? human : ai);
            root = new GameObject("presentation-restore-tests"); root.SetActive(false);
            map = root.AddComponent<HexMap>();
            map.SetData(1, 1, new Dictionary<HexCoord, TerrainTypeEntry> {
                { Hex, new TerrainTypeEntry { resourceYields = new ResourceYields { human = 1 } } },
                { BonusOnly, new TerrainTypeEntry() } });
            events = root.AddComponent<MapEventDisplay>(); resources = root.AddComponent<MapResourceDisplay>();
            config = ScriptableObject.CreateInstance<GameConfig>();
            var eventPrefab = new GameObject("event-prefab"); eventPrefab.transform.SetParent(root.transform);
            config.eventMarkerPrefab = eventPrefab.AddComponent<EventMarkerVisual>();
            var resourcePrefab = new GameObject("resource-prefab"); resourcePrefab.transform.SetParent(root.transform);
            config.resourceIconPrefab = resourcePrefab.AddComponent<ResourceIconVisual>();
            Set(events, "gameConfig", config); Set(resources, "gameConfig", config);
        }
        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root); Object.DestroyImmediate(config);
            VisionSystem.Clear(); HexEventRegistry.Clear(); HexResourceBonusRegistry.Clear();
        }
        private void RestoreEvent(PlayerSetupData discoverer, bool skipped = true, bool consumed = false)
        {
            var entry = new HexEventRegistry.Entry { Skipped = skipped, Consumed = consumed };
            if (discoverer != null) { entry.DiscoveredBy.Add(discoverer); entry.KnownBy.Add(discoverer); }
            HexEventRegistry.RestoreEntries(new[] { new HexEventEntryState<HexEventRegistry.Entry> { Hex = Hex, Payload = entry } }, (hex, payload) => payload);
        }
        [Test]
        public void EventRebuildRetainsMarkersWithoutLearningAnotherPlayersDiscovery()
        {
            int notifications = 0;
            Action<HexCoord> changed = hex => notifications++;
            Action<HexCoord, PlayerSetupData> discovered = (hex, viewer) => notifications++;
            HexEventRegistry.EventSkipped += changed; HexEventRegistry.EventConsumed += changed; HexEventRegistry.EventDiscovered += discovered;
            try
            {
                RestoreEvent(human); RefreshEvents();
                var cache = Cache(events, "_markersByHex");
                var marker = (EventMarkerVisual)cache[Hex];
                Assert.That(marker.gameObject.activeSelf, Is.True);
                RefreshEvents();
                Assert.That(cache.Count, Is.EqualTo(1)); Assert.That(cache[Hex], Is.SameAs(marker));
                RestoreEvent(ai); RefreshEvents();
                Assert.That(marker.gameObject.activeSelf, Is.False, "AI discovery must not become human knowledge.");
                VisionSystem.CurrentViewer = ai; RefreshEvents();
                Assert.That(marker.gameObject.activeSelf, Is.True);
                RestoreEvent(null); RefreshEvents();
                Assert.That(marker.gameObject.activeSelf, Is.False);
                Assert.That(notifications, Is.Zero, "A rebuild must not publish discovery, skip or reward events.");
            }
            finally { HexEventRegistry.EventSkipped -= changed; HexEventRegistry.EventConsumed -= changed; HexEventRegistry.EventDiscovered -= discovered; }
        }
        [Test]
        public void EventRebuildRemovesConsumedUnskippedAndMissingMarkers()
        {
            foreach (int replacement in new[] { 0, 1, 2 })
            {
                RestoreEvent(human); RefreshEvents();
                var marker = (EventMarkerVisual)Cache(events, "_markersByHex")[Hex];
                if (replacement == 0) RestoreEvent(human, consumed: true);
                else if (replacement == 1) RestoreEvent(human, skipped: false);
                else HexEventRegistry.Clear();
                RefreshEvents();
                Assert.That(Cache(events, "_markersByHex").Count, Is.Zero);
                Assert.That(marker == null || !marker.gameObject.activeSelf, Is.True, "Obsolete markers must disappear immediately.");
            }
        }
        [Test]
        public void ResourceRebuildUsesRestoredBonusesIncludingPreviouslyEmptyHexes()
        {
            resources.RefreshAll();
            var cache = Cache(resources, "_iconsByHex");
            Assert.That(cache.Count, Is.EqualTo(1));
            HexResourceBonusRegistry.RestoreState(new[] {
                new HexResourceBonusState { Hex = Hex, Bonus = new ResourceYields { human = 3 } },
                new HexResourceBonusState { Hex = BonusOnly, Bonus = new ResourceYields { energy = 2 } } });
            resources.RefreshAll();
            Assert.That(cache.Count, Is.EqualTo(2));
            var ordinary = (List<ResourceIconVisual>)cache[Hex]; var bonusOnly = (List<ResourceIconVisual>)cache[BonusOnly];
            Assert.That(typeof(ResourceIconVisual).GetField("_amount", Private).GetValue(ordinary[0]), Is.EqualTo(4));
            Assert.That(typeof(ResourceIconVisual).GetField("_amount", Private).GetValue(bonusOnly[0]), Is.EqualTo(2));
        }
    }
}
#endif
