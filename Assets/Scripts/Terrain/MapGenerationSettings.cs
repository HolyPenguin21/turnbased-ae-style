using System.Collections.Generic;
using UnityEngine;

namespace Game.Terrain
{
    // Pre-game map-size choice — the field is a hexagon of hexes (see HexMapGenerator), so
    // "size" is its ring radius around the (0,0) centre, not a width/height pair. Values match
    // GameSetupModel's pre-game dropdown, in ring count, so `(int)size` is directly usable as
    // MapGenerationSettings.radius.
    public enum MapSize
    {
        Small = 5,
        Medium = 6,
        Large = 7,
        Huge = 8,
    }

    // Pre-game terrain-palette choice — which BiomeTerrainSet HexMapGenerator paints the field
    // with. Arid is the original, always-populated palette (MapGenerationSettings' own
    // terrainTypes/mountain/colour fields, kept where they've always lived so the existing
    // GameConfig asset's authored textures aren't disturbed); Desert is an optional override
    // (see MapGenerationSettings.desertOverride) the project owner fills in separately.
    public enum Biome
    {
        Arid,
        Desert,
    }

    // One full terrain palette: everything HexMapGenerator needs to paint a field/border in a
    // given biome. MapGenerationSettings' own top-level fields ARE the Arid set (see
    // ResolveBiome) — this class only exists so a second biome (desertOverride) can carry the
    // same shape without duplicating every field at the top level twice.
    [System.Serializable]
    public class BiomeTerrainSet
    {
        public List<TerrainTypeEntry> terrainTypes = new List<TerrainTypeEntry>();
        public string mountainsTerrainName = "Mountains";
        public int mountainRangeCount = 2;
        public int mountainRangeLength = 4;
        public Color groundColor = new Color(0.29f, 0.26f, 0.22f);
        public Color borderTint = new Color(0.35f, 0.35f, 0.35f);
    }

    // All the tunable numbers/lists that drive HexMapGenerator, grouped here (lives inside
    // GameConfig) instead of as separate [SerializeField] fields on the generator itself, so
    // map balancing sits in the same one place as everything else.
    [System.Serializable]
    public class MapGenerationSettings
    {
        [Header("Grid")]
        // Design-time/editor-only default (e.g. the "Generate Map" context menu, or an
        // ExecuteAlways OnEnable outside Play mode) — at runtime HexMapGenerator prefers
        // GameSession.SelectedMapSize instead, set by the pre-game setup panel. Ring radius
        // around the field's (0,0) centre, matching MapSize's own values (Medium = 6).
        public int radius = 6;
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

        // Decorative, non-interactive hexes generated past the playable field's own outer ring
        // — same terrain pool, darkened and thinning out raggedly with distance, so the camera
        // sees a continuation of the landscape instead of the empty background when zoomed out.
        // Never written into HexMap's data, so they can't be selected/pathed to.
        [Header("Border (decorative, non-interactive)")]
        // How far past the field's outer ring these hexes extend, as a fraction of the field's
        // own world-space diameter.
        [Range(0f, 1f)] public float borderDepthFraction = 0.5f;
        public Color borderTint = new Color(0.35f, 0.35f, 0.35f);
        // How much per-hex noise perturbs the dropout threshold — 0 gives a clean ring, higher
        // values give a raggedly-holed edge.
        [Range(0f, 1f)] public float borderRaggedness = 0.85f;
        public float borderNoiseScale = 0.22f;

        // Optional second palette (project owner fills in the actual textures/tuning
        // separately) — HexMapGenerator falls back to the Arid fields above whenever this is
        // still empty, so selecting Desert before it's populated silently paints Arid instead
        // of breaking.
        [Header("Biomes (Desert override, optional)")]
        public BiomeTerrainSet desertOverride = new BiomeTerrainSet();

        // Which full palette HexMapGenerator actually paints with for the given biome choice —
        // Arid is always these top-level fields themselves (never duplicated), Desert is
        // desertOverride once it has at least one terrain type assigned.
        public BiomeTerrainSet ResolveBiome(Biome biome)
        {
            if (biome == Biome.Desert && desertOverride.terrainTypes.Count > 0)
                return desertOverride;

            return new BiomeTerrainSet
            {
                terrainTypes = terrainTypes,
                mountainsTerrainName = mountainsTerrainName,
                mountainRangeCount = mountainRangeCount,
                mountainRangeLength = mountainRangeLength,
                groundColor = groundColor,
                borderTint = borderTint,
            };
        }

        // How far past the field's outer ring the decorative border reaches, in world units —
        // shared by HexMapGenerator (to place the border hexes themselves) and
        // FogOfWarController (to size the fog overlay quad to match), so the two never drift
        // apart into a visible seam. `radius` is the design-time/editor-only fallback (see its
        // own comment) — HexMapGenerator passes the actually-active radius in wherever it might
        // differ (GameSession.SelectedMapSize at runtime).
        public float ComputeBorderDepthWorld(int? activeRadius = null)
        {
            int effectiveRadius = activeRadius ?? radius;
            float fieldDiameter = outerRadius * Mathf.Sqrt(3f) * (effectiveRadius * 2 + 1);
            return borderDepthFraction * fieldDiameter;
        }
    }
}
