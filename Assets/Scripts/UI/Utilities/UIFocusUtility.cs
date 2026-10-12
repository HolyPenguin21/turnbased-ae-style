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
        private static readonly System.Collections.Generic.HashSet<UnityEngine.Object> overlays = new System.Collections.Generic.HashSet<UnityEngine.Object>();
        private static int blockedThroughFrame = -1;
        public static event System.Action BlockingChanged;
        public static bool HasOverlay { get { overlays.RemoveWhere(o => o == null); return overlays.Count > 0; } }
        public static void SetOverlay(UnityEngine.Object owner, bool showing)
        {
            bool changed = showing ? overlays.Add(owner) : overlays.Remove(owner);
            if (!showing) blockedThroughFrame = Time.frameCount;
            if (changed) BlockingChanged?.Invoke();
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOverlays() { overlays.Clear(); BlockingChanged = null; blockedThroughFrame = -1; }
        public static bool IsGameplayInputBlocked => HasOverlay || blockedThroughFrame == Time.frameCount || GameMenuPanelUI.GameplayInputBlocked;
        public static bool IsGameplayShortcutBlocked => IsGameplayInputBlocked || GameMenuPanelUI.OwnsKeyboardSelection;
        public static bool IsTextFieldFocused()
        {
            GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return selected != null && selected.GetComponent<TMP_InputField>() != null;
        }

        // The "Space bar as a shortcut for whatever this popup's own primary button does" check,
        // duplicated identically across half a dozen popups (PopupPanelUI,
        // BattleArrangePopupUI, TurnOrderPopupUI, MainMenuController) before being pulled out
        // here — each caller still owns its OWN guard conditions (is the popup showing, is the
        // button interactable, etc.), only the actual key-poll was ever truly identical.
        public static bool WasSpacePressed() => !IsGameplayShortcutBlocked && Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
    }
}

