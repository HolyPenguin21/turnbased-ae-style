using UnityEngine;

namespace Game.Styles
{
    // Art for the strategic map's sandstorm layer (see Game.Map.SandstormMapLayer). One sprite
    // per stormy hex, played as a looping flipbook. With no frames assigned the layer still
    // tracks the storm but draws nothing, so the art can be dropped in later without code.
    // Draw order is fixed (Game.Map.MapSortingOrder.Sandstorm): above armies, below the move
    // arrows and the fog overlay.
    [System.Serializable]
    public class SandstormVisualStyle
    {
        public Sprite[] frames = new Sprite[0];
        [Min(0f)] public float framesPerSecond = 8f;
        // Sprite width in hex widths (1 = the hex's corner-to-corner width).
        [Min(0.01f)] public float sizeInHexes = 1f;
        public Color tint = Color.white;
    }
}
