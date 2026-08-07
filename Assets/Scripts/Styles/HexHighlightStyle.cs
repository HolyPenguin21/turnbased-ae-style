using UnityEngine;

namespace Game.Styles
{
    // Tunables for the HexSelectionGlow/HexClusterGlow shaders (ragged noise edge + glow) —
    // everything except colour, which stays driven separately per use (a player's own colour
    // for the region highlight, fixed TechnicalColors for citadel/hex selection) rather than
    // baked into a shared style. GameConfig holds one of these per usage context (region
    // highlight, citadel placement pick, hex selection) so each can be tuned independently
    // instead of all sharing the shader files' own Properties-block defaults.
    [System.Serializable]
    public class HexHighlightStyle
    {
        // Scales only the visual ring radius, not the real hex grid spacing — HexBlend.shader
        // (the terrain tiles) fades out via vertex-alpha before reaching each tile's true
        // geometric edge, so the tile reads as visually smaller than outerRadius. Tune this
        // below 1 to pull the ring in to match. Never touches HexClusterHighlight's actual
        // grid math (which hex a pixel belongs to) — only where the ring itself is drawn
        // within that hex.
        public float radiusScale = 1f;
        // World-space padding beyond the hex radius — must comfortably exceed
        // noiseReach + glowWidth or the pattern clips at the mesh boundary.
        public float margin = 0.6f;
        public float lineThickness = 0.03f;
        public float noiseReach = 0.22f;
        public float noiseScale = 3f;
        public float noiseSpeed = 1.2f;
        public float glowIntensity = 0.7f;
        public float glowWidth = 0.25f;

        // The map's own hex tiles (HexBlend.shader) are ALSO in the Transparent queue with
        // ZWrite Off, so draw order against them is resolved by sortingOrder alone — everything
        // sits flat at the same Y, so a highlight tying with the terrain's own order (0) would
        // intermittently vanish under the tile texture depending on the transparent
        // back-to-front sort. Keep above 0; bump higher still for a highlight that must draw
        // over another one (e.g. the confirmed citadel pick over the region cluster).
        public int sortingOrder = 1;
    }
}
