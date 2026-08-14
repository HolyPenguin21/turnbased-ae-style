using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // The battle-ends announcement — "Victory!"/"Defeat" + OK, per the manual's own "Battle
    // Results" (one side has no more combat-capable units left). Same panelRoot+text+button shape
    // as BattleArrangePopupUI, just with a caller-supplied message instead of a fixed one.
    public class BattleOutcomePopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        // Short headline (Victory!/Defeat!/Attacker wins./Draw./...) — messageText's original
        // sole purpose (see the scene's own "Title" GameObject, renamed by the user from
        // MessageText; the SerializeField link here survived that rename unchanged since Unity
        // resolves it by fileID, not name).
        [SerializeField] private TMP_Text messageText;
        // The longer breakdown added per the user's own request: which army beat which, then on
        // its own line what happens once this popup closes (chain into the next battle already
        // waiting on this hex, or return to the map) — see BattleScreenUI.Combat.cs's
        // FinishBattleEnd/BattleScreenUI.Retreat.cs's ResolveRetreat, the only two callers.
        [SerializeField] private TMP_Text messageDetailText;
        [SerializeField] private Button okButton;

        private Action _onOk;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        private void Awake()
        {
            if (okButton != null)
                okButton.onClick.AddListener(OnOkClicked);
        }

        // Space as a shortcut for Ok, per the user's own request — same UIFocusUtility pattern
        // already used by BattleArrangePopupUI/BattleAttackPopupUI's own Result state.
        private void Update()
        {
            if (IsShowing && UIFocusUtility.WasSpacePressed())
                OnOkClicked();
        }

        public void Show(string title, string message, Action onOk)
        {
            _onOk = onOk;
            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            if (messageText != null)
                messageText.text = title;
            if (messageDetailText != null)
                messageDetailText.text = message;
        }

        private void OnOkClicked()
        {
            Hide();
            Action callback = _onOk;
            _onOk = null;
            callback?.Invoke();
        }

        public void Hide()
        {
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }
    }
}
