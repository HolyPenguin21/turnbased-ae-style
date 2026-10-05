using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    [DefaultExecutionOrder(-10000)]
    public sealed class GameMenuPanelUI : MonoBehaviour
    {
        [SerializeField] private GameObject blockingRoot;
        [SerializeField] private GameObject menuPanel;
        [SerializeField] private GameSettingsPanelUI optionsPanel;
        [SerializeField] private BattleScreenUI battleScreen;
        [SerializeField] private Button gearButton;
        [SerializeField] private Button optionsButton;
        [SerializeField] private Button saveButton;
        [SerializeField] private Button loadButton;
        [SerializeField] private Button continueButton;
        private Game.Turns.GameTurnController turnController;
        private bool MenuAvailable => !BattleActive && turnController != null && turnController.CurrentPlayer != null && turnController.CurrentPlayer.IsHuman;
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
            if (optionsButton != null) optionsButton.onClick.AddListener(OpenOptions);
            if (continueButton != null) continueButton.onClick.AddListener(ContinueGame);
            if (optionsPanel != null) optionsPanel.Closed += OnOptionsClosed;
            if (battleScreen != null) battleScreen.VisibilityChanged += RefreshBattleAvailability;
            if (saveButton != null) saveButton.interactable = false;
            if (loadButton != null) loadButton.interactable = false;
            RefreshBattleAvailability();
        }
        private void RefreshBattleAvailability()
        {
            if (!MenuAvailable && IsShowing) ContinueGame();
            if (gearButton != null)
            {
                gearButton.gameObject.SetActive(!IsShowing);
                gearButton.interactable = MenuAvailable;
            }
        }
        public void OpenMenu()
        {
            if (!MenuAvailable || !isActiveAndEnabled || IsShowing || blockingRoot == null || menuPanel == null) return;
            instance = this; // Also supports entering Play Mode without domain/scene reload.
            previousSelection = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            IsShowing = true;
            BlockGameplayCanvases();
            if (gearButton != null) gearButton.gameObject.SetActive(false);
            blockingRoot.SetActive(true);
            menuPanel.SetActive(true);
            if (optionsPanel != null) optionsPanel.gameObject.SetActive(false);
            VisibilityChanged?.Invoke();
            optionsButton?.Select();
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
        private void Update()
        {
            if (!MenuAvailable) return;
            if (Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame || UIFocusUtility.IsTextFieldFocused()) return;
            Game.Audio.GameAudioManager.Instance?.PlayClick();
            blockedThroughFrame = Time.frameCount;
            if (!IsShowing) OpenMenu();
            else if (optionsPanel != null && optionsPanel.gameObject.activeSelf) optionsPanel.Close();
            else ContinueGame();
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
            foreach (var state in groups)
                if (state.Group != null) { state.Group.interactable = state.Interactable; state.Group.blocksRaycasts = state.BlocksRaycasts; }
            groups.Clear();
        }
        private void OnDisable()
        {
            ContinueGame();
            RestoreGameplayCanvases();
            if (gearButton != null) gearButton.onClick.RemoveListener(OpenMenu);
            if (optionsButton != null) optionsButton.onClick.RemoveListener(OpenOptions);
            if (continueButton != null) continueButton.onClick.RemoveListener(ContinueGame);
            if (optionsPanel != null) optionsPanel.Closed -= OnOptionsClosed;
            if (battleScreen != null) battleScreen.VisibilityChanged -= RefreshBattleAvailability;
            if (turnController != null) turnController.TurnStateChanged -= RefreshBattleAvailability;
            if (instance == this) instance = null;
        }
    }
}
