using UnityEngine;

namespace Game.Map
{
    // A generic map marker: a coloured circle sprite with a smaller icon sprite layered on
    // top. The icon itself stays plain white in its source art — SetColor tints the circle
    // underneath (e.g. to a player's colour), which is what reads as the marker's colour;
    // the white icon on top just shows through untinted.
    public class MapObjectVisual : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer innerCircle;
        [SerializeField] private SpriteRenderer objectImage;

        // Fraction of the marker's own art half-width that actually counts as a click on it
        // (see ContainsScreenPoint). Below 1 so the transparent margin baked into the circle
        // sprite — and a bit of the opaque rim — doesn't register, which is what kept the old
        // hit area filling most of a hex and swallowing plain hex clicks into the army modal.
        [SerializeField, Range(0.2f, 1f)] private float clickRadiusFactor = 0.7f;

        public void SetColor(Color color)
        {
            if (innerCircle != null)
                innerCircle.color = color;
        }

        public void SetIcon(Sprite icon)
        {
            if (objectImage != null)
                objectImage.sprite = icon;
        }

        // Copies the complete rendered marker state into a separate last-seen snapshot. A
        // snapshot must not keep reading the live building marker after vision leaves: captures
        // change its colour and destroyed facilities delete it, either of which would leak an
        // unseen world-state change to the human player.
        public void CopyAppearanceFrom(MapObjectVisual source)
        {
            if (source == null)
                return;
            CopyRenderer(source.innerCircle, innerCircle);
            CopyRenderer(source.objectImage, objectImage);
        }

        private static void CopyRenderer(SpriteRenderer source, SpriteRenderer target)
        {
            if (source == null || target == null)
                return;
            target.sprite = source.sprite;
            target.color = source.color;
            target.sharedMaterial = source.sharedMaterial;
            target.sortingLayerID = source.sortingLayerID;
            target.sortingOrder = source.sortingOrder;
            target.transform.localPosition = source.transform.localPosition;
            target.transform.localRotation = source.transform.localRotation;
            target.transform.localScale = source.transform.localScale;
        }

        // Circle and icon are two independent SpriteRenderers on the same flat (Y=0) marker —
        // the icon needs a higher order than its own circle to actually show up on top of it,
        // and callers (building vs. unit markers) use different GameConfig values so buildings
        // and units can each sit at their own layer overall (see GameConfig's sorting fields).
        public void SetSortingOrder(int circleOrder, int iconOrder)
        {
            if (innerCircle != null)
                innerCircle.sortingOrder = circleOrder;
            if (objectImage != null)
                objectImage.sortingOrder = iconOrder;
        }

        // Toggles the sprites only — never the GameObject itself, so a hidden army's
        // ArmyController (coroutines, selection pulse, move animation) keeps working normally
        // underneath. Used to collapse all of one owner's armies sharing a hex down to a single
        // visible marker (see HexSelectionController.RestackArmiesOn) — a unit has no map
        // presence of its own at all, only its army does.
        public void SetVisible(bool visible)
        {
            if (innerCircle != null)
                innerCircle.enabled = visible;
            if (objectImage != null)
                objectImage.enabled = visible;
        }

        // Whether the last SetVisible call left this marker showing — used to tell an owner's
        // currently-representative army marker (see HexSelectionController.RestackArmiesOn)
        // apart from one of their other armies sharing the same hex, which stays instantiated
        // but hidden rather than destroyed.
        public bool IsVisible => innerCircle != null && innerCircle.enabled;

        // Hit-tests the marker as a circle around its projected centre, sized from the art's
        // own half-width. This replaced a projected-AABB rectangle: that box circumscribed a
        // round marker (over-claiming its diagonals by ~41%) and, because Inner_Circle is laid
        // almost flat on the ground, its world AABB carried a big Z (depth) extent that
        // projected into a tall on-screen rectangle — so a click well outside the visible disc
        // still counted, and zooming out never freed up hex border to click (2026-09-09
        // collider investigation). Measuring the radius along the screen horizontal drops the
        // ground-tilt inflation, and it still shrinks/grows with orthographic zoom because it's
        // a projected distance. Called only on a click and only for markers on the clicked hex.
        public bool ContainsScreenPoint(Camera camera, Vector2 screenPoint, float paddingPixels = 3f)
        {
            if (camera == null || !IsVisible)
                return false;

            SpriteRenderer shape = ResolveHitRenderer();
            if (shape == null)
                return false;

            Vector3 worldCentre = shape.bounds.center;
            Vector3 centreScreen = camera.WorldToScreenPoint(worldCentre);
            if (centreScreen.z <= 0f)
                return false;

            // Sprite.bounds is already in units (PPU-divided) and pivot-centred; take the
            // horizontal extent only, scaled by the renderer's world scale, so Inner_Circle's
            // ~80deg ground tilt doesn't stretch it.
            float worldRadius = shape.sprite.bounds.extents.x
                * Mathf.Abs(shape.transform.lossyScale.x) * Mathf.Clamp01(clickRadiusFactor);
            Vector3 edgeScreen = camera.WorldToScreenPoint(worldCentre + camera.transform.right * worldRadius);

            Vector2 centre2D = new Vector2(centreScreen.x, centreScreen.y);
            float radiusPixels = Vector2.Distance(centre2D, new Vector2(edgeScreen.x, edgeScreen.y));
            float padding = Mathf.Max(0f, paddingPixels);
            return Vector2.Distance(screenPoint, centre2D) <= radiusPixels + padding;
        }

        // The round circle is the marker's clickable shape; fall back to the icon renderer for
        // a marker with no circle (or whose circle is momentarily spriteless).
        private SpriteRenderer ResolveHitRenderer()
        {
            if (innerCircle != null && innerCircle.enabled && innerCircle.sprite != null)
                return innerCircle;
            if (objectImage != null && objectImage.enabled && objectImage.sprite != null)
                return objectImage;
            return null;
        }
    }
}
