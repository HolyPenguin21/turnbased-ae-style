using System.Collections.Generic;
using UnityEngine;

namespace Game.Terrain
{
    // All the tunable numbers/lists that drive HexMapGenerator, grouped here (lives inside
    // GameConfig) instead of as separate [SerializeField] fields on the generator itself, so
    // map balancing sits in the same one place as everything else.
    [System.Serializable]
    public class MapGenerationSettings
    {
        [Header("Grid")]
        // Manual's "Normal" map size is 12 hexes by 9 hexes.
        public int width = 12;
        public int height = 9;
        public float outerRadius = 1f;
        [Range(0f, 1f)] public float blend = 0.25f;
        [Range(0f, 1f)] public float alpha = 0.5f;
        public Color groundColor = new Color(0.29f, 0.26f, 0.22f);

        // Every hex on the map (Ruins, Salt Flats, Wasteland, plain Desert, all of it) is drawn
        // from this one weighted pool via its own baselineWeight — there's no separate
        // "resource type" placement rule any more, since in the original game any hex can carry
        // resources, it's purely a matter of how much (see TerrainTypeEntry.resourceYields).
        [Header("Terrain Types")]
        public List<TerrainTypeEntry> terrainTypes = new List<TerrainTypeEntry>();

        // Mountains are the one type still placed by a dedicated rule instead of the baseline
        // weighted pool — they form a few connected chains rather than scattering as single
        // hexes, so this name lookup pulls them out of the pool the same way Ruins/Wasteland
        // used to be pulled out by role. Leave blank (or non-matching) to disable range-forming
        // entirely and let whatever type this points to fall back into the normal weighted pool.
        [Header("Mountain Ranges")]
        public string mountainsTerrainName = "Mountains";
        public int mountainRangeCount = 2;
        public int mountainRangeLength = 4;

        // Decorative, non-interactive hexes generated past the playable field's rectangular
        // edge — same terrain pool, darkened and thinning out raggedly with distance, so the
        // camera sees a continuation of the landscape instead of the empty background when
        // zoomed out. Never written into HexMap's data, so they can't be selected/pathed to.
        [Header("Border (decorative, non-interactive)")]
        // How far past the field edge these hexes extend, as a fraction of the field's own
        // world-space size (the larger of its width/height).
        [Range(0f, 1f)] public float borderDepthFraction = 0.5f;
        public Color borderTint = new Color(0.35f, 0.35f, 0.35f);
        // How much per-hex noise perturbs the dropout threshold — 0 gives a clean rectangle
        // ring, higher values give a raggedly-holed edge.
        [Range(0f, 1f)] public float borderRaggedness = 0.85f;
        public float borderNoiseScale = 0.22f;

        // How far past the field's rectangular edge the decorative border reaches, in world
        // units — shared by HexMapGenerator (to place the border hexes themselves) and
        // FogOfWarController (to size the fog overlay quad to match), so the two never drift
        // apart into a visible seam.
        public float ComputeBorderDepthWorld()
        {
            float fieldSizeX = outerRadius * 1.5f * width + outerRadius * 0.5f;
            float fieldSizeZ = outerRadius * Mathf.Sqrt(3f) * (height + 0.5f);
            return borderDepthFraction * Mathf.Max(fieldSizeX, fieldSizeZ);
        }
    }
}
