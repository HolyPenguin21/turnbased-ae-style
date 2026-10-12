using Game.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace Game.UI
{
    // Hook these methods up to Button OnClick events in the MainMenu scene.
    public class MainMenuController : MonoBehaviour
    {
        [SerializeField] private GameObject mainMenuPanel;
        [SerializeField] private GameObject gameSetupPanel;
        [SerializeField] private GameObject settingsPanel;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Game.Core.GameConfig gameConfig;
        [SerializeField] private CollectionScreensUI collectionScreens;
        [SerializeField] private Sprite campaignIronConcordLogo, campaignAshenLogo, campaignVesselsLogo;
        private RectTransform campaignSetup;
        private Button continueCampaignButton;
        private GameObject campaignPreviousSelection;
        private void Awake()
        {
            Game.Core.GameSession.EndRewardEligibility();
            foreach (var button in GetComponentsInChildren<Button>(true))
                if (button.gameObject.name == "Continue Campaign") continueCampaignButton = button;
            if (continueCampaignButton == null && mainMenuPanel != null)
                foreach (var button in mainMenuPanel.GetComponentsInChildren<Button>(true))
                    if (button.gameObject.name == "Continue Campaign") continueCampaignButton = button;
            bool canContinue = Game.Campaign.CampaignMatchBridge.Load(out _);
            if (continueCampaignButton != null) continueCampaignButton.interactable = canContinue;
            if (gameConfig == null) return;
            if (collectionScreens != null) collectionScreens.Configure(gameConfig);
            else Debug.LogError("MainMenuController: CollectionWindow is not assigned. Run Tools/UI/Build Collection Window.", this);
            if (Game.Core.GameSession.SetupError != null)
            {
                OnNewGameClicked();
                CollectionScreensUI.ShowMessage(transform, Game.Core.GameSession.SetupError);
                Game.Core.GameSession.SetupError = null;
            }
            else gameObject.AddComponent<CollectionRewardUI>().Resume(gameConfig);
        }
        public void OnCollectionClicked()
        {
            if (collectionScreens != null) collectionScreens.Show(mainMenuPanel);
        }
        public void OpenDeckBuilderFromSetup(System.Action onClosed)
        {
            if (collectionScreens == null || gameSetupPanel == null || UIFocusUtility.HasOverlay) return;
            if (!Game.Progression.ProgressionContext.Initialize(gameConfig))
            { CollectionScreensUI.ShowMessage(transform, Game.Progression.ProgressionContext.Error); return; }
            // Hide interaction without disabling the setup model: OnEnable normally resets it.
            var group = gameSetupPanel.GetComponent<CanvasGroup>();
            // Unity objects can be missing without being CLR-null; do not use ?? here.
            if (group == null) group = gameSetupPanel.AddComponent<CanvasGroup>();
            bool interactable = group.interactable, blocksRaycasts = group.blocksRaycasts;
            group.interactable = false; group.blocksRaycasts = false;
            collectionScreens.Show(null, () =>
            {
                if (group != null) { group.interactable = interactable; group.blocksRaycasts = blocksRaycasts; }
                onClosed?.Invoke();
            });
        }

        private void Update()
        {
            if (campaignSetup != null && UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame)
            { CloseCampaignSetup(); return; }
            // Guarded by mainMenuPanel's own active state — this component isn't disabled
            // when the setup screen takes over, so Space would otherwise keep re-triggering
            // "New Game" from there too.
            if (UIFocusUtility.HasOverlay || (settingsPanel != null && settingsPanel.activeSelf) ||
                (mainMenuPanel != null && !mainMenuPanel.activeSelf) || UIFocusUtility.IsTextFieldFocused())
                return;

            // Space is only a fallback when no menu control owns keyboard submission.
            // Otherwise EventSystem and this shortcut could activate two different actions.
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected != null && selected.activeInHierarchy) return;

            if (UIFocusUtility.WasSpacePressed())
            {
                Game.Audio.GameAudioManager.Instance?.PlayClick();
                OnNewGameClicked();
            }
        }

        public void OnNewCampaignClicked()
        {
            if (campaignSetup != null) return;
            campaignPreviousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            campaignSetup = CollectionUIElements.Canvas("NewCampaign", new Vector2(1920, 1080));
            UIFocusUtility.SetOverlay(this, true);
            ShowCampaignFactions();
        }
        private void ShowCampaignFactions()
        {
            var window = CampaignSetupWindow("CampaignSetup", "NEW PLANETARY CAMPAIGN");
            CampaignSetupLabel(window, "Choose your faction. A new planet has "
                + new Game.Campaign.CampaignGenerationSettings().RegionCount + " regions.", 35, 108, 880, 70, 24);
            int index = 0;
            Button first = null;
            foreach (var faction in Game.Cards.DeckRules.PlayableFactions)
            {
                var chosen = faction;
                var button = CampaignSetupButton(window, Game.Campaign.CampaignUI.FactionName(chosen),
                    35, 202 + index * 114, 880, 96, () => ChooseCampaignFaction(chosen), 28);
                var label = button.GetComponentInChildren<TMP_Text>();
                CollectionUIElements.Place((RectTransform)label.transform, 126, 18, 570, 60);
                label.alignment = TextAlignmentOptions.Left;
                CampaignFactionLogo(button.transform, chosen, 22, 10, 76);
                CampaignSetupLabel(button.transform, "SELECT", 720, 29, 134, 38, 19);
                if (first == null) first = button;
                index++;
            }
            CampaignSetupButton(window, "CANCEL", 35, 604, 880, 58, CloseCampaignSetup);
            if (first != null) first.Select();
        }
        private void ChooseCampaignFaction(Game.Players.Faction human)
        {
            var window = CampaignSetupWindow("ChosenFaction", "YOUR FACTION");
            CampaignFactionLogo(window, human, 393, 132, 164);
            CampaignSetupLabel(window, Game.Campaign.CampaignUI.FactionName(human), 35, 322, 880, 52, 32);
            CampaignSetupLabel(window, "Lead your faction and conquer the planet.\nChoose a saved deck before each battle.", 35, 394, 880, 82, 24);
            var start = CampaignSetupButton(window, "START CAMPAIGN", 35, 548, 880, 58, () => ConfirmCampaign(human), 26);
            CampaignSetupButton(window, "BACK", 35, 628, 880, 40, ShowCampaignFactions);
            start.Select();
        }
        private void ConfirmCampaign(Game.Players.Faction human)
        {
            bool previous = System.IO.File.Exists(System.IO.Path.Combine(Application.persistentDataPath, "campaign-v1.json"))
                || System.IO.File.Exists(System.IO.Path.Combine(Application.persistentDataPath, "campaign-v1.json.bak"));
            if (!previous) { StartCampaign(human); return; }
            var window = CampaignSetupWindow("OverwriteCampaign", "REPLACE SAVED CAMPAIGN?");
            CampaignFactionLogo(window, human, 419, 128, 112);
            CampaignSetupLabel(window, "The current planet and campaign progress will be replaced.\nYour collection and saved decks are retained.", 35, 270, 880, 128, 26);
            CampaignSetupLabel(window, "New faction: " + Game.Campaign.CampaignUI.FactionName(human), 35, 430, 880, 48, 26);
            CampaignSetupButton(window, "REPLACE AND START", 35, 548, 880, 58, () => StartCampaign(human), 26);
            var cancel = CampaignSetupButton(window, "CANCEL", 35, 628, 880, 40, CloseCampaignSetup);
            cancel.Select();
        }
        private RectTransform CampaignSetupWindow(string name, string title)
        {
            CollectionUIElements.Clear(campaignSetup);
            var backdrop = CollectionUIElements.Panel(campaignSetup, "CampaignBackdrop"); CollectionUIElements.Stretch(backdrop);
            backdrop.GetComponent<Image>().color = new Color(0, 0, 0, .8f);
            var window = CollectionUIElements.Panel(backdrop, name);
            CollectionUIElements.Place(window, 485, 190, 950, 700);
            window.GetComponent<Image>().color = Color.clear;
            var metal = CollectionUIElements.Panel(window, "CampaignMetal"); CollectionUIElements.Stretch(metal);
            metal.offsetMin = new Vector2(19, 19); metal.offsetMax = new Vector2(-19, -19);
            var image = metal.GetComponent<Image>();
            image.sprite = GameAssetCatalog.Load<Sprite>("UI/Backgrounds/Campaign/ConsoleMetalClean") ?? GameAssetCatalog.Load<Sprite>("UI/Backgrounds/Campaign/ConsoleMetal");
            image.type = Image.Type.Tiled; image.color = new Color(.82f, .82f, .82f); image.raycastTarget = false;
            metal.SetAsFirstSibling();
            var frame = CollectionUIElements.Panel(window, "CampaignFrame"); CollectionUIElements.Stretch(frame);
            CollectionUIElements.ApplyCampaignSurface(frame, "Panel_Inset"); frame.GetComponent<Image>().raycastTarget = false;
            var plaque = CollectionUIElements.Panel(window, "CampaignTitle"); CollectionUIElements.Place(plaque, 35, 25, 880, 58);
            CollectionUIElements.ApplyCampaignSurface(plaque, "Plaque"); plaque.GetComponent<Image>().raycastTarget = false;
            var heading = CampaignSetupLabel(plaque, title, 18, 4, 844, 50, 30);
            heading.color = CollectionUIElements.CollectionInk; heading.fontStyle = FontStyles.Bold;
            return window;
        }
        private static TMP_Text CampaignSetupLabel(Transform parent, string value, float x, float y, float w, float h, int size)
        {
            var label = CollectionUIElements.Label(parent, value, x, y, w, h, size);
            label.color = CollectionUIElements.CollectionText; label.alignment = TextAlignmentOptions.Center;
            return label;
        }
        private static Button CampaignSetupButton(Transform parent, string value, float x, float y, float w, float h, System.Action action, int size = 22)
        {
            var button = CollectionUIElements.Button(parent, value, x, y, w, h, action, size);
            CollectionUIElements.ApplyControlsSkin(button.transform);
            button.GetComponentInChildren<TMP_Text>().fontStyle = FontStyles.Bold;
            return button;
        }
        private void CampaignFactionLogo(Transform parent, Game.Players.Faction faction, float x, float y, float size)
        {
            var rect = CollectionUIElements.Rect(parent, "FactionLogo"); CollectionUIElements.Place(rect, x, y, size, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = faction == Game.Players.Faction.IronConcord ? campaignIronConcordLogo
                : faction == Game.Players.Faction.Ashen ? campaignAshenLogo : campaignVesselsLogo;
            image.preserveAspect = true; image.raycastTarget = false; image.enabled = image.sprite != null;
        }
        private void StartCampaign(Game.Players.Faction human)
        {
            try
            {
                if (!Game.Progression.ProgressionContext.Initialize(gameConfig)) throw new System.InvalidOperationException(Game.Progression.ProgressionContext.Error);
                Game.Campaign.CampaignMatchBridge.Create(human);
                CloseCampaignSetup(); UnityEngine.SceneManagement.SceneManager.LoadScene(Game.Core.SceneNames.Campaign);
            }
            catch (System.Exception ex) { CollectionScreensUI.ShowMessage(campaignSetup, ex.Message); }
        }
        private void CloseCampaignSetup()
        {
            if (campaignSetup == null) return;
            UIFocusUtility.SetOverlay(this, false); campaignSetup.gameObject.SetActive(false);
            Destroy(campaignSetup.gameObject); campaignSetup = null;
            if (EventSystem.current != null && campaignPreviousSelection != null && campaignPreviousSelection.activeInHierarchy)
                EventSystem.current.SetSelectedGameObject(campaignPreviousSelection);
            campaignPreviousSelection = null;
        }
        public void OnContinueCampaignClicked()
        {
            if (!Game.Campaign.CampaignMatchBridge.Load(out string error)) { CollectionScreensUI.ShowMessage(transform, error ?? "No saved campaign."); return; }
            UnityEngine.SceneManagement.SceneManager.LoadScene(Game.Core.SceneNames.Campaign);
        }
        private void OnDestroy() { CloseCampaignSetup(); }

        public void OnNewGameClicked()
        {
            if (settingsPanel != null && settingsPanel.activeSelf) return;
            if (gameSetupPanel != null)
                gameSetupPanel.SetActive(true);
            if (mainMenuPanel != null)
                mainMenuPanel.SetActive(false);
        }

        public void OnSettingsClicked()
        {
            if (settingsPanel == null) return;
            if (mainMenuPanel != null) mainMenuPanel.SetActive(false);
            settingsPanel.SetActive(true);
        }

        public void OnSettingsClosed()
        {
            if (settingsPanel != null) settingsPanel.SetActive(false);
            if (mainMenuPanel != null) mainMenuPanel.SetActive(true);
            if (settingsButton != null) settingsButton.Select();
        }

        public void OnQuitClicked()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}

