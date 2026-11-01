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

        // Message area while the Confirm button is hidden ("Current turn: X"), as anchors in the
        // panel's normalized space; the text stretches to them with zero offsets.
        [Header("Message area without button")]
        [SerializeField] private Vector2 noButtonTextAnchorMin = new Vector2(0.0508f, 0.0325f);
        [SerializeField] private Vector2 noButtonTextAnchorMax = new Vector2(0.9582f, 0.594f);

        private bool _textLayoutCaptured;
        private Vector2 _textAnchorMin, _textAnchorMax, _textOffsetMin, _textOffsetMax;
        private VerticalAlignmentOptions _textVerticalAlignment;

        private Game.Core.MatchDecision decision;
        public bool HasPendingDecision => decision?.IsPending == true;
        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;
        private bool _isOtherTurn;
        public bool IsOtherTurnShowing => IsShowing && _isOtherTurn;

        // Lets GameTurnController react to this popup opening/closing instead of polling
        // IsShowing every frame (see GameTurnController.InputBlocked/CardDraggingBlocked).
        public event Action VisibilityChanged;
        public event Action Hidden;
        public event Action HumanTurnShown;

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
            decision?.Cancel();
            _isOtherTurn = true;
            SetButton(false, null);
            Display($"Current turn: {NameOf(player)}");
        }

        // Human turn — Confirm is what unlocks map input; hiding the popup is left to onConfirm
        // (see GameTurnController.OnTurnConfirmed), not done automatically here.
        public void ShowForHuman(PlayerSetupData player, Action onConfirm)
        {
            _isOtherTurn = false;
            decision?.Cancel();var request=decision=new Game.Core.MatchDecision();
            SetButton(true, () => {if(request.TryComplete())onConfirm?.Invoke();});
            Display($"Your turn, {NameOf(player)}");
            if (player != null && player.IsHuman) HumanTurnShown?.Invoke();
        }

        // A one-off blocking hint — dismissing it just closes it, no external callback.
        public void ShowHint(string message)
        {
            _isOtherTurn = false;
            decision?.Cancel();var request=decision=new Game.Core.MatchDecision();
            SetButton(true, () => {if(request.TryComplete())Hide();});
            Display(message);
        }

        public void Hide()
        {
            decision?.Cancel();
            _isOtherTurn = false;
            if (panelRoot != null)
                panelRoot.SetActive(false);
            VisibilityChanged?.Invoke();
            Hidden?.Invoke();
        }

        // Inspector tweaks during Play apply to the message that is already showing.
        private void OnValidate()
        {
            if (Application.isPlaying && _textLayoutCaptured && IsShowing && confirmButton != null && !confirmButton.gameObject.activeSelf)
                SetTextLayout(false);
        }

        private void SetButton(bool visible, Action onClick)
        {
            SetTextLayout(visible);
            if (confirmButton == null)
                return;
            confirmButton.gameObject.SetActive(visible);
            Game.UI.UIUtility.ResetRuntimeListeners(confirmButton);
            if (onClick != null)
                confirmButton.onClick.AddListener(() => onClick());
        }

        private void SetTextLayout(bool hasButton)
        {
            if (infoText == null)
                return;
            RectTransform rect = infoText.rectTransform;
            if (!_textLayoutCaptured)
            {
                _textAnchorMin = rect.anchorMin;
                _textAnchorMax = rect.anchorMax;
                _textOffsetMin = rect.offsetMin;
                _textOffsetMax = rect.offsetMax;
                _textVerticalAlignment = infoText.verticalAlignment;
                _textLayoutCaptured = true;
            }

            if (hasButton)
            {
                rect.anchorMin = _textAnchorMin;
                rect.anchorMax = _textAnchorMax;
                rect.offsetMin = _textOffsetMin;
                rect.offsetMax = _textOffsetMax;
                infoText.verticalAlignment = _textVerticalAlignment;
                return;
            }

            rect.anchorMin = noButtonTextAnchorMin;
            rect.anchorMax = noButtonTextAnchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            infoText.verticalAlignment = VerticalAlignmentOptions.Middle;
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
