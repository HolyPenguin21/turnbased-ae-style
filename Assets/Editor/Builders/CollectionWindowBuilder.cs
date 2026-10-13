#if UNITY_EDITOR
using Game.Core;
using Game.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Editor-only authoring helper: builds the collection window as ordinary scene objects under the
// scene canvas (plus two prefabs for the pooled grid cell and deck row). After it runs the
// hierarchy is a regular, hand-editable part of the scene. The shared UI skin also repairs
// the appearance of older loaded window instances when they are opened.
public static class CollectionWindowBuilder
{
    private const string PrefabFolder = "Assets/Prefabs/UI/Collection";
    private static readonly Color PanelColor = new Color(.085f, .075f, .065f, 1f);
    private static readonly Color TextColor = CollectionUIElements.CollectionText;

    [MenuItem("Tools/UI/Build Collection Window")]
    private static void Build()
    {
        var menu = Object.FindAnyObjectByType<MainMenuController>();
        if (menu == null) { EditorUtility.DisplayDialog("Collection window", "Open the MainMenu scene first (no MainMenuController found).", "OK"); return; }
        var menuObject = new SerializedObject(menu);
        var panel = menuObject.FindProperty("mainMenuPanel").objectReferenceValue as GameObject;
        var canvas = (panel != null ? panel.GetComponentInParent<Canvas>() : menu.GetComponentInParent<Canvas>());
        if (canvas == null) canvas = Object.FindAnyObjectByType<Canvas>();
        if (canvas == null) { EditorUtility.DisplayDialog("Collection window", "No Canvas found in the scene.", "OK"); return; }
        canvas = canvas.rootCanvas;
        var config = menuObject.FindProperty("gameConfig").objectReferenceValue as Game.Core.GameConfig;
        if (config == null || config.playerRowPrefab == null)
        { EditorUtility.DisplayDialog("Collection window", "MainMenuController.gameConfig (with playerRowPrefab, used as the dropdown/input style source) is not assigned.", "OK"); return; }
        var existing = canvas.transform.Find("CollectionWindow");
        if (existing != null)
        {
            if (!EditorUtility.DisplayDialog("Collection window", "CollectionWindow already exists. Replace it (manual edits to it are lost)?", "Replace", "Cancel")) return;
            Undo.DestroyObjectImmediate(existing.gameObject);
        }
        var rowObject = new SerializedObject(config.playerRowPrefab);
        var dropdown = rowObject.FindProperty("factionDropdown").objectReferenceValue as TMP_Dropdown;
        var input = rowObject.FindProperty("nicknameField").objectReferenceValue as TMP_InputField;
        if (dropdown == null || input == null) { EditorUtility.DisplayDialog("Collection window", "PlayerRow prefab has no faction dropdown / nickname field to copy the style from.", "OK"); return; }
        var font = GameAssetCatalog.Load<TMP_FontAsset>("Fonts/GameMenuFont");

        var cell = LoadOrCreatePrefab("CollectionCard", () => BuildCell(font).gameObject).GetComponent<CollectionCardCellView>();
        var row = LoadOrCreatePrefab("CollectionDeckRow", () => BuildRow(font).gameObject).GetComponent<CollectionDeckRowView>();
        var window = BuildWindow(canvas.transform, cell, row, dropdown, input, font);
        Undo.RegisterCreatedObjectUndo(window.gameObject, "Build Collection Window");
        menuObject.FindProperty("collectionScreens").objectReferenceValue = window;
        menuObject.ApplyModifiedProperties();
        EditorSceneManager.MarkSceneDirty(menu.gameObject.scene);
        Selection.activeObject = window.gameObject;
        Debug.Log("Collection window built under " + canvas.name + ". Save the scene.", window);
    }

    private static GameObject LoadOrCreatePrefab(string name, System.Func<GameObject> create)
    {
        string path = PrefabFolder + "/" + name + ".prefab";
        var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (loaded != null) return loaded;
        if (!AssetDatabase.IsValidFolder(PrefabFolder)) AssetDatabase.CreateFolder("Assets/Prefabs/UI", "Collection");
        var root = create();
        var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return saved;
    }

    // ---- window ----------------------------------------------------------------------------------
    // Layout is authored in the scene canvas reference space (1280x720), top-left anchored.
    internal static CollectionScreensUI BuildWindow(Transform parent, CollectionCardCellView cell, CollectionDeckRowView row,
        TMP_Dropdown dropdownTemplate, TMP_InputField inputTemplate, TMP_FontAsset font)
    {
        var root = Panel(parent, "CollectionWindow"); Stretch(root);
        var view = root.gameObject.AddComponent<CollectionScreensUI>();
        var titlePlaque = Panel(root, "TitlePlaque"); Place(titlePlaque, 360, 8, 560, 44);
        Skin(titlePlaque.GetComponent<Image>(), "Plaque");
        var title = Text(root, "Title", "COLLECTION & DECKS", 376, 13, 528, 32, 24, font);
        title.alignment = TextAlignmentOptions.Center; title.color = InkColor;
        var back = MakeButton(root, "Back", "Back", 1152, 12, 108, 32, 14, font);

        var filters = Panel(root, "Filters"); Place(filters, 16, 62, 240, 170);
        Text(filters, "FactionTitle", "FACTION", 14, 10, 212, 16, 12, font);
        Text(filters, "CategoryTitle", "TYPE", 14, 61, 212, 16, 12, font);
        Text(filters, "OwnershipTitle", "OWNERSHIP", 14, 112, 212, 16, 12, font);
        var faction = CloneDropdown(dropdownTemplate, filters, "Faction", 12, 28, 216);
        var category = CloneDropdown(dropdownTemplate, filters, "Category", 12, 79, 216);
        var ownership = CloneDropdown(dropdownTemplate, filters, "Ownership", 12, 130, 216);

        var detailsScroll = Scroll(root, "CardDetails", 16, 240, 240, 454, false, font, out var detailsContent);
        var detailsLayout = detailsContent.gameObject.AddComponent<VerticalLayoutGroup>();
        detailsLayout.padding = new RectOffset(8, 8, 8, 8); detailsLayout.spacing = 8;
        detailsLayout.childControlWidth = detailsLayout.childControlHeight = true;
        detailsLayout.childForceExpandWidth = true; detailsLayout.childForceExpandHeight = false;
        var art = Rect(detailsContent, "Art"); var artImage = art.gameObject.AddComponent<Image>();
        artImage.preserveAspect = true; artImage.raycastTarget = false;
        art.gameObject.AddComponent<LayoutElement>().preferredHeight = 210;
        var detailsText = Text(detailsContent, "Description", "", 0, 0, 0, 0, 14, font);
        detailsText.alignment = TextAlignmentOptions.TopLeft;

        var cardsScroll = Scroll(root, "Cards", 272, 62, 624, 632, true, font, out var gridContent);
        var grid = gridContent.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(140, 194); grid.spacing = new Vector2(8, 8);
        grid.padding = new RectOffset(6, 6, 8, 8);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 4;
        grid.childAlignment = TextAnchor.UpperLeft;

        var deckPanel = Panel(root, "Deck"); Place(deckPanel, 912, 62, 352, 632);
        var deckTitle = Panel(deckPanel, "DeckTitle"); Place(deckTitle, 9, 9, 333, 32);
        Skin(deckTitle.GetComponent<Image>(), "Plaque");
        var deckTitleLabel = Text(deckTitle, "Label", "DECK", 12, 2, 309, 28, 20, font);
        deckTitleLabel.alignment = TextAlignmentOptions.Center; deckTitleLabel.color = InkColor;
        var deckDropdown = CloneDropdown(dropdownTemplate, deckPanel, "DeckPicker", 9, 47, 333);
        var create = MakeButton(deckPanel, "Create", "Create", 9, 83, 333, 26, 14, font);
        var nameInput = CloneInput(inputTemplate, deckPanel, "DeckName", 9, 115, 333, "Deck name");
        var starter = MakeButton(deckPanel, "Starter", "Starter", 9, 151, 103, 27, 14, font);
        var copy = MakeButton(deckPanel, "Copy", "Copy", 119, 151, 103, 27, 14, font);
        var delete = MakeButton(deckPanel, "Delete", "Delete", 228, 151, 115, 27, 14, font);
        var save = MakeButton(deckPanel, "Save", "SAVE DECK", 9, 581, 333, 40, 20, font);
        var points = Text(deckPanel, "Points", "", 9, 188, 333, 20, 15, font);
        Text(deckPanel, "TotalCostTitle", "Total cost", 9, 212, 333, 17, 12, font);
        var icons = new Image[5]; var values = new TMP_Text[5];
        for (int i = 0; i < 5; i++)
        {
            var badge = Rect(deckPanel, "CostIcon" + i); Place(badge, 9 + i * 67, 260, 19, 19);
            icons[i] = badge.gameObject.AddComponent<Image>(); icons[i].preserveAspect = true; icons[i].raycastTarget = false;
            values[i] = Text(deckPanel, "CostValue" + i, "0", 32 + i * 67, 260, 44, 19, 14, font);
        }
        var bar = Panel(deckPanel, "Budget"); Place(bar, 9, 233, 333, 20); Skin(bar.GetComponent<Image>(), "Meter");
        var fill = Panel(bar, "Fill"); fill.anchorMin = Vector2.zero; fill.anchorMax = Vector2.one;
        fill.offsetMin = new Vector2(4, 4); fill.offsetMax = new Vector2(-4, -4);
        fill.GetComponent<Image>().color = new Color(.56f, .55f, .27f);
        Text(deckPanel, "CardsHeader", "Cards", 9, 290, 260, 19, 14, font);
        Text(deckPanel, "CountHeader", "Count", 269, 290, 73, 19, 14, font);
        var deckScroll = Scroll(deckPanel, "DeckCards", 9, 316, 333, 256, true, font, out var deckContent);
        var deckLayout = deckContent.gameObject.AddComponent<VerticalLayoutGroup>();
        deckLayout.spacing = 4; deckLayout.childControlWidth = deckLayout.childControlHeight = true;
        deckLayout.childForceExpandWidth = true; deckLayout.childForceExpandHeight = false;
        var checks = Text(deckContent, "Checks", "", 0, 0, 0, 0, 12, font);
        checks.alignment = TextAlignmentOptions.TopLeft;

        var status = Text(root, "Status", "", 272, 696, 992, 18, 13, font);

        var unsaved = Modal(root, "UnsavedModal", font, out _);
        Text(unsaved, "Title", "Unsaved changes", 440, 280, 400, 32, 20, font).alignment = TextAlignmentOptions.Center;
        var unsavedSave = MakeButton(unsaved, "Save", "Save", 440, 328, 120, 32, 14, font);
        var unsavedDiscard = MakeButton(unsaved, "Discard", "Discard", 580, 328, 120, 32, 14, font);
        var unsavedCancel = MakeButton(unsaved, "Cancel", "Cancel", 720, 328, 120, 32, 14, font);
        var confirm = Modal(root, "ConfirmModal", font, out _);
        var confirmLabel = Text(confirm, "Message", "", 440, 280, 400, 54, 18, font); confirmLabel.alignment = TextAlignmentOptions.Center;
        var confirmAccept = MakeButton(confirm, "Delete", "Delete", 440, 355, 190, 32, 14, font);
        var confirmCancel = MakeButton(confirm, "Cancel", "Cancel", 650, 355, 190, 32, 14, font);
        unsaved.gameObject.SetActive(false); confirm.gameObject.SetActive(false);

        var so = new SerializedObject(view);
        void Set(string field, Object value) { so.FindProperty(field).objectReferenceValue = value; }
        Set("factionDropdown", faction); Set("categoryDropdown", category); Set("ownershipDropdown", ownership);
        Set("cardsScroll", cardsScroll); Set("gridContent", gridContent); Set("cellPrefab", cell);
        Set("detailsScroll", detailsScroll); Set("detailsArt", artImage); Set("detailsText", detailsText);
        Set("deckDropdown", deckDropdown); Set("deckNameInput", nameInput);
        Set("createButton", create); Set("starterButton", starter); Set("copyButton", copy); Set("deleteButton", delete);
        Set("saveButton", save); Set("backButton", back);
        Set("pointsLabel", points); Set("statusLabel", status); Set("checksLabel", checks); Set("budgetFill", fill);
        Set("deckScroll", deckScroll); Set("deckContent", deckContent); Set("rowPrefab", row);
        Set("unsavedModal", unsaved.gameObject); Set("unsavedSaveButton", unsavedSave); Set("unsavedDiscardButton", unsavedDiscard); Set("unsavedCancelButton", unsavedCancel);
        Set("confirmModal", confirm.gameObject); Set("confirmLabel", confirmLabel); Set("confirmAcceptButton", confirmAccept); Set("confirmCancelButton", confirmCancel);
        var iconArray = so.FindProperty("costIcons"); iconArray.arraySize = icons.Length;
        var valueArray = so.FindProperty("costValues"); valueArray.arraySize = values.Length;
        for (int i = 0; i < icons.Length; i++)
        { iconArray.GetArrayElementAtIndex(i).objectReferenceValue = icons[i]; valueArray.GetArrayElementAtIndex(i).objectReferenceValue = values[i]; }
        so.ApplyModifiedPropertiesWithoutUndo();
        CollectionUIElements.ApplyWindowSkin(root);
        root.gameObject.SetActive(false);
        return view;
    }

    // ---- prefabs ---------------------------------------------------------------------------------
    // Bare fallback only: the real Assets/Prefabs/UI/Collection/CollectionCard.prefab is authored by hand
    // (nested Card_Army, mask, fonts) and is loaded by LoadOrCreatePrefab; this stub has no card preview.
    internal static CollectionCardCellView BuildCell(TMP_FontAsset font)
    {
        var root = Panel(null, "CollectionCard"); root.sizeDelta = new Vector2(140, 194);
        var host = Rect(root, "Card");
        host.anchorMin = host.anchorMax = new Vector2(.5f, 1); host.pivot = new Vector2(.5f, 1);
        host.anchoredPosition = Vector2.zero; host.sizeDelta = new Vector2(106, 155);
        var info = Text(root, "Info", "", 1, 158, 138, 15, 12, font);
        info.alignment = TextAlignmentOptions.Center;
        var add = MakeButton(root, "Add", "", 8, 176, 124, 18, 12, font);
        var view = root.gameObject.AddComponent<CollectionCardCellView>();
        var so = new SerializedObject(view);
        so.FindProperty("cardHost").objectReferenceValue = host;
        so.FindProperty("infoLabel").objectReferenceValue = info;
        so.FindProperty("addButton").objectReferenceValue = add;
        so.ApplyModifiedPropertiesWithoutUndo();
        return view;
    }

    internal static CollectionDeckRowView BuildRow(TMP_FontAsset font)
    {
        var root = Rect(null, "CollectionDeckRow");
        var layout = root.gameObject.AddComponent<LayoutElement>(); layout.preferredHeight = 52;
        var header = Rect(root, "Header"); Stretch(header);
        var headerLabel = Text(header, "Label", "", 0, 0, 0, 0, 13, font); Stretch((RectTransform)headerLabel.transform);
        var row = Panel(root, "Row"); Stretch(row);
        var art = Rect(row, "Art"); Place(art, 3, 3, 35, 47);
        var artImage = art.gameObject.AddComponent<Image>(); artImage.preserveAspect = true; artImage.raycastTarget = false;
        var nameButton = MakeButton(row, "Name", "", 43, 3, 184, 27, 14, font);
        var cost = Text(row, "Cost", "", 46, 32, 153, 16, 12, font);
        var minus = MakeButton(row, "Minus", "−", 235, 16, 23, 23, 14, font);
        var count = Text(row, "Count", "", 260, 16, 27, 23, 15, font); count.alignment = TextAlignmentOptions.Center;
        var plus = MakeButton(row, "Plus", "+", 289, 16, 23, 23, 14, font);
        var view = root.gameObject.AddComponent<CollectionDeckRowView>();
        var so = new SerializedObject(view);
        so.FindProperty("layout").objectReferenceValue = layout;
        so.FindProperty("headerRoot").objectReferenceValue = header.gameObject;
        so.FindProperty("rowRoot").objectReferenceValue = row.gameObject;
        so.FindProperty("headerLabel").objectReferenceValue = headerLabel;
        so.FindProperty("nameLabel").objectReferenceValue = nameButton.GetComponentInChildren<TMP_Text>();
        so.FindProperty("costLabel").objectReferenceValue = cost;
        so.FindProperty("countLabel").objectReferenceValue = count;
        so.FindProperty("art").objectReferenceValue = artImage;
        so.FindProperty("nameButton").objectReferenceValue = nameButton;
        so.FindProperty("minusButton").objectReferenceValue = minus;
        so.FindProperty("plusButton").objectReferenceValue = plus;
        so.ApplyModifiedPropertiesWithoutUndo();
        CollectionUIElements.ApplyDeckRowSkin(root);
        return view;
    }

    // ---- primitives ------------------------------------------------------------------------------
    private static RectTransform Rect(Transform parent, string name)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        if (parent != null) rect.SetParent(parent, false);
        return rect;
    }
    private static void Place(RectTransform r, float x, float y, float w, float h)
    { r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1); r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h); }
    private static void Stretch(RectTransform r)
    { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero; }
    private static RectTransform Panel(Transform parent, string name)
    {
        var r = Rect(parent, name); var image = r.gameObject.AddComponent<Image>();
        image.color = Color.clear;
        if (name == "CollectionWindow") Skin(image, "Panel_Outer");
        else if (name == "Filters" || name == "CardDetails" || name == "Cards" || name == "Deck" || name == "Window")
            Skin(image, "Panel_Inset");
        return r;
    }
    private static readonly Color InkColor = CollectionUIElements.CollectionInk;
    private static void Skin(Image image, string asset)
    { CollectionUIElements.ApplySurface(image, asset); }
    private static TMP_Text Text(Transform parent, string name, string value, float x, float y, float w, float h, float size, TMP_FontAsset font)
    {
        var r = Rect(parent, name); if (w > 0 || h > 0) Place(r, x, y, w, h);
        var text = r.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) text.font = font;
        text.text = value; text.fontSize = size; text.color = TextColor;
        text.raycastTarget = false; text.textWrappingMode = TextWrappingModes.Normal;
        return text;
    }
    private static Button MakeButton(Transform parent, string name, string label, float x, float y, float w, float h, float size, TMP_FontAsset font)
    {
        var r = Panel(parent, name); Place(r, x, y, w, h);
        var image = r.GetComponent<Image>();
        bool primary = name == "Save" || name == "Back";
        Skin(image, primary ? "Plaque" : "Button");
        var button = r.gameObject.AddComponent<Button>(); button.targetGraphic = image;
        var text = Text(r, "Label", label, 3, 1, w - 6, h - 2, size, font); text.alignment = TextAlignmentOptions.Center;
        text.color = primary ? InkColor : TextColor;
        CollectionUIElements.ApplyControlsSkin(r);
        return button;
    }
    private static RectTransform Modal(Transform parent, string name, TMP_FontAsset font, out Image backdrop)
    {
        var r = Rect(parent, name); Stretch(r);
        backdrop = r.gameObject.AddComponent<Image>(); backdrop.color = new Color(0, 0, 0, .75f);
        var card = Panel(r, "Window"); Place(card, 420, 250, 440, 170);
        card.anchorMin = card.anchorMax = new Vector2(.5f, .5f); card.pivot = new Vector2(.5f, .5f); card.anchoredPosition = Vector2.zero;
        return r;
    }
    private static ScrollRect Scroll(Transform parent, string name, float x, float y, float w, float h, bool scrollbar, TMP_FontAsset font, out RectTransform content)
    {
        var root = Panel(parent, name); Place(root, x, y, w, h);
        var viewport = Rect(root, "Viewport"); Stretch(viewport); viewport.gameObject.AddComponent<RectMask2D>();
        if (name == "Cards" || name == "CardDetails")
        { viewport.offsetMin = new Vector2(8, 8); viewport.offsetMax = new Vector2(scrollbar ? -20 : -8, -8); }
        else if (scrollbar) viewport.offsetMax = new Vector2(-12, 0);
        content = Rect(viewport, "Content");
        content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0, 1);
        content.sizeDelta = Vector2.zero;
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = root.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport; scroll.content = content;
        scroll.scrollSensitivity = 3f;
        scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        if (scrollbar)
        {
            var track = Panel(root, "Scrollbar"); Place(track, w - 11, 12, 6, h - 24);
            Skin(track.GetComponent<Image>(), "ScrollbarTrack");
            var area = Rect(track, "SlidingArea"); Stretch(area);
            var handle = Panel(area, "Handle"); Stretch(handle);
            Skin(handle.GetComponent<Image>(), "ScrollbarHandle");
            var bar = track.gameObject.AddComponent<Scrollbar>();
            bar.direction = Scrollbar.Direction.BottomToTop; bar.handleRect = handle; bar.targetGraphic = handle.GetComponent<Image>();
            scroll.verticalScrollbar = bar; scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }
        return scroll;
    }
    private static TMP_Dropdown CloneDropdown(TMP_Dropdown template, Transform parent, string name, float x, float y, float w)
    {
        var copy = Object.Instantiate(template.gameObject, parent, false); copy.name = name; copy.SetActive(true);
        var dropdown = copy.GetComponent<TMP_Dropdown>();
        // The PlayerRow template carries a faction-logo icon that only PlayerRowUI fills in; here it would be a blank square.
        var logo = copy.transform.Find("FactionLogo");
        if (logo != null) Object.DestroyImmediate(logo.gameObject);
        Place((RectTransform)copy.transform, x, y, w, 30);
        dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent(); dropdown.ClearOptions();
        if (dropdown.captionText != null) dropdown.captionText.fontSize = 14;
        if (dropdown.itemText != null) dropdown.itemText.fontSize = 13;
        CollectionUIElements.ApplyControlsSkin(copy.transform);
        return dropdown;
    }
    private static TMP_InputField CloneInput(TMP_InputField template, Transform parent, string name, float x, float y, float w, string placeholder)
    {
        var copy = Object.Instantiate(template.gameObject, parent, false); copy.name = name; copy.SetActive(true);
        var input = copy.GetComponent<TMP_InputField>();
        Place((RectTransform)copy.transform, x, y, w, 30);
        input.onValueChanged = new TMP_InputField.OnChangeEvent(); input.onEndEdit = new TMP_InputField.SubmitEvent();
        input.text = "";
        if (input.textComponent != null) input.textComponent.fontSize = 14;
        if (input.placeholder is TMP_Text hint) { hint.text = placeholder; hint.fontSize = 14; }
        CollectionUIElements.ApplyControlsSkin(copy.transform);
        return input;
    }
}
#endif




