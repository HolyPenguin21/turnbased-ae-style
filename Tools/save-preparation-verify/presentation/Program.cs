using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Turns;

// These checks execute production display/registry/coordinator code; only native Unity and
// unrelated scene owners are adapted. A failed assertion always produces a nonzero exit code.
internal static class PresentationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
    private static void Call(object target, string name) => target.GetType().GetMethod(name, Private).Invoke(target, null);
    private static IDictionary Cache(object target, string name) => (IDictionary)target.GetType().GetField(name, Private).GetValue(target);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void RefreshEvents(MapEventDisplay display)
    {
        var method = typeof(MapEventDisplay).GetMethod("RefreshAll", BindingFlags.Instance | BindingFlags.Public);
        Require(method != null, "MapEventDisplay needs a silent public RefreshAll boundary.");
        method.Invoke(display, null);
    }
    private sealed class Scene : IDisposable
    {
        public readonly PlayerSetupData Human = new PlayerSetupData(), Ai = new PlayerSetupData();
        public readonly HexMap Map = new HexMap();
        public readonly GameConfig Config = new GameConfig();
        public readonly GameTurnController Turns = new GameTurnController();
        public readonly MapEventDisplay Events = new MapEventDisplay();
        public readonly MapResourceDisplay Resources = new MapResourceDisplay();
        public Scene()
        {
            HexEventRegistry.Clear(); HexResourceBonusRegistry.Clear();
            UnityEngine.MonoBehaviour.Components[typeof(HexMap)] = Map;
            UnityEngine.MonoBehaviour.Components[typeof(MapEventDisplay)] = Events;
            UnityEngine.MonoBehaviour.Components[typeof(MapResourceDisplay)] = Resources;
            VisionSystem.CurrentViewer = Human;
            foreach (object display in new object[] { Events, Resources })
            { Set(display, "gameConfig", Config); Set(display, "turnController", Turns); Call(display, "OnEnable"); }
        }
        public void Dispose()
        {
            Call(Events, "OnDisable"); Call(Resources, "OnDisable");
            HexEventRegistry.Clear(); HexResourceBonusRegistry.Clear();
            UnityEngine.MonoBehaviour.Components.Clear(); VisionSystem.CurrentViewer = null; GameSession.PendingRestore = null;
            PresentationWrites.BeforeWrite = null;
        }
        public HexEventRegistry.Entry Entry(PlayerSetupData discoverer, bool skipped = true, bool consumed = false)
        {
            var entry = new HexEventRegistry.Entry { Skipped = skipped, Consumed = consumed };
            if (discoverer != null) { entry.DiscoveredBy.Add(discoverer); entry.KnownBy.Add(discoverer); }
            return entry;
        }
        public void RestoreEvent(HexEventRegistry.Entry entry) => HexEventRegistry.RestoreEntries(
            new[] { new HexEventEntryState<HexEventRegistry.Entry> { Hex = Map.Hex, Payload = entry } }, (hex, payload) => payload);
        public int Amount(HexCoord hex) => ((List<ResourceIconVisual>)Cache(Resources, "_iconsByHex")[hex])[0].Amount;
    }
    private static void EventsRebuildSilentlyAndRetainPersonalVisibility()
    {
        using (var scene = new Scene())
        {
            int skipped = 0, consumed = 0, discovered = 0;
            Action<HexCoord> onSkipped = h => skipped++, onConsumed = h => consumed++;
            Action<HexCoord, PlayerSetupData> onDiscovered = (h,p) => discovered++;
            HexEventRegistry.EventSkipped += onSkipped; HexEventRegistry.EventConsumed += onConsumed; HexEventRegistry.EventDiscovered += onDiscovered;
            try
            {
                scene.RestoreEvent(scene.Entry(scene.Human)); RefreshEvents(scene.Events);
                var cache = Cache(scene.Events, "_markersByHex");
                Require(cache.Count == 1, "Restored skipped event must have one marker.");
                var marker = (EventMarkerVisual)cache[scene.Map.Hex]; Require(marker.gameObject.active, "Human's discovery must be visible.");
                RefreshEvents(scene.Events);
                Require(cache.Count == 1 && ReferenceEquals(cache[scene.Map.Hex], marker), "Repeated rebuild must retain marker identity without duplicates.");
                scene.RestoreEvent(scene.Entry(scene.Ai)); RefreshEvents(scene.Events);
                Require(!marker.gameObject.active, "An AI's discovery must remain hidden from the human.");
                VisionSystem.CurrentViewer = scene.Ai; RefreshEvents(scene.Events);
                Require(marker.gameObject.active, "The discovering viewer must see its own marker.");
                scene.RestoreEvent(scene.Entry(null)); RefreshEvents(scene.Events);
                Require(!marker.gameObject.active, "An undiscovered event must remain invisible.");
                Require(skipped == 0 && consumed == 0 && discovered == 0, "Presentation rebuild must publish no registry events.");
            }
            finally { HexEventRegistry.EventSkipped -= onSkipped; HexEventRegistry.EventConsumed -= onConsumed; HexEventRegistry.EventDiscovered -= onDiscovered; }
        }
    }
    private static void EventsRemoveObsoleteConsumedUnskippedAndMissingEntries()
    {
        using (var scene = new Scene())
        {
            foreach (int replacement in new[] { 0, 1, 2 })
            {
                scene.RestoreEvent(scene.Entry(scene.Human)); RefreshEvents(scene.Events);
                Require(Cache(scene.Events, "_markersByHex").Count == 1, "Fixture must create a historical marker.");
                var oldMarker = (EventMarkerVisual)Cache(scene.Events, "_markersByHex")[scene.Map.Hex];
                if (replacement == 0) scene.RestoreEvent(scene.Entry(scene.Human, consumed: true));
                else if (replacement == 1) scene.RestoreEvent(scene.Entry(scene.Human, skipped: false));
                else HexEventRegistry.Clear();
                RefreshEvents(scene.Events);
                Require(Cache(scene.Events, "_markersByHex").Count == 0, "Consumed, unskipped and absent events must not retain markers.");
                Require(!oldMarker.gameObject.active, "An obsolete marker must hide before deferred destruction.");
            }
        }
    }
    private sealed class Source : IMatchRestoreSource
    {
        private readonly Scene scene;
        public Source(Scene scene) { this.scene = scene; }
        public MatchIdentityState Identity { get; } = new MatchIdentityState();
        public GameTurnState Turn { get; } = new GameTurnState();
        public void RestoreWorld(HexMap map)
        {
            Require(MatchRestoreScope.IsRestoring, "World restore must be quiet.");
            HexResourceBonusRegistry.RestoreState(new[] {
                new HexResourceBonusState { Hex = map.Hex, Bonus = new ResourceYields { human = 3 } },
                new HexResourceBonusState { Hex = map.BonusOnlyHex, Bonus = new ResourceYields { energy = 2 } } });
            scene.RestoreEvent(scene.Entry(scene.Human));
        }
        public void RestoreKnowledge() { Require(MatchRestoreScope.IsRestoring, "Knowledge restore must be quiet."); }
        public void RestorePresentation(HexSelectionController selection) { Require(MatchRestoreScope.IsRestoring, "Presentation restore must be quiet."); }
    }
    private static void CoordinatorRebuildsResourcesAndEventsBeforeResume()
    {
        using (var scene = new Scene())
        {
            scene.Map.Terrain.resourceYields.human = 1;
            scene.Resources.RefreshAll();
            Require(scene.Amount(scene.Map.Hex) == 1, "Generation initially displays only terrain yield.");
            Require(Cache(scene.Resources, "_iconsByHex").Count == 1, "Bonus-only hex initially has no icon.");
            bool resumed = false;
            scene.Turns.BeforeResume = () => {
                Require(scene.Amount(scene.Map.Hex) == 4, "Resume must see restored terrain 1 + bonus 3, not stale terrain 1.");
                Require(scene.Amount(scene.Map.BonusOnlyHex) == 2, "Resume must see bonus-only icons.");
                Require(Cache(scene.Events, "_markersByHex").Count == 1, "Resume must see the restored skipped event marker.");
                resumed = true;
            };
            PresentationWrites.BeforeWrite = () => Require(MatchRestoreScope.IsRestoring, "Final presentation writes must happen inside the quiet restore scope.");
            GameSession.PendingRestore = new Source(scene);
            MatchRestoreCoordinator.RestorePending(scene.Config, scene.Map, scene.Turns, new HexSelectionController());
            Require(resumed && GameSession.PendingRestore == null, "Successful restore must resume and complete.");
        }
    }
    private static int Main()
    {
        int failed = 0;
        foreach (Action check in new Action[] { EventsRebuildSilentlyAndRetainPersonalVisibility, EventsRemoveObsoleteConsumedUnskippedAndMissingEntries, CoordinatorRebuildsResourcesAndEventsBeforeResume })
        {
            try { check(); Console.WriteLine("PASS " + check.Method.Name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL " + check.Method.Name + ": " + (error.InnerException ?? error).Message); }
        }
        Console.WriteLine("3 checks, " + failed + " failures");
        return failed == 0 ? 0 : 1;
    }
}
