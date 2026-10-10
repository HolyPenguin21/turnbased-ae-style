#if UNITY_EDITOR
using Game.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Editor-only authoring helper: builds the collection window as ordinary scene objects under the
// scene canvas (plus two prefabs for the pooled grid cell and deck row). After it runs the
// hierarchy is a regular, hand-editable part of the scene; nothing is generated at runtime.
public static class CollectionWindowBuilder
{
    private const string PrefabFolder = "Assets/Prefabs/UI/Collection";
    private static readonly Color PanelColor = new Color(.055f, .065f, .07f, .98f);
    private static readonly Color ButtonColor = new Color(.16f, .2f, .17f);
    private static readonly Color TextColor = new Color(.82f, .85f, .8f);

    [MenuItem("Tools/UI/Build Collection Window")]
    private static void Build()
    {
        var menu = Object.FindFirstObjectByType<MainMenuController>();
        if (menu == null) { EditorUtility.DisplayDialog("Collection window", "Open the MainMenu scene first (no MainMenuController found).", "OK"); return; }
        var menuObject = new SerializedObject(menu);
        var panel = menuObject.FindProperty("mainMenuPanel").objectReferenceValue as GameObject;
        var canvas = (panel != null ? panel.GetComponentInParent<Canvas>() : menu.GetComponentInParent<Canvas>());
        if (canvas == null) canvas = Object.FindFirstObjectByType<Canvas>();
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
        var font = Resources.Load<TMP_FontAsset>("Fonts/GameMenuFont");

        var cell = LoadOrCreatePrefab("CollectionCardCell", () => BuildCell(font).gameObject).GetComponent<CollectionCardCellView>();
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
        Text(root, "Title", "COLLECTION & DECKS", 16, 12, 667, 32, 20, font);
        var back = MakeButton(root, "Back", "Back", 1164, 13, 100, 30, 14, font);

        var filters = Panel(root, "Filters"); Place(filters, 16, 59, 240, 111);
        var faction = CloneDropdown(dropdownTemplate, filters, "Faction", 9, 7, 221);
        var category = CloneDropdown(dropdownTemplate, filters, "Category", 9, 40, 221);
        var ownership = CloneDropdown(dropdownTemplate, filters, "Ownership", 9, 73, 221);

        var detailsScroll = Scroll(root, "CardDetails", 16, 180, 240, 492, false, font, out var detailsContent);
        var detailsLayout = detailsContent.gameObject.AddComponent<VerticalLayoutGroup>();
        detailsLayout.padding = new RectOffset(8, 8, 8, 8); detailsLayout.spacing = 8;
        detailsLayout.childControlWidth = detailsLayout.childControlHeight = true;
        detailsLayout.childForceExpandWidth = true; detailsLayout.childForceExpandHeight = false;
        var art = Rect(detailsContent, "Art"); var artImage = art.gameObject.AddComponent<Image>();
        artImage.preserveAspect = true; artImage.raycastTarget = false;
        art.gameObject.AddComponent<LayoutElement>().preferredHeight = 187;
        var detailsText = Text(detailsContent, "Description", "", 0, 0, 0, 0, 14, font);
        detailsText.alignment = TextAlignmentOptions.TopLeft;

        var cardsScroll = Scroll(root, "Cards", 272, 59, 624, 613, true, font, out var gridContent);
        var grid = gridContent.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(144, 200); grid.spacing = new Vector2(7, 7);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 4;
        grid.childAlignment = TextAnchor.UpperLeft;

        var deckPanel = Panel(root, "Deck"); Place(deckPanel, 912, 59, 352, 613);
        var deckDropdown = CloneDropdown(dropdownTemplate, deckPanel, "DeckPicker", 9, 7, 333);
        var create = MakeButton(deckPanel, "Create", "Create", 9, 43, 333, 29, 14, font);
        var nameInput = CloneInput(inputTemplate, deckPanel, "DeckName", 9, 79, 333, "Deck name");
        var starter = MakeButton(deckPanel, "Starter", "Starter", 9, 115, 103, 27, 14, font);
        var copy = MakeButton(deckPanel, "Copy", "Copy", 119, 115, 103, 27, 14, font);
        var delete = MakeButton(deckPanel, "Delete", "Delete", 228, 115, 115, 27, 14, font);
        var save = MakeButton(deckPanel, "Save", "Save Deck", 9, 148, 333, 29, 14, font);
        var points = Text(deckPanel, "Points", "", 9, 185, 333, 20, 15, font);
        Text(deckPanel, "TotalCostTitle", "Total cost", 9, 211, 333, 17, 12, font);
        var icons = new Image[5]; var values = new TMP_Text[5];
        for (int i = 0; i < 5; i++)
        {
            var badge = Rect(deckPanel, "CostIcon" + i); Place(badge, 9 + i * 67, 235, 19, 19);
            icons[i] = badge.gameObject.AddComponent<Image>(); icons[i].preserveAspect = true; icons[i].raycastTarget = false;
            values[i] = Text(deckPanel, "CostValue" + i, "0", 32 + i * 67, 235, 44, 19, 14, font);
        }
        var bar = Panel(deckPanel, "Budget"); Place(bar, 9, 263, 333, 5);
        var fill = Panel(bar, "Fill"); fill.anchorMin = Vector2.zero; fill.anchorMax = Vector2.one; fill.offsetMin = fill.offsetMax = Vector2.zero;
        fill.GetComponent<Image>().color = new Color(.36f, .55f, .3f);
        Text(deckPanel, "CardsHeader", "Cards", 9, 280, 260, 19, 14, font);
        Text(deckPanel, "CountHeader", "Count", 269, 280, 73, 19, 14, font);
        var deckScroll = Scroll(deckPanel, "DeckCards", 9, 305, 333, 299, true, font, out var deckContent);
        var deckLayout = deckContent.gameObject.AddComponent<VerticalLayoutGroup>();
        deckLayout.spacing = 4; deckLayout.childControlWidth = deckLayout.childControlHeight = true;
        deckLayout.childForceExpandWidth = true; deckLayout.childForceExpandHeight = false;
        var checks = Text(deckContent, "Checks", "", 0, 0, 0, 0, 12, font);
        checks.alignment = TextAlignmentOptions.TopLeft;

        var status = Text(root, "Status", "", 272, 680, 992, 32, 13, font);

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
        root.gameObject.SetActive(false);
        return view;
    }

    // ---- prefabs ---------------------------------------------------------------------------------
    internal static CollectionCardCellView BuildCell(TMP_FontAsset font)
    {
        var root = Panel(null, "CollectionCardCell"); root.sizeDelta = new Vector2(144, 200);
        var host = Rect(root, "Card");
        host.anchorMin = host.anchorMax = new Vector2(.5f, 1); host.pivot = new Vector2(.5f, 1);
        host.anchoredPosition = Vector2.zero; host.sizeDelta = new Vector2(139, 155);
        var info = Text(root, "Info", "", 3, 159, 138, 17, 12, font);
        var add = MakeButton(root, "Add", "", 3, 180, 138, 20, 13, font);
        var view = root.gameObject.AddComponent<CollectionCardCellView>();
        var so = new SerializedObject(view);
        so.FindProperty("cardHost").objectReferenceValue = host;
        so.FindProperty("infoLabel").objectReferenceValue = info;
        so.FindProperty("addButton").objectReferenceValue = add;
        so.FindProperty("addLabel").objectReferenceValue = add.GetComponentInChildren<TMP_Text>();
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
        var remove = MakeButton(row, "Remove", "×", 207, 32, 20, 17, 13, font);
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
        so.FindProperty("removeButton").objectReferenceValue = remove;
        so.ApplyModifiedPropertiesWithoutUndo();
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
    { var r = Rect(parent, name); r.gameObject.AddComponent<Image>().color = PanelColor; return r; }
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
        var image = r.GetComponent<Image>(); image.color = ButtonColor;
        var button = r.gameObject.AddComponent<Button>(); button.targetGraphic = image;
        var text = Text(r, "Label", label, 3, 1, w - 6, h - 2, size, font); text.alignment = TextAlignmentOptions.Center;
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
        if (scrollbar) viewport.offsetMax = new Vector2(-12, 0);
        content = Rect(viewport, "Content");
        content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0, 1);
        content.sizeDelta = Vector2.zero;
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = root.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport; scroll.content = content;
        scroll.scrollSensitivity = 1.69f; // wheel speed matches the previous generated scrolls scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        if (scrollbar)
        {
            var track = Panel(root, "Scrollbar"); Place(track, w - 11, 2, 9, h - 4);
            track.GetComponent<Image>().color = new Color(.1f, .13f, .11f);
            var area = Rect(track, "SlidingArea"); Stretch(area);
            var handle = Panel(area, "Handle"); Stretch(handle);
            handle.GetComponent<Image>().color = new Color(.36f, .48f, .34f);
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
        return input;
    }
}
#endif
