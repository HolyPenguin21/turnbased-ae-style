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

        // Hit-tests the marker against what is ACTUALLY drawn on screen. Unlike the old fixed
        // pixel radius in HexSelectionController, these projected renderer bounds naturally
        // shrink as an orthographic camera zooms out and grow as it zooms in. Called only on a
        // click and only for markers on the clicked hex, so projecting the eight Bounds corners
        // is negligible compared with keeping Physics colliders/raycasters on every map object.
        public bool ContainsScreenPoint(Camera camera, Vector2 screenPoint, float paddingPixels = 3f)
        {
            if (camera == null || !IsVisible)
                return false;

            Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            bool hasBounds = AccumulateScreenBounds(innerCircle, camera, ref min, ref max);
            hasBounds |= AccumulateScreenBounds(objectImage, camera, ref min, ref max);
            if (!hasBounds)
                return false;

            float padding = Mathf.Max(0f, paddingPixels);
            return screenPoint.x >= min.x - padding && screenPoint.x <= max.x + padding
                && screenPoint.y >= min.y - padding && screenPoint.y <= max.y + padding;
        }

        private static bool AccumulateScreenBounds(SpriteRenderer renderer, Camera camera,
            ref Vector2 min, ref Vector2 max)
        {
            if (renderer == null || !renderer.enabled || renderer.sprite == null)
                return false;

            Bounds bounds = renderer.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 world = new Vector3(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                Vector3 screen = camera.WorldToScreenPoint(world);
                if (screen.z <= 0f)
                    continue;
                min.x = Mathf.Min(min.x, screen.x);
                min.y = Mathf.Min(min.y, screen.y);
                max.x = Mathf.Max(max.x, screen.x);
                max.y = Mathf.Max(max.y, screen.y);
            }
            return !float.IsPositiveInfinity(min.x);
        }
    }
}
