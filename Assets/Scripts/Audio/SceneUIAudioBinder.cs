using System;
using System.Collections.Generic;
using Game.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.Audio
{
    public sealed class SceneUIAudioBinder : MonoBehaviour
    {
        private sealed class PopupBinding
        {
            public Component Owner;
            public Func<bool> IsShowing;
            public Action<Action> Remove;
            public Action Handler;
            public bool WasShowing;
        }
        private readonly Dictionary<Component, PopupBinding> popups = new Dictionary<Component, PopupBinding>();
        private readonly List<Component> removed = new List<Component>();
        private readonly List<AudioListener> removedListeners = new List<AudioListener>();
        private readonly Dictionary<AudioListener, bool> listenerStates = new Dictionary<AudioListener, bool>();
        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }
        public void ResetBindings()
        {
            OnDisable(); OnEnable(); Start();
        }
        private void Start()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) BindScene(SceneManager.GetSceneAt(i));
            RefreshListener();
        }
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) { BindScene(scene); RefreshListener(); }
        private void OnActiveSceneChanged(Scene previous, Scene next) => RefreshListener();
        public void BindScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            foreach (var root in scene.GetRootGameObjects()) BindSubtree(root);
        }
        public void BindSubtree(GameObject root)
        {
            if (root == null) return;
            BindButtons(root);
            foreach (var popup in root.GetComponentsInChildren<BattleContactPopupUI>(true))
                BindPopup(popup, () => popup.IsShowing, h => popup.VisibilityChanged += h, h => popup.VisibilityChanged -= h);
            foreach (var popup in root.GetComponentsInChildren<EventChoicePopupUI>(true))
                BindPopup(popup, () => popup.IsShowing, h => popup.VisibilityChanged += h, h => popup.VisibilityChanged -= h);
            foreach (var popup in root.GetComponentsInChildren<AaChoicePopupUI>(true))
                BindPopup(popup, () => popup.IsShowing, h => popup.VisibilityChanged += h, h => popup.VisibilityChanged -= h);
            foreach (var popup in root.GetComponentsInChildren<PopupPanelUI>(true))
                BindHumanTurnPopup(popup);
        }
        public void BindButton(Button button)
        {
            if (button == null) return;
            var sound = button.GetComponent<UIButtonSound>();
            if (sound == null) sound = button.gameObject.AddComponent<UIButtonSound>();
            sound.Bind(button);
        }
        private static void BindButtons(GameObject root)
        {
            foreach (var button in root.GetComponentsInChildren<Button>(true))
            {
                var sound = button.GetComponent<UIButtonSound>();
                if (sound == null) sound = button.gameObject.AddComponent<UIButtonSound>();
                sound.Bind(button);
            }
        }
        public static void BindCreatedRoot(Component root)
        {
            if (root == null) return;
            var manager = GameAudioManager.Instance;
            var binder = manager != null ? manager.GetComponent<SceneUIAudioBinder>() : null;
            if (binder != null) binder.BindSubtree(root.gameObject);
            else BindButtons(root.gameObject);
        }
        private void BindHumanTurnPopup(PopupPanelUI popup)
        {
            if (popups.ContainsKey(popup)) return;
            var binding = new PopupBinding { Owner = popup, Remove = h => popup.HumanTurnShown -= h };
            binding.Handler = () => GameAudioManager.Instance?.PlayInfo();
            popups.Add(popup, binding);
            popup.HumanTurnShown += binding.Handler;
        }
        private void BindPopup(Component owner, Func<bool> isShowing, Action<Action> add, Action<Action> remove)
        {
            if (popups.ContainsKey(owner)) return;
            var binding = new PopupBinding { Owner = owner, IsShowing = isShowing, Remove = remove, WasShowing = isShowing() };
            binding.Handler = () =>
            {
                if (owner == null) return;
                bool showing = binding.IsShowing();
                bool opened = showing && !binding.WasShowing;
                binding.WasShowing = showing;
                if (opened) GameAudioManager.Instance?.PlayInfo();
            };
            popups.Add(owner, binding); add(binding.Handler);
        }
        private void OnSceneUnloaded(Scene scene)
        {
            removed.Clear();
            foreach (var pair in popups)
                if (pair.Key == null || pair.Key.gameObject.scene == scene) removed.Add(pair.Key);
            foreach (var owner in removed)
            {
                var binding = popups[owner];
                if (binding.Owner != null) binding.Remove(binding.Handler);
                popups.Remove(owner);
            }
            removedListeners.Clear();
            foreach (var pair in listenerStates)
                if (pair.Key == null || pair.Key.gameObject.scene == scene) removedListeners.Add(pair.Key);
            foreach (var listener in removedListeners) listenerStates.Remove(listener);
            RefreshListener();
        }
        private void RefreshListener()
        {
            var listeners = Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Exclude);
            foreach (var listener in listeners)
                if (!listenerStates.ContainsKey(listener)) listenerStates.Add(listener, listener.enabled);
            var active = SceneManager.GetActiveScene();
            AudioListener chosen = null;
            int mainCameraCount = 0, cameraCount = 0;
            AudioListener ordinary = null;
            foreach (var listener in listeners)
            {
                var camera = listener.GetComponent<Camera>();
                if (!listenerStates[listener] || listener.gameObject.scene != active || camera == null || !camera.isActiveAndEnabled) continue;
                cameraCount++; ordinary = listener;
                if (camera.CompareTag("MainCamera")) { chosen = listener; mainCameraCount++; }
            }
            if (mainCameraCount > 1 || (mainCameraCount == 0 && cameraCount > 1))
            {
                Debug.LogError("Ambiguous AudioListener cameras in active scene: " + active.name);
                return;
            }
            if (chosen == null) chosen = ordinary;
            if (chosen == null) return; // Synthetic/test scenes can legitimately have no camera.
            foreach (var listener in listeners) listener.enabled = listener == chosen;
        }
        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            foreach (var binding in popups.Values) if (binding.Owner != null) binding.Remove(binding.Handler);
            popups.Clear();
            foreach (var pair in listenerStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
            listenerStates.Clear();
        }
    }
}
