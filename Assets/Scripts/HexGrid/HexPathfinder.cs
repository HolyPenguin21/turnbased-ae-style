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

        // ---- Flat grid view of one map revision ------------------------------------------------
        // The searches index hexes into arrays instead of hashing HexCoord into dictionaries on
        // every neighbour. One view per (map instance, PathingVersion), rebuilt on change.
        internal sealed class Grid
        {
            internal HexMap Map;
            internal int Version;
            internal int MinQ, MinR, Width;
            internal TerrainTypeEntry[] Terrain; // null where the bounding box has no map hex

            internal int Count => Terrain.Length;

            internal int IndexOf(HexCoord c)
            {
                int q = c.Q - MinQ, r = c.R - MinR;
                if (q < 0 || r < 0 || q >= Width) return -1;
                int i = r * Width + q;
                return i < Terrain.Length && Terrain[i] != null ? i : -1;
            }

            internal HexCoord CoordOf(int i) => new HexCoord(MinQ + i % Width, MinR + i / Width);

            // The one terrain-entry rule (HexMap.CanEnter) on the array.
            internal bool CanEnter(int i, bool airborne) =>
                i >= 0 && (airborne || !Terrain[i].blocksGroundMovement);
        }

        private static Grid _grid;

        internal static Grid GridFor(HexMap map)
        {
            if (_grid != null && ReferenceEquals(_grid.Map, map) && _grid.Version == map.PathingVersion)
                return _grid;
            int minQ = int.MaxValue, minR = int.MaxValue, maxQ = int.MinValue, maxR = int.MinValue;
            foreach (HexCoord c in map.AllCoords)
            {
                if (c.Q < minQ) minQ = c.Q;
                if (c.R < minR) minR = c.R;
                if (c.Q > maxQ) maxQ = c.Q;
                if (c.R > maxR) maxR = c.R;
            }
            var grid = new Grid { Map = map, Version = map.PathingVersion, MinQ = 0, MinR = 0, Width = 1 };
            if (minQ > maxQ)
            {
                grid.Terrain = new TerrainTypeEntry[0];
            }
            else
            {
                grid.MinQ = minQ;
                grid.MinR = minR;
                grid.Width = maxQ - minQ + 1;
                grid.Terrain = new TerrainTypeEntry[grid.Width * (maxR - minR + 1)];
                foreach (HexCoord c in map.AllCoords)
                    if (map.TryGetTerrainAt(c, out TerrainTypeEntry entry))
                        grid.Terrain[(c.R - minR) * grid.Width + (c.Q - minQ)] = entry;
            }
            _grid = grid;
            return grid;
        }

        // ---- Reusable search state ---------------------------------------------------------------
        // A generation stamp marks which slots belong to the current search, so nothing is cleared
        // between searches. Rented per search and returned, so a re-entrant search (a blockHex
        // callback that itself routes) gets its own buffers.
        private sealed class SearchBuffers
        {
            internal int[] Cost = new int[0];
            internal int[] Parent = new int[0];
            internal float[] Straight = new float[0];
            internal int[] Priority = new int[0];
            internal int[] Remaining = new int[0];
            internal long[] Order = new long[0];
            internal int[] HeapPos = new int[0];
            internal int[] Seen = new int[0];
            internal int[] Closed = new int[0];
            internal int[] Heap = new int[0];
            internal int HeapCount;
            internal int Generation;

            internal void Begin(int size)
            {
                if (Cost.Length < size)
                {
                    Cost = new int[size]; Parent = new int[size]; Straight = new float[size];
                    Priority = new int[size]; Remaining = new int[size]; Order = new long[size];
                    HeapPos = new int[size]; Seen = new int[size]; Closed = new int[size];
                    Heap = new int[size];
                    Generation = 0;
                }
                HeapCount = 0;
                if (++Generation == int.MaxValue)
                {
                    System.Array.Clear(Seen, 0, Seen.Length);
                    System.Array.Clear(Closed, 0, Closed.Length);
                    Generation = 1;
                }
            }

            // Priority, then Remaining (deeper first among equal priority), then the destination
            // line straightness, then insertion order — fully deterministic.
            private bool Less(int a, int b)
            {
                if (Priority[a] != Priority[b]) return Priority[a] < Priority[b];
                if (Remaining[a] != Remaining[b]) return Remaining[a] < Remaining[b];
                if (Straight[a] != Straight[b]) return Straight[a] < Straight[b];
                return Order[a] < Order[b];
            }

            private void Place(int slot, int node) { Heap[slot] = node; HeapPos[node] = slot; }

            private void SiftUp(int slot)
            {
                int node = Heap[slot];
                while (slot > 0)
                {
                    int parent = (slot - 1) / 2;
                    if (!Less(node, Heap[parent])) break;
                    Place(slot, Heap[parent]);
                    slot = parent;
                }
                Place(slot, node);
            }

            private void SiftDown(int slot)
            {
                int node = Heap[slot];
                while (true)
                {
                    int left = slot * 2 + 1;
                    if (left >= HeapCount) break;
                    int best = left;
                    if (left + 1 < HeapCount && Less(Heap[left + 1], Heap[left])) best = left + 1;
                    if (!Less(Heap[best], node)) break;
                    Place(slot, Heap[best]);
                    slot = best;
                }
                Place(slot, node);
            }

            // `inHeap` — the node is already queued in this generation (its keys just improved).
            internal void PushOrDecrease(int node, bool inHeap)
            {
                if (inHeap) { SiftUp(HeapPos[node]); return; }
                Place(HeapCount, node);
                HeapCount++;
                SiftUp(HeapCount - 1);
            }

            internal int PopMin()
            {
                int top = Heap[0];
                HeapCount--;
                if (HeapCount > 0)
                {
                    Place(0, Heap[HeapCount]);
                    SiftDown(0);
                }
                HeapPos[top] = -1;
                return top;
            }
        }

        private static readonly Stack<SearchBuffers> _bufferPool = new Stack<SearchBuffers>();
        private static SearchBuffers RentBuffers(int size)
        {
            SearchBuffers buffers = _bufferPool.Count > 0 ? _bufferPool.Pop() : new SearchBuffers();
            buffers.Begin(size);
            return buffers;
        }
        private static void ReturnBuffers(SearchBuffers buffers) => _bufferPool.Push(buffers);

        // By coordinate, not by index offset: an offset would wrap a neighbour past the bounding
        // box edge into the next row.
        private static int NeighbourIndex(Grid grid, HexCoord at, int dq, int dr) =>
            grid.IndexOf(new HexCoord(at.Q + dq, at.R + dr));

        // avoidHex is a soft penalty; the returned TotalCost is terrain cost only. blockHex
        // forbids entry outright. flatCost is used by aviation (one MP per hex). maxStepCost
        // forbids entering any hex whose terrain costs more than it (a mover's MaxMovement).
        // A* with the hex distance as the remaining-cost bound: every step costs at least 1, so
        // the bound never overestimates and the returned path is still a cheapest one. Among
        // equally cheap paths the destination-line straightness decides as far as the search
        // order reaches them (2026-10-17: owner accepted that equal-cost ties may differ from
        // the earlier full Dijkstra in exchange for not flooding the whole map per query).
        public static HexPath FindPath(HexMap map, HexCoord start, HexCoord destination,
            Func<HexCoord, bool> avoidHex = null, Func<HexCoord, bool> blockHex = null,
            bool flatCost = false, int? maxStepCost = null)
        {
            if (map == null)
                return null;
            Grid grid = GridFor(map);
            int startIndex = grid.IndexOf(start);
            int destIndex = grid.IndexOf(destination);
            if (!grid.CanEnter(startIndex, flatCost) || !grid.CanEnter(destIndex, flatCost))
                return null;
            if (start.Equals(destination))
                return new HexPath(new List<HexCoord> { start }, 0);

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

            SearchBuffers b = RentBuffers(grid.Count);
            try
            {
                int gen = b.Generation;
                long order = 0;
                b.Seen[startIndex] = gen;
                b.Cost[startIndex] = 0;
                b.Straight[startIndex] = 0f;
                b.Parent[startIndex] = -1;
                b.Remaining[startIndex] = HexGridMath.Distance(start, destination);
                b.Priority[startIndex] = b.Remaining[startIndex];
                b.Order[startIndex] = order++;
                b.PushOrDecrease(startIndex, inHeap: false);

                bool found = false;
                while (b.HeapCount > 0)
                {
                    int current = b.PopMin();
                    if (current == destIndex) { found = true; break; }
                    b.Closed[current] = gen;
                    HexCoord at = grid.CoordOf(current);
                    int currentCost = b.Cost[current];
                    float currentStraightness = b.Straight[current];
                    foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
                    {
                        int next = NeighbourIndex(grid, at, dq, dr);
                        if (!grid.CanEnter(next, flatCost) || b.Closed[next] == gen)
                            continue;
                        TerrainTypeEntry entry = grid.Terrain[next];
                        int terrainCost = Mathf.Max(1, entry.moveCost);
                        if (!flatCost && maxStepCost.HasValue && terrainCost > maxStepCost.Value)
                            continue;
                        HexCoord nextHex = grid.CoordOf(next);
                        if (blockHex != null && blockHex(nextHex))
                            continue;
                        int stepCost = flatCost ? 1 : terrainCost;
                        if (avoidHex != null && avoidHex(nextHex))
                            stepCost += AvoidPenalty;
                        int newCost = currentCost + stepCost;
                        bool seen = b.Seen[next] == gen;
                        if (seen && b.Cost[next] < newCost)
                            continue;
                        float newStraightness = currentStraightness + OffsetFromLine(nextHex);
                        if (seen && b.Cost[next] == newCost && b.Straight[next] <= newStraightness)
                            continue;
                        if (!seen)
                        {
                            b.Seen[next] = gen;
                            b.Remaining[next] = HexGridMath.Distance(nextHex, destination);
                            b.Order[next] = order++;
                        }
                        b.Cost[next] = newCost;
                        b.Straight[next] = newStraightness;
                        b.Parent[next] = current;
                        b.Priority[next] = newCost + b.Remaining[next];
                        b.PushOrDecrease(next, inHeap: seen);
                    }
                }

                if (!found)
                    return null;
                var hexes = new List<HexCoord>();
                for (int walk = destIndex; walk >= 0; walk = b.Parent[walk])
                    hexes.Add(grid.CoordOf(walk));
                hexes.Reverse();
                return new HexPath(hexes, RealCost(grid, hexes, flatCost));
            }
            finally
            {
                ReturnBuffers(b);
            }
        }

        private static int RealCost(Grid grid, List<HexCoord> hexes, bool flatCost)
        {
            if (flatCost)
                return hexes.Count - 1;
            int realCost = 0;
            for (int i = 1; i < hexes.Count; i++)
            {
                int index = grid.IndexOf(hexes[i]);
                realCost += index >= 0 ? Mathf.Max(1, grid.Terrain[index].moveCost) : 1;
            }
            return realCost;
        }

        // One source, every target: a ground Dijkstra tree from `source` under the SAME rules as
        // FindPath with a transit blocker and maxStepCost — a hex in `transitBlocked` may END a
        // path (the destination exemption every route caller uses) but is never crossed, and a
        // hex costing more than maxStepCost is never entered. A caller asking many targets from
        // one mover builds this once instead of one point-to-point search per target. Costs are
        // exact; among equally cheap paths the tree keeps the first one found (not the
        // per-destination straightness of FindPath).
        public sealed class CostTree
        {
            private readonly Grid _grid;
            private readonly int[] _cost;
            private readonly int[] _parent;

            internal CostTree(Grid grid, int[] cost, int[] parent)
            {
                _grid = grid;
                _cost = cost;
                _parent = parent;
            }

            public int CostTo(HexCoord target)
            {
                int index = _grid.IndexOf(target);
                return index >= 0 && _cost[index] != int.MaxValue ? _cost[index] : int.MaxValue;
            }

            // A fresh path (callers may keep or edit it), or null when unreachable.
            public HexPath PathTo(HexCoord target)
            {
                int index = _grid.IndexOf(target);
                if (index < 0 || _cost[index] == int.MaxValue)
                    return null;
                var hexes = new List<HexCoord>();
                for (int walk = index; walk >= 0; walk = _parent[walk])
                    hexes.Add(_grid.CoordOf(walk));
                hexes.Reverse();
                return new HexPath(hexes, _cost[index]);
            }
        }

        public static CostTree BuildCostTree(HexMap map, HexCoord source,
            ICollection<HexCoord> transitBlocked = null, int? maxStepCost = null)
        {
            if (map == null)
                return null;
            Grid grid = GridFor(map);
            var cost = new int[grid.Count];
            var parent = new int[grid.Count];
            for (int i = 0; i < cost.Length; i++) { cost[i] = int.MaxValue; parent[i] = -1; }
            int sourceIndex = grid.IndexOf(source);
            if (!grid.CanEnter(sourceIndex, false))
                return new CostTree(grid, cost, parent);

            bool[] blocked = null;
            if (transitBlocked != null && transitBlocked.Count > 0)
            {
                blocked = new bool[grid.Count];
                foreach (HexCoord hex in transitBlocked)
                {
                    int index = grid.IndexOf(hex);
                    if (index >= 0) blocked[index] = true;
                }
            }

            SearchBuffers b = RentBuffers(grid.Count);
            try
            {
                int gen = b.Generation;
                long order = 0;
                cost[sourceIndex] = 0;
                b.Seen[sourceIndex] = gen;
                b.Priority[sourceIndex] = 0;
                b.Remaining[sourceIndex] = 0;
                b.Straight[sourceIndex] = 0f;
                b.Order[sourceIndex] = order++;
                b.PushOrDecrease(sourceIndex, inHeap: false);
                while (b.HeapCount > 0)
                {
                    int current = b.PopMin();
                    b.Closed[current] = gen;
                    // A blocked hex is an endpoint only: reached, never expanded (the source,
                    // which the mover already stands on, always expands).
                    if (current != sourceIndex && blocked != null && blocked[current])
                        continue;
                    HexCoord at = grid.CoordOf(current);
                    int currentCost = cost[current];
                    foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
                    {
                        int next = NeighbourIndex(grid, at, dq, dr);
                        if (!grid.CanEnter(next, false) || b.Closed[next] == gen)
                            continue;
                        int stepCost = Mathf.Max(1, grid.Terrain[next].moveCost);
                        if (maxStepCost.HasValue && stepCost > maxStepCost.Value)
                            continue;
                        int newCost = currentCost + stepCost;
                        bool seen = b.Seen[next] == gen;
                        if (seen && cost[next] <= newCost)
                            continue;
                        if (!seen)
                        {
                            b.Seen[next] = gen;
                            b.Remaining[next] = 0;
                            b.Straight[next] = 0f;
                            b.Order[next] = order++;
                        }
                        cost[next] = newCost;
                        parent[next] = current;
                        b.Priority[next] = newCost;
                        b.PushOrDecrease(next, inHeap: seen);
                    }
                }
            }
            finally
            {
                ReturnBuffers(b);
            }
            return new CostTree(grid, cost, parent);
        }

        // Turn-aware ground route (project owner, 2026-10-02: a 2-movement army crawled one hex per
        // turn over cost-2 terrain). A ground army pays hexes out of a per-turn budget and CANNOT
        // enter a hex costing more than what is left, so the cheapest route by total cost is not
        // the fastest: [1,2,1,1,2,1] wastes a point whenever a 2 follows a 1. This search runs over
        // (hex, movement left) states and minimises TURNS first, then total cost, then the same
        // destination-line straightness as FindPath. A forced wait costs one extra cost point so a
        // route that can step now beats an equal-turn route that has to wait first.
        public static HexPath FindPathByTurns(HexMap map, HexCoord start, HexCoord destination,
            int currentMovement, int maxMovement, Func<HexCoord, bool> blockHex = null)
        {
            if (map == null || maxMovement <= 0 || !map.CanEnter(start) || !map.CanEnter(destination))
                return null;
            if (start.Equals(destination))
                return new HexPath(new List<HexCoord> { start }, 0);
            currentMovement = Mathf.Clamp(currentMovement, 0, maxMovement);

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

            var best = new Dictionary<(HexCoord, int), TurnLabel>();
            var cameFrom = new Dictionary<(HexCoord, int), (HexCoord, int)>();
            var open = new SortedSet<TurnNode>(TurnNodeComparer.Instance);
            long order = 0;
            var origin = (start, currentMovement);
            best[origin] = new TurnLabel(0, 0, 0f);
            open.Add(new TurnNode(best[origin], order++, start, currentMovement));
            (HexCoord, int)? goal = null;

            while (open.Count > 0)
            {
                TurnNode node = open.Min;
                open.Remove(node);
                var key = (node.Hex, node.Left);
                if (best.TryGetValue(key, out TurnLabel known) && TurnNodeComparer.Compare(known, node.Label) < 0)
                    continue;
                if (node.Hex.Equals(destination))
                {
                    goal = key;
                    break;
                }
                foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
                {
                    var next = new HexCoord(node.Hex.Q + dq, node.Hex.R + dr);
                    if (!map.CanEnter(next) || !map.TryGetTerrainAt(next, out TerrainTypeEntry entry))
                        continue;
                    if (blockHex != null && blockHex(next))
                        continue;
                    int step = Mathf.Max(1, entry.moveCost);
                    if (step > maxMovement)
                        continue;
                    int turns = node.Label.Turns;
                    int cost = node.Label.Cost + step;
                    int left = node.Left - step;
                    if (left < 0)
                    {
                        turns++;
                        cost++; // the forced wait
                        left = maxMovement - step;
                    }
                    var label = new TurnLabel(turns, cost, node.Label.Straightness + OffsetFromLine(next));
                    var nextKey = (next, left);
                    if (best.TryGetValue(nextKey, out TurnLabel old) && TurnNodeComparer.Compare(old, label) <= 0)
                        continue;
                    best[nextKey] = label;
                    cameFrom[nextKey] = key;
                    open.Add(new TurnNode(label, order++, next, left));
                }
            }

            if (!goal.HasValue)
                return null;
            var hexes = new List<HexCoord>();
            (HexCoord, int) walk = goal.Value;
            hexes.Add(walk.Item1);
            while (!walk.Equals(origin))
            {
                walk = cameFrom[walk];
                hexes.Add(walk.Item1);
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

        private readonly struct TurnLabel
        {
            public readonly int Turns;
            public readonly int Cost;
            public readonly float Straightness;
            public TurnLabel(int turns, int cost, float straightness)
            {
                Turns = turns; Cost = cost; Straightness = straightness;
            }
        }

        private readonly struct TurnNode
        {
            public readonly TurnLabel Label;
            public readonly long Order;
            public readonly HexCoord Hex;
            public readonly int Left;
            public TurnNode(TurnLabel label, long order, HexCoord hex, int left)
            {
                Label = label; Order = order; Hex = hex; Left = left;
            }
        }

        private sealed class TurnNodeComparer : IComparer<TurnNode>
        {
            public static readonly TurnNodeComparer Instance = new TurnNodeComparer();

            public static int Compare(TurnLabel a, TurnLabel b)
            {
                int c = a.Turns.CompareTo(b.Turns);
                if (c != 0) return c;
                c = a.Cost.CompareTo(b.Cost);
                return c != 0 ? c : a.Straightness.CompareTo(b.Straightness);
            }

            public int Compare(TurnNode a, TurnNode b)
            {
                int c = Compare(a.Label, b.Label);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            }
        }

        // Cost-only Dijkstra for a fixed base (forward) or the nearest of several bases
        // (reverse). A blocked hex is allowed as an ENDPOINT but never expanded as transit;
        // this is exactly FindSafePathCost's destination exemption for every queried endpoint.
        // Reverse edges charge the terrain of 'current', not 'next': entering a hex is directed,
        // so cost(A -> B) cannot be inferred from cost(B -> A).
        public static Dictionary<HexCoord, int> FindCosts(HexMap map,
            IEnumerable<HexCoord> sources, Func<HexCoord, bool> blockHex = null,
            int? maxMovement = null, bool reverse = false, bool flatCost = false)
        {
            var costs = new Dictionary<HexCoord, int>();
            if (map == null || sources == null)
                return costs;
            var sourceSet = new HashSet<HexCoord>();
            var frontier = new Frontier();
            foreach (HexCoord start in sources)
                if (map.CanEnter(start, flatCost) && sourceSet.Add(start))
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
                    reverseStepCost = flatCost ? 1 : Mathf.Max(1, currentEntry.moveCost);
                    // Even a destination-exempt base cannot be ENTERED if its terrain costs
                    // more than the mover can ever pay; standing on it still costs zero.
                    if (maxMovement.HasValue && reverseStepCost > maxMovement.Value)
                        continue;
                }
                foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
                {
                    var next = new HexCoord(current.Q + dq, current.R + dr);
                    if (!map.CanEnter(next, flatCost) || !map.TryGetTerrainAt(next, out TerrainTypeEntry nextEntry))
                        continue;
                    int stepCost = flatCost ? 1 : reverse ? reverseStepCost : Mathf.Max(1, nextEntry.moveCost);
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
