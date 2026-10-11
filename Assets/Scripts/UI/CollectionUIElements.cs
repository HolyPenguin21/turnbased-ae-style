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
        private static TMP_FontAsset headingFont;

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
            At("Filters", 24, 56, 240, 200);
            At("Filters/Faction", 14, 30, 212, 34);
            At("Filters/Category", 14, 89, 212, 34);
            At("Filters/Ownership", 14, 150, 212, 34);
            At("CardDetails", 24, 260, 240, 442);
            At("Cards", 276, 56, 632, 646);
            At("Deck", 920, 56, 336, 646);
            At("Title", 384, 11, 506, 38);
            At("Back", 1128, 10, 118, 40);
            At("Back/Label", 6, 4, 106, 32);
            At("Status", 276, 702, 980, 16);
            At("Deck/DeckPicker", 16, 60, 304, 32);
            At("Deck/Create", 16, 95, 304, 32);
            At("Deck/Create/Label", 6, 3, 292, 26);
            At("Deck/DeckName", 16, 133, 304, 32);
            At("Deck/Starter", 16, 170, 107, 34);
            At("Deck/Copy", 131, 170, 85, 34);
            At("Deck/Delete", 224, 170, 96, 34);
            At("Deck/Save", 16, 577, 304, 56);
            At("Deck/Save/Label", 12, 7, 280, 42);
            At("Deck/Points", 20, 224, 190, 24);
            At("Deck/TotalCostTitle", 210, 226, 106, 20);
            At("Deck/Budget", 18, 254, 300, 26);
            At("Deck/CardsHeader", 20, 329, 220, 19);
            At("Deck/CountHeader", 262, 329, 54, 19);
            At("Deck/DeckCards", 18, 351, 300, 221);
            for (int i = 0; i < 5; i++)
            {
                At("Deck/CostIcon" + i, 20 + i * 60, 291, 18, 18);
                At("Deck/CostValue" + i, 43 + i * 60, 291, 32, 19);
            }
            Surface(root.Find("Deck/Budget"), "Meter");
            if (root.Find("Cards/Viewport/Content") is RectTransform content)
            {
                var grid = content.GetComponent<GridLayoutGroup>();
                if (grid != null)
                {
                    grid.cellSize = new Vector2(140, 198); grid.spacing = new Vector2(8, 8);
                    grid.padding = new RectOffset(6, 6, 8, 8);
                    grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 4;
                }
            }
            var artLayout = root.Find("CardDetails/Viewport/Content/Art")?.GetComponent<LayoutElement>();
            if (artLayout != null) artLayout.preferredHeight = 210;
            ApplyScrollSkin(root.Find("Cards"), true);
            ApplyScrollSkin(root.Find("CardDetails"), true);
            ApplyScrollSkin(root.Find("Deck/DeckCards"), false);
            var deckRows = root.Find("Deck/DeckCards/Viewport/Content")?.GetComponent<VerticalLayoutGroup>();
            if (deckRows != null) deckRows.spacing = 3;

            var plaque = Decoration(root, "TitlePlaque", "Plaque"); Place(plaque, 360, 4, 560, 52);
            plaque.SetAsFirstSibling();
            var title = root.Find("Title")?.GetComponent<TMP_Text>();
            Heading(title, 30);
            var filters = root.Find("Filters");
            if (filters != null)
            {
                Caption(filters, "FactionTitle", "FACTION", 18, 12, 204, 16, 13);
                Caption(filters, "CategoryTitle", "TYPE", 18, 71, 204, 16, 13);
                Caption(filters, "OwnershipTitle", "OWNERSHIP", 18, 132, 204, 16, 13);
            }
            var deck = root.Find("Deck");
            if (deck != null)
            {
                var statistics = Decoration(deck, "StatisticsFrame", "Panel_Inset"); Place(statistics, 8, 212, 320, 360);
                statistics.SetSiblingIndex(deck.Find("MetalBackground") != null ? 1 : 0);
                var divider = Decoration(deck, "StatisticsDivider", "Divider"); Place(divider, 20, 320, 296, 2);
                var heading = Decoration(deck, "DeckTitle", "DeckHeader"); Place(heading, 12, 10, 312, 42);
                heading.SetSiblingIndex(deck.Find("MetalBackground") != null ? 2 : 1);
                var text = Caption(heading, "Label", "DECK", 12, 4, 288, 34, 24);
                Heading(text, 24);
            }
            var saveLabel = root.Find("Deck/Save/Label")?.GetComponent<TMP_Text>();
            if (saveLabel != null) { saveLabel.text = "SAVE DECK"; Heading(saveLabel, 28); }
            Heading(root.Find("Back/Label")?.GetComponent<TMP_Text>(), 20);
            foreach (string path in new[] { "Deck/Starter", "Deck/Copy", "Deck/Delete" })
                if (root.Find(path) is RectTransform button)
                    if (button.Find("Label") is RectTransform label) Place(label, 5, 3, button.sizeDelta.x - 10, button.sizeDelta.y - 6);
            var costTitle = root.Find("Deck/TotalCostTitle")?.GetComponent<TMP_Text>();
            if (costTitle != null) costTitle.alignment = TextAlignmentOptions.Right;
            foreach (string path in new[] { "UnsavedModal/Window", "ConfirmModal/Window" })
                root.Find(path)?.SetAsFirstSibling();
        }

        public static void ApplyCellSkin(RectTransform root)
        {
            if (root == null) return;
            root.sizeDelta = new Vector2(140, 198);
            var background = root.GetComponent<Image>();
            if (background != null) { background.sprite = null; background.color = Color.clear; background.raycastTarget = false; }
            if (root.Find("Card") is RectTransform card) card.sizeDelta = new Vector2(106, 155);
            if (root.Find("Info") is RectTransform info)
            {
                Place(info, 1, 158, 138, 15);
                var text = info.GetComponent<TMP_Text>();
                if (text != null) { text.alignment = TextAlignmentOptions.Center; text.color = CollectionText; }
            }
            if (root.Find("Add") is RectTransform add) Place(add, 5, 174, 130, 24);
            if (root.Find("Add/Label") is RectTransform label) Place(label, 5, 3, 120, 18);
            ApplyControlsSkin(root);
        }

        public static void ApplyDeckRowSkin(RectTransform root)
        {
            if (root == null) return;
            void At(string path, float x, float y, float w, float h)
            { if (root.Find(path) is RectTransform rect) Place(rect, x, y, w, h); }
            At("Row/Art", 0, 0, 30, 38);
            At("Row/Name", 34, 0, 164, 38);
            At("Row/Name/Label", 6, 5, 114, 28);
            At("Row/Cost", 158, 9, 34, 20);
            At("Row/Minus", 204, 5, 26, 28);
            At("Row/Count", 260, 5, 20, 28);
            At("Row/Plus", 232, 5, 26, 28);
            foreach (string path in new[] { "Row/Minus/Label", "Row/Plus/Label" }) At(path, 2, 2, 22, 24);
            ApplyControlsSkin(root);
            Surface(root.Find("Row/Name"), "RowPlate");
            foreach (string path in new[] { "Row/Minus", "Row/Plus" }) Surface(root.Find(path), "SquareButton");
            foreach (var text in root.GetComponentsInChildren<TMP_Text>(true))
            { text.color = CollectionText; text.textWrappingMode = TextWrappingModes.NoWrap; text.overflowMode = TextOverflowModes.Ellipsis; }
            var name = root.Find("Row/Name/Label")?.GetComponent<TMP_Text>();
            if (name != null) { name.alignment = TextAlignmentOptions.Left; name.fontSize = 13; }
            var cost = root.Find("Row/Cost")?.GetComponent<TMP_Text>();
            if (cost != null) { cost.alignment = TextAlignmentOptions.Right; cost.fontSize = 11; }
        }

        private static void Heading(TMP_Text text, int size)
        {
            if (text == null) return;
            if (headingFont == null) headingFont = Resources.Load<TMP_FontAsset>("Fonts/Rajdhani-SemiBold SDF");
            if (headingFont != null) text.font = headingFont;
            text.fontSize = size; text.fontStyle = FontStyles.Bold;
            text.color = CollectionInk; text.alignment = TextAlignmentOptions.Center;
        }

        public static void ApplySurface(Image image, string asset)
        {
            if (image == null) return;
            var sprite = Resources.Load<Sprite>("UI/Collection/" + asset);
            if (sprite == null) return;
            image.sprite = sprite; image.color = Color.white;
            image.type = Image.Type.Sliced; image.pixelsPerUnitMultiplier = 1.30625f;
            image.fillCenter = asset != "Panel_Inset";
            if (asset == "Panel_Inset")
            {
                // Keep the metal grain at native scale; only the frame rails are stretched.
                var metal = image.transform.Find("MetalBackground") as RectTransform;
                if (metal == null) metal = Panel(image.transform, "MetalBackground");
                Stretch(metal); metal.offsetMin = new Vector2(19, 19); metal.offsetMax = new Vector2(-19, -19);
                var background = metal.GetComponent<Image>();
                background.sprite = Resources.Load<Sprite>("UI/Collection/Metal"); background.color = Color.white;
                background.type = Image.Type.Tiled; background.pixelsPerUnitMultiplier = 1.30625f; background.raycastTarget = false;
                metal.SetAsFirstSibling();
            }
        }
        private static void Surface(Transform target, string asset)
        { if (target != null) ApplySurface(target.GetComponent<Image>(), asset); }

        internal static RectTransform RewardWindow(Transform parent, string name, bool large = false)
        {
            var window = Panel(parent, name);
            window.anchorMin = window.anchorMax = window.pivot = new Vector2(.5f, .5f);
            window.anchoredPosition = Vector2.zero;
            window.sizeDelta = large ? new Vector2(1440, 900) : new Vector2(1060, 630);
            var image = window.GetComponent<Image>();
            image.sprite = Resources.Load<Sprite>(large ? "UI/Rewards/RewardConsole_1440x900" : "UI/Rewards/RewardDialog_1060x630");
            image.type = Image.Type.Simple; image.preserveAspect = true; image.color = Color.white;
            if (image.sprite == null) ApplyCampaignSurface(window, "Panel_Outer");
            return window;
        }
        internal static TMP_Text RewardTitle(Transform parent, string value, bool large = false, TMP_Text existing = null)
        {
            var title = existing != null ? existing : Label(parent, value, 0, 0, 1, 1);
            title.text = value;
            Place((RectTransform)title.transform, large ? 280 : 210, large ? 26 : 19, large ? 880 : 640, large ? 70 : 58);
            Heading(title, large ? 38 : 30);
            return title;
        }
        internal static Button RewardButton(Transform parent, string value, float x, float y, float w, float h, Action action, int size = 22)
        {
            var button = Button(parent, value, x, y, w, h, action, size);
            ApplyControlsSkin(button.transform);
            var label = button.GetComponentInChildren<TMP_Text>();
            label.fontStyle = FontStyles.Bold;
            Place((RectTransform)label.transform, 10, 4, w - 20, h - 8);
            return button;
        }
        internal static RectTransform RewardScroll(Transform parent, string name, float x, float y, float w, float h)
        {
            var content = Scroll(parent, name, x, y, w, h, true);
            var root = content.parent.parent;
            root.GetComponent<Image>().color = Color.clear;
            ApplyScrollbarSkin(root.GetComponent<ScrollRect>().verticalScrollbar);
            return content;
        }
        internal static void FitCardPreview(RectTransform card, RectTransform host)
        {
            Vector2 area = host.rect.size;
            float scale = Mathf.Min(area.x / Mathf.Max(1, card.rect.width), area.y / Mathf.Max(1, card.rect.height));
            card.localScale = Vector3.one * scale;
            card.anchorMin = card.anchorMax = new Vector2(.5f, 1); card.pivot = new Vector2(.5f, 1); card.anchoredPosition = Vector2.zero;
        }

        // Campaign and its entry dialogs share the same transparent frame/plaque imports.
        // Collection windows continue to use ApplySurface and their authored control templates.
        public static void ApplyCampaignSurface(RectTransform rect, string asset)
        {
            var image = rect.GetComponent<Image>();
            string path = asset == "Panel_Outer" ? "Campaign/ConsoleFrame"
                : asset == "Panel_Inset" ? "Campaign/InsetFrame"
                : asset == "Plaque" ? $"Campaign/Plaque_{(int)rect.sizeDelta.x}x{(int)rect.sizeDelta.y}" : null;
            Sprite sprite = null;
            if (path != null)
            {
                if (asset == "Plaque")
                {
                    var sprites = Resources.LoadAll<Sprite>(path);
                    if (sprites.Length > 0) sprite = sprites[0];
                }
                else sprite = Resources.Load<Sprite>(path);
            }
            if (sprite == null) { ApplySurface(image, asset); return; }
            image.sprite = sprite; image.color = Color.white;
            image.type = asset == "Plaque" ? Image.Type.Simple : Image.Type.Sliced;
            image.preserveAspect = asset == "Plaque";
            image.pixelsPerUnitMultiplier = asset == "Panel_Outer" ? 3 : 2;
            image.fillCenter = asset == "Plaque";
        }
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
                Surface(button.transform, primary ? (button.name == "Save" ? "SavePlate" : "BackPlate") : "Button"); ControlColors(button);
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
                // One parchment surface for the popup; items are flat highlight strips.
                ApplySurface(dropdown.template.GetComponent<Image>(), "Input");
                var template = dropdown.template;
                template.anchorMin = new Vector2(0, 0); template.anchorMax = new Vector2(1, 0);
                template.pivot = new Vector2(.5f, 1); template.anchoredPosition = new Vector2(0, -4);
                template.sizeDelta = new Vector2(0, 240);
                foreach (var image in dropdown.template.GetComponentsInChildren<Image>(true))
                {
                    if (image.name == "Item Background")
                    { image.sprite = null; image.type = Image.Type.Simple; image.color = Color.white; }
                    else if (image.name == "Item Checkmark") image.color = CollectionInk;
                }
                var item = dropdown.itemText != null ? dropdown.itemText.GetComponentInParent<Toggle>() : null;
                if (item != null)
                {
                    var rect = (RectTransform)item.transform;
                    rect.anchorMin = new Vector2(0, 1); rect.anchorMax = new Vector2(1, 1);
                    rect.pivot = new Vector2(.5f, 1); rect.anchoredPosition = Vector2.zero;
                    rect.sizeDelta = new Vector2(0, 32);
                    var colors = item.colors;
                    colors.normalColor = Color.clear;
                    colors.highlightedColor = new Color(.36f, .25f, .12f, .18f);
                    colors.selectedColor = colors.highlightedColor;
                    colors.pressedColor = new Color(.36f, .25f, .12f, .3f);
                    colors.disabledColor = Color.clear; item.colors = colors;
                    var label = (RectTransform)dropdown.itemText.transform;
                    Stretch(label); label.offsetMin = new Vector2(24, 3); label.offsetMax = new Vector2(-8, -3);
                    dropdown.itemText.fontSize = 14; dropdown.itemText.textWrappingMode = TextWrappingModes.NoWrap;
                    dropdown.itemText.overflowMode = TextOverflowModes.Ellipsis;
                    var check = item.transform.Find("Item Checkmark") as RectTransform;
                    if (check != null)
                    {
                        check.anchorMin = check.anchorMax = new Vector2(0, .5f);
                        check.pivot = new Vector2(.5f, .5f); check.anchoredPosition = new Vector2(12, 0);
                        check.sizeDelta = new Vector2(10, 10);
                    }
                }
                var popup = template.GetComponent<ScrollRect>();
                if (popup != null)
                {
                    popup.horizontal = false; popup.vertical = true;
                    popup.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
                    popup.verticalScrollbarSpacing = 2;
                    if (popup.viewport != null)
                    {
                        Stretch(popup.viewport); popup.viewport.offsetMin = new Vector2(8, 8); popup.viewport.offsetMax = new Vector2(-22, -8);
                        var mask = popup.viewport.GetComponent<Mask>();
                        if (mask != null) mask.showMaskGraphic = false;
                        else
                        {
                            var graphic = popup.viewport.GetComponent<Image>();
                            if (graphic != null) graphic.color = Color.clear;
                        }
                    }
                    if (popup.content != null)
                    {
                        popup.content.anchorMin = new Vector2(0, 1); popup.content.anchorMax = new Vector2(1, 1);
                        popup.content.pivot = new Vector2(.5f, 1); popup.content.anchoredPosition = Vector2.zero;
                        popup.content.sizeDelta = new Vector2(0, 32);
                    }
                    if (popup.verticalScrollbar != null)
                    {
                        var bar = (RectTransform)popup.verticalScrollbar.transform;
                        bar.anchorMin = new Vector2(1, 0); bar.anchorMax = new Vector2(1, 1); bar.pivot = new Vector2(1, .5f);
                        bar.anchoredPosition = new Vector2(-8, 0); bar.sizeDelta = new Vector2(10, -20);
                        ApplyScrollbarSkin(popup.verticalScrollbar);
                    }
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
                scroll.viewport.offsetMin = new Vector2(scroll.verticalScrollbar != null ? 6 : 8, 8);
                scroll.viewport.offsetMax = new Vector2(scroll.verticalScrollbar != null ? -28 : -8, -8);
            }
            if (scroll.verticalScrollbar == null) return;
            var bar = scroll.verticalScrollbar;
            var rect = (RectTransform)bar.transform;
            var size = ((RectTransform)root).sizeDelta;
            Place(rect, size.x - (inset ? 26 : 14), 16, 10, size.y - 32);
            ApplyScrollbarSkin(bar);
        }
        private static void ApplyScrollbarSkin(Scrollbar bar)
        {
            var track = bar.GetComponent<Image>();
            if (track != null)
            {
                track.sprite = null; track.type = Image.Type.Simple;
                track.color = new Color(.035f, .032f, .025f, .95f);
                var edge = track.GetComponent<Outline>();
                if (edge == null) edge = track.gameObject.AddComponent<Outline>();
                edge.effectColor = new Color(.38f, .25f, .12f, .85f); edge.effectDistance = new Vector2(.65f, .65f);
            }
            if (bar.handleRect != null)
            {
                var area = bar.handleRect.parent as RectTransform;
                if (area != null && area != bar.transform)
                { Stretch(area); area.offsetMin = new Vector2(1, 2); area.offsetMax = new Vector2(-1, -2); }
                Surface(bar.handleRect, "SquareButton");
                var handle = bar.handleRect.GetComponent<Image>();
                if (handle != null) { handle.pixelsPerUnitMultiplier = 3; handle.raycastTarget = true; bar.targetGraphic = handle; }
                for (int i = 0; i < 3; i++)
                {
                    string name = "Grip" + i;
                    var grip = bar.handleRect.Find(name) as RectTransform;
                    if (grip == null) grip = Panel(bar.handleRect, name);
                    grip.anchorMin = grip.anchorMax = new Vector2(.5f, .5f); grip.pivot = new Vector2(.5f, .5f);
                    grip.anchoredPosition = new Vector2(0, (i - 1) * 3); grip.sizeDelta = new Vector2(4, 1);
                    var line = grip.GetComponent<Image>(); line.sprite = null; line.color = CollectionText; line.raycastTarget = false;
                }
            }
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




