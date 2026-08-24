using System;
using System.Collections;
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
        private Coroutine _autoCloseRoutine;

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

        // autoCloseNoHuman: per the user's own request — an AI-vs-AI (or AI-vs-neutrals/event
        // guard) battle has nobody at the keyboard to press Ok, so this outcome would otherwise
        // sit on screen forever while the AI's turn stalls behind it. Callers pass their own
        // `_localArmy == null` check (no human participated in this battle) for this. Closes on
        // the very next frame rather than pausing to be readable (2026-08-24, superseding the
        // 2026-08-21 spectator-pacing delay this used to carry) — the user's own call was that
        // speed now matters more than a spectator getting to read this screen.
        public void Show(string title, string message, Action onOk, bool autoCloseNoHuman = false)
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

            if (_autoCloseRoutine != null)
                StopCoroutine(_autoCloseRoutine);
            _autoCloseRoutine = autoCloseNoHuman ? StartCoroutine(AutoCloseAfterDelay()) : null;
        }

        private IEnumerator AutoCloseAfterDelay()
        {
            // A single frame, not a real pause — still async (so Show()'s own caller finishes
            // unwinding first) but no longer paced for a spectator to read (see this field group's
            // own comment above).
            yield return null;
            _autoCloseRoutine = null;
            OnOkClicked();
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
            if (_autoCloseRoutine != null)
            {
                StopCoroutine(_autoCloseRoutine);
                _autoCloseRoutine = null;
            }
            if (panelRoot != null)
                panelRoot.SetActive(false);
        }
    }
}
