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
        [UnityTest] public IEnumerator OtherThreePopupTypesOnlySoundOnOpeningEdge()
        {
            foreach (Type type in new[] { typeof(EventChoicePopupUI), typeof(AaChoicePopupUI), typeof(BattleContactPopupUI) })
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
        [UnityTest] public IEnumerator PopupPanelOnlySoundsForHumanTurnIncludingAlreadyVisiblePanel()
        {
            var root = New("handoff-popup"); root.SetActive(false);
            var popup = root.AddComponent<PopupPanelUI>(); Set(popup, "panelRoot", root);
            SceneUIAudioBinder.BindCreatedRoot(popup); SceneUIAudioBinder.BindCreatedRoot(popup);
            UI.Stop(); popup.ShowForOther(new Game.Players.PlayerSetupData { IsHuman = false });
            Assert.IsFalse(UI.isPlaying); yield return null;
            popup.ShowForOther(null); Assert.IsFalse(UI.isPlaying); yield return null;
            popup.ShowHint("Hint"); Assert.IsFalse(UI.isPlaying); yield return null;
            popup.ShowForHuman(new Game.Players.PlayerSetupData { IsHuman = true }, null);
            Assert.IsTrue(UI.isPlaying); yield return null;
            UI.Stop(); popup.Hide(); Assert.IsFalse(UI.isPlaying); yield return null;
            popup.ShowHint("Another hint"); Assert.IsFalse(UI.isPlaying);
        }
        [UnityTest] public IEnumerator GameMenuBlocksAndRestoresUIAndExistingPopupState()
        {
            var es = EventSystem.current != null ? EventSystem.current : New("menu-events").AddComponent<EventSystem>();
            var gameplay = New("gameplay-canvas"); gameplay.AddComponent<Canvas>();
            var group = gameplay.AddComponent<CanvasGroup>(); group.interactable = false; group.blocksRaycasts = true;
            var overlay = New("test-menu-canvas"); overlay.SetActive(false); overlay.AddComponent<Canvas>();
            var menu = overlay.AddComponent<GameMenuPanelUI>();
            var block = New("menu-block"); block.transform.SetParent(overlay.transform); block.SetActive(false);
            var panel = New("menu-panel"); panel.transform.SetParent(block.transform);
            var optionsRoot = New("menu-options"); optionsRoot.transform.SetParent(block.transform); optionsRoot.SetActive(false);
            var options = optionsRoot.AddComponent<GameSettingsPanelUI>();
            var slider = New("options-slider").AddComponent<Slider>(); slider.transform.SetParent(optionsRoot.transform);
            Set(options, "masterVolumeSlider", slider);
            var gear = New("gear").AddComponent<Button>(); gear.transform.SetParent(overlay.transform);
            var opt = New("options-button").AddComponent<Button>(); opt.transform.SetParent(panel.transform);
            var save = New("save-button").AddComponent<Button>(); save.transform.SetParent(panel.transform);
            var load = New("load-button").AddComponent<Button>(); load.transform.SetParent(panel.transform);
            var resume = New("resume-button").AddComponent<Button>(); resume.transform.SetParent(panel.transform);
            Set(menu, "blockingRoot", block); Set(menu, "menuPanel", panel); Set(menu, "optionsPanel", options);
            Set(menu, "gearButton", gear); Set(menu, "optionsButton", opt); Set(menu, "saveButton", save);
            Set(menu, "loadButton", load); Set(menu, "continueButton", resume);
            var turnRoot = New("test-turn"); turnRoot.SetActive(false);
            var turn = turnRoot.AddComponent<Game.Turns.GameTurnController>(); Set(turn, "gameMenu", menu);
            var popupRoot = New("existing-popup"); var popup = popupRoot.AddComponent<PopupPanelUI>();
            Set(popup, "panelRoot", popupRoot); Set(turn, "popupPanel", popup);
            overlay.SetActive(true); turnRoot.SetActive(true);
            Assert.IsFalse(save.interactable); Assert.IsFalse(load.interactable);
            gear.Select();
            Assert.IsTrue(UIFocusUtility.IsGameplayShortcutBlocked); // Submit belongs to gear, never End Turn/Confirm.
            Assert.IsFalse(UIFocusUtility.IsGameplayInputBlocked);
            gear.onClick.Invoke();
            Assert.IsTrue(menu.IsShowing); Assert.IsTrue(turn.InputBlocked); Assert.IsTrue(turn.CardDraggingBlocked);
            Assert.IsFalse(group.interactable); Assert.IsFalse(group.blocksRaycasts);
            Assert.IsTrue(UIFocusUtility.IsGameplayInputBlocked); Assert.AreEqual(opt.gameObject, es.currentSelectedGameObject);
            opt.onClick.Invoke(); Assert.IsTrue(optionsRoot.activeSelf); Assert.IsFalse(panel.activeSelf);
            Assert.AreEqual(slider.gameObject, es.currentSelectedGameObject);
            options.Close(); Assert.IsTrue(panel.activeSelf); Assert.IsTrue(menu.IsShowing);
            Assert.AreEqual(opt.gameObject, es.currentSelectedGameObject);
            resume.onClick.Invoke(); Assert.IsFalse(menu.IsShowing);
            Assert.IsFalse(group.interactable); Assert.IsTrue(group.blocksRaycasts);
            Assert.IsTrue(turn.InputBlocked); // The previous popup still owns its independent block.
            Assert.IsTrue(UIFocusUtility.IsGameplayInputBlocked); // Consume the closing frame.
            popup.Hide(); Assert.IsFalse(turn.InputBlocked); Assert.IsFalse(turn.CardDraggingBlocked);
            yield return null; Assert.IsFalse(UIFocusUtility.IsGameplayInputBlocked);
            menu.OpenMenu(); overlay.SetActive(false);
            Assert.IsFalse(menu.IsShowing); Assert.IsFalse(group.interactable); Assert.IsTrue(group.blocksRaycasts);
        }
        [UnityTest] public IEnumerator BattleHidesGearAndRejectsMenuUntilBattleCloses()
        {
            var battleRoot = New("menu-battle-state"); battleRoot.SetActive(false);
            var battle = battleRoot.AddComponent<BattleScreenUI>(); Set(battle, "panelRoot", battleRoot);
            var overlay = New("battle-locked-menu"); overlay.SetActive(false);
            var menu = overlay.AddComponent<GameMenuPanelUI>();
            var block = New("battle-menu-block"); block.transform.SetParent(overlay.transform); block.SetActive(false);
            var panel = New("battle-menu-panel"); panel.transform.SetParent(block.transform);
            var gear = New("battle-menu-gear").AddComponent<Button>(); gear.transform.SetParent(overlay.transform);
            Set(menu, "blockingRoot", block); Set(menu, "menuPanel", panel);
            Set(menu, "gearButton", gear); Set(menu, "battleScreen", battle);
            overlay.SetActive(true);
            Assert.IsTrue(gear.gameObject.activeSelf);
            battleRoot.SetActive(true); RaiseVisibility(battle);
            Assert.IsFalse(gear.gameObject.activeSelf);
            menu.OpenMenu(); Assert.IsFalse(menu.IsShowing);
            Assert.IsFalse(block.activeSelf);
            battleRoot.SetActive(false); RaiseVisibility(battle);
            Assert.IsTrue(gear.gameObject.activeSelf);
            menu.OpenMenu(); Assert.IsTrue(menu.IsShowing);
            // AI combat may start while the menu is already open.
            battleRoot.SetActive(true); RaiseVisibility(battle);
            Assert.IsFalse(menu.IsShowing); Assert.IsFalse(block.activeSelf);
            Assert.IsFalse(gear.gameObject.activeSelf);
            battleRoot.SetActive(false); RaiseVisibility(battle);
            Assert.IsTrue(gear.gameObject.activeSelf);
            yield return null;
        }
        [UnityTest] public IEnumerator MenuBlocksArmyAndBattleDragsAlreadyCapturedByEventSystem()
        {
            var es = EventSystem.current != null ? EventSystem.current : New("drag-events").AddComponent<EventSystem>();
            var gameplay = New("drag-gameplay"); gameplay.AddComponent<Canvas>();
            var armyRoot = New("army-drag"); armyRoot.transform.SetParent(gameplay.transform);
            var army = armyRoot.AddComponent<ArmyUnitCardUI>();
            army.Setup(null, new Game.Units.UnitData());
            var battleRoot = New("battle-drag"); battleRoot.transform.SetParent(gameplay.transform);
            var image = battleRoot.AddComponent<Image>();
            var battle = battleRoot.AddComponent<BattleGridCellUI>(); Set(battle, "artImage", image);
            battle.Setup(null, new Game.Units.UnitData(), 0, 0, true);
            var pointer = new PointerEventData(es) { position = Vector2.zero, delta = new Vector2(40f, 20f) };
            army.OnBeginDrag(pointer); battle.OnBeginDrag(pointer);
            Assert.IsTrue(army.IsDragging); Assert.IsTrue(battle.IsDragging);
            var armyRect = (RectTransform)armyRoot.transform;
            var armyPosition = armyRect.anchoredPosition;
            var ghost = Get<RectTransform>(battle, "_ghost"); var ghostPosition = ghost.anchoredPosition;
            var overlay = New("drag-menu"); var menu = overlay.AddComponent<GameMenuPanelUI>();
            var block = New("drag-menu-block"); block.transform.SetParent(overlay.transform);
            var panel = New("drag-menu-panel"); panel.transform.SetParent(block.transform);
            Set(menu, "blockingRoot", block); Set(menu, "menuPanel", panel);
            menu.OpenMenu();
            // CanvasGroup changes do not revoke an EventSystem pointer's captured drag target.
            army.OnDrag(pointer); battle.OnDrag(pointer);
            Assert.AreEqual(armyPosition, armyRect.anchoredPosition);
            Assert.AreEqual(ghostPosition, ghost.anchoredPosition);
            army.OnEndDrag(pointer); battle.OnEndDrag(pointer);
            Assert.IsFalse(army.IsDragging); Assert.IsFalse(battle.IsDragging);
            Assert.IsNull(Get<RectTransform>(battle, "_ghost"));
            army.OnBeginDrag(pointer); battle.OnBeginDrag(pointer);
            Assert.IsFalse(army.IsDragging); Assert.IsFalse(battle.IsDragging);
            menu.ContinueGame(); yield return null;
            army.OnBeginDrag(pointer); battle.OnBeginDrag(pointer);
            Assert.IsTrue(army.IsDragging); Assert.IsTrue(battle.IsDragging);
            army.OnEndDrag(pointer); battle.OnEndDrag(pointer);
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
