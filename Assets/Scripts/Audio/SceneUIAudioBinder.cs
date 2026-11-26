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
        private readonly Dictionary<MonoBehaviour, IInfoSoundSource> cues = new Dictionary<MonoBehaviour, IInfoSoundSource>();
        private readonly List<MonoBehaviour> removed = new List<MonoBehaviour>();
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
            if (!isActiveAndEnabled) return;
            OnDisable();
            OnEnable();
            Start();
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
            foreach (var view in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!(view is IInfoSoundSource source) || cues.ContainsKey(view)) continue;
                cues.Add(view, source);
                source.InfoSoundRequested += PlayInfo;
            }
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
        private static void PlayInfo() => GameAudioManager.Instance?.PlayInfo();

        private void OnSceneUnloaded(Scene scene)
        {
            removed.Clear();
            foreach (var pair in cues)
                if (pair.Key == null || pair.Key.gameObject.scene == scene) removed.Add(pair.Key);
            foreach (var owner in removed)
            {
                cues[owner].InfoSoundRequested -= PlayInfo;
                cues.Remove(owner);
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
            foreach (var source in cues.Values) source.InfoSoundRequested -= PlayInfo;
            cues.Clear();
            foreach (var pair in listenerStates) if (pair.Key != null) pair.Key.enabled = pair.Value;
            listenerStates.Clear();
        }
    }
}
