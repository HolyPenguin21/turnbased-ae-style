using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;
using Game.Core;
using Game.Campaign;
using UnityEngine.SceneManagement;

namespace Game.UI
{
    [DefaultExecutionOrder(-10000)]
    public sealed class GameMenuPanelUI : MonoBehaviour
    {
        [SerializeField] private GameObject blockingRoot;
        [SerializeField] private GameObject menuPanel;
        [SerializeField] private GameSettingsPanelUI optionsPanel;
        [SerializeField] private MatchSaveSlotsUI saveSlots;
        [SerializeField] private BattleScreenUI battleScreen;
        [SerializeField] private Button gearButton;
        [SerializeField] private Button helpButton;
        [SerializeField] private GameHelpPanelUI helpPanel;
        [SerializeField] private Button optionsButton;
        [SerializeField] private Button saveButton;
        [SerializeField] private Button loadButton;
        [SerializeField] private Button continueButton;
        [SerializeField] private Button exitButton;
        [SerializeField] private TMP_Text exitButtonText;
        private Game.Turns.GameTurnController turnController;
        // An AI-only match has no human turn at all (also after its winner is announced): the
        // spectator may open the menu at any moment outside a battle.
        private bool ObserverMatch => turnController != null && turnController.IsAiObserverMatch;
        private bool HumanTurn => turnController != null && turnController.CurrentPlayer != null && turnController.CurrentPlayer.IsHuman;
        private bool MenuAvailable => !UIFocusUtility.HasOverlay && !BattleActive && (ObserverMatch || HumanTurn);
        // The whole panel (menu/help buttons, play-pause, utility) sits over the battle screen,
        // so it is hidden for the duration of a battle.
        private CanvasGroup panelGroup;
        private struct GroupState
        {
            public CanvasGroup Group;
            public bool Interactable, BlocksRaycasts;
        }
        private readonly List<GroupState> groups = new List<GroupState>();
        private GameObject previousSelection;
        private static GameMenuPanelUI instance;
        private static int blockedThroughFrame = -1;
        public bool IsShowing { get; private set; }
        private bool BattleActive => battleScreen != null && battleScreen.IsShowing;
        public event Action VisibilityChanged;
        public static bool GameplayInputBlocked => (instance != null && instance.IsShowing) || blockedThroughFrame == Time.frameCount;
        public static bool OwnsKeyboardSelection
        {
            get
            {
                var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                return instance != null && selected != null && selected.activeInHierarchy && selected.transform.IsChildOf(instance.transform);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { instance = null; blockedThroughFrame = -1; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void RestoreInstance() => instance = UnityEngine.Object.FindAnyObjectByType<GameMenuPanelUI>();
        private void OnEnable()
        {
            instance = this;
            turnController = UnityEngine.Object.FindAnyObjectByType<Game.Turns.GameTurnController>();
            if (turnController != null) turnController.TurnStateChanged += RefreshBattleAvailability;
            if (gearButton != null) gearButton.onClick.AddListener(OpenMenu);
            if (helpButton != null) helpButton.onClick.AddListener(OpenHelp);
            if (helpPanel != null) helpPanel.Closed += ContinueGame;
            if (optionsButton != null) optionsButton.onClick.AddListener(OpenOptions);
            if (saveButton != null) saveButton.onClick.AddListener(OpenSave);
            if (loadButton != null) loadButton.onClick.AddListener(OpenLoad);
            if (continueButton != null) continueButton.onClick.AddListener(ContinueGame);
            if (exitButton != null) exitButton.onClick.AddListener(ExitMatch);
            if (optionsPanel != null) optionsPanel.Closed += OnOptionsClosed;
            if (saveSlots != null) saveSlots.Closed += OnSlotsClosed;
            if (battleScreen != null) battleScreen.VisibilityChanged += RefreshBattleAvailability;
            RefreshBattleAvailability();
        }
        private void RefreshBattleAvailability()
        {
            if (saveButton != null) saveButton.interactable = turnController != null && turnController.CanSaveMatch(out _);
            if (loadButton != null) loadButton.interactable = turnController != null && turnController.SaveConfiguration != null;
            bool ownsSlots = saveSlots != null && saveSlots.IsShowing;
            bool closeForTurn = BattleActive || (!ObserverMatch && !HumanTurn);
            if (IsShowing && (closeForTurn || (!MenuAvailable && !ownsSlots))) ContinueGame();
            SetPanelHidden(BattleActive);
            if (helpButton != null)
            {
                helpButton.gameObject.SetActive(!IsShowing);
                helpButton.interactable = MenuAvailable;
            }
            if (gearButton != null)
            {
                gearButton.gameObject.SetActive(!IsShowing);
                gearButton.interactable = MenuAvailable;
            }
        }
        private void SetPanelHidden(bool hidden)
        {
            if (panelGroup == null)
            {
                if (!hidden) return;
                panelGroup = GetComponent<CanvasGroup>();
                if (panelGroup == null) panelGroup = gameObject.AddComponent<CanvasGroup>();
            }
            panelGroup.alpha = hidden ? 0f : 1f;
            panelGroup.interactable = !hidden;
            panelGroup.blocksRaycasts = !hidden;
        }
        public void OpenHelp()
        {
            if (!MenuAvailable || !isActiveAndEnabled || IsShowing || helpPanel == null || !helpPanel.HasBindings) return;
            BeginModal();
            helpPanel.Show();
            VisibilityChanged?.Invoke();
            RefreshBattleAvailability();
        }
        private void BeginModal()
        {
            instance = this;
            previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            IsShowing = true;
            turnController.SetAiObserverMenuHold(true);
            BlockGameplayCanvases();
        }
        public void OpenMenu()
        {
            if (!MenuAvailable || !isActiveAndEnabled || IsShowing || blockingRoot == null || menuPanel == null) return;
            BeginModal();
            if (gearButton != null) gearButton.gameObject.SetActive(false);
            blockingRoot.SetActive(true);
            menuPanel.SetActive(true);
            if (exitButtonText != null)
                exitButtonText.text = CampaignMatchBridge.ReturnScene(GameSession.MatchId) == SceneNames.Campaign
                    ? "Campaign" : "Main Menu";
            if (optionsPanel != null) optionsPanel.gameObject.SetActive(false);
            VisibilityChanged?.Invoke();
            optionsButton?.Select();
            RefreshBattleAvailability();
        }
        private void OpenSave() => OpenSlots(true);
        private void OpenLoad() => OpenSlots(false);
        private void OpenSlots(bool saving)
        {
            if (!IsShowing || saveSlots == null || turnController == null) return;
            if (saveSlots.Show(turnController.SaveConfiguration, turnController, saving)) menuPanel.SetActive(false);
        }
        private void OnSlotsClosed()
        {
            if (!isActiveAndEnabled || !IsShowing) return;
            menuPanel.SetActive(true);
            RefreshBattleAvailability();
            if (IsShowing) loadButton?.Select();
        }
        public void OpenOptions()
        {
            if (!IsShowing || optionsPanel == null) return;
            menuPanel.SetActive(false);
            optionsPanel.gameObject.SetActive(true);
        }
        private void OnOptionsClosed()
        {
            if (!IsShowing) return;
            menuPanel.SetActive(true);
            optionsButton?.Select();
        }
        public void ContinueGame()
        {
            if (!IsShowing) return;
            blockedThroughFrame = Time.frameCount;
            IsShowing = false;
            if (turnController != null) turnController.SetAiObserverMenuHold(false);
            if (helpPanel != null) helpPanel.Close();
            if (saveSlots != null) saveSlots.Close();
            if (optionsPanel != null) optionsPanel.gameObject.SetActive(false);
            if (blockingRoot != null) blockingRoot.SetActive(false);
            RestoreGameplayCanvases();
            RefreshBattleAvailability();
            VisibilityChanged?.Invoke();
            var selectable = previousSelection != null ? previousSelection.GetComponent<Selectable>() : null;
            if (selectable != null && selectable.isActiveAndEnabled && selectable.IsInteractable()) selectable.Select();
            else if (MenuAvailable) gearButton?.Select();
            previousSelection = null;
        }

        public void ExitMatch()
        {
            if (!IsShowing) return;
            string destination = CampaignMatchBridge.ReturnScene(GameSession.MatchId);
            ContinueGame();
            Game.Setup.MatchStateInventory.EndMatch(turnController);
            SceneManager.LoadScene(destination);
        }
        private void BlockGameplayCanvases()
        {
            groups.Clear();
            var seen = new HashSet<CanvasGroup>();
            foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include))
            {
                if (canvas.gameObject.scene != gameObject.scene || canvas.transform.IsChildOf(transform)) continue;
                if (canvas.transform.parent != null && canvas.transform.parent.GetComponentInParent<Canvas>(true) != null) continue;
                BlockGameplayBranch(canvas.transform, seen);
            }
        }
        private void BlockGameplayBranch(Transform root, HashSet<CanvasGroup> seen)
        {
            if (root == transform || root.IsChildOf(transform)) return;
            if (transform.IsChildOf(root))
            {
                // The shared Canvas must stay enabled for the menu's own controls.
                foreach (Transform child in root) BlockGameplayBranch(child, seen);
                return;
            }
            if (root.GetComponent<CanvasGroup>() == null) root.gameObject.AddComponent<CanvasGroup>();
            foreach (var group in root.GetComponentsInChildren<CanvasGroup>(true))
            {
                if (!seen.Add(group)) continue;
                groups.Add(new GroupState { Group = group, Interactable = group.interactable, BlocksRaycasts = group.blocksRaycasts });
                group.interactable = false; group.blocksRaycasts = false;
            }
        }
        private void LateUpdate()
        {
            // Popups reorder themselves on show; keep the gear/menu above those siblings.
            if (transform.parent != null && transform.GetSiblingIndex() != transform.parent.childCount - 1)
                transform.SetAsLastSibling();
        }
        private void RestoreGameplayCanvases()
        {
            // A spectated AI keeps acting until its next action boundary, so a group's owner may
            // have set it again while the menu was open: only a value still blocked by the menu
            // gets its snapshot back.
            foreach (var state in groups)
            {
                if (state.Group == null) continue;
                if (!state.Group.interactable) state.Group.interactable = state.Interactable;
                if (!state.Group.blocksRaycasts) state.Group.blocksRaycasts = state.BlocksRaycasts;
            }
            groups.Clear();
        }
        private void OnDisable()
        {
            ContinueGame();
            RestoreGameplayCanvases();
            SetPanelHidden(false);
            if (gearButton != null) gearButton.onClick.RemoveListener(OpenMenu);
            if (helpButton != null) helpButton.onClick.RemoveListener(OpenHelp);
            if (helpPanel != null) helpPanel.Closed -= ContinueGame;
            if (optionsButton != null) optionsButton.onClick.RemoveListener(OpenOptions);
            if (saveButton != null) saveButton.onClick.RemoveListener(OpenSave);
            if (loadButton != null) loadButton.onClick.RemoveListener(OpenLoad);
            if (continueButton != null) continueButton.onClick.RemoveListener(ContinueGame);
            if (exitButton != null) exitButton.onClick.RemoveListener(ExitMatch);
            if (optionsPanel != null) optionsPanel.Closed -= OnOptionsClosed;
            if (saveSlots != null) saveSlots.Closed -= OnSlotsClosed;
            if (battleScreen != null) battleScreen.VisibilityChanged -= RefreshBattleAvailability;
            if (turnController != null) turnController.TurnStateChanged -= RefreshBattleAvailability;
            if (instance == this) instance = null;
        }
    }
}

