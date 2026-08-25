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
    }
}
