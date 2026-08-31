using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Tactical-only continuation AFTER the assigned strategic Explore focus is satisfied. It never
    // replaces that focus: TaskExecutor permits at most one such step, and only to an adjacent,
    // unvisited, currently-safe hex selected from live post-move information.
    internal static class ScoutExploreContinuation
    {
        public static HexCoord? Pick(PlayerSetupData player, HexMap map, ArmyData army, int turn)
        {
            if (player == null || map == null || army == null || army.CurrentMovement <= 0)
                return null;

            List<HexCoord> candidates = HexGridMath.Neighbors(army.Hex)
                .Where(h => map.TryGetTerrainAt(h, out _))
                .Where(h => !VisionSystem.IsVisited(player, h))
                .Where(h => !AiMapMemory.IsScoutDangerous(player, h))
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
                if (map.TryGetTerrainAt(h, out _)
                    && !VisionSystem.IsVisited(player, h)
                    && !AiMapMemory.IsScoutDangerous(player, h))
                    n++;
            return n;
        }
    }
}
