using System;
using Game.Players;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    // Shown for every AI/Neutral turn ("Current turn: X", no button) and for the human's own
    // turn until they dismiss it ("Your turn, X" + Confirm). The Confirm click is what actually
    // unlocks map input for the human — see GameTurnController.OnTurnConfirmed /
    // HexSelectionController.IsInputAllowed — not just "it became their turn".
    public class TurnInfoPopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text infoText;
        [SerializeField] private Button confirmButton;

        // Space bar does the same thing as clicking Confirm — same pattern as
        // GameTurnController's Enter-key shortcut for the end-turn button.
        private void Update()
        {
            if (confirmButton == null || !confirmButton.gameObject.activeInHierarchy || !confirmButton.interactable)
                return;
            if (Keyboard.current == null)
                return;
            if (Keyboard.current.spaceKey.wasPressedThisFrame)
                confirmButton.onClick.Invoke();
        }

        public void ShowForOther(PlayerSetupData player)
        {
            if (panelRoot != null)
                panelRoot.SetActive(true);
            if (infoText != null)
                infoText.text = $"Current turn: {NameOf(player)}";
            if (confirmButton != null)
                confirmButton.gameObject.SetActive(false);
        }

        public void ShowForHuman(PlayerSetupData player, Action onConfirm)
        {
            if (panelRoot != null)
                panelRoot.SetActive(true);
            if (infoText != null)
                infoText.text = $"Your turn, {NameOf(player)}";
            if (confirmButton != null)
            {
                confirmButton.gameObject.SetActive(true);
                confirmButton.onClick.RemoveAllListeners();
                confirmButton.onClick.AddListener(() => onConfirm?.Invoke());
            }
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }

        // null represents Neutral everywhere else in the turn system (see GameTurnController).
        private static string NameOf(PlayerSetupData player) => player != null ? player.Nickname : "Neutral";
    }
}
