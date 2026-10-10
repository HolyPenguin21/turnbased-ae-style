using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Technical layout helpers used only by the collection screens. Existing card prefab/art
    // and authored dropdown/input templates supply all game-specific visuals.
    internal static class CollectionUIElements
    {
        internal static readonly Color PanelColor = new Color(.055f, .065f, .07f, .98f);
        internal static RectTransform Rect(Transform parent, string name)
        { var go = new GameObject(name, typeof(RectTransform)); var rect = (RectTransform)go.transform; rect.SetParent(parent, false); return rect; }
        internal static void Place(RectTransform r, float x, float y, float width, float height)
        { r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1); r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(width, height); }
        internal static void Stretch(RectTransform r)
        { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero; }
        internal static RectTransform Panel(Transform parent, string name)
        { var r = Rect(parent, name); r.gameObject.AddComponent<Image>().color = PanelColor; return r; }
        internal static TMP_Text Label(Transform parent, string value, float x, float y, float w, float h, int size = 16)
        {
            var r = Rect(parent, "Label"); Place(r, x, y, w, h);
            var text = r.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = Resources.Load<TMP_FontAsset>("Fonts/GameMenuFont") ?? TMP_Settings.defaultFontAsset;
            text.text = value; text.fontSize = size; text.color = new Color(.82f, .85f, .8f);
            text.raycastTarget = false; text.enableWordWrapping = true; return text;
        }
        internal static Button Button(Transform parent, string value, float x, float y, float w, float h, Action action, int size = 16)
        {
            var r = Panel(parent, value); Place(r, x, y, w, h);
            r.GetComponent<Image>().color = new Color(.16f, .2f, .17f);
            var button = r.gameObject.AddComponent<Button>(); button.targetGraphic = r.GetComponent<Image>();
            var label = Label(r, value, 5, 1, w - 10, h - 2, size); label.alignment = TextAlignmentOptions.Center;
            button.onClick.AddListener(() => action());
            Game.Audio.SceneUIAudioBinder.BindCreatedRoot(button);
            return button;
        }
        internal static RectTransform Scroll(Transform parent, string name, float x, float y, float w, float h)
        {
            var root = Panel(parent, name); Place(root, x, y, w, h);
            var viewport = Rect(root, "Viewport"); Stretch(viewport); viewport.gameObject.AddComponent<RectMask2D>();
            var content = Rect(viewport, "Content"); Place(content, 0, 0, w - 14, 1);
            var scroll = root.gameObject.AddComponent<ScrollRect>(); scroll.viewport = viewport; scroll.content = content;
            scroll.horizontal = false; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped;
            return content;
        }
        internal static void Clear(Transform root)
        { foreach (Transform child in root) { child.gameObject.SetActive(false); UnityEngine.Object.Destroy(child.gameObject); } }
        internal static RectTransform Canvas(string name, Vector2? referenceResolution = null)
        {
            var root = Rect(null, name);
            var canvas = root.gameObject.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 200;
            var scaler = root.gameObject.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution ?? new Vector2(1024, 768); scaler.matchWidthOrHeight = .5f;
            root.gameObject.AddComponent<GraphicRaycaster>();
            return root;
        }
    }
}
