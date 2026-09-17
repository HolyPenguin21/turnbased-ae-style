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
        // Earth/charcoal source tint. Custom/FogOfWar darkens this further in shader space so
        // fogged territory reads clearly as a separate mass while underlying terrain remains
        // recognisable through the transparent overlay.
        public Color color = new Color(0.32f, 0.24f, 0.14f, 0.88f);

        // Same draw-order role as HexHighlightStyle.sortingOrder — everything sits flat at Y=0,
        // so this keeps the fog quad above the terrain tiles (sortingOrder 0).
        public int sortingOrder = 16;

        // Amount of static dry-edge erosion. It distorts only the visibility boundary; it never
        // blurs the terrain itself. Moderate values create the broken, dusty contour used by the
        // current art direction.
        [Range(0f, 1f)] public float edgeSoftness = 0.42f;

        // Controls how tightly the transition hugs the eroded boundary. Values near 1 keep the
        // contour crisp and preserve terrain readability immediately on either side of the seam.
        [Range(0f, 1f)] public float edgeSharpness = 0.92f;

        // World-space scale shared by the boundary erosion and the larger internal patina. It is
        // intentionally low-frequency so fog reads as broad material variation, not TV noise.
        public float edgeNoiseScale = 0.1f;

        // Leave at zero for the intended static-map treatment. Existing serialized configs with a
        // tiny non-zero value are heavily damped by the shader and remain visually almost static.
        public float edgeNoiseSpeed = 0f;

        // Optional authored dry/grime texture, sampled statically in world space as secondary
        // density variation. Procedural patina carries the main look, so this stays restrained.
        public Texture2D detailTexture;
        public float detailTextureScale = 0.11f;
        [Range(0f, 1f)] public float detailTextureStrength = 0.45f;

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
