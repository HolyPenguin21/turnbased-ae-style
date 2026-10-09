using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;

namespace Game.Terrain
{
    public static class TerrainComplexPlacement
    {
        // Choose after family allocation; orientations must not change template weights.
        public static int ChooseRotationSteps(TerrainComplexTemplate template, Func<int, int> randomBelow) =>
            template != null && template.randomizeRotation ? randomBelow(6) : 0;

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

        // For each non-empty exclusiveGroup keeps ONE member, picked by weight = max(1, count) so
        // equal counts give even odds; returns the indices to keep (ungrouped always kept), in
        // the original order. randomBelow(n) is a uniform integer in [0, n).
        public static List<int> ChooseFromExclusiveGroups(IReadOnlyList<TerrainComplexTemplate> templates,
            Func<int, int> randomBelow)
        {
            var kept = new HashSet<int>();
            var groups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < templates.Count; i++)
            {
                string g = templates[i]?.exclusiveGroup;
                if (string.IsNullOrEmpty(g)) { kept.Add(i); continue; }
                if (!groups.TryGetValue(g, out List<int> members)) groups[g] = members = new List<int>();
                members.Add(i);
            }
            foreach (List<int> members in groups.Values)
            {
                int total = 0;
                foreach (int m in members) total += Math.Max(1, templates[m].count);
                int roll = randomBelow(total);
                foreach (int m in members)
                {
                    roll -= Math.Max(1, templates[m].count);
                    if (roll < 0) { kept.Add(m); break; }
                }
            }
            return kept.OrderBy(i => i).ToList();
        }

        // Inclusive centre-distance range (in rings) every footprint cell must lie in. The band
        // comes from the template's fractions of the radius; a blocking terrain is further capped
        // at radius - edgeMarginRings. min is raised to 1 when the band starts above 0 so a
        // "near the middle" template never lands on the exact centre hex.
        public static void CenterDistanceRange(TerrainComplexTemplate template, bool blocksGround,
            int radius, int edgeMarginRings, out int min, out int max)
        {
            float lo = template == null ? 0f : template.minCenterFraction;
            float hi = template == null ? 1f : template.maxCenterFraction;
            min = (int)Math.Round(lo * radius, MidpointRounding.AwayFromZero);
            if (lo > 0f) min = Math.Max(1, min);
            max = (int)Math.Round(hi * radius, MidpointRounding.AwayFromZero);
            if (blocksGround) max = Math.Min(max, radius - Math.Max(0, edgeMarginRings));
        }

        // Validate the entire footprint and resulting ground graph before any assignment.
        // The caller commits all returned cells together; failure returns no partial footprint.
        public static bool TryValidate(TerrainComplexTemplate template, HexCoord origin,
            IReadOnlyDictionary<HexCoord, int> assignment, IReadOnlyList<TerrainTypeEntry> types,
            int terrainIndex, HashSet<HexCoord> claimed, Func<HexCoord, bool> protectedHex,
            out HexCoord[] cells, int minCenterDistance = 0, int maxCenterDistance = int.MaxValue,
            int rotationSteps = 0)
        {
            cells = null;
            if (template == null || !template.useInGeneration || !template.IsValid()
                || terrainIndex < 0 || terrainIndex >= types.Count) return false;
            var footprint = new HashSet<HexCoord>();
            var candidates = new HexCoord[template.parts.Length];
            for (int i = 0; i < candidates.Length; i++)
            {
                var authored = template.parts[i].offset;
                HexCoord offset = HexGridMath.RotateOffset60(new HexCoord(authored.x, authored.y), rotationSteps);
                HexCoord h = new HexCoord(origin.Q + offset.Q, origin.R + offset.R);
                if (!assignment.TryGetValue(h, out int existing) || claimed.Contains(h)
                    || (protectedHex != null && protectedHex(h)) || !footprint.Add(h)
                    || HexGridMath.Distance(new HexCoord(0, 0), h) < minCenterDistance
                    || HexGridMath.Distance(new HexCoord(0, 0), h) > maxCenterDistance
                    || !Array.Exists(template.allowedTerrainNames, name =>
                        string.Equals(name, types[existing].terrainName, StringComparison.OrdinalIgnoreCase)))
                    return false;
                candidates[i] = h;
            }
            // Two separate impassable complexes never touch: a footprint cell next to a cell of an
            // already placed blocking complex would fuse them into one wall (and pinch the ground).
            if (types[terrainIndex].blocksGroundMovement)
                foreach (HexCoord cell in footprint)
                    foreach (HexCoord n in HexGridMath.Neighbors(cell))
                        if (!footprint.Contains(n) && claimed.Contains(n)
                            && assignment.TryGetValue(n, out int neighbourType)
                            && types[neighbourType].blocksGroundMovement)
                            return false;
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

