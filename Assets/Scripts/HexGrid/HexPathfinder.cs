using System;
using System.Collections.Generic;
using Game.Map;
using Game.Terrain;
using UnityEngine;

namespace Game.HexGrid
{
    // Cheapest-path search over terrain-weighted hexes. Equal-cost paths still prefer the
    // route with the smallest accumulated distance from the start-to-destination line.
    public static class HexPathfinder
    {
        private const int AvoidPenalty = 20;

        // Decrease-key heap instead of scanning the full frontier and using List.Contains for
        // every expanded neighbor. Order is retained when a queued node improves, matching the
        // old List's stable first-in tie break (cost, then straightness, then insertion order).
        private struct FrontierNode
        {
            public HexCoord Hex;
            public int Cost;
            public float Straightness;
            public long Order;
        }

        private sealed class Frontier
        {
            private readonly List<FrontierNode> _heap = new List<FrontierNode>();
            private readonly Dictionary<HexCoord, int> _indices = new Dictionary<HexCoord, int>();
            private long _nextOrder;

            public int Count => _heap.Count;

            private static bool Less(FrontierNode a, FrontierNode b)
            {
                if (a.Cost != b.Cost) return a.Cost < b.Cost;
                if (a.Straightness != b.Straightness) return a.Straightness < b.Straightness;
                return a.Order < b.Order;
            }

            private void Swap(int a, int b)
            {
                FrontierNode temp = _heap[a];
                _heap[a] = _heap[b];
                _heap[b] = temp;
                _indices[_heap[a].Hex] = a;
                _indices[_heap[b].Hex] = b;
            }

            private void SiftUp(int index)
            {
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (!Less(_heap[index], _heap[parent])) break;
                    Swap(index, parent);
                    index = parent;
                }
            }

            private void SiftDown(int index)
            {
                while (true)
                {
                    int left = index * 2 + 1;
                    if (left >= _heap.Count) break;
                    int best = left;
                    int right = left + 1;
                    if (right < _heap.Count && Less(_heap[right], _heap[left])) best = right;
                    if (!Less(_heap[best], _heap[index])) break;
                    Swap(index, best);
                    index = best;
                }
            }

            public void AddOrDecrease(HexCoord hex, int cost, float straightness)
            {
                if (_indices.TryGetValue(hex, out int index))
                {
                    FrontierNode node = _heap[index];
                    node.Cost = cost;
                    node.Straightness = straightness;
                    _heap[index] = node;
                    SiftUp(index);
                    return;
                }
                var added = new FrontierNode
                {
                    Hex = hex, Cost = cost, Straightness = straightness, Order = _nextOrder++
                };
                _indices[hex] = _heap.Count;
                _heap.Add(added);
                SiftUp(_heap.Count - 1);
            }

            public HexCoord PopMin()
            {
                HexCoord result = _heap[0].Hex;
                _indices.Remove(result);
                int last = _heap.Count - 1;
                if (last == 0)
                {
                    _heap.RemoveAt(0);
                    return result;
                }
                FrontierNode replacement = _heap[last];
                _heap.RemoveAt(last);
                _heap[0] = replacement;
                _indices[replacement.Hex] = 0;
                SiftDown(0);
                return result;
            }
        }

        // avoidHex is a soft penalty; the returned TotalCost is terrain cost only. blockHex
        // forbids entry outright. flatCost is used by aviation (one MP per hex). Preserve the
        // exact destination-specific straightness tie break: Economy's route-threat witness
        // depends on the resulting HEX SEQUENCE, not merely the minimum travel cost.
        public static HexPath FindPath(HexMap map, HexCoord start, HexCoord destination,
            Func<HexCoord, bool> avoidHex = null, Func<HexCoord, bool> blockHex = null,
            bool flatCost = false)
        {
            if (map == null)
                return null;
            if (start.Equals(destination))
                return new HexPath(new List<HexCoord> { start }, 0);

            var costSoFar = new Dictionary<HexCoord, int> { [start] = 0 };
            var cameFrom = new Dictionary<HexCoord, HexCoord>();
            var straightnessCost = new Dictionary<HexCoord, float> { [start] = 0f };
            var frontier = new Frontier();
            frontier.AddOrDecrease(start, 0, 0f);

            Vector3 startPlane = HexGridMath.AxialToWorld(start.Q, start.R, 1f);
            Vector3 lineDir = HexGridMath.AxialToWorld(destination.Q, destination.R, 1f) - startPlane;
            float lineLen = lineDir.magnitude;
            float OffsetFromLine(HexCoord h)
            {
                if (lineLen < 1e-4f)
                    return 0f;
                Vector3 p = HexGridMath.AxialToWorld(h.Q, h.R, 1f) - startPlane;
                return Mathf.Abs(p.x * lineDir.z - p.z * lineDir.x) / lineLen;
            }

            while (frontier.Count > 0)
            {
                HexCoord current = frontier.PopMin();
                if (current.Equals(destination))
                    break;
                int currentCost = costSoFar[current];
                float currentStraightness = straightnessCost[current];
                // Avoid the iterator allocation of HexGridMath.Neighbors once per visited hex.
                foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
                {
                    var next = new HexCoord(current.Q + dq, current.R + dr);
                    if (!map.TryGetTerrainAt(next, out TerrainTypeEntry entry))
                        continue;
                    if (blockHex != null && blockHex(next))
                        continue;
                    int stepCost = flatCost ? 1 : Mathf.Max(1, entry.moveCost);
                    if (avoidHex != null && avoidHex(next))
                        stepCost += AvoidPenalty;
                    int newCost = currentCost + stepCost;
                    bool seen = costSoFar.TryGetValue(next, out int existing);
                    if (seen && existing < newCost)
                        continue;
                    float newStraightness = currentStraightness + OffsetFromLine(next);
                    if (seen && existing == newCost && straightnessCost[next] <= newStraightness)
                        continue;
                    costSoFar[next] = newCost;
                    straightnessCost[next] = newStraightness;
                    cameFrom[next] = current;
                    frontier.AddOrDecrease(next, newCost, newStraightness);
                }
            }

            if (!costSoFar.ContainsKey(destination))
                return null;
            var hexes = new List<HexCoord> { destination };
            HexCoord walk = destination;
            while (!walk.Equals(start))
            {
                walk = cameFrom[walk];
                hexes.Add(walk);
            }
            hexes.Reverse();
            int realCost = 0;
            if (flatCost)
                realCost = hexes.Count - 1;
            else
                for (int i = 1; i < hexes.Count; i++)
                {
                    map.TryGetTerrainAt(hexes[i], out TerrainTypeEntry stepEntry);
                    realCost += stepEntry != null ? Mathf.Max(1, stepEntry.moveCost) : 1;
                }
            return new HexPath(hexes, realCost);
        }

        // Cost-only Dijkstra for a fixed base (forward) or the nearest of several bases
        // (reverse). A blocked hex is allowed as an ENDPOINT but never expanded as transit;
        // this is exactly FindSafePathCost's destination exemption for every queried endpoint.
        // Reverse edges charge the terrain of 'current', not 'next': entering a hex is directed,
        // so cost(A -> B) cannot be inferred from cost(B -> A).
        public static Dictionary<HexCoord, int> FindCosts(HexMap map,
            IEnumerable<HexCoord> sources, Func<HexCoord, bool> blockHex = null,
            int? maxMovement = null, bool reverse = false)
        {
            var costs = new Dictionary<HexCoord, int>();
            if (map == null || sources == null)
                return costs;
            var sourceSet = new HashSet<HexCoord>();
            var frontier = new Frontier();
            foreach (HexCoord start in sources)
                if (map.TryGetTerrainAt(start, out TerrainTypeEntry _) && sourceSet.Add(start))
                {
                    costs[start] = 0;
                    frontier.AddOrDecrease(start, 0, 0f);
                }

            while (frontier.Count > 0)
            {
                HexCoord current = frontier.PopMin();
                if (!sourceSet.Contains(current) && blockHex != null && blockHex(current))
                    continue;
                int currentCost = costs[current];
                int reverseStepCost = 0;
                if (reverse)
                {
                    map.TryGetTerrainAt(current, out TerrainTypeEntry currentEntry);
                    reverseStepCost = Mathf.Max(1, currentEntry.moveCost);
                    // Even a destination-exempt base cannot be ENTERED if its terrain costs
                    // more than the mover can ever pay; standing on it still costs zero.
                    if (maxMovement.HasValue && reverseStepCost > maxMovement.Value)
                        continue;
                }
                foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
                {
                    var next = new HexCoord(current.Q + dq, current.R + dr);
                    if (!map.TryGetTerrainAt(next, out TerrainTypeEntry nextEntry))
                        continue;
                    int stepCost = reverse ? reverseStepCost : Mathf.Max(1, nextEntry.moveCost);
                    if (!reverse && maxMovement.HasValue && stepCost > maxMovement.Value)
                        continue;
                    int newCost = currentCost + stepCost;
                    if (costs.TryGetValue(next, out int oldCost) && oldCost <= newCost)
                        continue;
                    costs[next] = newCost;
                    frontier.AddOrDecrease(next, newCost, 0f);
                }
            }
            return costs;
        }
    }
}
