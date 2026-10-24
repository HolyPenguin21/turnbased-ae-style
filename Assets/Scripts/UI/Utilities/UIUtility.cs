using System.Collections.Generic;
using Game.Audio;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.UI
{
    // Stateless helpers shared by UI components. Input blocking state lives in UIFocusUtility.
    public static class UIUtility
    {
        // ---- Buttons ----

        public static void ResetRuntimeListeners(Button button)
        {
            if (button == null) return;
            button.onClick.RemoveAllListeners();
            SceneUIAudioBinder.BindCreatedRoot(button);
        }

        // ---- Lists ----
        // Shared by every UI component that instantiates a list of child components and needs to
        // tear them all down before repopulating (ArmyButtonRowUI's button row, ArmyViewerModalUI's
        // unit grid) — same "foreach Destroy, then Clear" shape, just for different component types.

        // A temporarily slotted item may return just beyond a window shortened by its removal.
        public static int OffsetToReveal(int offset, int index, int visibleCount) => index < 0 ? offset
            : Mathf.Clamp(offset, Mathf.Max(0, index - visibleCount + 1), index);

        public static void DestroyAndClear<T>(List<T> items) where T : Component
        {
            foreach (T item in items)
                if (item != null)
                    Object.Destroy(item.gameObject);
            items.Clear();
        }

        // ---- Dragging ----
        // Shared by every draggable UI element that should track the pointer 1:1 (CardUI,
        // ArmyUnitCardUI, battle ghosts). Resolve the delta in the actual parent's coordinates
        // so nested scales cannot make an element drift away from the pointer.
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
