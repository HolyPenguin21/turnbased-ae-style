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
        private CollectionScreensUI collectionScreens;
        private void Awake()
        {
            Game.Core.GameSession.EndRewardEligibility();
            if (gameConfig == null || gameConfig.playerRowPrefab == null) return;
            collectionScreens = gameObject.AddComponent<CollectionScreensUI>();
            collectionScreens.Configure(gameConfig, gameConfig.playerRowPrefab.FactionTemplate, gameConfig.playerRowPrefab.NicknameTemplate);
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
            collectionScreens?.Show(mainMenuPanel);
        }
        public void OpenDeckBuilderFromSetup(System.Action onClosed)
        {
            if (!Game.Progression.ProgressionContext.Initialize(gameConfig))
            { CollectionScreensUI.ShowMessage(transform, Game.Progression.ProgressionContext.Error); return; }
            // Hide interaction without disabling the setup model: OnEnable normally resets it.
            var group = gameSetupPanel.GetComponent<CanvasGroup>() ?? gameSetupPanel.AddComponent<CanvasGroup>();
            group.interactable = false; group.blocksRaycasts = false;
            collectionScreens.Show(null, () => { group.interactable = true; group.blocksRaycasts = true; onClosed(); });
        }

        private void Update()
        {
            // Guarded by mainMenuPanel's own active state — this component isn't disabled
            // when the setup screen takes over, so Space would otherwise keep re-triggering
            // "New Game" from there too.
            if ((settingsPanel != null && settingsPanel.activeSelf) ||
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
