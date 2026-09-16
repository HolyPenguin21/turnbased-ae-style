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

        // Optional layers used by richer marker prefabs. Existing circle+icon prefabs leave
        // these empty and keep their current behavior; the flagged citadel uses factionAccent
        // for its cloth and auxiliaryRenderers for neutral, non-faction-tinted layers.
        [SerializeField] private SpriteRenderer factionAccent;
        [SerializeField] private SpriteRenderer[] auxiliaryRenderers;
        [SerializeField] private bool tintInnerCircle = true;
        // Some layered markers use Object_Image as the neutral/base layer and factionAccent as
        // a matching colour layer. If SetIcon replaces that marker with a fundamentally
        // different icon (the army prefab does this for aviation), the old accent must not
        // remain behind the replacement sprite. Off by default so existing rich building
        // prefabs keep their authored accent when their icon changes.
        [SerializeField] private bool clearFactionAccentOnSetIcon;
        // Optional full layered replacement keyed by the same Sprite value existing callers
        // already use with SetIcon. This lets a shared marker keep its established faction-icon
        // selection contract while one icon upgrades to a complete Base + FactionAccent visual.
        [SerializeField] private Sprite layeredIconTrigger;
        [SerializeField] private MapObjectVisual layeredIconVisual;
        [SerializeField] private SpriteRenderer hitRendererOverride;

        // Fraction of the marker's own art half-width that actually counts as a click on it
        // (see ContainsScreenPoint). Below 1 so the transparent margin baked into the circle
        // sprite — and a bit of the opaque rim — doesn't register, which is what kept the old
        // hit area filling most of a hex and swallowing plain hex clicks into the army modal.
        [SerializeField, Range(0.2f, 1f)] private float clickRadiusFactor = 0.7f;

        public void SetColor(Color color)
        {
            if (tintInnerCircle && innerCircle != null)
                innerCircle.color = color;
            if (factionAccent != null)
                factionAccent.color = color;
        }

        public void SetIcon(Sprite icon)
        {
            if (icon != null && layeredIconVisual != null && icon == layeredIconTrigger)
            {
                Color accentColor = factionAccent != null ? factionAccent.color : Color.white;
                int objectSortingOrder = objectImage != null ? objectImage.sortingOrder : 0;
                int accentSortingOrder = factionAccent != null ? factionAccent.sortingOrder : 0;

                CopyRenderer(layeredIconVisual.objectImage, objectImage);
                CopyRenderer(layeredIconVisual.factionAccent, factionAccent);
                CopyRenderers(layeredIconVisual.auxiliaryRenderers, auxiliaryRenderers);

                // CreateArmyMarker applies owner colour/sorting before SetIcon. The replacement
                // supplies art/transforms/materials only; preserve the live marker's runtime state.
                if (objectImage != null)
                    objectImage.sortingOrder = objectSortingOrder;
                if (factionAccent != null)
                {
                    factionAccent.color = accentColor;
                    factionAccent.sortingOrder = accentSortingOrder;
                }
                return;
            }

            if (objectImage != null)
                objectImage.sprite = icon;
            if (clearFactionAccentOnSetIcon && factionAccent != null)
                factionAccent.sprite = null;
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
            CopyRenderer(source.factionAccent, factionAccent);
            CopyRenderers(source.auxiliaryRenderers, auxiliaryRenderers);
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

        private static void CopyRenderers(SpriteRenderer[] source, SpriteRenderer[] target)
        {
            if (source == null || target == null)
                return;
            int count = Mathf.Min(source.Length, target.Length);
            for (int i = 0; i < count; i++)
                CopyRenderer(source[i], target[i]);
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
            if (factionAccent != null)
                factionAccent.enabled = visible;
            if (auxiliaryRenderers != null)
                foreach (SpriteRenderer renderer in auxiliaryRenderers)
                    if (renderer != null)
                        renderer.enabled = visible;
        }

        // Whether the last SetVisible call left this marker showing — used to tell an owner's
        // currently-representative army marker (see HexSelectionController.RestackArmiesOn)
        // apart from one of their other armies sharing the same hex, which stays instantiated
        // but hidden rather than destroyed.
        public bool IsVisible
        {
            get
            {
                SpriteRenderer renderer = hitRendererOverride != null ? hitRendererOverride : innerCircle;
                return renderer != null && renderer.enabled;
            }
        }

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
            if (hitRendererOverride != null && hitRendererOverride.enabled && hitRendererOverride.sprite != null)
                return hitRendererOverride;
            if (innerCircle != null && innerCircle.enabled && innerCircle.sprite != null)
                return innerCircle;
            if (objectImage != null && objectImage.enabled && objectImage.sprite != null)
                return objectImage;
            return null;
        }
    }
}
