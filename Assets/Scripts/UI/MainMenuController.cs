using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

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
        private RectTransform campaignSetup;
        private Button continueCampaignButton;
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
            campaignSetup = CollectionUIElements.Canvas("NewCampaign", new Vector2(1920, 1080));
            var backdrop = CollectionUIElements.Panel(campaignSetup, "CampaignSetup"); CollectionUIElements.Stretch(backdrop);
            UIFocusUtility.SetOverlay(this, true);
            CollectionUIElements.Label(backdrop, "NEW PLANETARY CAMPAIGN", 500, 160, 920, 70, 34);
            CollectionUIElements.Label(backdrop, "Choose your faction. A new planet has 24 regions.", 500, 265, 920, 70, 24);
            int index = 0;
            foreach (var faction in Game.Cards.DeckRules.PlayableFactions)
            {
                var chosen = faction;
                CollectionUIElements.Button(backdrop, Game.Campaign.CampaignUI.FactionName(chosen), 500, 365 + index * 90, 920, 65, () => ChooseCampaignFaction(chosen), 26);
                index++;
            }
            CollectionUIElements.Button(backdrop, "Cancel", 500, 755, 920, 60, CloseCampaignSetup, 22);
        }
        private void ChooseCampaignFaction(Game.Players.Faction human)
        {
            CollectionUIElements.Clear(campaignSetup);
            var backdrop = CollectionUIElements.Panel(campaignSetup, "ChosenFaction"); CollectionUIElements.Stretch(backdrop);
            CollectionUIElements.Label(backdrop, "Your faction: " + Game.Campaign.CampaignUI.FactionName(human), 500, 280, 920, 120, 32);
            CollectionUIElements.Button(backdrop, "Start Campaign", 500, 525, 920, 65, () => ConfirmCampaign(human), 26);
            CollectionUIElements.Button(backdrop, "Back", 500, 645, 920, 65, () => { CloseCampaignSetup(); OnNewCampaignClicked(); }, 24);
        }
        private void ConfirmCampaign(Game.Players.Faction human)
        {
            bool previous = System.IO.File.Exists(System.IO.Path.Combine(Application.persistentDataPath, "campaign-v1.json"))
                || System.IO.File.Exists(System.IO.Path.Combine(Application.persistentDataPath, "campaign-v1.json.bak"));
            if (!previous) { StartCampaign(human); return; }
            CollectionUIElements.Clear(campaignSetup);
            var backdrop = CollectionUIElements.Panel(campaignSetup, "OverwriteCampaign"); CollectionUIElements.Stretch(backdrop);
            CollectionUIElements.Label(backdrop, "Replace the saved campaign?\nYour collection and saved decks are retained.", 500, 280, 920, 180, 30);
            CollectionUIElements.Button(backdrop, "Start Campaign — " + Game.Campaign.CampaignUI.FactionName(human), 500, 570, 920, 65, () => StartCampaign(human), 24);
            CollectionUIElements.Button(backdrop, "Cancel", 500, 680, 920, 65, CloseCampaignSetup, 24);
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
        { UIFocusUtility.SetOverlay(this, false); if (campaignSetup != null) Destroy(campaignSetup.gameObject); campaignSetup = null; }
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
