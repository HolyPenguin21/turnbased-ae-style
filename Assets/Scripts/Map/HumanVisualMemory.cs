using System.Collections.Generic;
using Game.HexGrid;
using Game.Players;

namespace Game.Map
{
    // Player-facing fog memory only. This deliberately does not feed AI decisions; AiMapMemory
    // remains the sole source for those. Moving enemy armies are remembered only for the rest
    // of the observing human's current turn, while stationary buildings remain known until the
    // same hex is observed again and found empty.
    public static class HumanVisualMemory
    {
        private static readonly Dictionary<PlayerSetupData, HashSet<int>> ArmiesSeenThisTurn =
            new Dictionary<PlayerSetupData, HashSet<int>>();
        private static readonly Dictionary<PlayerSetupData, HashSet<HexCoord>> KnownBuildingHexes =
            new Dictionary<PlayerSetupData, HashSet<HexCoord>>();
        private static readonly HashSet<HexCoord> EmptyHexes = new HashSet<HexCoord>();

        public static void Clear()
        {
            ArmiesSeenThisTurn.Clear();
            KnownBuildingHexes.Clear();
        }

        public static void ObserveArmy(PlayerSetupData viewer, int armyId)
        {
            if (viewer == null || !viewer.IsHuman)
                return;
            if (!ArmiesSeenThisTurn.TryGetValue(viewer, out HashSet<int> armies))
            {
                armies = new HashSet<int>();
                ArmiesSeenThisTurn[viewer] = armies;
            }
            armies.Add(armyId);
        }

        public static bool WasArmySeenThisTurn(PlayerSetupData viewer, int armyId)
        {
            return viewer != null && viewer.IsHuman
                && ArmiesSeenThisTurn.TryGetValue(viewer, out HashSet<int> armies)
                && armies.Contains(armyId);
        }

        public static void EndTurn(PlayerSetupData viewer)
        {
            if (viewer != null)
                ArmiesSeenThisTurn.Remove(viewer);
        }

        public static void ObserveBuilding(PlayerSetupData viewer, HexCoord hex, bool exists)
        {
            if (viewer == null || !viewer.IsHuman)
                return;
            if (!KnownBuildingHexes.TryGetValue(viewer, out HashSet<HexCoord> buildings))
            {
                if (!exists)
                    return;
                buildings = new HashSet<HexCoord>();
                KnownBuildingHexes[viewer] = buildings;
            }

            if (exists)
                buildings.Add(hex);
            else
                buildings.Remove(hex);
        }

        public static bool IsBuildingKnown(PlayerSetupData viewer, HexCoord hex)
        {
            return viewer != null && viewer.IsHuman
                && KnownBuildingHexes.TryGetValue(viewer, out HashSet<HexCoord> buildings)
                && buildings.Contains(hex);
        }

        public static IEnumerable<HexCoord> BuildingsKnownBy(PlayerSetupData viewer)
        {
            return viewer != null && viewer.IsHuman
                && KnownBuildingHexes.TryGetValue(viewer, out HashSet<HexCoord> buildings)
                ? buildings
                : EmptyHexes;
        }
    }
}
