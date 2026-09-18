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
        // Same draw-order role as HexHighlightStyle.sortingOrder — everything sits flat at Y=0,
        // so this keeps the fog quad above the terrain tiles (sortingOrder 0).
        public int sortingOrder = 16;

        // Tint, edge erosion/sharpness and patina texture are NOT exposed here on purpose: they
        // live entirely as Custom/FogOfWar's own shader Properties defaults. A previous version
        // of this style object duplicated those knobs, and its serialized GameConfig values had
        // drifted out of sync with the shader's own tuned defaults (e.g. edgeSharpness serialized
        // at 0.4 vs. the shader's intended 0.92), silently overriding the intended look every
        // frame. See FogOfWarController.RefreshVisibility, which now only feeds the runtime
        // visibility mask/geometry — never style/appearance — into the material.
        //
        // An earlier revision of this shader also had a pair of GameConfig-fed seam-gating
        // thresholds here (guarding the eroded seam from bleeding onto guaranteed-open ground).
        // Custom/FogOfWar.shader's own frag() now gets that guarantee structurally, from which
        // hex owns a shared edge (a hard binary select, not an approximate gate), so those
        // thresholds no longer correspond to anything the shader reads.

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
