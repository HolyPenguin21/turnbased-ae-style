using UnityEngine;

namespace Game.Map
{
    // A standalone marker shown on a hex once its Hex Event has been skipped rather than
    // explored — see MapEventDisplay, the only thing that ever spawns one of these. Whatever
    // image the prefab itself already has baked onto its SpriteRenderer is what shows — this
    // never swaps in a per-event sprite, unlike MapObjectVisual.SetIcon.
    public class EventMarkerVisual : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer image;

        public void SetSortingOrder(int order)
        {
            if (image != null)
                image.sortingOrder = order;
        }
    }
}
