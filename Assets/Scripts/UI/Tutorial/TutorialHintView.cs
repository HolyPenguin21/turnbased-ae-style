using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // All visuals and text are serialized in the five prefabs, including their editor preview.
    // The overlay has no background raycast; only the visible bubble receives pointer input.
    public sealed class TutorialHintView : MonoBehaviour
    {
        [SerializeField] private TMP_Text titleText;
        [SerializeField] private TMP_Text bodyText;
        [SerializeField] private Button dismissButton;
        [SerializeField] private RectTransform bubble;
        [SerializeField] private RectTransform pointer;
        [SerializeField] private RectTransform highlight;
        [SerializeField] private bool highlightTarget = true;
        [SerializeField] private bool showPointer = true;
        [SerializeField] private float gap = 18f;
        [SerializeField] private float screenMargin = 12f;
        private readonly Vector3[] corners = new Vector3[4];
        public event Action Dismissed;

        private void Awake()
        {
            if (dismissButton != null) dismissButton.onClick.AddListener(Dismiss);
        }
        private void Dismiss() => Dismissed?.Invoke();
        private void OnDestroy()
        {
            if (dismissButton != null) dismissButton.onClick.RemoveListener(Dismiss);
        }

        public void Follow(RectTransform target)
        {
            var root = transform as RectTransform;
            if (root == null || bubble == null || target == null) return;
            var targetCanvas = target.GetComponentInParent<Canvas>();
            var ownCanvas = GetComponentInParent<Canvas>();
            Camera targetCamera = targetCanvas != null && targetCanvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? targetCanvas.rootCanvas.worldCamera : null;
            Camera ownCamera = ownCanvas != null && ownCanvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? ownCanvas.rootCanvas.worldCamera : null;
            target.GetWorldCorners(corners);
            Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (Vector3 corner in corners)
            {
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(targetCamera, corner);
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, ownCamera, out var local)) return;
                min = Vector2.Min(min, local);
                max = Vector2.Max(max, local);
            }
            if (highlight != null)
            {
                highlight.gameObject.SetActive(highlightTarget);
                highlight.anchoredPosition = (min + max) * 0.5f;
                highlight.sizeDelta = max - min + Vector2.one * 8f;
            }
            // Canvas scaling and screen bounds are evaluated in the same local coordinates.
            // Keep the Inspector width unless the actual viewport cannot accommodate it.
            float available = Mathf.Max(1f, root.rect.width - 2f * screenMargin);
            float scale = Mathf.Min(1f, available / Mathf.Max(1f, bubble.rect.width),
                Mathf.Max(1f, root.rect.height - 2f * screenMargin) / Mathf.Max(1f, bubble.rect.height));
            bubble.localScale = Vector3.one * scale;
            Vector2 half = bubble.rect.size * (scale * 0.5f);
            Rect bounds = root.rect;
            bool above = max.y + gap + 2f * half.y <= bounds.yMax - screenMargin;
            float x = Mathf.Clamp((min.x + max.x) * 0.5f,
                bounds.xMin + screenMargin + half.x, bounds.xMax - screenMargin - half.x);
            float y = above ? max.y + gap + half.y : min.y - gap - half.y;
            y = Mathf.Clamp(y, bounds.yMin + screenMargin + half.y, bounds.yMax - screenMargin - half.y);
            bubble.anchoredPosition = new Vector2(x, y);
            if (pointer != null)
            {
                pointer.gameObject.SetActive(showPointer);
                pointer.anchoredPosition = new Vector2(Mathf.Clamp((min.x + max.x) * 0.5f,
                    x - half.x + 16f, x + half.x - 16f), y + (above ? -half.y : half.y));
            }
            transform.SetAsLastSibling();
        }
    }
}
