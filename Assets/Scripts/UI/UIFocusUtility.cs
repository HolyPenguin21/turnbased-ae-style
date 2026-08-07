using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

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
    }
}
