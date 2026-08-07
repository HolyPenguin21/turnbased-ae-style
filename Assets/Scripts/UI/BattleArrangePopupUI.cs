using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    // The very first thing shown when a battle opens for the local human side — "Arrange your
    // units for the battle" + Ok. Same panelRoot+button shape as SpawnHintPopupUI, but with a
    // caller-supplied callback instead of a bare dismiss (see BattleScreenUI.Show, which uses
    // the Ok click to actually enter the Arrangement phase rather than just closing a hint).
    public class BattleArrangePopupUI : MonoBehaviour
    {
        [SerializeField] private GameObject panelRoot;
        [SerializeField] private Button okButton;

        private Action _onOk;

        public bool IsShowing => panelRoot != null && panelRoot.activeSelf;

        private void Awake()
        {
            if (okButton != null)
                okButton.onClick.AddListener(OnOkClicked);
        }

        // Space as a shortcut for Ok, per the user's own request — this is the very first thing
        // a battle shows, so there's no text field anywhere yet that could be capturing Space
        // instead (unlike RtsCameraController's WASD, which does need that guard).
        private void Update()
        {
            if (IsShowing && Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                OnOkClicked();
        }

        public void Show(Action onOk)
        {
            _onOk = onOk;
            if (panelRoot != null)
            {
                panelRoot.SetActive(true);
                panelRoot.transform.SetAsLastSibling();
            }
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
