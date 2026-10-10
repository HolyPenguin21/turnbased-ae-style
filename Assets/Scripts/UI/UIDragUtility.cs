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
            if (rectTransform == null || eventData == null)
                return;

            // Convert both pointer positions through the actual parent. This also handles
            // nested Canvas scales, transformed parents and camera-space canvases.
            if (rectTransform.parent is RectTransform parent)
            {
                Canvas root = canvas != null ? canvas.rootCanvas : null;
                Camera camera = root != null && root.renderMode != RenderMode.ScreenSpaceOverlay
                    ? (eventData.pressEventCamera != null ? eventData.pressEventCamera : root.worldCamera)
                    : null;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,
                        eventData.position, camera, out Vector2 current) &&
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(parent,
                        eventData.position - eventData.delta, camera, out Vector2 previous))
                {
                    rectTransform.anchoredPosition += current - previous;
                    return;
                }
            }

            float scaleFactor = canvas != null ? canvas.rootCanvas.scaleFactor : 1f;
            rectTransform.anchoredPosition += eventData.delta / Mathf.Max(0.001f, scaleFactor);
        }
    }
}

