#if UNITY_EDITOR
using Game.Core;
using Game.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Editor-only authoring helper: builds the two "new campaign" popups (faction choice and replace-saved-campaign
// confirmation) as ordinary, hand-editable objects in the MainMenu scene under a "NewCampaignPopups" canvas.
// MainMenuController only shows/hides them and binds button listeners; it creates no UI at run time.
public static class CampaignSetupPopupsBuilder
{
    private static readonly Color Cream = CollectionUIElements.CollectionText;
    private static readonly Color Ink = CollectionUIElements.CollectionInk;
    private static TMP_FontAsset font;

    [MenuItem("Tools/UI/Build Campaign Setup Popups")]
    private static void BuildMenu() => Build(true);

    // Batch entry: Unity -batchmode -executeMethod CampaignSetupPopupsBuilder.BuildAndSave
    public static void BuildAndSave()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/MainMenu.unity", OpenSceneMode.Single);
        if (!Build(false)) { EditorApplication.Exit(1); return; }
        EditorSceneManager.SaveScene(scene);
        EditorApplication.Exit(0);
    }

    private static bool Build(bool interactive)
    {
        var menu = Object.FindAnyObjectByType<MainMenuController>();
        if (menu == null) { Report(interactive, "Open the MainMenu scene first (no MainMenuController found)."); return false; }
        var mainPanel = new SerializedObject(menu).FindProperty("mainMenuPanel").objectReferenceValue as GameObject;
        var canvas = (mainPanel != null ? mainPanel.GetComponentInParent<Canvas>() : menu.GetComponentInParent<Canvas>());
        if (canvas == null) canvas = Object.FindAnyObjectByType<Canvas>();
        if (canvas == null) { Report(interactive, "No Canvas found in the scene."); return false; }
        canvas = canvas.rootCanvas;
        // The popups live in the scene's main (1280x720) canvas; also clears the older standalone 1920x1080 canvas version.
        var old = new System.Collections.Generic.List<GameObject>();
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t.name == "NewCampaignPopups" && t.gameObject.scene == menu.gameObject.scene) old.Add(t.gameObject);
        if (old.Count > 0)
        {
            if (interactive && !EditorUtility.DisplayDialog("Campaign popups", "NewCampaignPopups already exists. Replace it (manual edits to it are lost)?", "Replace", "Cancel")) return false;
            foreach (var o in old) Undo.DestroyObjectImmediate(o);
        }
        font = GameAssetCatalog.Load<TMP_FontAsset>("Fonts/GameMenuFont");
        var so = new SerializedObject(menu);
        var logos = new[]
        {
            so.FindProperty("campaignIronConcordLogo").objectReferenceValue as Sprite,
            so.FindProperty("campaignAshenLogo").objectReferenceValue as Sprite,
            so.FindProperty("campaignVesselsLogo").objectReferenceValue as Sprite,
        };

        var backdrop = Panel(canvas.transform, "NewCampaignPopups"); Stretch(backdrop);
        backdrop.GetComponent<Image>().color = new Color(0, 0, 0, .8f);
        backdrop.SetAsLastSibling();
        Undo.RegisterCreatedObjectUndo(backdrop.gameObject, "Build Campaign Setup Popups");
        var root = backdrop;

        // ---- faction choice ----
        var window = Window(backdrop, "CampaignSetup", "NEW PLANETARY CAMPAIGN");
        Label(window, "Description", "Choose your faction. A new planet has "
            + new Game.Campaign.CampaignGenerationSettings().RegionCount + " regions.", 35, 108, 880, 70, 24);
        var factions = Game.Cards.DeckRules.PlayableFactions;
        var buttons = new Button[factions.Length];
        for (int i = 0; i < factions.Length; i++)
        {
            var button = MakeButton(window, "Faction_" + factions[i], Game.Campaign.CampaignUI.FactionName(factions[i]), 35, 202 + i * 114, 880, 96, 28);
            var label = button.GetComponentInChildren<TMP_Text>();
            Place((RectTransform)label.transform, 126, 18, 570, 60);
            label.alignment = TextAlignmentOptions.Left;
            Logo(button.transform, "FactionLogo", 22, 10, 76, logos[Mathf.Clamp((int)IndexOf(factions[i]), 0, 2)]);
            Label(button.transform, "Start", "START", 720, 29, 134, 38, 19);
            buttons[i] = button;
        }
        var cancel = MakeButton(window, "Cancel", "CANCEL", 35, 604, 880, 58, 22);

        // ---- replace saved campaign ----
        var overwrite = Window(backdrop, "OverwriteCampaign", "REPLACE SAVED CAMPAIGN?");
        var overwriteLogo = Logo(overwrite, "FactionLogo", 419, 128, 112, null);
        Label(overwrite, "Description", "The current planet and campaign progress will be replaced.\nYour collection and saved decks are retained.", 35, 270, 880, 128, 26);
        var newFaction = Label(overwrite, "NewFaction", "New faction: ", 35, 430, 880, 48, 26);
        var replace = MakeButton(overwrite, "Replace", "REPLACE AND START", 35, 548, 880, 58, 26);
        var overwriteCancel = MakeButton(overwrite, "Cancel", "CANCEL", 35, 628, 880, 40, 22);
        overwrite.gameObject.SetActive(false);

        so.FindProperty("campaignRoot").objectReferenceValue = root.gameObject;
        so.FindProperty("campaignFactionWindow").objectReferenceValue = window.gameObject;
        so.FindProperty("campaignOverwriteWindow").objectReferenceValue = overwrite.gameObject;
        var array = so.FindProperty("campaignFactionButtons");
        array.arraySize = buttons.Length;
        for (int i = 0; i < buttons.Length; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = buttons[i];
        so.FindProperty("campaignCancelButton").objectReferenceValue = cancel;
        so.FindProperty("campaignOverwriteLogo").objectReferenceValue = overwriteLogo;
        so.FindProperty("campaignOverwriteFaction").objectReferenceValue = newFaction;
        so.FindProperty("campaignOverwriteReplaceButton").objectReferenceValue = replace;
        so.FindProperty("campaignOverwriteCancelButton").objectReferenceValue = overwriteCancel;
        so.ApplyModifiedProperties();

        root.gameObject.SetActive(false);
        EditorSceneManager.MarkSceneDirty(menu.gameObject.scene);
        Selection.activeObject = root.gameObject;
        Debug.Log("Campaign setup popups built. Save the scene.", root);
        return true;
    }

    // Logo slot order of MainMenuController: Iron Concord, Ashen, Vessels.
    private static int IndexOf(Game.Players.Faction faction) =>
        faction == Game.Players.Faction.IronConcord ? 0 : faction == Game.Players.Faction.Ashen ? 1 : 2;

    private static void Report(bool interactive, string message)
    { if (interactive) EditorUtility.DisplayDialog("Campaign popups", message, "OK"); else Debug.LogError(message); }

    // ---- composite pieces ------------------------------------------------------------------------
    private static RectTransform Window(Transform parent, string name, string title)
    {
        // Authored at the original 1920x1080 size (plaque sprites are picked by size); scaled to the 1280x720 canvas.
        var window = Panel(parent, name); window.sizeDelta = new Vector2(950, 700);
        window.anchorMin = window.anchorMax = window.pivot = new Vector2(.5f, .5f);
        window.anchoredPosition = Vector2.zero; window.localScale = new Vector3(2f / 3f, 2f / 3f, 1f);
        window.GetComponent<Image>().color = Color.clear;
        var metal = Panel(window, "CampaignMetal"); Stretch(metal);
        metal.offsetMin = new Vector2(19, 19); metal.offsetMax = new Vector2(-19, -19);
        var image = metal.GetComponent<Image>();
        image.sprite = GameAssetCatalog.Load<Sprite>("UI/Backgrounds/Campaign/ConsoleMetalClean") ?? GameAssetCatalog.Load<Sprite>("UI/Backgrounds/Campaign/ConsoleMetal");
        image.type = Image.Type.Tiled; image.color = new Color(.82f, .82f, .82f); image.raycastTarget = false;
        var frame = Panel(window, "CampaignFrame"); Stretch(frame);
        CollectionUIElements.ApplyCampaignSurface(frame, "Panel_Inset"); frame.GetComponent<Image>().raycastTarget = false;
        var plaque = Panel(window, "CampaignTitle"); Place(plaque, 35, 25, 880, 58);
        CollectionUIElements.ApplyCampaignSurface(plaque, "Plaque"); plaque.GetComponent<Image>().raycastTarget = false;
        var heading = Label(plaque, "Heading", title, 18, 4, 844, 50, 30);
        heading.color = Ink; heading.fontStyle = FontStyles.Bold;
        return window;
    }
    private static Image Logo(Transform parent, string name, float x, float y, float size, Sprite sprite)
    {
        var rect = Rect(parent, name); Place(rect, x, y, size, size);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = sprite; image.preserveAspect = true; image.raycastTarget = false; image.enabled = sprite != null;
        return image;
    }
    private static Button MakeButton(Transform parent, string name, string label, float x, float y, float w, float h, float size)
    {
        var rect = Panel(parent, name); Place(rect, x, y, w, h);
        var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = rect.GetComponent<Image>();
        var text = Label(rect, "Label", label, 5, 1, w - 10, h - 2, size);
        CollectionUIElements.ApplyControlsSkin(rect);
        text.fontStyle = FontStyles.Bold;
        return button;
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
        var r = Rect(parent, name); r.gameObject.AddComponent<Image>().color = Color.white;
        return r;
    }
    private static TMP_Text Label(Transform parent, string name, string value, float x, float y, float w, float h, float size)
    {
        var r = Rect(parent, name); Place(r, x, y, w, h);
        var text = r.gameObject.AddComponent<TextMeshProUGUI>();
        text.font = font != null ? font : TMP_Settings.defaultFontAsset;
        text.text = value; text.fontSize = size; text.color = Cream; text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false; text.textWrappingMode = TextWrappingModes.Normal;
        return text;
    }
}
#endif
