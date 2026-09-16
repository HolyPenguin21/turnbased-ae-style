using System;
using Game.Players;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Single blocking modal used for both turn handoff ("Current turn: X" for AI/Neutral, no
    // button; "Your turn, X" + Confirm for the human — the Confirm click is what actually
    // unlocks map input, see GameTurnController.OnTurnConfirmed / HexSelectionController.
    // IsInputAllowed) and one-off "you can't do that" hints (aviation damage reports, stealth
    // detection notices — a caller-supplied message whose Confirm just closes the popup, see
    // GameTurnController.ShowSpawnHint, CardHandUI's card-play flow). Space bar does the same
    // thing as clicking Confirm, and the panel jumps to the front of the shared Canvas on every
    // show so it always wins over any other already-open modal.
    public class PopupPanelUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text infoText;
        [SerializeField] private Button confirmButton;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        // Lets GameTurnController react to this popup opening/closing instead of polling
        // IsShowing every frame (see GameTurnController.InputBlocked/CardDraggingBlocked).
        public event Action VisibilityChanged;
        public event Action Hidden;

        private void Update()
        {
            if (confirmButton == null || !confirmButton.gameObject.activeInHierarchy || !confirmButton.interactable)
                return;
            if (UIFocusUtility.WasSpacePressed())
                confirmButton.onClick.Invoke();
        }

        // AI/Neutral turn — informational only, no Confirm button.
        public void ShowForOther(PlayerSetupData player)
        {
            SetButton(false, null);
            Display($"Current turn: {NameOf(player)}");
        }

        // Human turn — Confirm is what unlocks map input; hiding the popup is left to onConfirm
        // (see GameTurnController.OnTurnConfirmed), not done automatically here.
        public void ShowForHuman(PlayerSetupData player, Action onConfirm)
        {
            SetButton(true, () => onConfirm?.Invoke());
            Display($"Your turn, {NameOf(player)}");
        }

        // A one-off blocking hint — dismissing it just closes it, no external callback.
        public void ShowHint(string message)
        {
            SetButton(true, Hide);
            Display(message);
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
            VisibilityChanged?.Invoke();
            Hidden?.Invoke();
        }

        private void SetButton(bool visible, Action onClick)
        {
            if (confirmButton == null)
                return;
            confirmButton.gameObject.SetActive(visible);
            confirmButton.onClick.RemoveAllListeners();
            if (onClick != null)
                confirmButton.onClick.AddListener(() => onClick());
        }

        private void Display(string message)
        {
            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                // Sibling order is draw order on this project's single shared Canvas — this can
                // be triggered while another modal (e.g. ArmyViewerModalUI, itself already
                // forced to the front on open — see its own ActivatePanel) is already showing,
                // most commonly a failed card drop into it. A blocking popup always needs to win
                // that fight, not end up hidden behind whatever's already open.
                panelRoot.transform.SetAsLastSibling();
            }
            if (infoText != null)
                infoText.text = message;
            VisibilityChanged?.Invoke();
        }

        // null represents Neutral everywhere else in the turn system (see GameTurnController).
        private static string NameOf(PlayerSetupData player) => player != null ? player.Nickname : "Neutral";
    }
}
