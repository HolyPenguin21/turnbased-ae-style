using TMPro;
using UnityEngine;

namespace Game.Styles
{
    // Every tunable for the strategic map's per-hex content fog (see Game.Map.VisionSystem /
    // Game.Map.FogOfWarController / Custom/FogOfWar.shader) — same shared-in-GameConfig pattern
    // as CloudStyle/HexHighlightStyle, so it's tuned in one Inspector spot instead of scattered
    // [SerializeField]s on the overlay component itself. Two independent features share this one
    // style object since they're both part of the same "hex content visibility" feature: the
    // dimming overlay (gates army/building/resource content) and the coordinate label (permanent
    // per-player "visited" marker) — per the project owner's own call, visiting a hex does NOT
    // un-fog its content, the two are unrelated.
    [System.Serializable]
    public class FogOfWarStyle
    {
        [Header("Fog Overlay")]
        // Tint+alpha applied over a hex's terrain when it's outside the current viewer's vision
        // (see VisionSystem.CurrentViewer) — alpha-blended over the terrain, not multiplicative
        // (unlike CloudStyle), since this needs to read as "obscured", not just "shadowed".
        public Color color = new Color(0.03f, 0.04f, 0.07f, 0.75f);
        // Same draw-order role as HexHighlightStyle.sortingOrder — everything sits flat at Y=0,
        // so this is what keeps the fog quad drawing above the terrain tiles (sortingOrder 0).
        public int sortingOrder = 4;
        // How much the drifting fbm haze perturbs the boundary between visible and fogged hexes
        // (see Custom/FogOfWar.shader) — 0 is a smooth gradient with no roughness at all, higher
        // values read as a more organic, uneven edge. Only ever affects the SEAM itself; deep fog
        // and clear ground both stay stable regardless of this value.
        [Range(0f, 1f)] public float edgeSoftness = 0.35f;
        // How narrow the blend band is around a hex's TRUE geometric boundary (see
        // Custom/FogOfWar.shader's hexSDF-based frag()) — 0 blends across most of the hex (wide,
        // soft), 1 narrows it to a thin antialiased line hugging the real hex edge. This is the
        // main crispness knob; edgeSoftness above only roughens the seam with haze on top of
        // whatever this produces.
        [Range(0f, 1f)] public float edgeSharpness = 0.8f;
        public float edgeNoiseScale = 0.12f;
        public float edgeNoiseSpeed = 0.04f;
        // Optional hand-picked detail texture layered on top of the procedural haze — left
        // unassigned (shader default "white") until the project owner picks and tunes one; a
        // strength of 0 makes it a complete no-op even if a texture IS assigned, so it's safe to
        // wire one in ahead of actually deciding to use it.
        public Texture2D detailTexture;
        public float detailTextureScale = 0.05f;
        [Range(0f, 1f)] public float detailTextureStrength = 0f;

        [Header("Coordinate Label")]
        // Font asset for the per-hex "q:r" label (see Game.Map.HexCoordLabel) — built at
        // runtime (no prefab, same reasoning as HexShaderHighlight's own runtime mesh/material),
        // so the font has to be wired here instead of coming from a prefab reference.
        public TMP_FontAsset coordLabelFont;
        public float coordLabelFontSize = 3f;
        public Color coordLabelColor = new Color(1f, 1f, 1f, 0.55f);
        // World-space offset from the hex centre, in hex-radius units (same convention as
        // GameConfig.buildingIconOffset/armyIconOffset) — x = left/right, y = world Z.
        public Vector2 coordLabelOffset = new Vector2(0f, -0.55f);
        public int coordLabelSortingOrder = 6;
    }
}
