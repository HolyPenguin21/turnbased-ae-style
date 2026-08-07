using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Game.UI
{
    // Shared by every global keyboard-polling system that must not fire while the player is
    // typing into a text field (GameSetupController's Start-Game shortcut, GameTurnController's
    // End-Turn shortcut, RtsCameraController's WASD pan) — none of those read input through the
    // UI event system, so a focused TMP_InputField doesn't stop them on its own; each has to
    // check this explicitly instead.
    public static class UIFocusUtility
    {
        public static bool IsTextFieldFocused()
        {
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.GetComponent<TMP_InputField>() != null;
        }

        // The "Space bar as a shortcut for whatever this popup's own primary button does" check,
        // duplicated identically across half a dozen popups (SpawnHintPopupUI, TurnInfoPopupUI,
        // BattleArrangePopupUI, TurnOrderPopupUI, MainMenuController) before being pulled out
        // here — each caller still owns its OWN guard conditions (is the popup showing, is the
        // button interactable, etc.), only the actual key-poll was ever truly identical.
        public static bool WasSpacePressed() => Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
    }
}
