#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Audio;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class UIAudioLifecyclePlayModeTests
    {
        private readonly List<GameObject> objects = new List<GameObject>();
        private float master, music;
        private bool enabled;
        [UnitySetUp] public IEnumerator SetUp()
        {
            yield return new EnterPlayMode(); yield return null;
            Assert.NotNull(GameAudioManager.Instance);
            var settings = GameAudioManager.Instance.Settings;
            master = settings.MasterVolume; music = settings.MusicVolume; enabled = settings.MusicEnabled;
            settings.MasterVolume = 1f; settings.MusicEnabled = true; settings.MusicVolume = 0.5f;
        }
        [UnityTearDown] public IEnumerator TearDown()
        {
            foreach (var go in objects) if (go != null) Object.Destroy(go);
            objects.Clear(); Time.timeScale = 1f;
            if (GameAudioManager.Instance != null)
            {
                var settings = GameAudioManager.Instance.Settings;
                settings.MasterVolume = master; settings.MusicVolume = music; settings.MusicEnabled = enabled; settings.Save();
            }
            yield return null; yield return new ExitPlayMode();
        }
        private AudioSource UI => Get<AudioSource>(GameAudioManager.Instance, "uiSource");
        private AudioSource Music => Get<AudioSource>(GameAudioManager.Instance, "musicSource");
        private GameObject New(string name)
        {
            var go = new GameObject(name, typeof(RectTransform)); objects.Add(go); return go;
        }
        [UnityTest] public IEnumerator ResetAndReconfigureButtonStillPlaysSingleClick()
        {
            var go = New("runtime-button"); go.SetActive(false); var button = go.AddComponent<Button>();
            SceneUIAudioBinder.BindCreatedRoot(button); SceneUIAudioBinder.BindCreatedRoot(button);
            UIButtonEventUtility.ResetRuntimeListeners(button);
            int actions = 0; button.onClick.AddListener(() => { actions++; go.SetActive(false); });
            go.SetActive(true); UI.Stop(); button.onClick.Invoke();
            Assert.AreEqual(1, actions); Assert.IsTrue(UI.isPlaying);
            Assert.AreEqual(1, go.GetComponents<UIButtonSound>().Length);
            Assert.AreEqual(2, RuntimeCalls(button)); // one audio callback, one business action
            yield return null;
        }
        [UnityTest] public IEnumerator DisabledButtonPointerDoesNotPlay()
        {
            var button = New("disabled-button").AddComponent<Button>(); SceneUIAudioBinder.BindCreatedRoot(button);
            var es = EventSystem.current != null ? EventSystem.current : New("event-system").AddComponent<EventSystem>(); button.interactable = false;
            UI.Stop(); button.OnPointerClick(new PointerEventData(es) { button = PointerEventData.InputButton.Left });
            Assert.IsFalse(UI.isPlaying); yield return null;
        }
        [UnityTest] public IEnumerator MusicOffAndZeroVolumeDoNotMuteUI()
        {
            var manager = GameAudioManager.Instance;
            manager.Settings.MusicEnabled = false; manager.Settings.MusicVolume = 0f;
            UI.Stop(); manager.PlayClick(); Assert.IsTrue(UI.isPlaying); Assert.IsFalse(Music.isPlaying);
            yield return null;
            manager.Settings.MasterVolume = 0f;
            Assert.IsTrue(UI.mute); Assert.IsTrue(Music.mute);
            UI.Stop(); manager.PlayClick(); Assert.IsFalse(UI.isPlaying);
        }
        [UnityTest] public IEnumerator PauseResumeKeepsTrackAndTimeScaleDoesNotFreezeIt()
        {
            yield return new WaitForSecondsRealtime(0.3f);
            var clip = Music.clip; Assert.NotNull(clip);
            GameAudioManager.Instance.Settings.MusicEnabled = false;
            int position = Music.timeSamples;
            yield return new WaitForSecondsRealtime(0.05f);
            Assert.AreEqual(position, Music.timeSamples);
            GameAudioManager.Instance.Settings.MusicEnabled = true;
            Time.timeScale = 0f;
            yield return new WaitForSecondsRealtime(0.06f);
            Assert.AreEqual(clip, Music.clip); Assert.Greater(Music.timeSamples, position);
        }
        [UnityTest] public IEnumerator AllFourPopupTypesOnlySoundOnOpeningEdge()
        {
            foreach (Type type in new[] { typeof(PopupPanelUI), typeof(EventChoicePopupUI), typeof(AaChoicePopupUI), typeof(BattleContactPopupUI) })
            {
                var root = New(type.Name); root.SetActive(false);
                var controller = root.AddComponent(type); Set(controller, "panelRoot", root);
                SceneUIAudioBinder.BindCreatedRoot(controller); SceneUIAudioBinder.BindCreatedRoot(controller);
                UI.Stop(); Assert.IsFalse(UI.isPlaying); yield return null;
                root.SetActive(true); RaiseVisibility(controller); Assert.IsTrue(UI.isPlaying);
                yield return null; UI.Stop(); RaiseVisibility(controller); Assert.IsFalse(UI.isPlaying);
                root.SetActive(false); RaiseVisibility(controller); Assert.IsFalse(UI.isPlaying);
                yield return null; root.SetActive(true); RaiseVisibility(controller); Assert.IsTrue(UI.isPlaying);
                root.SetActive(false); RaiseVisibility(controller); yield return null;
            }
        }
        [UnityTest] public IEnumerator SceneUnloadReloadKeepsOneManagerAndBindings()
        {
            var manager = GameAudioManager.Instance;
            Scene scene = SceneManager.CreateScene("audio-lifecycle-scene");
            var button = New("scene-button").AddComponent<Button>(); SceneManager.MoveGameObjectToScene(button.gameObject, scene);
            manager.GetComponent<SceneUIAudioBinder>().BindScene(scene);
            Assert.AreEqual(1, button.GetComponents<UIButtonSound>().Length);
            yield return SceneManager.UnloadSceneAsync(scene);
            Assert.AreSame(manager, GameAudioManager.Instance);
            Assert.AreEqual(1, Object.FindObjectsByType<GameAudioManager>(FindObjectsSortMode.None).Length);
            scene = SceneManager.CreateScene("audio-lifecycle-scene");
            button = New("reloaded-scene-button").AddComponent<Button>();
            SceneManager.MoveGameObjectToScene(button.gameObject, scene);
            manager.GetComponent<SceneUIAudioBinder>().BindScene(scene);
            Assert.AreEqual(1, button.GetComponents<UIButtonSound>().Length);
            Assert.AreSame(manager, GameAudioManager.Instance);
            yield return SceneManager.UnloadSceneAsync(scene);
        }
        [UnityTest] public IEnumerator MainCameraListenerWinsAndUnloadedListenersAreRemoved()
        {
            var binder = GameAudioManager.Instance.GetComponent<SceneUIAudioBinder>();
            var previous = SceneManager.GetActiveScene();
            var scene = SceneManager.CreateScene("audio-listener-scene");
            AudioListener chosen = null;
            var listeners = new List<AudioListener>();
            for (int i = 0; i < 3; i++)
            {
                var camera = New("listener-camera-" + i); camera.AddComponent<Camera>();
                var listener = camera.AddComponent<AudioListener>(); listeners.Add(listener);
                SceneManager.MoveGameObjectToScene(camera, scene);
                if (i == 2) { camera.tag = "MainCamera"; chosen = listener; }
            }
            SceneManager.SetActiveScene(scene);
            Assert.IsTrue(chosen.enabled);
            Assert.IsFalse(listeners[0].enabled); Assert.IsFalse(listeners[1].enabled);
            SceneManager.SetActiveScene(previous);
            yield return SceneManager.UnloadSceneAsync(scene);
            var states = Get<Dictionary<AudioListener, bool>>(binder, "listenerStates");
            foreach (var listener in listeners) Assert.IsFalse(states.ContainsKey(listener));
        }
        [UnityTest] public IEnumerator SettingsPanelLoadsValuesWithoutChangingThem()
        {
            var manager = GameAudioManager.Instance;
            manager.Settings.MasterVolume = .31f; manager.Settings.MusicVolume = .63f; manager.Settings.MusicEnabled = false;
            var root = New("settings"); root.SetActive(false); var panel = root.AddComponent<GameSettingsPanelUI>();
            var masterSlider = New("master-slider").AddComponent<Slider>();
            var musicSlider = New("music-slider").AddComponent<Slider>();
            var toggle = New("music-toggle").AddComponent<Toggle>();
            Set(panel, "masterVolumeSlider", masterSlider); Set(panel, "musicVolumeSlider", musicSlider); Set(panel, "musicEnabledToggle", toggle);
            root.SetActive(true);
            Assert.AreEqual(.31f, masterSlider.value); Assert.AreEqual(.63f, musicSlider.value);
            Assert.IsFalse(toggle.isOn); Assert.IsFalse(musicSlider.interactable);
            masterSlider.value = .44f; Assert.AreEqual(.44f, manager.Settings.MasterVolume);
            toggle.isOn = true; Assert.IsTrue(manager.Settings.MusicEnabled); Assert.IsTrue(musicSlider.interactable);
            root.SetActive(false); yield return null;
        }
        [UnityTest] public IEnumerator BootstrapReinitializationDoesNotDuplicatePlaylistOrMuteUI()
        {
            var manager = GameAudioManager.Instance;
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            typeof(GameAudioManager).GetMethod("ResetStatics", flags).Invoke(null, null);
            typeof(GameAudioManager).GetMethod("Bootstrap", flags).Invoke(null, null);
            yield return null; yield return null;
            Assert.AreSame(manager, GameAudioManager.Instance);
            Assert.AreEqual(4, Get<List<AudioClip>>(manager, "playlist").Count);
            manager.Settings.MasterVolume = 1f;
            Assert.IsFalse(UI.mute);
            UI.Stop(); manager.PlayClick(); Assert.IsTrue(UI.isPlaying);
        }
        [UnityTest] public IEnumerator SettingsKeyboardSelectionMovesIntoPanelAndBack()
        {
            var es = EventSystem.current != null ? EventSystem.current : New("event-system").AddComponent<EventSystem>();
            var menu = New("menu-controller").AddComponent<MainMenuController>();
            var main = New("main-menu");
            var settingsButton = New("settings-button").AddComponent<Button>(); settingsButton.transform.SetParent(main.transform);
            var panelRoot = New("settings-panel"); panelRoot.SetActive(false);
            var panel = panelRoot.AddComponent<GameSettingsPanelUI>();
            var slider = New("master-slider").AddComponent<Slider>(); slider.transform.SetParent(panelRoot.transform);
            Set(menu, "mainMenuPanel", main); Set(menu, "settingsPanel", panelRoot); Set(menu, "settingsButton", settingsButton);
            Set(panel, "menuController", menu); Set(panel, "masterVolumeSlider", slider);
            settingsButton.Select(); menu.OnSettingsClicked();
            Assert.AreEqual(slider.gameObject, es.currentSelectedGameObject);
            panel.Close(); Assert.AreEqual(settingsButton.gameObject, es.currentSelectedGameObject);
            yield return null;
        }
        private static int RuntimeCalls(Button button)
        {
            var calls = typeof(UnityEngine.Events.UnityEventBase).GetField("m_Calls", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(button.onClick);
            return ((ICollection)calls.GetType().GetField("m_RuntimeCalls", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(calls)).Count;
        }
        private static void RaiseVisibility(Component component) => ((Action)component.GetType().GetField("VisibilityChanged", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(component))?.Invoke();
        private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
    }
}
#endif
