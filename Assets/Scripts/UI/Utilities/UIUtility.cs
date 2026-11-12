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

        // Keep authored contents in one coordinate space. The stretched anchors describe
        // the available window area; the root keeps its design size and uniformly fits it.
        // Unlike stretching the window itself, this includes runtime children with fixed metrics.
        public static bool FitPanelToAnchors(RectTransform panel, Vector2 designSize)
        {
            if (panel == null || !(panel.parent is RectTransform parent)
                || designSize.x <= 0f || designSize.y <= 0f) return false;
            Vector2 available = Vector2.Scale(parent.rect.size, panel.anchorMax - panel.anchorMin);
            if (available.x <= 0f || available.y <= 0f) return false;
            float scale = Mathf.Min(available.x / designSize.x, available.y / designSize.y);
            bool resized = (panel.rect.size - designSize).sqrMagnitude > .0001f;
            bool rescaled = (panel.localScale - Vector3.one * scale).sqrMagnitude > .00000001f;
            if (resized)
            {
                panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, designSize.x);
                panel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, designSize.y);
            }
            if (rescaled) panel.localScale = Vector3.one * scale;
            return resized || rescaled;
        }

        // The viewport retains the authored text rectangle; only its content grows.
        public static ScrollRect EnsureDetailScroll(TMPro.TMP_Text detailText, string viewportName)
        {
            if (detailText == null) return null;
            var content = detailText.rectTransform;
            ScrollRect detailScroll = content.GetComponentInParent<ScrollRect>(true);
            if (detailScroll != null && detailScroll.content == content) return detailScroll;
            var viewportObject = new GameObject(viewportName, typeof(RectTransform),
                typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
            viewportObject.layer = content.gameObject.layer;
            var viewport = (RectTransform)viewportObject.transform;
            viewport.SetParent(content.parent, false);
            viewport.SetSiblingIndex(content.GetSiblingIndex());
            viewport.anchorMin = content.anchorMin; viewport.anchorMax = content.anchorMax;
            viewport.pivot = content.pivot; viewport.sizeDelta = content.sizeDelta;
            viewport.anchoredPosition = content.anchoredPosition;
            var background = viewportObject.GetComponent<Image>();
            background.color = Color.clear; background.raycastTarget = true;
            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0f, 1f); content.anchorMax = Vector2.one;
            content.pivot = new Vector2(.5f, 1f);
            content.anchoredPosition = Vector2.zero; content.sizeDelta = Vector2.zero;
            var fitter = content.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            detailScroll = viewportObject.GetComponent<ScrollRect>();
            detailScroll.viewport = viewport; detailScroll.content = content;
            detailScroll.horizontal = false; detailScroll.vertical = true;
            detailScroll.movementType = ScrollRect.MovementType.Clamped;
            detailScroll.scrollSensitivity = 24f;
            return detailScroll;
        }

        public static void ResetDetailScroll(ScrollRect scroll)
        {
            if (scroll == null || scroll.content == null) return;
            scroll.StopMovement();
            scroll.content.anchoredPosition = Vector2.zero;
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
