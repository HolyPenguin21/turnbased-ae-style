using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;

namespace Game.Terrain
{
    public static class TerrainComplexPlacement
    {
        // Splits `total` instances across templates in proportion to their shares (largest
        // remainder). Ties in the remainder go in random order, so a small total still varies
        // which templates appear. A zero share never receives an instance.
        public static int[] AllocateInstances(IReadOnlyList<int> shares, int total, Func<int, int> randomBelow)
        {
            var result = new int[shares.Count];
            long shareSum = 0;
            foreach (int share in shares) shareSum += Math.Max(0, share);
            if (total <= 0 || shareSum == 0) return result;
            var remainders = new long[shares.Count];
            int assigned = 0;
            for (int i = 0; i < shares.Count; i++)
            {
                long scaled = (long)total * Math.Max(0, shares[i]);
                result[i] = (int)(scaled / shareSum);
                remainders[i] = scaled % shareSum;
                assigned += result[i];
            }
            var order = new int[shares.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = randomBelow(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            // OrderByDescending is stable: equal remainders keep the shuffled order.
            int[] ranked = order.OrderByDescending(i => remainders[i]).ToArray();
            for (int k = 0; k < total - assigned; k++) result[ranked[k]]++;
            return result;
        }

        // Validate the entire footprint and resulting ground graph before any assignment.
        // The caller commits all returned cells together; failure returns no partial footprint.
        public static bool TryValidate(TerrainComplexTemplate template, HexCoord origin,
            IReadOnlyDictionary<HexCoord, int> assignment, IReadOnlyList<TerrainTypeEntry> types,
            int terrainIndex, HashSet<HexCoord> claimed, Func<HexCoord, bool> protectedHex,
            out HexCoord[] cells)
        {
            cells = null;
            if (template == null || !template.IsValid() || terrainIndex < 0 || terrainIndex >= types.Count) return false;
            var footprint = new HashSet<HexCoord>();
            var candidates = new HexCoord[template.parts.Length];
            for (int i = 0; i < candidates.Length; i++)
            {
                var offset = template.parts[i].offset;
                HexCoord h = new HexCoord(origin.Q + offset.x, origin.R + offset.y);
                if (!assignment.TryGetValue(h, out int existing) || claimed.Contains(h)
                    || (protectedHex != null && protectedHex(h)) || !footprint.Add(h)
                    || !Array.Exists(template.allowedTerrainNames, name =>
                        string.Equals(name, types[existing].terrainName, StringComparison.OrdinalIgnoreCase)))
                    return false;
                candidates[i] = h;
            }
            if (!GroundRemainsConnected(assignment, types, footprint, types[terrainIndex].blocksGroundMovement))
                return false;
            cells = candidates;
            return true;
        }

        public static bool GroundRemainsConnected(IReadOnlyDictionary<HexCoord, int> assignment,
            IReadOnlyList<TerrainTypeEntry> types, HashSet<HexCoord> footprint, bool blocksGround)
        {
            var ground = new HashSet<HexCoord>();
            foreach (var pair in assignment)
                if (footprint.Contains(pair.Key) ? !blocksGround : !types[pair.Value].blocksGroundMovement)
                    ground.Add(pair.Key);
            if (ground.Count == 0) return false;
            var seen = new HashSet<HexCoord>();
            var queue = new Queue<HexCoord>();
            foreach (HexCoord h in ground) { seen.Add(h); queue.Enqueue(h); break; }
            while (queue.Count > 0)
                foreach (HexCoord n in HexGridMath.Neighbors(queue.Dequeue()))
                    if (ground.Contains(n) && seen.Add(n)) queue.Enqueue(n);
            return seen.Count == ground.Count;
        }
    }
}
