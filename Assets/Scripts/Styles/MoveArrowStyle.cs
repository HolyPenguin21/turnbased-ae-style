using Game.Map;
using UnityEngine;

namespace Game.Styles
{
    // Every tunable for MoveArrowMarker's comet-trail move-order preview, grouped here (same
    // pattern as HexHighlightStyle) instead of scattered as [SerializeField]s directly on that
    // component. MoveArrowMarker is created purely from code (see
    // HexSelectionController.ShowPathArrow's AddComponent<MoveArrowMarker>()), so it never had
    // its own Inspector instance to tune in the first place — this is the actual place to do
    // that now, shared like every other GameConfig style.
    [System.Serializable]
    public class MoveArrowStyle
    {
        [Header("Curve")]
        public int segmentsPerLeg = 10;
        public float curveBend = 0.28f;

        [Header("Shaft")]
        public float tailWidth = 0.9f;

        // A fully open-ended silhouette for the head instead of a fixed set of named zones
        // (wing/notch/neck as separate float fields) — add or remove points here to sculpt
        // whatever shape is needed (a swallowtail notch, a longer sweep, extra facets) without
        // touching code. ELEMENT ORDER MATTERS: element 0 must be the tip (distanceFromTip = 0,
        // halfWidth = 0), the last element is the base where the head ends and the shaft's own
        // taper begins, and everything between is walked in that order — that's also the order
        // the head's outline is traced in (see MoveArrowMarker.BuildArrowMesh's polygon-
        // triangulation note), so e.g. a narrow point meant to cut a notch behind a wide wing
        // should be listed after that wing, even if its own distanceFromTip is numerically
        // smaller (closer to the tip) than the wing's.
        [Header("Arrowhead Profile (element 0 = tip; last = base, meets the shaft)")]
        public ArrowHeadPoint[] headProfile =
        {
            new ArrowHeadPoint { distanceFromTip = 0f, halfWidth = 0f },       // tip
            new ArrowHeadPoint { distanceFromTip = 0.16f, halfWidth = 0.3f },  // wing (widest point)
            new ArrowHeadPoint { distanceFromTip = 0.3f, halfWidth = 0.03f },  // notch (swallowtail pinch)
            new ArrowHeadPoint { distanceFromTip = 0.4f, halfWidth = 0.06f },  // base — meets the shaft here
        };

        [Header("Outline")]
        public float outlineThickness = 0.035f;
        public Color outlineColor = new Color(0.05f, 0.04f, 0.06f, 1f);

        // Longitudinal fade, tip -> tail — NOT the old per-cross-section "glassy stripe"
        // transparency effect that was removed earlier. Applies to the outline as well as the
        // fill (both share the same AlphaAtDistance calculation), so the whole silhouette
        // fades as one shape instead of a fading fill inside a solid frame.
        //
        // The fade is measured along the tail only (not the whole arrow including the head):
        // spreading it across the full length made the head's own share of the ramp too small
        // to read as a gradient at all, so the head looked like a flat, disconnected solid
        // block sitting on top of a fading tail. gradientStart is the fraction of the TAIL's
        // length (measured from the head end) that stays fully opaque before the fade to
        // tailAlpha begins — 0.75 means the head plus the near-head 75% of the tail are one
        // uniform solid colour, and only the last 25% of the tail (nearest the selected unit)
        // actually fades.
        [Header("Colour (longitudinal fade, tip -> tail)")]
        [Range(0f, 1f)] public float tailAlpha = 0.1f;
        [Range(0f, 1f)] public float gradientStart = 0.75f;

        // Two badges — move cost and AP cost — straddling the arrow's midpoint, same radius
        // and text colour, only the fill colour differs. apBadgeColor matches the AP icon
        // colour in ResourceBarUI's HUD so both read as "the same currency" at a glance.
        [Header("Cost Badge")]
        public float badgeRadius = 0.22f;
        public float badgeSpacing = 0.5f;
        // The badge pair sits at the path's arc-length midpoint (see MoveArrowMarker.
        // BadgePosition) EXCEPT never closer to the start than this — a short (e.g.
        // single-hex) path's true midpoint falls right on top of the moving army's own icon,
        // which is what it's trying to show a cost for. Whichever is farther along the path,
        // the true midpoint or this minimum, wins; capped at the path's own total length so a
        // very short hop still places it at the far end rather than overshooting.
        public float badgeMinDistanceFromStart = 0.9f;
        public Color badgeColor = new Color(0.08f, 0.08f, 0.08f, 0.92f);
        public Color apBadgeColor = new Color(0.5f, 0.08f, 0.08f, 1f);
        public Color badgeTextColor = Color.white;
        // A slightly larger white circle drawn behind each badge (same trick as the arrow's own
        // outline — a solid duplicate peeking out around the edges), so the badge reads as a
        // ring-bordered coin instead of a flat dot.
        public float badgeBorderThickness = 0.035f;
        public Color badgeBorderColor = Color.white;

        // Everything sits flat at Y=0 now — draw order (against the map, the hex highlights,
        // and between these four renderers themselves) is resolved entirely by sortingOrder.
        // Outline MUST stay below the fill: only the shaft's outline is a hollow strip (safe
        // either way) — the head's outline is a solid, slightly larger duplicate of the head
        // silhouette (see BuildArrowMesh), so if it drew on top it would blot out the fill's
        // whole arrowhead instead of just framing it.
        // Above every army marker's own sorting order (GameConfig.armyIconSortingOrder = 10,
        // the highest currently in use) — the arrow and its badges must never draw behind an
        // army icon they happen to pass under or sit beside, or that info reads as missing
        // instead of just covered.
        [Header("Sorting (order in layer — see GameConfig for map/highlight/army values)")]
        public int outlineSortingOrder = 11;
        public int fillSortingOrder = 12;
        public int badgeBorderSortingOrder = 13;
        public int badgeSortingOrder = 14;
        public int badgeTextSortingOrder = 15;
    }
}
