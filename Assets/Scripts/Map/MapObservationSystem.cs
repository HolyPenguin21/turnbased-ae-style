using System;
using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Players;

namespace Game.Map
{
    // Knowledge is a world observer, not a side effect of selection or redraw. These entrypoints
    // run only at committed mutation boundaries, before presentation receives the notification.
    public static class MapObservationSystem
    {
        private static HexMap map;
        public static event Action<PlayerSetupData, HexCoord> Observed;
        public static event Action<PlayerSetupData> ArmyMemoryExpired;
        internal static void ExpireArmies(PlayerSetupData viewer) => VisionSystem.PublishAfterMutation(ArmyMemoryExpired, viewer);
        public static void Configure(HexMap source) { map = source; EnsureSubscribed(); }
        internal static void Reset() { map = null; EnsureSubscribed(); }
        internal static int TerrainDefenseAt(HexCoord hex) => map != null && map.TryGetTerrainAt(hex, out var terrain)
            && terrain != null ? terrain.defenseModifier : 0;
        internal static void EnsureSubscribed()
        {
            StealthSystem.StealthChanged -= OnStealthChanged;
            StealthSystem.StealthChanged += OnStealthChanged;
            BuildingRegistry.VisualStateChanged -= OnBuildingChanged;
            BuildingRegistry.VisualStateChanged += OnBuildingChanged;
        }
        public static void ObserveVisible(PlayerSetupData viewer)
        {
            if (!HumanVisualMemory.Tracks(viewer) || Game.Setup.MatchRestoreScope.IsRestoring) return;
            foreach (var hex in new List<HexCoord>(VisionSystem.VisibleHexesFor(viewer))) ObserveHex(viewer, hex);
        }
        public static void ObserveHex(PlayerSetupData viewer, HexCoord hex)
        {
            if (!HumanVisualMemory.Tracks(viewer) || !VisionSystem.IsVisible(viewer, hex)
                || Game.Setup.MatchRestoreScope.IsRestoring) return;
            var enemies = ObservedEnemyArmiesAt(viewer, hex);
            HumanVisualMemory.ReconcileVisibleHex(viewer, hex, enemies.Select(a => a.Id));
            foreach (var army in enemies) HumanVisualMemory.ObserveArmy(viewer, army, hex);
            HumanVisualMemory.ObserveBuilding(viewer, hex, BuildingRegistry.FindAt(hex) != null);
            VisionSystem.PublishAfterMutation(Observed, viewer, hex);
        }
        private static List<ArmyData> ObservedEnemyArmiesAt(PlayerSetupData viewer, HexCoord hex) =>
            ArmyRegistry.At(hex).Where(a => a.Owner != viewer && a.Owner != null
                && a.Members.Count > 0 && !a.IsPrison && !a.IsAirfield && !a.IsCarrierContainer
                && !HexEventRegistry.IsEventGuardArmy(hex, a)
                && !StealthSystem.ArmyFullyHiddenFrom(a, viewer)).ToList();
        public static void ObserveAllViewers()
        {
            foreach (var player in VisionSystem.KnownViewers()) ObserveVisible(player);
        }
        private static void OnStealthChanged() => ObserveAllViewers();
        private static void OnBuildingChanged(HexCoord hex, BuildingData building)
        {
            // Uses legitimate prior witnesses before losing a building's own vision source.
            foreach (var viewer in VisionSystem.ViewersOf(hex)) ObserveHex(viewer, hex);
        }
        // Capture the permitted payload before leaving visibility, publish it only after the
        // relocation commits. Never consult the hidden destination's building/roster/appearance.
        internal static Action CaptureDeparture(ArmyData army, HexCoord from, HexCoord to)
        {
            if (Game.Setup.MatchRestoreScope.IsRestoring) return null;
            var observations = new List<(PlayerSetupData Viewer, ObservedArmy Payload)>();
            foreach (var viewer in VisionSystem.ViewersOf(from))
                if (HumanVisualMemory.Tracks(viewer) && !VisionSystem.IsVisible(viewer, to)
                    && ObservedEnemyArmiesAt(viewer, from).Contains(army))
                    observations.Add((viewer, ObservedArmy.Capture(army, from, viewer).AtObservedEndpoint(to)));
            int sourceId = army.Id;
            return () =>
            {
                foreach (var entry in observations)
                {
                    HumanVisualMemory.StoreObservation(entry.Viewer, sourceId, entry.Payload);
                    bool firstObservation = VisionSystem.RecordObservedHex(entry.Viewer, to);
                    VisionSystem.PublishAfterMutation(Observed, entry.Viewer, to);
                    if (firstObservation) VisionSystem.NotifyObservationChanged(entry.Viewer);
                }
            };
        }
    }
}
