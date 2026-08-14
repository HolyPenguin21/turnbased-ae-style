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
        // Added to a hex's routing cost when `avoidHex` flags it (see FindPath) — steers the
        // search toward a detour when a reasonable one exists, without making the hex
        // impassable: a route with no other way through (including the hex actually BEING the
        // destination, e.g. a deliberate attack order) still gets found, just costed higher.
        // A flat constant well above this project's terrain moveCost range (small integers)
        // rather than derived from it, since even a several-hex detour should usually still
        // come out cheaper than eating this penalty once.
        private const int AvoidPenalty = 20;

        // avoidHex: optional soft-avoidance predicate (see the user's own request — route
        // around a hex holding an enemy army when a reasonable detour exists, e.g.
        // `hex => BattleInitiator.FindEnemyAt(hex, mover.Owner) != null`, left to the caller so
        // this stays free of a Game.Combat dependency). Only steers which route gets chosen —
        // the returned HexPath.TotalCost is real terrain cost only, recomputed separately below,
        // matching what ArmyController.MoveRoutine will actually charge during the move itself
        // (it recomputes cost from terrain independently, never reads this search's own routing
        // cost) — so a preview/order never shows or spends an inflated AP/MP figure.
        //
        // blockHex: optional HARD block — unlike avoidHex, a flagged hex is never entered at
        // all, even if that means a longer detour or no route exists (see AiTurnController's
        // own "герой должен идти по посещённым хексам" case: an unvisited hex can hide an
        // enemy the hero has no way to react to, so it must never be crossed blind, not just
        // discouraged). The caller is responsible for exempting `destination` itself if it
        // should still be reachable despite being flagged.
        public static HexPath FindPath(HexMap map, HexCoord start, HexCoord destination,
            System.Func<HexCoord, bool> avoidHex = null, System.Func<HexCoord, bool> blockHex = null)
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
                    if (blockHex != null && blockHex(next))
                        continue;

                    int stepCost = Mathf.Max(1, entry.moveCost);
                    if (avoidHex != null && avoidHex(next))
                        stepCost += AvoidPenalty;
                    int newCost = costSoFar[current] + stepCost;
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

            int realCost = 0;
            for (int i = 1; i < hexes.Count; i++)
            {
                map.TryGetTerrainAt(hexes[i], out TerrainTypeEntry stepEntry);
                realCost += stepEntry != null ? Mathf.Max(1, stepEntry.moveCost) : 1;
            }

            return new HexPath(hexes, realCost);
        }
    }
}
