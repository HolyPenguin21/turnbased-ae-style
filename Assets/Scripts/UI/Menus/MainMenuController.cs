using Game.Core;
using Game.Saves;
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
        [Header("Campaign popups")]
        [SerializeField] private GameObject campaignRoot;
        [SerializeField] private GameObject campaignFactionWindow;
        [SerializeField] private GameObject campaignOptionsWindow;
        [SerializeField] private Button[] campaignFactionButtons = new Button[0]; // in DeckRules.PlayableFactions order
        [SerializeField] private Button campaignFactionBackButton;
        [SerializeField] private Button campaignContinueButton, campaignNewButton, campaignOptionsCancelButton;
        [SerializeField] private Button continueCampaignButton;
        [SerializeField] private MatchSaveSlotsUI saveSlots;
        private void Awake()
        {
            Game.Core.GameSession.EndRewardEligibility();
            GameSession.ClearMapConditions();
            // Retain campaign initialization for pending reward acknowledgements, independently
            // of the first button's routing priority.
            Game.Campaign.CampaignMatchBridge.Load(out _);
            RefreshContinue();
            BindCampaignButtons();
            if (saveSlots != null)
            {
                saveSlots.Closed += OnSlotsClosed;
                saveSlots.SlotsChanged += RefreshContinue;
            }
            if (gameConfig == null) return;
            if (collectionScreens != null) collectionScreens.Configure(gameConfig);
            else Debug.LogError("MainMenuController: CollectionWindow is not assigned in the MainMenu scene.", this);
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
        // 0 = unavailable, 1 = campaign, 2 = match list. Match files have strict priority.
        internal static int ContinueAction(bool hasMatches, bool hasCampaign) => hasMatches ? 2 : hasCampaign ? 1 : 0;
        private bool HasSavedMatches()
        {
            try { return MatchSaveApplication.HasSavedMatches(); }
            catch (System.Exception ex)
            {
                // Keep the list reachable to display a storage error instead of silently
                // routing into a campaign when the save directory cannot be inspected.
                Debug.LogWarning(ex); return true;
            }
        }
        private void RefreshContinue()
        {
            if (!isActiveAndEnabled || continueCampaignButton == null) return;
            bool matches = HasSavedMatches();
            bool campaign = !matches && Game.Campaign.CampaignMatchBridge.Load(out _);
            continueCampaignButton.interactable = ContinueAction(matches, campaign) != 0;
        }
        private void OnApplicationFocus(bool focused)
        {
            if (!focused) return;
            RefreshContinue();
            if (campaignOptionsWindow != null && campaignOptionsWindow.activeInHierarchy) RefreshCampaignContinue();
        }
        public void OnContinueClicked()
        {
            if (UIFocusUtility.HasOverlay || (mainMenuPanel != null && !mainMenuPanel.activeSelf)) return;
            if (HasSavedMatches()) { OnLoadMatchClicked(); return; }
            OnContinueCampaignClicked();
        }
        public void OnLoadMatchClicked()
        {
            if (saveSlots != null && saveSlots.Show(gameConfig, null, false) && mainMenuPanel != null)
                mainMenuPanel.SetActive(false); // Exclude background buttons from keyboard navigation too.
        }
        private void OnSlotsClosed()
        {
            if (!isActiveAndEnabled) return;
            if (mainMenuPanel != null) mainMenuPanel.SetActive(true);
            RefreshContinue();
            if (continueCampaignButton != null && continueCampaignButton.IsInteractable()) continueCampaignButton.Select();
            else settingsButton?.Select();
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
            bool campaignOpen = campaignRoot != null && campaignRoot.activeSelf;
            UIFocusUtility.KeepFocusWithinModal(restoreMissingSelection: campaignOpen && UIFocusUtility.IsTop(this));
            if (campaignOpen && UIFocusUtility.WasEscapePressed(this))
            {
                if (campaignFactionWindow.activeSelf) ShowCampaignOptions();
                else CloseCampaignSetup();
                return;
            }
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

        public void OnCampaignClicked()
        {
            if (campaignRoot == null || campaignRoot.activeSelf || UIFocusUtility.HasOverlay) return;
            campaignRoot.SetActive(true);
            ShowCampaignOptions();
        }
        private void ShowCampaignOptions()
        {
            campaignFactionWindow.SetActive(false);
            campaignOptionsWindow.SetActive(true);
            RefreshCampaignContinue();
            var focus = campaignContinueButton.IsInteractable() ? campaignContinueButton : campaignNewButton;
            UIFocusUtility.SetModal(this, campaignRoot, ModalBlock.Full, focus, sceneControls: campaignRoot.transform);
            focus.Select();
        }
        private void RefreshCampaignContinue()
        {
            campaignContinueButton.interactable = Game.Campaign.CampaignMatchBridge.Store.Exists;
        }
        private void ShowCampaignFactions()
        {
            campaignOptionsWindow.SetActive(false);
            campaignFactionWindow.SetActive(true);
            if (campaignFactionButtons.Length > 0 && campaignFactionButtons[0] != null) campaignFactionButtons[0].Select();
        }
        private void BindCampaignButtons()
        {
            if (campaignRoot == null) return;
            var factions = Game.Cards.DeckRules.PlayableFactions;
            for (int i = 0; i < campaignFactionButtons.Length && i < factions.Length; i++)
            {
                var chosen = factions[i];
                UIUtility.ResetRuntimeListeners(campaignFactionButtons[i]);
                campaignFactionButtons[i].onClick.AddListener(() => StartCampaign(chosen));
            }
            Bind(campaignContinueButton, OnContinueCampaignClicked);
            Bind(campaignNewButton, ShowCampaignFactions);
            Bind(campaignOptionsCancelButton, CloseCampaignSetup);
            Bind(campaignFactionBackButton, ShowCampaignOptions);

            void Bind(Button button, UnityEngine.Events.UnityAction action)
            {
                UIUtility.ResetRuntimeListeners(button);
                button.onClick.AddListener(action);
            }
        }
        private void StartCampaign(Game.Players.Faction human)
        {
            try
            {
                if (!Game.Progression.ProgressionContext.Initialize(gameConfig))
                {
                    CollectionScreensUI.ShowMessage(transform, Game.Progression.ProgressionContext.Error);
                    return;
                }
                Game.Campaign.CampaignMatchBridge.Create(human);
                OpenCampaignScene();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning(ex);
                CollectionScreensUI.ShowMessage(transform, "Could not start the campaign. Try again.");
            }
        }
        private void CloseCampaignSetup()
        {
            if (campaignRoot == null || !campaignRoot.activeSelf) return;
            campaignRoot.SetActive(false); UIFocusUtility.SetModal(this, null);
        }
        public void OnContinueCampaignClicked()
        {
            if (!Game.Campaign.CampaignMatchBridge.Load(out string error))
            {
                if (campaignOptionsWindow != null && campaignOptionsWindow.activeInHierarchy) RefreshCampaignContinue();
                CollectionScreensUI.ShowMessage(transform, error ?? "No saved campaign.");
                return;
            }
            OpenCampaignScene();
        }
        private void OpenCampaignScene()
        {
            CloseCampaignSetup();
            UnityEngine.SceneManagement.SceneManager.LoadScene(SceneNames.Campaign);
        }
        private void OnDestroy()
        {
            if (saveSlots != null)
            {
                saveSlots.Closed -= OnSlotsClosed;
                saveSlots.SlotsChanged -= RefreshContinue;
            }
            CloseCampaignSetup();
        }

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
            if (mainMenuPanel != null) mainMenuPanel.SetActive(true);
            if (settingsPanel != null) settingsPanel.SetActive(false);
            RefreshContinue();
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

