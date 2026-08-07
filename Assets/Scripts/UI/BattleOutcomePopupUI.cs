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
        [SerializeField] private TMP_Text messageText;
        [SerializeField] private Button okButton;

        private Action _onOk;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        private void Awake()
        {
            if (okButton != null)
                okButton.onClick.AddListener(OnOkClicked);
        }

        public void Show(string message, Action onOk)
        {
            _onOk = onOk;
            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
            if (messageText != null)
                messageText.text = message;
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
