using Game.Core;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Collection window scroll/dropdown skin and small runtime UI construction helpers.
    // Window geometry is authored by hand in the scene; the skin pass never moves rects
    // except the dropdown popup, whose height and rows follow the option count.
    public static class CollectionUIElements
    {
        private const int DropdownVisibleRows = 10;
        private const float DropdownRowHeight = 32f;
        private const float DropdownPadding = 8f;

        public static void ApplyCollectionScrollSkin(Transform root)
        {
            if (root == null) return;
            foreach (var dropdown in root.GetComponentsInChildren<TMP_Dropdown>(true))
                ConfigureDropdownScroll(dropdown);
            foreach (var bar in root.GetComponentsInChildren<Scrollbar>(true))
                ApplyScrollbarSkin(bar);
        }

        public static void ConfigureDropdownScroll(TMP_Dropdown dropdown)
        {
            if (dropdown == null || dropdown.template == null) return;
            var template = dropdown.template;
            var popup = template.GetComponent<ScrollRect>();
            if (popup == null || popup.content == null || popup.viewport == null) return;
            ApplySurface(template.GetComponent<Image>(), "DropdownBackground");
            bool needsScroll = dropdown.options.Count > DropdownVisibleRows;
            template.sizeDelta = new Vector2(template.sizeDelta.x,
                DropdownVisibleRows * DropdownRowHeight + 2 * DropdownPadding);
            popup.horizontal = false; popup.vertical = needsScroll;
            popup.movementType = ScrollRect.MovementType.Clamped; popup.inertia = false;
            // Count owns visibility. AutoHide tests pixel overflow and could fight this rule.
            popup.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            Stretch(popup.viewport);
            popup.viewport.offsetMin = new Vector2(DropdownPadding, 0);
            popup.viewport.offsetMax = new Vector2(needsScroll ? -22 : -DropdownPadding, 0);
            var mask = popup.viewport.GetComponent<Mask>();
            if (mask != null) mask.showMaskGraphic = false;
            var content = popup.content;
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(.5f, 1); content.anchoredPosition = Vector2.zero;
            // TMP_Dropdown.Show shrinks the popup to Content. Padding belongs in Content,
            // not in the viewport, or even a one-row popup clips and needs scrolling.
            content.sizeDelta = new Vector2(0, DropdownRowHeight + 2 * DropdownPadding);
            var item = content.GetComponentInChildren<Toggle>(true);
            if (item != null)
            {
                var rect = (RectTransform)item.transform;
                rect.anchorMin = new Vector2(0, 1); rect.anchorMax = new Vector2(1, 1);
                rect.pivot = new Vector2(.5f, 1);
                rect.anchoredPosition = new Vector2(0, -DropdownPadding);
                rect.sizeDelta = new Vector2(0, DropdownRowHeight);
            }
            if (popup.verticalScrollbar == null) return;
            // Scrollbar geometry is authored by hand in the scene; only visibility and skin apply here.
            popup.verticalScrollbar.gameObject.SetActive(needsScroll);
            ApplyScrollbarSkin(popup.verticalScrollbar);
        }

        // Skin only: track, sliding area and handle geometry stay as authored in the scene.
        private static void ApplyScrollbarSkin(Scrollbar bar)
        {
            var track = bar.GetComponent<Image>();
            if (track != null)
            {
                ApplySurface(track, "ScrollbarTrack"); track.pixelsPerUnitMultiplier = 3.2f;
                track.raycastTarget = true;
                var edge = track.GetComponent<Outline>();
                if (edge != null) edge.enabled = false;
            }
            if (bar.handleRect != null)
            {
                ApplySurface(bar.handleRect.GetComponent<Image>(), "ScrollbarHandle");
                var handle = bar.handleRect.GetComponent<Image>();
                if (handle != null) { handle.pixelsPerUnitMultiplier = 3.2f; handle.raycastTarget = true; bar.targetGraphic = handle; }
                for (int i = 0; i < 3; i++)
                {
                    string name = "Grip" + i;
                    var grip = bar.handleRect.Find(name);
                    if (grip != null) grip.gameObject.SetActive(false);
                }
            }
            ControlColors(bar);
        }

        // Control sprites: ScrollbarTrack, ScrollbarHandle, DropdownBackground.
        private static void ApplySurface(Image image, string asset)
        {
            if (image == null) return;
            var sprite = GameAssetCatalog.Load<Sprite>($"UI/Controls/Collection/{asset}");
            if (sprite == null) return;
            image.sprite = sprite; image.color = Color.white;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = asset == "DropdownBackground" ? 2.5f : 1.30625f;
            image.fillCenter = true;
        }

        private static void ControlColors(Selectable control)
        {
            var colors = control.colors;
            colors.highlightedColor = new Color(1f, .92f, .76f);
            colors.pressedColor = new Color(.72f, .62f, .47f);
            colors.disabledColor = new Color(.5f, .5f, .5f, .55f);
            control.colors = colors;
        }

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
            text.font = GameAssetCatalog.Load<TMP_FontAsset>("Fonts/GameMenuFont") ?? TMP_Settings.defaultFontAsset;
            text.text = value; text.fontSize = size; text.color = new Color(.82f, .85f, .8f);
            text.raycastTarget = false; text.textWrappingMode = TextWrappingModes.Normal; return text;
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
        internal static RectTransform Scroll(Transform parent, string name, float x, float y, float w, float h, bool showScrollbar = false)
        {
            var root = Panel(parent, name); Place(root, x, y, w, h);
            var viewport = Rect(root, "Viewport"); Stretch(viewport); viewport.gameObject.AddComponent<RectMask2D>();
            if (showScrollbar) viewport.offsetMax = new Vector2(-18, 0);
            var content = Rect(viewport, "Content"); Place(content, 0, 0, w - (showScrollbar ? 22 : 14), 1);
            var scroll = root.gameObject.AddComponent<ScrollRect>(); scroll.viewport = viewport; scroll.content = content;
            scroll.scrollSensitivity = 3f; // Wheel-only multiplier; dragging is unchanged.
            scroll.horizontal = false; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped;
            if (showScrollbar)
            {
                var track = Panel(root, "Scrollbar"); Place(track, w - 16, 2, 14, h - 4);
                track.GetComponent<Image>().color = new Color(.1f, .13f, .11f);
                var area = Rect(track, "SlidingArea"); Stretch(area);
                var handle = Panel(area, "Handle"); Stretch(handle);
                handle.GetComponent<Image>().color = new Color(.36f, .48f, .34f);
                var scrollbar = track.gameObject.AddComponent<Scrollbar>();
                scrollbar.direction = Scrollbar.Direction.BottomToTop;
                scrollbar.handleRect = handle; scrollbar.targetGraphic = handle.GetComponent<Image>();
                scroll.verticalScrollbar = scrollbar;
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            }
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
