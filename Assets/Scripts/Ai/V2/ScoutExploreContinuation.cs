using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Tactical-only continuation after the strategic Explore focus is already reached. It never
    // changes the mission/intention: it merely spends movement that has already been activated on
    // an adjacent, still-unvisited, currently-safe hex. Every call reads live vision/memory after
    // the previous MoveArmyRoutine settled, so newly revealed danger stops further expansion.
    internal static class ScoutExploreContinuation
    {
        public static HexCoord? Pick(PlayerSetupData player, HexMap map, ArmyData army, int turn)
        {
            if (player == null || map == null || army == null || army.CurrentMovement <= 0)
                return null;

            List<HexCoord> candidates = HexGridMath.Neighbors(army.Hex)
                .Where(h => map.TryGetTerrainAt(h, out _))
                .Where(h => !VisionSystem.IsVisited(player, h))
                .Where(h => !ScoutExecutionSafety.VantageBlockedNow(player, h, turn))
                .Where(h => VisitHexTask.FindNextSafeStep(map, army, h).HasValue)
                .OrderByDescending(h => FreshNeighborCount(player, map, h))
                .ThenBy(h => h.Q)
                .ThenBy(h => h.R)
                .ToList();

            return candidates.Count > 0 ? candidates[0] : (HexCoord?)null;
        }

        private static int FreshNeighborCount(PlayerSetupData player, HexMap map, HexCoord center)
        {
            int n = 0;
            foreach (HexCoord h in HexGridMath.Neighbors(center))
                if (map.TryGetTerrainAt(h, out _) && !VisionSystem.IsVisited(player, h))
                    n++;
            return n;
        }
    }
}
