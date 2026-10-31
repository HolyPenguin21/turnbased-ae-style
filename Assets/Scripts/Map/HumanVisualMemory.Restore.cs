using System;
using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;

namespace Game.Map
{
    [Serializable] public sealed class HumanArmySightingState
    {
        public int SourceArmyId; public HexCoord Hex; public string DisplaySnapshotKey;
    }
    [Serializable] public sealed class HumanPlayerMemoryState
    {
        public string PlayerId;
        public List<HumanArmySightingState> ArmySightings = new List<HumanArmySightingState>();
        public List<HexCoord> KnownBuildings = new List<HexCoord>();
    }
    [Serializable] public sealed class HumanVisualMemoryState
    {
        public bool ObserverMatch;
        public List<HumanPlayerMemoryState> Players = new List<HumanPlayerMemoryState>();
    }
    public static partial class HumanVisualMemory
    {
        // Read-only visits let the appearance owner verify that every saved sighting has a
        // frozen marker. They never observe, reconcile, or expose a mutable backing table.
        internal static void ForEachRememberedBuilding(Action<PlayerSetupData, HexCoord> visit)
        {
            foreach (var viewer in KnownBuildingHexes)
                foreach (var hex in viewer.Value) visit(viewer.Key, hex);
        }
        internal static void ForEachRememberedArmy(Action<PlayerSetupData, ArmySighting> visit)
        {
            foreach (var viewer in ArmySightings)
                foreach (var sighting in viewer.Value.Values) visit(viewer.Key, sighting);
        }

        // Snapshot codecs belong to the future entity serializer. The callback receives the
        // already frozen DISPLAY army, never its current live counterpart. It must encode the
        // complete observed roster/art/stats, not merely store a reference to a live army id.
        public static HumanVisualMemoryState CaptureState(Func<ArmyData, string> captureDisplaySnapshot)
        {
            if (captureDisplaySnapshot == null) throw new ArgumentNullException(nameof(captureDisplaySnapshot));
            var result = new HumanVisualMemoryState { ObserverMatch = ObserverMatch };
            var viewers = new HashSet<PlayerSetupData>(ArmySightings.Keys); viewers.UnionWith(KnownBuildingHexes.Keys);
            foreach (var viewer in viewers)
            {
                var state = new HumanPlayerMemoryState { PlayerId = StateReference.PlayerId(viewer) };
                if (KnownBuildingHexes.TryGetValue(viewer, out var buildings)) state.KnownBuildings.AddRange(buildings);
                if (ArmySightings.TryGetValue(viewer, out var sightings)) foreach (var sighting in sightings.Values)
                {
                    string key = captureDisplaySnapshot(sighting.Army);
                    if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("Snapshot codec returned no key.");
                    state.ArmySightings.Add(new HumanArmySightingState { SourceArmyId = sighting.SourceArmyId, Hex = sighting.Hex, DisplaySnapshotKey = key });
                }
                result.Players.Add(state);
            }
            return result;
        }
        // The decoder must create display snapshots (Id == -1) independently from live units.
        // No ObserveArmy call: that would replace old knowledge with today's hidden state.
        public static void RestoreState(HumanVisualMemoryState state, Func<string, PlayerSetupData> resolvePlayer,
            Func<string, ArmyData> resolveDisplaySnapshot)
        {
            if (state?.Players == null || resolveDisplaySnapshot == null) throw new ArgumentException("Missing visual memory state or codec.");
            var armies = new Dictionary<PlayerSetupData, Dictionary<int, ArmySighting>>();
            var buildings = new Dictionary<PlayerSetupData, HashSet<HexCoord>>();
            foreach (var entry in state.Players)
            {
                if (entry?.ArmySightings == null || entry.KnownBuildings == null) throw new ArgumentException("Invalid visual memory player.");
                var viewer = StateReference.Player(entry.PlayerId, resolvePlayer);
                if (armies.ContainsKey(viewer)) throw new ArgumentException("Duplicate visual memory player.");
                var sightings = new Dictionary<int, ArmySighting>();
                foreach (var sighting in entry.ArmySightings)
                {
                    if (sighting == null || sighting.SourceArmyId < 0 || sighting.SourceArmyId == int.MaxValue || string.IsNullOrEmpty(sighting.DisplaySnapshotKey)) throw new ArgumentException("Invalid army sighting.");
                    var snapshot = resolveDisplaySnapshot(sighting.DisplaySnapshotKey);
                    if (snapshot == null || snapshot.Id != -1 || ArmyRegistry.IsRegistered(snapshot) || !snapshot.Hex.Equals(sighting.Hex))
                        throw new ArgumentException("Visual memory requires a frozen display-only snapshot at its observed hex.");
                    if (sightings.ContainsKey(sighting.SourceArmyId)) throw new ArgumentException("Duplicate army sighting.");
                    sightings.Add(sighting.SourceArmyId, new ArmySighting(sighting.SourceArmyId, sighting.Hex, ObservedArmy.FromDisplay(snapshot)));
                }
                armies.Add(viewer, sightings); buildings.Add(viewer, new HashSet<HexCoord>(entry.KnownBuildings));
            }
            ArmySightings.Clear(); KnownBuildingHexes.Clear();
            foreach (var pair in armies) ArmySightings.Add(pair.Key, pair.Value);
            foreach (var pair in buildings) KnownBuildingHexes.Add(pair.Key, pair.Value);
            ObserverMatch = state.ObserverMatch;
        }
    }
}
