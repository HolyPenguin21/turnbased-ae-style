using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.UI
{
    [Flags]
    internal enum ModalBlock
    {
        Map = 1,
        Camera = 2,
        Hand = 4,
        EndTurn = 8,
        Overlay = 16,
        Viewer = Map | EndTurn,
        Decision = Viewer | Hand,
        Full = Decision | Camera | Overlay
    }

    public static class UIFocusUtility
    {
        private sealed class Window
        {
            public MonoBehaviour Owner;
            public GameObject Root;
            public GameObject PreviousFocus;
            public Selectable DefaultFocus;
            public ModalBlock Blocks;
            public bool Nested;
            public Transform SceneControls;
            public List<Transform> SceneCanvases;
        }

        private struct GroupState
        {
            public bool Interactable;
            public bool BlocksRaycasts;
        }

        private static readonly List<Window> windows = new List<Window>();
        private static readonly Dictionary<CanvasGroup, GroupState> suspended = new Dictionary<CanvasGroup, GroupState>();
        private static int closedFrame = -1;

        public static event Action BlockingChanged;
        internal static uint InputGeneration { get; private set; }
        private static Window Top => windows.Count == 0 ? null : windows[windows.Count - 1];
        private static GameObject Selection => EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;

        internal static bool ClosedThisFrame => closedFrame == Time.frameCount;
        internal static bool HasModal { get { Prune(); return Top != null; } }
        public static bool HasOverlay => Blocks(ModalBlock.Overlay);
        public static bool IsGameplayInputBlocked => HasOverlay || ClosedThisFrame;
        public static bool IsGameplayShortcutBlocked => HasModal || ClosedThisFrame || GameMenuPanelUI.OwnsKeyboardSelection;

        internal static bool Blocks(ModalBlock capability)
        {
            Prune();
            foreach (var window in windows)
                if ((window.Blocks & capability) != 0) return true;
            return false;
        }

        internal static bool Contains<T>() where T : MonoBehaviour
        {
            Prune();
            foreach (var window in windows)
                if (window.Owner is T) return true;
            return false;
        }

        public static void SetOverlay(MonoBehaviour owner, bool showing)
            => SetModal(owner, showing && owner != null ? owner.gameObject : null, ModalBlock.Full);

        internal static void SetModal(MonoBehaviour owner, GameObject root,
            ModalBlock blocks = ModalBlock.Decision, Selectable defaultFocus = null, Transform sceneControls = null)
            => SetWindow(owner, root, blocks, defaultFocus, nested: false, sceneControls);

        internal static void SetNestedModal(MonoBehaviour owner, GameObject root, Selectable defaultFocus = null)
            => SetWindow(owner, root, ModalBlock.Full, defaultFocus, nested: true);

        private static void SetWindow(MonoBehaviour owner, GameObject root, ModalBlock blocks, Selectable focus, bool nested, Transform sceneControls = null)
        {
            if (ReferenceEquals(owner, null)) return;
            int index = windows.FindIndex(window => ReferenceEquals(owner, window.Owner) && window.Nested == nested);
            if (owner == null || root == null || !owner.isActiveAndEnabled || !root.activeInHierarchy)
            {
                if (index < 0) return;
                bool wasTop = windows[index] == Top;
                var previous = Remove(index);
                UpdateInput(wasTop, previous);
                return;
            }
            if (index >= 0) return;

            Prune();
            windows.Add(new Window
            {
                Owner = owner, Root = root, PreviousFocus = Selection,
                DefaultFocus = focus, Blocks = blocks, Nested = nested, SceneControls = sceneControls,
                SceneCanvases = sceneControls != null ? FindSceneCanvases(sceneControls) : null
            });
            UpdateInput(restoreFocus: true, preferred: null);
        }

        private static GameObject Remove(int index)
        {
            var window = windows[index];
            if (index + 1 < windows.Count)
            {
                var next = windows[index + 1];
                if (next.PreviousFocus == null || Contains(window.Root, next.PreviousFocus))
                    next.PreviousFocus = window.PreviousFocus;
            }
            windows.RemoveAt(index);
            closedFrame = Time.frameCount;
            return window.PreviousFocus;
        }

        private static void Prune()
        {
            Window formerTop = Top;
            GameObject previous = null;
            bool removed = false;
            for (int i = windows.Count - 1; i >= 0; i--)
            {
                var window = windows[i];
                if (window.Owner != null && window.Owner.isActiveAndEnabled
                    && window.Root != null && window.Root.activeInHierarchy) continue;
                var focus = Remove(i);
                if (window == formerTop || (removed && Contains(window.Root, previous))) previous = focus;
                removed = true;
            }
            if (removed) UpdateInput(formerTop != Top, previous);
        }

        private static bool Contains(GameObject root, GameObject child)
            => root != null && child != null && child.transform.IsChildOf(root.transform);

        private static void UpdateInput(bool restoreFocus, GameObject preferred)
        {
            RestoreGroups();
            var top = Top;
            if (top != null)
                foreach (var window in windows)
                {
                    if (window.SceneControls != null)
                        foreach (var canvas in window.SceneCanvases)
                            if (canvas != null && !canvas.IsChildOf(window.SceneControls))
                                SuspendBranch(canvas, top.Root.transform, window.SceneControls);
                    if (window != top && !Contains(top.Root, window.Root))
                        SuspendBranch(window.Root.transform, top.Root.transform);
                }
            InputGeneration++;
            if (restoreFocus) Focus(preferred, top);
            BlockingChanged?.Invoke();
        }

        private static List<Transform> FindSceneCanvases(Transform controls)
        {
            var roots = new List<Transform>();
            foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include))
            {
                if (canvas.gameObject.scene != controls.gameObject.scene) continue;
                var parent = canvas.transform.parent;
                if (parent == null || parent.GetComponentInParent<Canvas>(true) == null)
                    roots.Add(canvas.transform);
            }
            return roots;
        }

        private static void SuspendBranch(Transform branch, Transform top, Transform controls = null)
        {
            if (branch == top || branch == controls) return;
            if (top.IsChildOf(branch) || (controls != null && controls.IsChildOf(branch)))
            {
                foreach (Transform child in branch) SuspendBranch(child, top, controls);
                return;
            }

            var group = branch.GetComponent<CanvasGroup>();
            if (group == null) group = branch.gameObject.AddComponent<CanvasGroup>();
            Suspend(group);
            // Some authored groups opt out of their parent's raycast and navigation settings.
            foreach (var descendant in branch.GetComponentsInChildren<CanvasGroup>(true))
                if (descendant.ignoreParentGroups) Suspend(descendant);
        }

        private static void Suspend(CanvasGroup group)
        {
            if (suspended.ContainsKey(group)) return;
            suspended.Add(group, new GroupState { Interactable = group.interactable, BlocksRaycasts = group.blocksRaycasts });
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        private static void RestoreGroups()
        {
            foreach (var pair in suspended)
            {
                if (pair.Key == null) continue;
                pair.Key.interactable = pair.Value.Interactable;
                pair.Key.blocksRaycasts = pair.Value.BlocksRaycasts;
            }
            suspended.Clear();
        }

        private static GameObject ValidFocus(GameObject target, Window window)
        {
            if (target == null || !target.activeInHierarchy || (window != null && !Contains(window.Root, target))) return null;
            var control = target.GetComponent<Selectable>();
            return control != null && control.isActiveAndEnabled && control.IsInteractable() ? target : null;
        }

        private static void Focus(GameObject preferred, Window window)
        {
            var events = EventSystem.current;
            if (events == null || events.alreadySelecting) return;
            var target = ValidFocus(preferred, window) ?? ValidFocus(Selection, window);
            if (target == null && window != null)
            {
                target = ValidFocus(window.DefaultFocus != null ? window.DefaultFocus.gameObject : null, window);
                if (target == null)
                    foreach (var control in window.Root.GetComponentsInChildren<Selectable>())
                    {
                        target = ValidFocus(control.gameObject, window);
                        if (target != null) break;
                    }
            }
            if (target == null && window == null) target = ValidFocus(events.firstSelectedGameObject, null);
            if (events.currentSelectedGameObject != target) events.SetSelectedGameObject(target);
        }

        internal static void KeepFocusWithinModal()
        {
            Prune();
            if (Top != null && Selection != null && !Contains(Top.Root, Selection)) Focus(null, Top);
        }

        internal static bool IsTop(MonoBehaviour owner)
        {
            Prune();
            return Top != null && ReferenceEquals(Top.Owner, owner);
        }

        internal static bool CanHandleInput(MonoBehaviour owner) => IsTop(owner) && !ClosedThisFrame;
        internal static bool WasEscapePressed(MonoBehaviour owner)
            => CanHandleInput(owner) && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
        // Esc for scene-level input (not a window): only while no window owns input and none
        // closed this frame, so one press never closes a window and also cancels scene state.
        internal static bool WasSceneEscapePressed()
            => !HasModal && !ClosedThisFrame && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;

        public static bool IsTextFieldFocused()
        {
            var selected = Selection;
            return selected != null && selected.GetComponent<TMP_InputField>() != null;
        }
        public static bool WasSpacePressed()
            => !IsGameplayShortcutBlocked && Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame
                && !IsTextFieldFocused();

        internal static bool WasSpacePressed(MonoBehaviour owner)
            => CanHandleInput(owner) && Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame
                && !IsTextFieldFocused();

        private static void OnSceneUnloaded(Scene scene) => Prune();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOverlays()
        {
            RestoreGroups();
            windows.Clear();
            BlockingChanged = null;
            closedFrame = -1;
            InputGeneration = 0;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }
    }
}
