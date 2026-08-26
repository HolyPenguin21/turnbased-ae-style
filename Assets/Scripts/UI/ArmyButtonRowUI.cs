using System;
using System.Collections.Generic;
using Game.Core;
using Game.Map;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // A row of ArmyButtonUI, one per army — shared between two contexts: the hex-side row
    // (HexSelectionController.SelectHex swaps it in for ArmyInfoPanelUI once a hex has 2+
    // armies) and the row embedded inside ArmyViewerModalUI (for switching which army is shown
    // without closing it, and as the drag-and-drop target for moving units between armies).
    // Same instantiate-per-item + tracked-list-cleared-before-every-render pattern as
    // TurnOrderPopupUI's dice rows, windowed by maxVisible/scroll buttons once there are more
    // armies than fit — same idea as CardHandUI's own scrollLeftButton/scrollRightButton.
    public class ArmyButtonRowUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Transform buttonContainer;
        [SerializeField] private GameConfig gameConfig;
        // Left unwired (null) on instances that don't need paging — the hex-side row currently
        // has no scroll buttons in the scene, so maxVisible effectively never kicks in there.
        [SerializeField] private Button scrollLeftButton;
        [SerializeField] private Button scrollRightButton;
        [SerializeField] private int maxVisible = 99;

        private readonly List<ArmyButtonUI> _buttons = new List<ArmyButtonUI>();
        private List<ArmyData> _armies = new List<ArmyData>();
        private Action<ArmyData> _onArmyClicked;
        private ArmyData _selectedArmy;
        private bool _showStats;
        private int _scrollOffset;

        public IReadOnlyList<ArmyButtonUI> Buttons => _buttons;

        // The modal owns the "eight direct entries" UX rule; the map-side row can retain its
        // independently configured capacity and never inherits this presentation constraint.
        public void SetMaxVisible(int value)
        {
            maxVisible = Mathf.Max(1, value);
        }

        private void Awake()
        {
            if (scrollLeftButton != null)
                scrollLeftButton.onClick.AddListener(() => Scroll(-1));
            if (scrollRightButton != null)
                scrollRightButton.onClick.AddListener(() => Scroll(1));
        }

        // selectedArmy/showStats are only meaningful for the hex-side "pick an army to move"
        // use (see HexSelectionController.RefreshArmyButtonRow) — the in-modal row switching
        // which army is displayed just leaves them at their defaults.
        public void Show(IReadOnlyList<ArmyData> armies, Action<ArmyData> onArmyClicked, ArmyData selectedArmy = null, bool showStats = false)
        {
            _armies = armies != null ? new List<ArmyData>(armies) : new List<ArmyData>();
            _onArmyClicked = onArmyClicked;
            _selectedArmy = selectedArmy;
            _showStats = showStats;
            _scrollOffset = 0;

            Render();
            if (panelRoot != null)
                panelRoot.SetActive(true);
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
            ClearButtons();
        }

        private void Scroll(int direction)
        {
            _scrollOffset = Mathf.Clamp(_scrollOffset + direction, 0, Mathf.Max(0, _armies.Count - maxVisible));
            Render();
        }

        private void Render()
        {
            ClearButtons();
            if (buttonContainer != null && gameConfig != null && gameConfig.armyButtonPrefab != null)
            {
                int end = Mathf.Min(_armies.Count, _scrollOffset + maxVisible);
                for (int i = _scrollOffset; i < end; i++)
                {
                    ArmyData army = _armies[i];
                    ArmyButtonUI button = Instantiate(gameConfig.armyButtonPrefab, buttonContainer);
                    // The garrison always stays clickable (it opens its modal, never a move
                    // selection — see HexSelectionController.OnArmyButtonClicked), regardless
                    // of what's currently selected.
                    bool selected = _showStats && army == _selectedArmy && !army.IsGarrison;
                    button.Setup(army, _onArmyClicked, selected, _showStats);
                    _buttons.Add(button);
                }
            }

            bool needsPaging = _armies.Count > maxVisible;
            if (scrollLeftButton != null)
            {
                scrollLeftButton.gameObject.SetActive(needsPaging);
                scrollLeftButton.interactable = _scrollOffset > 0;
            }
            if (scrollRightButton != null)
            {
                scrollRightButton.gameObject.SetActive(needsPaging);
                scrollRightButton.interactable = _scrollOffset + maxVisible < _armies.Count;
            }
        }

        private void ClearButtons() => UIListUtility.DestroyAndClear(_buttons);
    }
}
