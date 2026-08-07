using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    // A blocking "you can't do that" hint — same shape as TurnInfoPopupUI (panelRoot + infoText
    // + confirmButton, Space-bar shortcut) but with a caller-supplied message instead of turn
    // text, and no callback: dismissing it just closes it. GameTurnController.InputBlocked
    // reflects IsShowing so map/card input stays locked out (like a turn handoff) until the
    // player acknowledges it — see CardHandUI's card-play flow, the only caller so far.
    public class SpawnHintPopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private TMP_Text infoText;
        [SerializeField] private Button confirmButton;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        // Relies solely on the scene's own initial inactive state (m_IsActive: 0) for "hidden
        // until shown" — panelRoot IS this component's own GameObject, so calling
        // SetActive(false) here would be actively wrong: Awake() only runs the moment
        // something FIRST activates this (inactive) GameObject — i.e. inside Show()'s own
        // SetActive(true) — so a SetActive(false) here would immediately undo that same
        // activation and the popup would never appear the first time Show() was called.
        private void Awake()
        {
            if (confirmButton != null)
                confirmButton.onClick.AddListener(Hide);
        }

        private void Update()
        {
            if (confirmButton == null || !confirmButton.gameObject.activeInHierarchy || !confirmButton.interactable)
                return;
            if (Keyboard.current == null)
                return;
            if (Keyboard.current.spaceKey.wasPressedThisFrame)
                Hide();
        }

        public void Show(string message)
        {
            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                // Sibling order is draw order on this project's single shared Canvas — this can
                // be triggered while another modal (e.g. ArmyViewerModalUI, itself already
                // forced to the front on open — see its own ActivatePanel) is already showing,
                // most commonly a failed card drop into it. A blocking hint always needs to win
                // that fight, not end up hidden behind whatever's already open.
                panelRoot.transform.SetAsLastSibling();
            }
            if (infoText != null)
                infoText.text = message;
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }
    }
}
