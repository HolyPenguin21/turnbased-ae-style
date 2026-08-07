using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.UI
{
    // Shared by every draggable UI element that should track the pointer 1:1 (CardUI,
    // ArmyUnitCardUI) — screen-space pointer delta has to be divided by the Canvas's scale
    // factor before it's a valid anchoredPosition delta, or dragged elements drift at
    // non-1:1 UI scales.
    public static class UIDragUtility
    {
        public static void ApplyScreenDelta(RectTransform rectTransform, PointerEventData eventData, Canvas canvas)
        {
            float scaleFactor = canvas != null ? canvas.scaleFactor : 1f;
            rectTransform.anchoredPosition += eventData.delta / scaleFactor;
        }
    }
}
