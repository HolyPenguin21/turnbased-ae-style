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
        private readonly struct Candidate
        {
            public readonly HexCoord Hex;
            public readonly int FreshNeighbors;
            public readonly int MoveCost;
            public readonly float InformationPerMovement;

            public Candidate(HexCoord hex, int freshNeighbors, int moveCost)
            {
                Hex = hex;
                FreshNeighbors = freshNeighbors;
                MoveCost = System.Math.Max(1, moveCost);
                // Visiting the destination itself is one useful reveal in addition to whatever
                // fresh neighbours it opens. Comparing this against the REAL terrain MP cost keeps
                // a rough/mountain step from winning merely because it has one extra dark neighbour
                // when a cheap step would leave enough movement for more scouting this turn.
                InformationPerMovement = (1f + freshNeighbors) / MoveCost;
            }
        }

        public static HexCoord? Pick(PlayerSetupData player, HexMap map, ArmyData army, int turn)
        {
            if (player == null || map == null || army == null || army.CurrentMovement <= 0)
                return null;

            var candidates = new List<Candidate>();
            foreach (HexCoord h in HexGridMath.Neighbors(army.Hex))
            {
                if (!map.TryGetTerrainAt(h, out var terrain))
                    continue;
                if (VisionSystem.IsVisited(player, h)
                    || AiMapMemory.IsScoutDangerous(player, h)
                    || ScoutExecutionSafety.VantageBlockedNow(player, h, turn))
                    continue;
                if (!VisitHexTask.FindNextSafeStep(map, army, h).HasValue)
                    continue;

                int moveCost = terrain != null ? System.Math.Max(1, terrain.moveCost) : 1;
                candidates.Add(new Candidate(h, FreshNeighborCount(player, map, h), moveCost));
            }

            Candidate? best = candidates
                .OrderByDescending(c => c.InformationPerMovement)
                .ThenByDescending(c => c.FreshNeighbors)
                .ThenBy(c => c.MoveCost)
                .ThenBy(c => c.Hex.Q)
                .ThenBy(c => c.Hex.R)
                .Cast<Candidate?>()
                .FirstOrDefault();

            return best.HasValue ? best.Value.Hex : (HexCoord?)null;
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
