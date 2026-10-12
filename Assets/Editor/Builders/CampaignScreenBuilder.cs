#if UNITY_EDITOR
using System.Collections.Generic;
using Game.Campaign;
using Game.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Editor-only authoring helper: builds the whole Campaign screen (console, map panel, sidebar, footer and the
// four modal windows) as ordinary scene objects under a "PlanetaryCampaign" canvas, plus two row prefabs for
// the lists filled at run time. Afterwards every object is a regular, hand-editable part of the scene;
// CampaignUI only binds listeners and fills text, it creates no panels. Region meshes are still generated at
// run time (CampaignMapView) and are the only part that is not visible in the editor.
public static class CampaignScreenBuilder
{
    private const string PrefabFolder = "Assets/Prefabs/UI/Campaign";
    private static readonly Color Cream = CollectionUIElements.CollectionText;
    private static readonly Color Ink = CollectionUIElements.CollectionInk;
    private static TMP_FontAsset font;
    private static Sprite endTurnSprite, actionSprite;

    [MenuItem("Tools/UI/Build Campaign Screen")]
    private static void Build()
    {
        var ui = Object.FindAnyObjectByType<CampaignUI>();
        if (ui == null) { EditorUtility.DisplayDialog("Campaign screen", "Open the Campaign scene first (no CampaignUI found).", "OK"); return; }
        var existing = GameObject.Find("PlanetaryCampaign");
        if (existing != null)
        {
            if (!EditorUtility.DisplayDialog("Campaign screen", "PlanetaryCampaign already exists. Replace it (manual edits to it are lost)?", "Replace", "Cancel")) return;
            Undo.DestroyObjectImmediate(existing);
        }
        var so = new SerializedObject(ui);
        endTurnSprite = so.FindProperty("endTurnSprite").objectReferenceValue as Sprite;
        actionSprite = so.FindProperty("actionButtonSprite").objectReferenceValue as Sprite;
        font = Resources.Load<TMP_FontAsset>("Fonts/GameMenuFont");
        var frame = Resources.Load<Sprite>("UI/Panels/Campaign/MetalFrameRounded");
        var metal = Resources.Load<Sprite>("UI/Backgrounds/Campaign/ConsoleMetalClean") ?? Resources.Load<Sprite>("UI/Backgrounds/Campaign/ConsoleMetal");

        var neighborRow = LoadOrCreatePrefab("CampaignNeighborRow", BuildNeighborRow).GetComponent<CampaignNeighborRowView>();
        var deckRow = LoadOrCreatePrefab("CampaignDeckRow", BuildDeckRow).GetComponent<CampaignDeckRowView>();

        var canvas = Rect(null, "PlanetaryCampaign");
        var canvasComponent = canvas.gameObject.AddComponent<Canvas>();
        canvasComponent.renderMode = RenderMode.ScreenSpaceOverlay; canvasComponent.sortingOrder = 200;
        var scaler = canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
        canvas.gameObject.AddComponent<GraphicRaycaster>();
        Undo.RegisterCreatedObjectUndo(canvas.gameObject, "Build Campaign Screen");

        var background = Panel(canvas, "PlanetBackground"); Stretch(background);
        var backgroundImage = background.GetComponent<Image>(); backgroundImage.sprite = metal; backgroundImage.color = new Color(.82f, .82f, .82f);
        var console = Panel(canvas, "CampaignConsole"); Place(console, 26, 18, 1868, 1040);
        CollectionUIElements.ApplyCampaignSurface(console, "Panel_Outer"); console.GetComponent<Image>().raycastTarget = false;

        var header = Panel(console, "CampaignHeader"); Place(header, 24, 16, 1376, 70);
        CollectionUIElements.ApplyCampaignSurface(header, "Plaque"); header.GetComponent<Image>().raycastTarget = false;
        var heading = Text(header, "Heading", "PLANETARY CAMPAIGN", 28, 8, 1320, 52, 31, Ink, true, TextAlignmentOptions.Center);
        var menu = MakeButton(console, "MenuButton", "MENU", 1646, 23, 176, 48, 22, "Plaque");
        menu.GetComponentInChildren<TMP_Text>().color = Ink;

        // Map panel: terrain meshes are generated under "Regions" at run time.
        var surface = Panel(console, "PlanetSurface"); Place(surface, 24, 100, 1376, 768);
        var surfaceImage = surface.GetComponent<Image>(); surfaceImage.sprite = metal; surfaceImage.color = new Color(.82f, .82f, .82f); surfaceImage.raycastTarget = false;
        surface.gameObject.AddComponent<Mask>();
        var regions = Rect(surface, "Regions"); Stretch(regions);
        regions.offsetMin = new Vector2(12, 12); regions.offsetMax = new Vector2(-12, -12);
        regions.gameObject.AddComponent<RectMask2D>();
        var map = regions.gameObject.AddComponent<CampaignMapView>();
        var attackDirection = Rect(regions, "AttackDirection"); Stretch(attackDirection);
        var attackGraphic = attackDirection.gameObject.AddComponent<CampaignArrowGraphic>(); attackGraphic.raycastTarget = false;
        var mapProperties = new SerializedObject(map);
        mapProperties.FindProperty("arrow").objectReferenceValue = attackGraphic; mapProperties.ApplyModifiedPropertiesWithoutUndo();
        var clouds = Rect(surface, "AtmosphericClouds"); Stretch(clouds);
        var cloudImage = clouds.gameObject.AddComponent<RawImage>();
        cloudImage.texture = Resources.Load<Texture2D>("Campaign/Effects/AtmosphericClouds");
        cloudImage.uvRect = new Rect(.04f, .04f, .92f, .92f);
        cloudImage.color = new Color(.90f, .87f, .81f, .94f); cloudImage.raycastTarget = false;
        Frame(surface, frame);
        var hoverLabel = Text(surface, "HoverLabel", "", 30, 24, 1310, 34, 19, Cream);

        // Sidebar
        var sidebar = Panel(console, "RegionInformation"); Place(sidebar, 1414, 100, 430, 768);
        CollectionUIElements.ApplyCampaignSurface(sidebar, "Panel_Inset"); sidebar.GetComponent<Image>().raycastTarget = false;
        Plaque(sidebar, "TerritoriesPlaque", "TERRITORIES", 24, 18, 382, 46, 26);
        var standings = new List<RectTransform>(); var standingRefs = new List<(Image logo, TMP_Text name, TMP_Text count)>();
        for (int i = 0; i < 3; i++)
        {
            var row = Panel(sidebar, "FactionStanding" + i); Place(row, 28, 82 + 55 * i, 374, 48);
            CollectionUIElements.ApplyCampaignSurface(row, "Button"); row.GetComponent<Image>().raycastTarget = false;
            var logo = LogoImage(row, "FactionLogo", 12, 6, 36);
            var name = Text(row, "Name", "Faction", 60, 8, 232, 32, 22, Cream); NoWrap(name);
            var count = Text(row, "Count", "0", 298, 8, 58, 32, 25, Cream, false, TextAlignmentOptions.Right);
            standings.Add(row); standingRefs.Add((logo, name, count));
        }
        Divider(sidebar, 28, 253, 374);
        var sidebarTitle = Plaque(sidebar, "SidebarTitle", "SELECT A REGION", 24, 268, 382, 62, 25);

        var noSelection = Rect(sidebar, "NoSelectionPanel"); Stretch(noSelection);
        Text(noSelection, "Hint", "Select a territory to see its owner and neighboring regions.\n\nSelect your territory to highlight available attacks.", 36, 356, 358, 154, 22, Cream);
        var humanLogo = LogoImage(noSelection, "HumanLogo", 143, 534, 144);
        var humanName = Text(noSelection, "HumanFactionName", "Faction", 30, 694, 370, 34, 24, Cream, false, TextAlignmentOptions.Center);

        var regionPanel = Rect(sidebar, "RegionPanel"); Stretch(regionPanel);
        var preview = Rect(regionPanel, "RegionTerrainPreview"); Place(preview, 30, 344, 370, 122);
        var terrain = preview.gameObject.AddComponent<RawImage>(); terrain.raycastTarget = false;
        preview.gameObject.AddComponent<Mask>();
        Frame(preview, frame);
        Text(regionPanel, "OwnerCaption", "OWNER", 30, 482, 88, 34, 20, Cream);
        var ownerLogo = LogoImage(regionPanel, "OwnerLogo", 122, 478, 36);
        var ownerName = Text(regionPanel, "OwnerName", "Faction", 170, 482, 230, 34, 23, Cream);
        Plaque(regionPanel, "NeighborsPlaque", "NEIGHBORING REGIONS", 28, 530, 374, 34, 20);
        var neighbors = Scroll(regionPanel, "Neighbors", 28, 574, 374, 106, new Color(0, 0, 0, .32f));
        var attack = MakeButton(regionPanel, "AttackButton", "ATTACK REGION", 28, 704, 374, 48, 22, "Action");
        var unavailable = Text(regionPanel, "AttackUnavailable", "Attack unavailable this turn.", 30, 704, 370, 48, 19, Cream);

        // Footer
        var footer = Panel(console, "CampaignHistory"); Place(footer, 24, 889, 1820, 130);
        CollectionUIElements.ApplyCampaignSurface(footer, "Panel_Inset"); footer.GetComponent<Image>().raycastTarget = false;
        var status = Text(footer, "Status", "", 28, 16, 880, 32, 23, Cream, true);
        Divider(footer, 28, 54, 1570);
        Plaque(footer, "RecentBattlesPlaque", "RECENT BATTLES", 28, 64, 205, 46, 18);
        var noBattles = Text(footer, "NoBattles", "No battles recorded yet.", 248, 68, 650, 30, 19, Cream);
        var history = new List<TMP_Text>();
        for (int i = 0; i < 4; i++)
        {
            var entry = Text(footer, "HistoryEntry" + i, "", 248 + (i % 2) * 665, 64 + (i / 2) * 25, 650, 24, 17, Cream); NoWrap(entry);
            history.Add(entry);
        }
        var summary = Text(footer, "Summary", "", 950, 17, 820, 34, 18, Cream);
        var endTurn = MakeButton(footer, "EndTurn", endTurnSprite == null ? "END TURN" : "", 1684, 9, 112, 112, 22, "Action");
        // This sprite already contains the action text.
        var endTurnImage = endTurn.GetComponent<Image>(); endTurnImage.sprite = endTurnSprite; endTurnImage.type = Image.Type.Simple; endTurnImage.preserveAspect = true;
        var resume = MakeButton(footer, "ResumeBattle", "RESUME BATTLE", 1604, 38, 192, 56, 22, "Action");

        // Modal windows (hidden; open each one in the hierarchy to edit it)
        var backdrop = Panel(canvas, "ModalBackdrop"); Stretch(backdrop);
        backdrop.GetComponent<Image>().color = new Color(0, 0, 0, .8f);
        var confirm = Window(backdrop, "ConfirmAttackWindow", "CONFIRM ATTACK");
        var confirmText = Text(confirm, "Text", "", 35, 110, 880, 90, 26, Cream);
        var confirmChoose = MakeButton(confirm, "ChooseDeck", "Choose Deck", 35, 570, 400, 56, 22, "Action");
        var confirmCancel = MakeButton(confirm, "Cancel", "Cancel", 490, 570, 400, 56, 22, "Action");
        var deck = Window(backdrop, "DeckSelectionWindow", "CAMPAIGN BATTLE");
        var deckInfo = Text(deck, "Info", "", 35, 92, 880, 125, 20, Cream);
        var deckList = Scroll(deck, "SavedDecks", 35, 232, 880, 310, new Color(0, 0, 0, .07f));
        var deckEmpty = Text(deck, "NoDecks", "No saved decks for this faction. Create one in Collection / Decks.", 35, 240, 880, 70, 20, Cream);
        var deckStart = MakeButton(deck, "Start", "Start Battle", 35, 584, 415, 52, 22, "Action");
        var deckCancel = MakeButton(deck, "Cancel", "Cancel", 490, 584, 415, 52, 22, "Action");
        var deckAuto = MakeButton(deck, "TestAutoResolve", "Test Auto Resolve (no collection rewards)", 35, 648, 870, 36, 16, "Action");
        var result = Window(backdrop, "ResultWindow", "CAMPAIGN BATTLE RESULT");
        var resultText = Text(result, "Text", "", 35, 120, 880, 390, 25, Cream);
        var resultContinue = MakeButton(result, "Continue", "Continue", 35, 580, 880, 58, 22, "Action");
        var error = Window(backdrop, "ErrorWindow", "CAMPAIGN ERROR");
        var errorText = Text(error, "Text", "", 35, 120, 880, 360, 23, Cream);
        var errorRetry = MakeButton(error, "Retry", "Retry", 35, 570, 420, 56, 22, "Action");
        var errorMenu = MakeButton(error, "MainMenu", "Main Menu", 485, 570, 420, 56, 22, "Action");
        foreach (var window in new[] { confirm, deck, result, error }) window.gameObject.SetActive(false);
        backdrop.gameObject.SetActive(false);

        foreach (var t in canvas.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 5;

        // Bind everything to CampaignUI.
        void Set(string field, Object value) => so.FindProperty(field).objectReferenceValue = value;
        Set("surfaceImage", surfaceImage); Set("cloudImage", cloudImage); Set("map", map);
        Set("heading", heading); Set("hoverLabel", hoverLabel); Set("menuButton", menu);
        var rows = so.FindProperty("standings"); rows.arraySize = standings.Count;
        for (int i = 0; i < standings.Count; i++)
        {
            var element = rows.GetArrayElementAtIndex(i);
            element.FindPropertyRelative("root").objectReferenceValue = standings[i].gameObject;
            element.FindPropertyRelative("logo").objectReferenceValue = standingRefs[i].logo;
            element.FindPropertyRelative("factionName").objectReferenceValue = standingRefs[i].name;
            element.FindPropertyRelative("count").objectReferenceValue = standingRefs[i].count;
        }
        Set("sidebarTitle", sidebarTitle); Set("noSelectionPanel", noSelection.gameObject); Set("regionPanel", regionPanel.gameObject);
        Set("humanLogo", humanLogo); Set("humanFactionName", humanName); Set("terrainPreview", terrain);
        Set("ownerLogo", ownerLogo); Set("ownerName", ownerName); Set("attackUnavailableLabel", unavailable);
        Set("neighborsContent", neighbors); Set("neighborRowPrefab", neighborRow); Set("attackButton", attack);
        Set("statusLabel", status); Set("noBattlesLabel", noBattles); Set("summaryLabel", summary);
        var entries = so.FindProperty("historyEntries"); entries.arraySize = history.Count;
        for (int i = 0; i < history.Count; i++) entries.GetArrayElementAtIndex(i).objectReferenceValue = history[i];
        Set("endTurnButton", endTurn); Set("resumeButton", resume);
        Set("modalBackdrop", backdrop.gameObject);
        Set("confirmWindow", confirm.gameObject); Set("deckWindow", deck.gameObject); Set("resultWindow", result.gameObject); Set("errorWindow", error.gameObject);
        Set("confirmText", confirmText); Set("deckInfo", deckInfo); Set("deckEmptyLabel", deckEmpty); Set("resultText", resultText); Set("errorText", errorText);
        Set("confirmChooseDeck", confirmChoose); Set("confirmCancel", confirmCancel); Set("deckStart", deckStart); Set("deckCancel", deckCancel);
        Set("deckAutoResolve", deckAuto); Set("resultContinue", resultContinue); Set("errorRetry", errorRetry); Set("errorMenu", errorMenu);
        Set("deckListContent", deckList); Set("deckRowPrefab", deckRow);
        so.ApplyModifiedProperties();
        EditorSceneManager.MarkSceneDirty(ui.gameObject.scene);
        Selection.activeObject = canvas.gameObject;
        Debug.Log("Campaign screen built under PlanetaryCampaign. Save the scene.", canvas.gameObject);
    }

    // ---- row prefabs -----------------------------------------------------------------------------
    private static GameObject LoadOrCreatePrefab(string name, System.Func<GameObject> create)
    {
        string path = PrefabFolder + "/" + name + ".prefab";
        var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (loaded != null) return loaded;
        if (!AssetDatabase.IsValidFolder(PrefabFolder)) AssetDatabase.CreateFolder("Assets/Prefabs/UI", "Campaign");
        var go = create();
        var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
        Object.DestroyImmediate(go);
        return saved;
    }
    private static GameObject BuildNeighborRow()
    {
        var root = Rect(null, "CampaignNeighborRow"); Place(root, 0, 0, 352, 30);
        var dot = Panel(root, "OwnerColor"); Place(dot, 12, 10, 8, 8); dot.GetComponent<Image>().raycastTarget = false;
        var label = Text(root, "Label", "Region", 30, 0, 308, 30, 20, Cream); NoWrap(label);
        var view = root.gameObject.AddComponent<CampaignNeighborRowView>();
        var so = new SerializedObject(view);
        so.FindProperty("dot").objectReferenceValue = dot.GetComponent<Image>(); so.FindProperty("label").objectReferenceValue = label;
        so.ApplyModifiedPropertiesWithoutUndo();
        return root.gameObject;
    }
    private static GameObject BuildDeckRow()
    {
        var root = Rect(null, "CampaignDeckRow"); Place(root, 0, 0, 850, 105);
        var button = MakeButton(root, "Select", "Deck", 8, 6, 830, 42, 20, "Action");
        var status = Text(root, "Status", "", 12, 50, 820, 52, 16, Cream);
        var view = root.gameObject.AddComponent<CampaignDeckRowView>();
        var so = new SerializedObject(view);
        so.FindProperty("button").objectReferenceValue = button;
        so.FindProperty("buttonLabel").objectReferenceValue = button.GetComponentInChildren<TMP_Text>();
        so.FindProperty("status").objectReferenceValue = status;
        so.ApplyModifiedPropertiesWithoutUndo();
        return root.gameObject;
    }

    // ---- composite pieces ------------------------------------------------------------------------
    private static RectTransform Window(Transform parent, string name, string title)
    {
        var window = Panel(parent, name); Place(window, 485, 190, 950, 700);
        CollectionUIElements.ApplyCampaignSurface(window, "Panel_Inset");
        Plaque(window, "Title", title, 35, 25, 880, 58, 30);
        return window;
    }
    private static TMP_Text Plaque(Transform parent, string name, string value, float x, float y, float w, float h, float size)
    {
        var plaque = Panel(parent, name); Place(plaque, x, y, w, h);
        CollectionUIElements.ApplyCampaignSurface(plaque, "Plaque"); plaque.GetComponent<Image>().raycastTarget = false;
        return Text(plaque, "Label", value, 18, 4, w - 36, h - 8, size, Ink, true, TextAlignmentOptions.Center);
    }
    private static void Divider(Transform parent, float x, float y, float width)
    {
        var rule = Panel(parent, "Divider"); Place(rule, x, y, width, 1);
        var image = rule.GetComponent<Image>(); image.color = new Color(.60f, .43f, .23f, .65f); image.raycastTarget = false;
    }
    private static void Frame(RectTransform parent, Sprite frame)
    {
        var rect = Rect(parent, "MetalBorder"); Stretch(rect);
        var image = rect.gameObject.AddComponent<Image>(); image.sprite = frame;
        image.type = Image.Type.Sliced; image.fillCenter = false; image.pixelsPerUnitMultiplier = 4; image.raycastTarget = false;
    }
    private static Image LogoImage(Transform parent, string name, float x, float y, float size)
    {
        var rect = Rect(parent, name); Place(rect, x, y, size, size);
        var image = rect.gameObject.AddComponent<Image>(); image.preserveAspect = true; image.raycastTarget = false;
        return image;
    }
    // style: "Action" = authored action-button sprite, "Plaque" = campaign plaque of the same size.
    private static Button MakeButton(Transform parent, string name, string label, float x, float y, float w, float h, float size, string style)
    {
        var rect = Panel(parent, name); Place(rect, x, y, w, h);
        var image = rect.GetComponent<Image>(); image.color = Color.white;
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
        var text = Text(rect, "Label", label, 5, 1, w - 10, h - 2, size, Cream, true, TextAlignmentOptions.Center);
        CollectionUIElements.ApplyControlsSkin(rect);
        if (style == "Plaque") CollectionUIElements.ApplyCampaignSurface(rect, "Plaque");
        else { image.sprite = actionSprite; image.color = Color.white; }
        text.color = style == "Plaque" || actionSprite != null ? Ink : Cream;
        return button;
    }
    private static RectTransform Scroll(Transform parent, string name, float x, float y, float w, float h, Color background)
    {
        var root = Panel(parent, name); Place(root, x, y, w, h);
        root.GetComponent<Image>().color = background;
        var viewport = Rect(root, "Viewport"); Stretch(viewport); viewport.gameObject.AddComponent<RectMask2D>();
        viewport.offsetMax = new Vector2(-18, 0);
        var content = Rect(viewport, "Content"); Place(content, 0, 0, w - 22, 1);
        var scroll = root.gameObject.AddComponent<ScrollRect>(); scroll.viewport = viewport; scroll.content = content;
        scroll.scrollSensitivity = 3f; scroll.horizontal = false; scroll.vertical = true; scroll.movementType = ScrollRect.MovementType.Clamped;
        var track = Panel(root, "Scrollbar"); Place(track, w - 16, 2, 14, h - 4);
        track.GetComponent<Image>().color = new Color(.1f, .13f, .11f);
        var area = Rect(track, "SlidingArea"); Stretch(area);
        var handle = Panel(area, "Handle"); Stretch(handle);
        handle.GetComponent<Image>().color = new Color(.36f, .48f, .34f);
        var scrollbar = track.gameObject.AddComponent<Scrollbar>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop; scrollbar.handleRect = handle; scrollbar.targetGraphic = handle.GetComponent<Image>();
        scroll.verticalScrollbar = scrollbar; scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        return content;
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
        var r = Rect(parent, name); r.gameObject.AddComponent<Image>().color = Color.clear;
        return r;
    }
    private static TMP_Text Text(Transform parent, string name, string value, float x, float y, float w, float h, float size, Color color,
        bool bold = false, TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft)
    {
        var r = Rect(parent, name); Place(r, x, y, w, h);
        var text = r.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) text.font = font;
        text.text = value; text.fontSize = size; text.color = color; text.alignment = alignment;
        if (bold) text.fontStyle = FontStyles.Bold;
        text.raycastTarget = false; text.textWrappingMode = TextWrappingModes.Normal;
        return text;
    }
    private static void NoWrap(TMP_Text text)
    { text.textWrappingMode = TextWrappingModes.NoWrap; text.overflowMode = TextOverflowModes.Ellipsis; }
}
#endif
