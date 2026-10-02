using System;
using System.Collections.Generic;
using Game.HexGrid;
using UnityEngine;

namespace Game.Terrain
{
    [Serializable]
    public sealed class TerrainComplexPart
    {
        public Vector2Int offset;
        public Texture2D[] frames;
    }

    // Authoring only. Gameplay terrain remains solely in HexMap's per-cell data.
    [Serializable]
    public sealed class TerrainComplexTemplate
    {
        public string name;
        public string terrainName;
        [Tooltip("Use this complex when generating maps for this biome. Disabled templates receive no placements.")]
        public bool useInGeneration = true;
        // Relative share of MapGenerationSettings.complexCount, not an absolute instance count.
        [Min(0)] public int count = 1;
        [Min(1)] public int placementAttempts = 64;
        // Templates of the same non-empty group are alternatives: per generated map exactly ONE of
        // the enabled members is used (chosen by `count` as relative weight, equal counts = even
        // odds), the others get no placements. Empty = not part of any group.
        public string exclusiveGroup = "";
        // Allowed distance of EVERY footprint cell from the map centre, as a fraction of the map
        // radius. 0..1 (default) = anywhere. Deep canyon uses a band near the middle, but not on
        // the exact centre. An impassable template is additionally kept off the map edge by
        // MapGenerationSettings.impassableEdgeMarginRings, whatever the band says.
        [Range(0f, 1f)] public float minCenterFraction = 0f;
        [Range(0f, 1f)] public float maxCenterFraction = 1f;
        [Min(0.01f)] public float framesPerSecond = 3f;
        // Offsets are the authored, final footprint. Runtime generation may translate the
        // whole complex to another origin, but never rotates or mirrors this shape.
        public string[] allowedTerrainNames = { "Desert", "Sand dunes", "Rock desert" };
        public TerrainComplexPart[] parts;

        public bool IsValid()
        {
            if (string.IsNullOrEmpty(terrainName) || parts == null || parts.Length < 1
                || parts.Length > 3 || allowedTerrainNames == null || allowedTerrainNames.Length == 0)
                return false;
            var offsets = new HashSet<HexCoord>();
            int frameCount = parts[0]?.frames?.Length ?? 0;
            if (frameCount == 0) return false;
            foreach (TerrainComplexPart part in parts)
            {
                if (part == null || part.frames == null || part.frames.Length != frameCount
                    || !offsets.Add(new HexCoord(part.offset.x, part.offset.y))) return false;
                foreach (Texture2D frame in part.frames) if (frame == null) return false;
            }
            // Reject disconnected shapes even when all their cells happen to exist.
            var seen = new HashSet<HexCoord>();
            var queue = new Queue<HexCoord>();
            var first = new HexCoord(parts[0].offset.x, parts[0].offset.y);
            seen.Add(first); queue.Enqueue(first);
            while (queue.Count > 0)
                foreach (HexCoord n in HexGridMath.Neighbors(queue.Dequeue()))
                    if (offsets.Contains(n) && seen.Add(n)) queue.Enqueue(n);
            return seen.Count == parts.Length;
        }

    }
}
