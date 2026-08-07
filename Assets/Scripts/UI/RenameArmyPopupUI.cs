using System;
using Game.Map;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Small popup opened from ArmyViewerModalUI's context button when viewing anything other
    // than the garrison (which gets Create Army there instead — see
    // ArmyViewerModalUI.OnContextButtonClicked). Renders on top of the still-open modal (it's a
    // child of the modal's own panel in the scene) rather than closing it. Same stock
    // TMP_InputField structure and live-update-per-keystroke behaviour as PlayerRowUI's
    // nickname field — no separate "confirm" semantics beyond just closing the popup.
    public class RenameArmyPopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_InputField nameField;
        [SerializeField] private Button confirmButton;

        private ArmyData _army;
        private Action _onRenamed;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        // Lets ArmyViewerModalUI relay this into its own VisibilityChanged (see there) — read by
        // GameTurnController.CardDraggingBlocked via ArmyViewerModalUI.IsRenamePopupShowing.
        public event Action VisibilityChanged;

        private void Awake()
        {
            if (confirmButton != null)
                confirmButton.onClick.AddListener(Hide);
        }

        public void Show(ArmyData army, Action onRenamed)
        {
            _army = army;
            _onRenamed = onRenamed;

            if (panelRoot != null)
                panelRoot.SetActive(true);
            if (nameField != null)
            {
                nameField.SetTextWithoutNotify(army != null ? army.Name : string.Empty);
                nameField.onValueChanged.RemoveAllListeners();
                nameField.onValueChanged.AddListener(OnValueChanged);
            }
            VisibilityChanged?.Invoke();
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
            VisibilityChanged?.Invoke();
        }

        private void OnValueChanged(string value)
        {
            if (_army != null)
                _army.Name = value;
            _onRenamed?.Invoke();
        }
    }
}
