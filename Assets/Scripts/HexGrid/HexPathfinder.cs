using System.Collections.Generic;
using Game.Map;
using Game.Terrain;
using UnityEngine;

namespace Game.HexGrid
{
    // Cheapest-path search over the hex grid, weighted by each terrain's moveCost — only
    // hexes off the map block movement; every on-map hex is enterable, just at whatever cost
    // its terrain sets (e.g. Mountains are simply expensive, not blocked). Plain Dijkstra
    // rather than A*: maps here are small enough (~100 hexes) that the extra heuristic
    // bookkeeping isn't worth it, and the frontier is just a linear-scanned list.
    public static class HexPathfinder
    {
        public static HexPath FindPath(HexMap map, HexCoord start, HexCoord destination)
        {
            if (map == null)
                return null;
            if (start.Equals(destination))
                return new HexPath(new List<HexCoord> { start }, 0);

            var costSoFar = new Dictionary<HexCoord, int> { [start] = 0 };
            var cameFrom = new Dictionary<HexCoord, HexCoord>();
            var frontier = new List<HexCoord> { start };

            while (frontier.Count > 0)
            {
                int bestIndex = 0;
                for (int i = 1; i < frontier.Count; i++)
                    if (costSoFar[frontier[i]] < costSoFar[frontier[bestIndex]])
                        bestIndex = i;

                HexCoord current = frontier[bestIndex];
                frontier.RemoveAt(bestIndex);

                if (current.Equals(destination))
                    break;

                foreach (HexCoord next in HexGridMath.Neighbors(current))
                {
                    if (!map.TryGetTerrainAt(next, out TerrainTypeEntry entry))
                        continue;

                    int newCost = costSoFar[current] + Mathf.Max(1, entry.moveCost);
                    if (costSoFar.TryGetValue(next, out int existing) && existing <= newCost)
                        continue;

                    costSoFar[next] = newCost;
                    cameFrom[next] = current;
                    frontier.Add(next);
                }
            }

            if (!costSoFar.ContainsKey(destination))
                return null; // unreachable — blocked off, or past the map edge

            var hexes = new List<HexCoord> { destination };
            HexCoord walk = destination;
            while (!walk.Equals(start))
            {
                walk = cameFrom[walk];
                hexes.Add(walk);
            }
            hexes.Reverse();

            return new HexPath(hexes, costSoFar[destination]);
        }
    }
}
