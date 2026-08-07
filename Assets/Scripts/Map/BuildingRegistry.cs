using System;
using System.Collections.Generic;
using Game.HexGrid;

namespace Game.Map
{
    // Every building currently on the map, keyed by hex — mirrors ArmyRegistry's role for
    // armies. Buildings never change which hex they're on once placed, but their on-hex OFFSET
    // still can (e.g. re-centring when the last army sharing their hex leaves), so
    // HexSelectionController needs a way to find a hex's building (BuildingData.Visual) to
    // reposition it — and card-play spawning needs a way to check what a hex's building can do
    // (BuildingData.Abilities) before deploying a unit there.
    public static class BuildingRegistry
    {
        private static readonly Dictionary<HexCoord, BuildingData> ByHex = new Dictionary<HexCoord, BuildingData>();

        // Fired by Unregister — GameTurnController listens for this to check
        // BuildingData.IsStartingCitadel (the win condition). No combat system exists yet to
        // ever actually call Unregister, same "wired for correctness, currently unreachable"
        // status as BaseViewerModalUI's own Repair button.
        public static event Action<BuildingData> BuildingDestroyed;

        public static void Clear()
        {
            ByHex.Clear();
        }

        public static void Register(HexCoord hex, BuildingData building)
        {
            ByHex[hex] = building;
        }

        public static BuildingData FindAt(HexCoord hex)
        {
            return ByHex.TryGetValue(hex, out BuildingData building) ? building : null;
        }

        // Every building on the map regardless of hex or owner — used by GameTurnController's
        // per-turn resource collection, which needs to visit every player's buildings, not just
        // one hex at a time.
        public static IEnumerable<BuildingData> AllBuildings() => ByHex.Values;

        public static void Unregister(HexCoord hex)
        {
            if (!ByHex.TryGetValue(hex, out BuildingData building))
                return;
            ByHex.Remove(hex);
            BuildingDestroyed?.Invoke(building);
        }
    }
}
