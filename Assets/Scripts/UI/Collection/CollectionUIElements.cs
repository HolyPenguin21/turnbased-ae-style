using Game.Core;
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
        private const int DropdownVisibleRows = 10;
        private const float DropdownRowHeight = 32f;
        private const float DropdownPadding = 8f;

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
            if (headingFont == null) headingFont = GameAssetCatalog.Load<TMP_FontAsset>("Fonts/Rajdhani-SemiBold SDF");
            if (headingFont != null) text.font = headingFont;
            text.fontSize = size; text.fontStyle = FontStyles.Bold;
            text.color = CollectionInk; text.alignment = TextAlignmentOptions.Center;
        }

        public static void ApplySurface(Image image, string asset)
        {
            if (image == null) return;
            string directory = asset switch
            {
                "Button" or "SquareButton" or "BackPlate" or "SavePlate" => "Buttons",
                "Panel_Outer" or "Panel_Inset" => "Panels",
                "Input" or "Meter" or "ScrollbarTrack" or "ScrollbarHandle" or "DropdownBackground" => "Controls",
                "Metal" => "Backgrounds",
                _ => "Decorations"
            };
            var sprite = GameAssetCatalog.Load<Sprite>($"UI/{directory}/Collection/{asset}");
            if (sprite == null) return;
            image.sprite = sprite; image.color = Color.white;
            image.type = Image.Type.Sliced; image.pixelsPerUnitMultiplier = 1.30625f;
            if (asset == "DropdownBackground") image.pixelsPerUnitMultiplier = 2.5f;
            image.fillCenter = asset != "Panel_Inset";
            if (asset == "Panel_Inset")
            {
                // Keep the metal grain at native scale; only the frame rails are stretched.
                var metal = image.transform.Find("MetalBackground") as RectTransform;
                if (metal == null) metal = Panel(image.transform, "MetalBackground");
                Stretch(metal); metal.offsetMin = new Vector2(19, 19); metal.offsetMax = new Vector2(-19, -19);
                var background = metal.GetComponent<Image>();
                background.sprite = GameAssetCatalog.Load<Sprite>("UI/Backgrounds/Collection/Metal"); background.color = Color.white;
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
            image.sprite = GameAssetCatalog.Load<Sprite>(large ? "UI/Panels/Rewards/RewardConsole_1440x900" : "UI/Panels/Rewards/RewardDialog_1060x630");
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

        // Campaign and its entry dialogs share the same transparent frame/plaque imports.
        // Collection windows continue to use ApplySurface and their authored control templates.
        public static void ApplyCampaignSurface(RectTransform rect, string asset)
        {
            var image = rect.GetComponent<Image>();
            string path = asset == "Panel_Outer" ? "UI/Panels/Campaign/ConsoleFrame"
                : asset == "Panel_Inset" ? "UI/Panels/Campaign/InsetFrame"
                : asset == "Plaque" ? $"UI/Decorations/Campaign/Plaque_{(int)rect.sizeDelta.x}x{(int)rect.sizeDelta.y}" : null;
            Sprite sprite = null;
            if (path != null)
            {
                if (asset == "Plaque")
                {
                    var sprites = GameAssetCatalog.LoadAll<Sprite>(path);
                    if (sprites.Length > 0) sprite = sprites[0];
                }
                else sprite = GameAssetCatalog.Load<Sprite>(path);
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
                var template = dropdown.template;
                template.anchorMin = new Vector2(0, 0); template.anchorMax = new Vector2(1, 0);
                template.pivot = new Vector2(.5f, 1); template.anchoredPosition = new Vector2(0, -4);
                template.sizeDelta = new Vector2(0, DropdownVisibleRows * DropdownRowHeight + 2 * DropdownPadding);
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
                    rect.pivot = new Vector2(.5f, 1); rect.anchoredPosition = new Vector2(0, -DropdownPadding);
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
                ConfigureDropdownScroll(dropdown);
            }
            foreach (var input in root.GetComponentsInChildren<TMP_InputField>(true))
            {
                Surface(input.transform, "Input"); ControlColors(input);
                if (input.textComponent != null) input.textComponent.color = CollectionInk;
                if (input.placeholder is TMP_Text text) text.color = new Color(.35f, .27f, .19f);
            }
        }
        // Scroll-only setup preserves manually authored window geometry, text and controls.
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
            var bar = (RectTransform)popup.verticalScrollbar.transform;
            bar.anchorMin = new Vector2(1, 0); bar.anchorMax = new Vector2(1, 1);
            bar.pivot = new Vector2(1, .5f); bar.anchoredPosition = new Vector2(-8, 0);
            bar.sizeDelta = new Vector2(10, -16);
            bar.gameObject.SetActive(needsScroll);
            ApplyScrollbarSkin(popup.verticalScrollbar);
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
                ApplySurface(track, "ScrollbarTrack"); track.pixelsPerUnitMultiplier = 3.2f;
                track.raycastTarget = true;
                var edge = track.GetComponent<Outline>();
                if (edge != null) edge.enabled = false;
            }
            if (bar.handleRect != null)
            {
                var area = bar.handleRect.parent as RectTransform;
                if (area != null && area != bar.transform)
                { Stretch(area); area.offsetMin = new Vector2(1, 2); area.offsetMax = new Vector2(-1, -2); }
                Surface(bar.handleRect, "ScrollbarHandle");
                bar.handleRect.offsetMin = bar.handleRect.offsetMax = Vector2.zero;
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





