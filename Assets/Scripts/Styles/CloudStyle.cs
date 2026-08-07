using UnityEngine;

namespace Game.Styles
{
    // Every tunable for the strategic map's drifting-cloud overlay (see Game.Map.
    // MapCloudOverlay / Custom/CloudDrift.shader) — same shared-in-GameConfig pattern as
    // HexHighlightStyle/MoveArrowStyle, so it's tuned in one Inspector spot rather than
    // scattered [SerializeField]s on the overlay component itself.
    [System.Serializable]
    public class CloudStyle
    {
        [Header("Look")]
        // Alpha here is the cloud's own maximum opacity at full coverage, not a separate knob —
        // 0 alpha would just render nothing regardless of the other settings below.
        public Color color = new Color(1f, 1f, 1f, 0.35f);
        // Smaller = larger, chunkier cloud shapes; larger = finer, more scattered ones.
        public float scale = 0.08f;
        [Range(0f, 1f)] public float coverage = 0.55f;
        [Range(0.01f, 1f)] public float softness = 0.25f;

        [Header("Motion")]
        public float speed = 0.03f;
        // Only the direction matters (normalized in-shader) — magnitude is ignored, so e.g.
        // (1, 0) and (5, 0) drift identically.
        public Vector2 direction = new Vector2(1f, 0.4f);

        [Header("Placement")]
        // World-space height above the map plane (Y=0) the overlay quad sits at — above every
        // map object (buildings/armies sit flat at Y=0, see GameConfig's own sorting-order
        // comment) so clouds always read as physically overhead, no sortingOrder trick needed.
        public float height = 4f;
        // Half-extent (world units) of the generated quad, centered on the map — deliberately
        // generous relative to any one map's actual hex-grid extents (see MapCloudOverlay) so
        // panning/zooming the camera never reveals a hard edge.
        public float planeHalfSize = 80f;
    }
}
