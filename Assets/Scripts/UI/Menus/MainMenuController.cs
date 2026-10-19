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
        // New-campaign popups are authored in the MainMenu scene (Tools/UI/Build Campaign Setup Popups).
        [Header("New campaign popups")]
        [SerializeField] private GameObject campaignRoot;
        [SerializeField] private GameObject campaignFactionWindow;
        [SerializeField] private GameObject campaignOverwriteWindow;
        [SerializeField] private Button[] campaignFactionButtons = new Button[0]; // in DeckRules.PlayableFactions order
        [SerializeField] private Button campaignCancelButton;
        [SerializeField] private Image campaignOverwriteLogo;
        [SerializeField] private TMP_Text campaignOverwriteFaction;
        [SerializeField] private Button campaignOverwriteReplaceButton;
        [SerializeField] private Button campaignOverwriteCancelButton;
        private Button continueCampaignButton;
        private GameObject campaignPreviousSelection;
        private Game.Players.Faction campaignPendingFaction;
        private void Awake()
        {
            Game.Core.GameSession.EndRewardEligibility();
            GameSession.ClearMapConditions();
            foreach (var button in GetComponentsInChildren<Button>(true))
                if (button.gameObject.name == "Continue Campaign") continueCampaignButton = button;
            if (continueCampaignButton == null && mainMenuPanel != null)
                foreach (var button in mainMenuPanel.GetComponentsInChildren<Button>(true))
                    if (button.gameObject.name == "Continue Campaign") continueCampaignButton = button;
            bool canContinue = Game.Campaign.CampaignMatchBridge.Load(out _);
            if (continueCampaignButton != null) continueCampaignButton.interactable = canContinue;
            BindCampaignButtons();
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
        public void OpenDeckBuilderFromSetup(System.Action onClosed, Game.Players.Faction? faction = null, string deckId = null)
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
            }, faction, deckId);
        }

        private void Update()
        {
            if (campaignRoot != null && campaignRoot.activeSelf && UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame)
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
            if (campaignRoot == null || campaignRoot.activeSelf) return;
            campaignPreviousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            campaignRoot.SetActive(true);
            UIFocusUtility.SetOverlay(this, true);
            ShowCampaignFactions();
        }
        private void ShowCampaignFactions()
        {
            campaignFactionWindow.SetActive(true);
            campaignOverwriteWindow.SetActive(false);
            if (campaignFactionButtons.Length > 0 && campaignFactionButtons[0] != null) campaignFactionButtons[0].Select();
        }
        private void BindCampaignButtons()
        {
            if (campaignRoot == null) return;
            var factions = Game.Cards.DeckRules.PlayableFactions;
            for (int i = 0; i < campaignFactionButtons.Length && i < factions.Length; i++)
            {
                var chosen = factions[i];
                UIButtonEventUtility.ResetRuntimeListeners(campaignFactionButtons[i]);
                campaignFactionButtons[i].onClick.AddListener(() => ConfirmCampaign(chosen));
            }
            foreach (var cancel in new[] { campaignCancelButton, campaignOverwriteCancelButton })
            { if (cancel == null) continue; UIButtonEventUtility.ResetRuntimeListeners(cancel); cancel.onClick.AddListener(CloseCampaignSetup); }
            UIButtonEventUtility.ResetRuntimeListeners(campaignOverwriteReplaceButton);
            campaignOverwriteReplaceButton.onClick.AddListener(() => StartCampaign(campaignPendingFaction));
        }
        private void ConfirmCampaign(Game.Players.Faction human)
        {
            if (!Game.Campaign.CampaignMatchBridge.Store.Exists) { StartCampaign(human); return; }
            campaignOverwriteLogo.sprite = CampaignLogo(human);
            campaignOverwriteLogo.enabled = campaignOverwriteLogo.sprite != null;
            campaignOverwriteFaction.text = "New faction: " + Game.Campaign.CampaignUI.FactionName(human);
            campaignPendingFaction = human;
            campaignFactionWindow.SetActive(false);
            campaignOverwriteWindow.SetActive(true);
            campaignOverwriteCancelButton.Select();
        }
        private Sprite CampaignLogo(Game.Players.Faction faction) =>
            faction == Game.Players.Faction.IronConcord ? campaignIronConcordLogo
            : faction == Game.Players.Faction.Ashen ? campaignAshenLogo : campaignVesselsLogo;
        private void StartCampaign(Game.Players.Faction human)
        {
            try
            {
                if (!Game.Progression.ProgressionContext.Initialize(gameConfig)) throw new System.InvalidOperationException(Game.Progression.ProgressionContext.Error);
                Game.Campaign.CampaignMatchBridge.Create(human);
                CloseCampaignSetup(); UnityEngine.SceneManagement.SceneManager.LoadScene(Game.Core.SceneNames.Campaign);
            }
            catch (System.Exception ex) { CollectionScreensUI.ShowMessage(transform, ex.Message); }
        }
        private void CloseCampaignSetup()
        {
            if (campaignRoot == null || !campaignRoot.activeSelf) return;
            UIFocusUtility.SetOverlay(this, false); campaignRoot.SetActive(false);
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

