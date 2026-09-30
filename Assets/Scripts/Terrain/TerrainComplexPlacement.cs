using System;
using System.Collections.Generic;
using Game.HexGrid;

namespace Game.Terrain
{
    public static class TerrainComplexPlacement
    {
        // Validate the entire footprint and resulting ground graph before any assignment.
        // The caller commits all returned cells together; failure returns no partial footprint.
        public static bool TryValidate(TerrainComplexTemplate template, HexCoord origin, int rotation,
            IReadOnlyDictionary<HexCoord, int> assignment, IReadOnlyList<TerrainTypeEntry> types,
            int terrainIndex, HashSet<HexCoord> claimed, Func<HexCoord, bool> protectedHex,
            out HexCoord[] cells)
        {
            cells = null;
            if (template == null || !template.IsValid() || terrainIndex < 0 || terrainIndex >= types.Count
                || !Array.Exists(template.rotations, x => x == rotation)) return false;
            var footprint = new HashSet<HexCoord>();
            var candidates = new HexCoord[template.parts.Length];
            for (int i = 0; i < candidates.Length; i++)
            {
                var offset = template.parts[i].offset;
                HexCoord relative = TerrainComplexTemplate.Rotate(new HexCoord(offset.x, offset.y), rotation);
                HexCoord h = new HexCoord(origin.Q + relative.Q, origin.R + relative.R);
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
