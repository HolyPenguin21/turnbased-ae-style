using TMPro;
using UnityEngine;

namespace Game.Styles
{
    // Every tunable for the strategic map's per-hex content fog (see Game.Map.VisionSystem /
    // Game.Map.FogOfWarController / Custom/FogOfWar.shader) — same shared-in-GameConfig pattern
    // as HexHighlightStyle, so it's tuned in one Inspector spot instead of scattered
    // [SerializeField]s on the overlay component itself. Two independent features share this one
    // style object since they're both part of the same "hex content visibility" feature: the
    // terrain-readable dimming overlay and the coordinate label (permanent per-player "visited"
    // marker). Visiting a hex does NOT un-fog its content; the two remain unrelated.
    [System.Serializable]
    public class FogOfWarStyle
    {
        [Header("Fog Overlay")]
        // Tint applied over terrain outside the current viewer's vision. The shader deliberately
        // caps the effective opacity so terrain type, silhouettes and texture features remain
        // readable. This is a visibility-state treatment, not literal opaque atmospheric fog.
        public Color color = new Color(0.30f, 0.24f, 0.17f, 0.82f);

        // Same draw-order role as HexHighlightStyle.sortingOrder — everything sits flat at Y=0,
        // so this keeps the fog quad above the terrain tiles (sortingOrder 0).
        public int sortingOrder = 16;

        // Small world-space irregularity at the seam between visible and fogged hexes. Keep this
        // restrained: high values are remapped by the shader so they cannot turn the edge into a
        // broad soft haze or make the tile contents difficult to read.
        [Range(0f, 1f)] public float edgeSoftness = 0.18f;

        // Controls how tightly the transition hugs the TRUE hex boundary. The shader remaps this
        // into a deliberately crisp range; values near 1 are preferred for the current art
        // direction because FoW must not blur terrain information.
        [Range(0f, 1f)] public float edgeSharpness = 0.9f;

        // Scale of the low-frequency, world-anchored patina used to break up large uniform fogged
        // regions. It stays subtle enough that terrain art remains the primary visual signal.
        public float edgeNoiseScale = 0.1f;

        // Optional extremely slow movement of boundary irregularity only. Leave at zero for the
        // intended static-map look; this never moves the detail texture across the terrain.
        public float edgeNoiseSpeed = 0f;

        // Optional authored dry/grime texture, sampled statically in world space. It contributes
        // only slight density variation; it does not blur or substantially recolour the terrain.
        public Texture2D detailTexture;
        public float detailTextureScale = 0.11f;
        [Range(0f, 1f)] public float detailTextureStrength = 0.55f;

        [Header("Coordinate Label")]
        // Font asset for the per-hex coordinate label (see Game.Map.HexCoordLabel) — built at
        // runtime (no prefab, same reasoning as HexShaderHighlight's runtime mesh/material), so
        // the font is wired here instead of coming from a prefab reference.
        public TMP_FontAsset coordLabelFont;
        public float coordLabelFontSize = 3f;
        public Color coordLabelColor = new Color(1f, 1f, 1f, 0.55f);

        // World-space offset from the hex centre, in hex-radius units (same convention as
        // GameConfig.buildingIconOffset/armyIconOffset) — x = left/right, y = world Z.
        public Vector2 coordLabelOffset = new Vector2(0f, -0.55f);
        public int coordLabelSortingOrder = 6;
    }
}
