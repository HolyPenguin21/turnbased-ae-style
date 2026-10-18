#if UNITY_EDITOR
using Game.Core;
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
        font = GameAssetCatalog.Load<TMP_FontAsset>("Fonts/GameMenuFont");
        var frame = GameAssetCatalog.Load<Sprite>("UI/Panels/Campaign/MetalFrameRounded");
        var metal = GameAssetCatalog.Load<Sprite>("UI/Backgrounds/Campaign/ConsoleMetalClean") ?? GameAssetCatalog.Load<Sprite>("UI/Backgrounds/Campaign/ConsoleMetal");

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
        cloudImage.texture = GameAssetCatalog.Load<Texture2D>("Campaign/Effects/AtmosphericClouds");
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
        var preview = Rect(regionPanel, "RegionTerrainPreview"); Place(preview, 20, 353, 135, 135);
        var terrain = preview.gameObject.AddComponent<RawImage>(); terrain.raycastTarget = false;
        preview.gameObject.AddComponent<Mask>();
        Frame(preview, frame);

        var ownerLogo = LogoImage(regionPanel, "OwnerLogo", 160, 353, 36);
        var ownerName = Text(regionPanel, "OwnerName", "Faction", 200, 353, 200, 36, 23, Cream);
        Plaque(regionPanel, "NeighborsPlaque", "NEIGHBORING REGIONS", 28, 530, 374, 34, 20);
        var neighbors = Scroll(regionPanel, "Neighbors", 28, 574, 374, 106, new Color(0, 0, 0, .32f));

        // Footer
        var footer = Panel(console, "CampaignHistory"); Place(footer, 24, 889, 1820, 130);
        CollectionUIElements.ApplyCampaignSurface(footer, "Panel_Inset"); footer.GetComponent<Image>().raycastTarget = false;
        var status = Text(footer, "Status", "", 28, 16, 880, 32, 23, Cream, true);
        Divider(footer, 28, 54, 1570);
        Plaque(footer, "RecentBattlesPlaque", "RECENT BATTLES", 28, 64, 205, 46, 18);
        var noBattles = Text(footer, "NoBattles", "No battles recorded yet.", 248, 68, 650, 30, 19, Cream);
        var summary = Text(footer, "Summary", "", 950, 17, 820, 34, 18, Cream);
        var endTurn = MakeButton(footer, "EndTurn", endTurnSprite == null ? "END TURN" : "", 1684, 9, 112, 112, 22, "Action");
        // This sprite already contains the action text.
        var endTurnImage = endTurn.GetComponent<Image>(); endTurnImage.sprite = endTurnSprite; endTurnImage.type = Image.Type.Simple; endTurnImage.preserveAspect = true;
        var resume = MakeButton(footer, "ResumeBattle", "RESUME BATTLE", 1604, 38, 192, 56, 22, "Action");

        // Modal windows (hidden; open each one in the hierarchy to edit it)
        var backdrop = Panel(canvas, "ModalBackdrop"); Stretch(backdrop);
        backdrop.GetComponent<Image>().color = new Color(0, 0, 0, .8f);
        var confirm = Window(backdrop, "ConfirmAttackWindow", "CONFIRM ATTACK");
        var confirmText = Text(confirm, "Text", "", 35, 110, 880, 300, 24, Cream);
        var confirmChoose = MakeButton(confirm, "ChooseDeck", "Choose Deck", 35, 570, 400, 56, 22, "Action");
        var confirmCancel = MakeButton(confirm, "Cancel", "Cancel", 490, 570, 400, 56, 22, "Action");
        var deck = Window(backdrop, "DeckSelectionWindow", "CAMPAIGN BATTLE");
        var deckInfo = Text(deck, "Info", "", 35, 92, 880, 212, 20, Cream);
        var deckList = Scroll(deck, "SavedDecks", 35, 320, 880, 222, new Color(0, 0, 0, .07f));
        var deckEmpty = Text(deck, "NoDecks", "No saved decks for this faction. Create one in Collection / Decks.", 35, 330, 880, 70, 20, Cream);
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
        Set("hoverLabel", hoverLabel); Set("menuButton", menu);
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
        Set("ownerLogo", ownerLogo); Set("ownerName", ownerName);
        Set("neighborsContent", neighbors); Set("neighborRowPrefab", neighborRow);
        Set("statusLabel", status); Set("noBattlesLabel", noBattles); Set("summaryLabel", summary);
        Set("endTurnButton", endTurn); Set("resumeButton", resume);
        Set("modalBackdrop", backdrop.gameObject);
        Set("confirmWindow", confirm.gameObject); Set("deckWindow", deck.gameObject); Set("resultWindow", result.gameObject); Set("errorWindow", error.gameObject);
        Set("confirmText", confirmText); Set("deckInfo", deckInfo); Set("deckEmptyLabel", deckEmpty); Set("resultText", resultText); Set("errorText", errorText);
        Set("confirmChooseDeck", confirmChoose); Set("confirmCancel", confirmCancel); Set("deckStart", deckStart); Set("deckCancel", deckCancel);
        Set("deckAutoResolve", deckAuto); Set("resultContinue", resultContinue); Set("errorRetry", errorRetry); Set("errorMenu", errorMenu);
        Set("deckListContent", deckList); Set("deckRowPrefab", deckRow);
        AddReferenceSlots(so);
        so.ApplyModifiedProperties();
        EditorSceneManager.MarkSceneDirty(ui.gameObject.scene);
        Selection.activeObject = canvas.gameObject;
        Debug.Log("Campaign screen built under PlanetaryCampaign. Save the scene.", canvas.gameObject);
    }

    // Adds the reference-layout slots (header plaques, faction bars, neighbor emblems, available attacks,
    // history columns, player emblem, map region markers) to an already built and hand-edited Campaign scene.
    // Idempotent: a slot whose CampaignUI/CampaignMapView field is already assigned is left untouched.
    private static void AddReferenceSlotsMenu()
    {
        var ui = Object.FindAnyObjectByType<CampaignUI>();
        if (ui == null) { EditorUtility.DisplayDialog("Campaign screen", "Open the Campaign scene first (no CampaignUI found).", "OK"); return; }
        var so = new SerializedObject(ui);
        endTurnSprite = so.FindProperty("endTurnSprite").objectReferenceValue as Sprite;
        actionSprite = so.FindProperty("actionButtonSprite").objectReferenceValue as Sprite;
        font = GameAssetCatalog.Load<TMP_FontAsset>("Fonts/GameMenuFont");
        Undo.SetCurrentGroupName("Add Campaign Reference Slots");
        Undo.RecordObject(ui, "Add Campaign Reference Slots");
        AddReferenceSlots(so);
        so.ApplyModifiedProperties();
        EditorSceneManager.MarkSceneDirty(ui.gameObject.scene);
        Debug.Log("Campaign reference slots added. Review the new objects and save the scene.", ui);
    }
    private static void AddReferenceSlots(SerializedObject so)
    {
        T Get<T>(string field) where T : Object => so.FindProperty(field).objectReferenceValue as T;
        bool Missing(string field) => so.FindProperty(field).objectReferenceValue == null;
        void Set(string field, Object value) => so.FindProperty(field).objectReferenceValue = value;
        T Created<T>(T rect) where T : Component
        {
            foreach (var t in rect.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 5;
            Undo.RegisterCreatedObjectUndo(rect.gameObject, "Add Campaign Reference Slots"); return rect;
        }
        void Hide(Component c) { if (c == null) return; Undo.RecordObject(c.gameObject, "Add Campaign Reference Slots"); c.gameObject.SetActive(false); }

        // Header: planet / round / turn faction plaques.
        var headerTransform = ((Component)so.targetObject).transform.root.Find("CampaignConsole/CampaignHeader");
        if (headerTransform != null && Missing("planetNameLabel"))
        {
            var header = (RectTransform)headerTransform;
            var plaques = Created(Rect(header, "HeaderPlaques")); Stretch(plaques);
            var planet = Panel(plaques, "PlanetPlaque"); Place(planet, 0, 0, 560, 70); CollectionUIElements.ApplyCampaignSurface(planet, "Plaque");
            Place(Panel(planet, "Icon"), 16, 9, 52, 52);
            Set("planetNameLabel", Text(planet, "PlanetName", "PLANET", 80, 6, 460, 36, 30, Ink, true));
            Set("campaignIdLabel", Text(planet, "CampaignId", "ID", 80, 42, 460, 22, 16, Ink));
            var round = Panel(plaques, "RoundPlaque"); Place(round, 572, 0, 250, 70); CollectionUIElements.ApplyCampaignSurface(round, "Plaque");
            Place(Panel(round, "Icon"), 16, 9, 52, 52);
            Set("roundLabel", Text(round, "Round", "ROUND 1", 76, 15, 160, 40, 30, Ink, true));
            var faction = Panel(plaques, "TurnFactionPlaque"); Place(faction, 834, 0, 542, 70); CollectionUIElements.ApplyCampaignSurface(faction, "Plaque");
            Set("turnFactionLogo", LogoImage(faction, "Logo", 16, 7, 56));
            Set("turnFactionName", Text(faction, "Name", "FACTION", 84, 6, 440, 36, 30, Ink, true));
            Set("turnFactionCaption", Text(faction, "Caption", "YOUR TURN", 84, 42, 440, 22, 16, Ink));
            foreach (var image in plaques.GetComponentsInChildren<Image>(true)) image.raycastTarget = false;
            var headerImage = header.GetComponent<Image>();
            if (headerImage != null) { Undo.RecordObject(headerImage, "Add Campaign Reference Slots"); headerImage.enabled = false; }
        }

        // Territory bars under each faction name; the bar is stretched across its track by anchors.
        var standings = so.FindProperty("standings");
        for (int i = 0; i < standings.arraySize; i++)
        {
            var element = standings.GetArrayElementAtIndex(i);
            var root = element.FindPropertyRelative("root").objectReferenceValue as GameObject;
            if (root == null || element.FindPropertyRelative("bar").objectReferenceValue != null) continue;
            var track = Created(Panel(root.transform, "TerritoryTrack")); Place(track, 60, 38, 296, 5);
            track.GetComponent<Image>().color = new Color(0, 0, 0, .55f); track.GetComponent<Image>().raycastTarget = false;
            var bar = Panel(track, "Bar"); Stretch(bar);
            var barImage = bar.GetComponent<Image>(); barImage.color = Color.white; barImage.raycastTarget = false;
            element.FindPropertyRelative("bar").objectReferenceValue = barImage;
        }

        var regionPanel = Get<GameObject>("regionPanel");
        if (regionPanel != null && Missing("regionConditionsText"))
        {
            var existing = regionPanel.transform.Find("RegionConditions");
            var text = existing != null ? existing.GetComponent<TMP_Text>()
                : Created(Text(regionPanel.transform, "RegionConditions", "Biome\nSpecialization\nRichness", 160, 397, 240, 84, 18, Cream));
            Set("regionConditionsText", text);
            // Older builder layouts had a full-width preview occupying the new text area.
            var preview = Get<RawImage>("terrainPreview");
            if (preview != null && preview.rectTransform.rect.width > 135)
            {
                Undo.RecordObject(preview.rectTransform, "Add region conditions"); Place(preview.rectTransform, 20, 353, 135, 135);
                var ownerLogo = Get<Image>("ownerLogo");
                if (ownerLogo != null) { Undo.RecordObject(ownerLogo.rectTransform, "Add region conditions"); Place(ownerLogo.rectTransform, 160, 353, 36, 36); }
                var ownerName = Get<TMP_Text>("ownerName");
                if (ownerName != null) { Undo.RecordObject(ownerName.rectTransform, "Add region conditions"); Place(ownerName.rectTransform, 200, 353, 200, 36); }
                Hide(regionPanel.transform.Find("OwnerCaption"));
            }
            var confirm = Get<TMP_Text>("confirmText");
            if (confirm != null) { Undo.RecordObject(confirm.rectTransform, "Add region conditions"); confirm.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 300); }
            // Enlarge only the existing battle summary; preserve the list's bottom edge/buttons.
            var info = Get<TMP_Text>("deckInfo");
            if (info != null) { Undo.RecordObject(info.rectTransform, "Add region conditions"); info.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 212); }
            var list = Get<RectTransform>("deckListContent");
            if (list != null && list.parent != null)
            {
                var scroll = list.parent.parent as RectTransform;
                if (scroll != null) { Undo.RecordObject(scroll, "Add region conditions"); scroll.anchoredPosition = new Vector2(scroll.anchoredPosition.x, -320); scroll.sizeDelta = new Vector2(scroll.sizeDelta.x, 222); }
            }
            var empty = Get<TMP_Text>("deckEmptyLabel");
            if (empty != null) { Undo.RecordObject(empty.rectTransform, "Add region conditions"); empty.rectTransform.anchoredPosition = new Vector2(empty.rectTransform.anchoredPosition.x, -330); }
        }
        if (regionPanel != null && Missing("neighborEmblemsContent"))
        {
            var row = Created(Rect(regionPanel.transform, "NeighborEmblemsRow")); Place(row, 28, 528, 374, 40);
            Text(row, "Caption", "NEIGHBORS", 2, 8, 130, 26, 18, Cream);
            var content = Rect(row, "Emblems"); Place(content, 140, 2, 234, 36);
            var layout = content.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6; layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = layout.childControlHeight = false; layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            Set("neighborEmblemsContent", content);
            Set("neighborEmblemPrefab", LoadOrCreatePrefab("CampaignNeighborEmblem", BuildNeighborEmblem).GetComponent<Image>());
            var neighbors = Get<RectTransform>("neighborsContent");
            // Content -> Viewport -> scroll root; the plaque is its sibling.
            if (neighbors != null && neighbors.parent != null) Hide(neighbors.parent.parent);
            var plaque = regionPanel.transform.Find("NeighborsPlaque"); if (plaque != null) Hide(plaque);
        }
        if (regionPanel != null && Missing("attacksContent"))
        {
            var group = Created(Rect(regionPanel.transform, "AvailableAttacks")); Stretch(group);
            Plaque(group, "AvailableAttacksPlaque", "AVAILABLE ATTACKS", 28, 578, 374, 34, 20);
            var content = Scroll(group, "AttackList", 28, 620, 374, 134, new Color(0, 0, 0, .32f));
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4; layout.childControlWidth = true; layout.childControlHeight = false;
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            Set("attacksContent", content);
            Set("attackRowPrefab", LoadOrCreatePrefab("CampaignAttackRow", BuildAttackRow).GetComponent<CampaignAttackRowView>());
            Set("noAttacksLabel", Text(group, "NoAttacks", "Attack unavailable this turn.", 36, 628, 350, 48, 18, Cream));
        }

        // Footer: status title, history columns and the player's emblem next to End Turn.
        var status = Get<TMP_Text>("statusLabel");
        if (status != null && Missing("statusTitle"))
        {
            var footer = status.transform.parent;
            var icon = Created(Panel(footer, "StatusIcon")); Place(icon, 28, 14, 34, 34); icon.GetComponent<Image>().raycastTarget = false;
            var title = Created(Text(footer, "StatusTitle", "YOUR TURN:", 70, 16, 170, 32, 23, new Color(.93f, .62f, .25f), true)); NoWrap(title);
            Set("statusTitle", title);
            Undo.RecordObject(status.rectTransform, "Add Campaign Reference Slots");
            status.rectTransform.anchoredPosition = new Vector2(246, status.rectTransform.anchoredPosition.y);
            status.rectTransform.sizeDelta = new Vector2(660, status.rectTransform.sizeDelta.y);
        }
        var history = so.FindProperty("historyRows");
        var noBattles = Get<TMP_Text>("noBattlesLabel");
        if (noBattles != null && history.arraySize == 0)
        {
            var table = Created(Rect(noBattles.transform.parent, "HistoryTable")); Place(table, 248, 64, 1330, 50);
            history.arraySize = 4;
            for (int i = 0; i < 4; i++)
            {
                var row = Rect(table, "HistoryRow" + i); Place(row, (i % 2) * 665, (i / 2) * 25, 650, 24);
                var refs = history.GetArrayElementAtIndex(i);
                refs.FindPropertyRelative("root").objectReferenceValue = row.gameObject;
                refs.FindPropertyRelative("round").objectReferenceValue = NoWrap(Text(row, "Round", "Round 1", 0, 0, 70, 24, 17, Cream));
                refs.FindPropertyRelative("attackerLogo").objectReferenceValue = LogoImage(row, "AttackerLogo", 74, 2, 20);
                refs.FindPropertyRelative("attacker").objectReferenceValue = NoWrap(Text(row, "Attacker", "Faction", 98, 0, 170, 24, 17, Cream));
                Text(row, "Arrow", ">", 272, 0, 24, 24, 17, Cream);
                refs.FindPropertyRelative("region").objectReferenceValue = NoWrap(Text(row, "Region", "Region", 300, 0, 200, 24, 17, Cream));
                refs.FindPropertyRelative("outcome").objectReferenceValue = NoWrap(Text(row, "Outcome", "Defended", 504, 0, 140, 24, 17, Cream));
                row.gameObject.SetActive(false);
            }
        }
        var endTurn = Get<Button>("endTurnButton");
        if (endTurn != null && Missing("playerEmblem"))
        {
            var block = Created(Rect(endTurn.transform.parent, "PlayerFaction")); Place(block, 1430, 8, 150, 114);
            Set("playerEmblem", LogoImage(block, "Emblem", 43, 4, 64));
            Set("playerFactionName", NoWrap(Text(block, "Name", "FACTION", 0, 74, 150, 30, 16, Cream, true, TextAlignmentOptions.Center)));
        }

        // Map: emblem + name over each region (instantiated at run time by CampaignMapView).
        var map = Get<CampaignMapView>("map");
        if (map != null)
        {
            var mapProperties = new SerializedObject(map);
            var marker = mapProperties.FindProperty("markerPrefab");
            if (marker.objectReferenceValue == null)
            {
                marker.objectReferenceValue = LoadOrCreatePrefab("CampaignRegionMarker", BuildRegionMarker).GetComponent<CampaignRegionMarkerView>();
                mapProperties.ApplyModifiedProperties();
            }
        }
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
    private static GameObject BuildNeighborEmblem()
    {
        var root = LogoImage(null, "CampaignNeighborEmblem", 0, 0, 32);
        return root.gameObject;
    }
    private static GameObject BuildAttackRow()
    {
        var root = Rect(null, "CampaignAttackRow"); Place(root, 0, 0, 352, 62);
        var background = root.gameObject.AddComponent<Image>(); background.color = new Color(0, 0, 0, .25f); background.raycastTarget = false;
        var preview = Rect(root, "TerrainPreview"); Place(preview, 4, 4, 96, 54);
        var terrain = preview.gameObject.AddComponent<RawImage>(); terrain.raycastTarget = false;
        var logo = LogoImage(root, "OwnerLogo", 106, 15, 32);
        var name = NoWrap(Text(root, "RegionName", "Region", 144, 6, 110, 26, 18, Cream, true));
        var owner = NoWrap(Text(root, "OwnerName", "Owner: Faction", 144, 32, 110, 22, 14, Cream));
        var attack = MakeButton(root, "Attack", "ATTACK", 258, 10, 90, 42, 16, "Action");
        var view = root.gameObject.AddComponent<CampaignAttackRowView>();
        var so = new SerializedObject(view);
        so.FindProperty("terrainPreview").objectReferenceValue = terrain; so.FindProperty("ownerLogo").objectReferenceValue = logo;
        so.FindProperty("regionName").objectReferenceValue = name; so.FindProperty("ownerName").objectReferenceValue = owner;
        so.FindProperty("attackButton").objectReferenceValue = attack;
        so.ApplyModifiedPropertiesWithoutUndo();
        return root.gameObject;
    }
    private static GameObject BuildRegionMarker()
    {
        var root = Rect(null, "CampaignRegionMarker"); root.sizeDelta = new Vector2(160, 66); root.pivot = new Vector2(.5f, .5f);
        var selected = Panel(root, "Selected"); Place(selected, 54, -2, 52, 52);
        selected.GetComponent<Image>().color = new Color(1f, .70f, .26f, .45f); selected.gameObject.SetActive(false);
        var emblem = LogoImage(root, "Emblem", 58, 2, 44);
        var plate = Panel(root, "Plate"); Place(plate, 8, 44, 144, 24); plate.GetComponent<Image>().color = new Color(.05f, .042f, .032f, .6f);
        var plateRim = plate.gameObject.AddComponent<Outline>(); plateRim.effectColor = new Color(.78f, .56f, .27f, .75f); plateRim.effectDistance = new Vector2(1, -1);
        var label = NoWrap(Text(plate, "Label", "REGION", 0, 0, 144, 24, 14, new Color(.95f, .89f, .78f), true, TextAlignmentOptions.Center));
        foreach (var g in root.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
        var view = root.gameObject.AddComponent<CampaignRegionMarkerView>();
        var so = new SerializedObject(view);
        so.FindProperty("emblem").objectReferenceValue = emblem; so.FindProperty("label").objectReferenceValue = label;
        so.FindProperty("selectedState").objectReferenceValue = selected.gameObject;
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
    private static TMP_Text NoWrap(TMP_Text text)
    { text.textWrappingMode = TextWrappingModes.NoWrap; text.overflowMode = TextOverflowModes.Ellipsis; return text; }
}
#endif
