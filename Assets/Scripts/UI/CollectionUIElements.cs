using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Collection theme and layout helpers. Editor authoring and existing scene windows
    // share the same skin; card art and authored control templates remain intact.
    public static class CollectionUIElements
    {
        public static readonly Color CollectionText = new Color(.93f, .85f, .69f);
        public static readonly Color CollectionInk = new Color(.16f, .12f, .08f);

        // One visual application path for both editor authoring and opening existing windows.
        // An already-loaded scene may retain its old serialized appearance after a code/asset
        // update. Apply only to this window; preserve its bindings, templates and listeners.
        public static void ApplyWindowSkin(RectTransform root)
        {
            if (root == null) return;
            Surface(root, "Panel_Outer");
            foreach (string path in new[] { "Filters", "CardDetails", "Cards", "Deck", "UnsavedModal/Window", "ConfirmModal/Window" })
                Surface(root.Find(path), "Panel_Inset");
            var deckCardsImage = root.Find("Deck/DeckCards")?.GetComponent<Image>();
            if (deckCardsImage != null) { deckCardsImage.sprite = null; deckCardsImage.color = Color.clear; }

            foreach (var text in root.GetComponentsInChildren<TMP_Text>(true)) text.color = CollectionText;
            ApplyControlsSkin(root);

            void At(string path, float x, float y, float w, float h)
            { if (root.Find(path) is RectTransform rect) Place(rect, x, y, w, h); }
            At("Filters", 16, 62, 240, 170);
            At("Filters/Faction", 12, 28, 216, 30);
            At("Filters/Category", 12, 79, 216, 30);
            At("Filters/Ownership", 12, 130, 216, 30);
            At("CardDetails", 16, 240, 240, 454);
            At("Cards", 272, 62, 624, 632);
            At("Deck", 912, 62, 352, 632);
            At("Title", 376, 13, 528, 32);
            At("Back", 1152, 12, 108, 32);
            At("Back/Label", 3, 1, 102, 30);
            At("Status", 272, 696, 992, 18);
            At("Deck/DeckPicker", 9, 47, 333, 30);
            At("Deck/Create", 9, 83, 333, 26);
            At("Deck/Create/Label", 3, 1, 327, 24);
            At("Deck/DeckName", 9, 115, 333, 30);
            At("Deck/Starter", 9, 151, 103, 27);
            At("Deck/Copy", 119, 151, 103, 27);
            At("Deck/Delete", 228, 151, 115, 27);
            At("Deck/Save", 9, 581, 333, 40);
            At("Deck/Save/Label", 10, 4, 313, 32);
            At("Deck/Points", 9, 188, 333, 20);
            At("Deck/TotalCostTitle", 9, 212, 333, 17);
            At("Deck/Budget", 9, 233, 333, 20);
            At("Deck/CardsHeader", 9, 290, 260, 19);
            At("Deck/CountHeader", 269, 290, 73, 19);
            At("Deck/DeckCards", 9, 316, 333, 256);
            for (int i = 0; i < 5; i++)
            {
                At("Deck/CostIcon" + i, 9 + i * 67, 260, 19, 19);
                At("Deck/CostValue" + i, 32 + i * 67, 260, 44, 19);
            }
            Surface(root.Find("Deck/Budget"), "Meter");
            if (root.Find("Cards/Viewport/Content") is RectTransform content)
            {
                var grid = content.GetComponent<GridLayoutGroup>();
                if (grid != null)
                {
                    grid.cellSize = new Vector2(140, 194); grid.spacing = new Vector2(8, 8);
                    grid.padding = new RectOffset(6, 6, 8, 8);
                    grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 4;
                }
            }
            var artLayout = root.Find("CardDetails/Viewport/Content/Art")?.GetComponent<LayoutElement>();
            if (artLayout != null) artLayout.preferredHeight = 210;
            ApplyScrollSkin(root.Find("Cards"), true);
            ApplyScrollSkin(root.Find("CardDetails"), true);
            ApplyScrollSkin(root.Find("Deck/DeckCards"), false);

            var plaque = Decoration(root, "TitlePlaque", "Plaque"); Place(plaque, 360, 8, 560, 44);
            plaque.SetAsFirstSibling();
            var title = root.Find("Title")?.GetComponent<TMP_Text>();
            if (title != null) { title.color = CollectionInk; title.fontSize = 24; title.alignment = TextAlignmentOptions.Center; }
            var filters = root.Find("Filters");
            if (filters != null)
            {
                Caption(filters, "FactionTitle", "FACTION", 14, 10, 212, 16, 12);
                Caption(filters, "CategoryTitle", "TYPE", 14, 61, 212, 16, 12);
                Caption(filters, "OwnershipTitle", "OWNERSHIP", 14, 112, 212, 16, 12);
            }
            var deck = root.Find("Deck");
            if (deck != null)
            {
                var heading = Decoration(deck, "DeckTitle", "Plaque"); Place(heading, 9, 9, 333, 32);
                heading.SetAsFirstSibling();
                var text = Caption(heading, "Label", "DECK", 12, 2, 309, 28, 20);
                text.color = CollectionInk; text.alignment = TextAlignmentOptions.Center;
            }
            var saveLabel = root.Find("Deck/Save/Label")?.GetComponent<TMP_Text>();
            if (saveLabel != null) { saveLabel.text = "SAVE DECK"; saveLabel.fontSize = 20; }
            foreach (string path in new[] { "UnsavedModal/Window", "ConfirmModal/Window" })
                root.Find(path)?.SetAsFirstSibling();
        }

        public static void ApplyCellSkin(RectTransform root)
        {
            if (root == null) return;
            root.sizeDelta = new Vector2(140, 194);
            var background = root.GetComponent<Image>();
            if (background != null) { background.sprite = null; background.color = Color.clear; background.raycastTarget = false; }
            if (root.Find("Card") is RectTransform card) card.sizeDelta = new Vector2(106, 155);
            if (root.Find("Info") is RectTransform info)
            {
                Place(info, 1, 158, 138, 15);
                var text = info.GetComponent<TMP_Text>();
                if (text != null) { text.alignment = TextAlignmentOptions.Center; text.color = CollectionText; }
            }
            if (root.Find("Add") is RectTransform add) Place(add, 8, 176, 124, 18);
            if (root.Find("Add/Label") is RectTransform label) Place(label, 3, 1, 118, 16);
            ApplyControlsSkin(root);
        }

        public static void ApplySurface(Image image, string asset)
        {
            if (image == null) return;
            var sprite = Resources.Load<Sprite>("UI/Collection/" + asset);
            if (sprite == null) return;
            image.sprite = sprite; image.color = Color.white;
            image.type = Image.Type.Sliced; image.pixelsPerUnitMultiplier = 4f;
        }
        private static void Surface(Transform target, string asset)
        { if (target != null) ApplySurface(target.GetComponent<Image>(), asset); }
        private static void ControlColors(Selectable control)
        {
            var colors = control.colors;
            colors.highlightedColor = new Color(1f, .92f, .76f);
            colors.pressedColor = new Color(.72f, .62f, .47f);
            colors.disabledColor = new Color(.5f, .5f, .5f, .55f);
            control.colors = colors;
        }
        public static void ApplyControlsSkin(Transform root)
        {
            foreach (var button in root.GetComponentsInChildren<Button>(true))
            {
                bool primary = button.name == "Save" || button.name == "Back";
                Surface(button.transform, primary ? "Plaque" : "Button"); ControlColors(button);
                var label = button.GetComponentInChildren<TMP_Text>(true);
                if (label != null) label.color = primary ? CollectionInk : CollectionText;
            }
            foreach (var dropdown in root.GetComponentsInChildren<TMP_Dropdown>(true))
            {
                Surface(dropdown.transform, "Input"); ControlColors(dropdown);
                if (dropdown.captionText != null) dropdown.captionText.color = CollectionInk;
                if (dropdown.itemText != null) dropdown.itemText.color = CollectionInk;
                var arrow = dropdown.transform.Find("Arrow")?.GetComponent<Image>();
                if (arrow != null) arrow.color = CollectionInk;
                if (dropdown.template == null) continue;
                foreach (var image in dropdown.template.GetComponentsInChildren<Image>(true))
                {
                    if (image.transform == dropdown.template || image.name == "Item Background") ApplySurface(image, "Input");
                    else if (image.name == "Handle") ApplySurface(image, "Button");
                    else if (image.name == "Scrollbar") ApplySurface(image, "Meter");
                    else if (image.name == "Item Checkmark") image.color = CollectionInk;
                }
            }
            foreach (var input in root.GetComponentsInChildren<TMP_InputField>(true))
            {
                Surface(input.transform, "Input"); ControlColors(input);
                if (input.textComponent != null) input.textComponent.color = CollectionInk;
                if (input.placeholder is TMP_Text text) text.color = new Color(.35f, .27f, .19f);
            }
        }
        private static void ApplyScrollSkin(Transform root, bool inset)
        {
            if (root == null) return;
            var scroll = root.GetComponent<ScrollRect>();
            if (scroll == null) return;
            scroll.horizontal = false; scroll.vertical = true;
            if (inset && scroll.viewport != null)
            {
                scroll.viewport.offsetMin = new Vector2(8, 8);
                scroll.viewport.offsetMax = new Vector2(scroll.verticalScrollbar != null ? -20 : -8, -8);
            }
            if (scroll.verticalScrollbar == null) return;
            var bar = scroll.verticalScrollbar;
            var rect = (RectTransform)bar.transform;
            var size = ((RectTransform)root).sizeDelta;
            Place(rect, size.x - 11, 12, 6, size.y - 24);
            Surface(bar.transform, "Meter");
            if (bar.handleRect != null) Surface(bar.handleRect, "Button");
            ControlColors(bar);
        }
        private static RectTransform Decoration(Transform parent, string name, string asset)
        {
            var rect = parent.Find(name) as RectTransform;
            if (rect == null) rect = Panel(parent, name);
            Surface(rect, asset); var image = rect.GetComponent<Image>();
            if (image != null) image.raycastTarget = false;
            return rect;
        }
        private static TMP_Text Caption(Transform parent, string name, string value, float x, float y, float w, float h, int size)
        {
            var text = parent.Find(name)?.GetComponent<TMP_Text>();
            if (text == null) { text = Label(parent, value, x, y, w, h, size); text.name = name; }
            Place((RectTransform)text.transform, x, y, w, h);
            text.text = value; text.fontSize = size; text.color = CollectionText;
            return text;
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
            text.font = Resources.Load<TMP_FontAsset>("Fonts/GameMenuFont") ?? TMP_Settings.defaultFontAsset;
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
            scroll.scrollSensitivity *= 1.69f; // Two successive 30% wheel-only increases; dragging is unchanged.
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

