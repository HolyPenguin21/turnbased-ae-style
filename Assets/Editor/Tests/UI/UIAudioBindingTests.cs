#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System;
using System.Reflection;
using Game.Audio;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class UIAudioBindingTests
    {
        private static readonly string[] Keys = { "Game.Audio.MasterVolume", "Game.Audio.MusicEnabled", "Game.Audio.MusicVolume" };
        private GameObject root;
        private string[] oldValues;
        private bool[] existed;

        [SetUp] public void SetUp()
        {
            oldValues = new string[3]; existed = new bool[3];
            for (int i = 0; i < Keys.Length; i++)
            {
                existed[i] = PlayerPrefs.HasKey(Keys[i]);
                oldValues[i] = i == 1 ? PlayerPrefs.GetInt(Keys[i]).ToString() : PlayerPrefs.GetFloat(Keys[i]).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                PlayerPrefs.DeleteKey(Keys[i]);
            }
        }
        [TearDown] public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
            for (int i = 0; i < Keys.Length; i++)
            {
                PlayerPrefs.DeleteKey(Keys[i]);
                if (!existed[i]) continue;
                if (i == 1) PlayerPrefs.SetInt(Keys[i], int.Parse(oldValues[i]));
                else PlayerPrefs.SetFloat(Keys[i], float.Parse(oldValues[i], System.Globalization.CultureInfo.InvariantCulture));
            }
            PlayerPrefs.Save();
        }
        [Test] public void Settings_Defaults()
        {
            var settings = new GameAudioSettings(); settings.Load();
            Assert.AreEqual(1f, settings.MasterVolume);
            Assert.IsTrue(settings.MusicEnabled);
            Assert.AreEqual(0.5f, settings.MusicVolume);
        }
        [Test] public void Settings_RoundTrip()
        {
            var settings = new GameAudioSettings(); settings.Load();
            settings.MasterVolume = 0.35f; settings.MusicVolume = 0.72f; settings.MusicEnabled = false; settings.Save();
            var restored = new GameAudioSettings(); restored.Load();
            Assert.AreEqual(0.35f, restored.MasterVolume);
            Assert.AreEqual(0.72f, restored.MusicVolume);
            Assert.IsFalse(restored.MusicEnabled);
        }
        [Test] public void Settings_InvalidValues()
        {
            PlayerPrefs.SetFloat(Keys[0], float.NaN); PlayerPrefs.SetFloat(Keys[2], 4f);
            var settings = new GameAudioSettings(); settings.Load();
            Assert.AreEqual(1f, settings.MasterVolume); Assert.AreEqual(1f, settings.MusicVolume);
            settings.MasterVolume = -1f; settings.MusicVolume = float.PositiveInfinity;
            Assert.AreEqual(0f, settings.MasterVolume); Assert.AreEqual(0.5f, settings.MusicVolume);
        }
        [Test] public void BindInactiveButton_ResetThenConfigureKeepsOneBinding()
        {
            root = new GameObject("audio-binding-test", typeof(RectTransform)); root.SetActive(false);
            var button = root.AddComponent<Button>();
            SceneUIAudioBinder.BindCreatedRoot(button); SceneUIAudioBinder.BindCreatedRoot(button);
            Assert.AreEqual(1, root.GetComponents<UIButtonSound>().Length);
            int actions = 0;
            for (int i = 0; i < 3; i++)
            {
                UIButtonEventUtility.ResetRuntimeListeners(button);
                button.onClick.AddListener(() => actions++);
                button.onClick.Invoke();
            }
            Assert.AreEqual(3, actions);
            Assert.AreEqual(1, root.GetComponents<UIButtonSound>().Length);
        }
        [Test] public void AudioPrefab_HasTwoIndependent2DSourcesAndNoListener()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Audio/GameAudio.prefab");
            Assert.NotNull(prefab);
            Assert.NotNull(prefab.GetComponent<GameAudioManager>());
            Assert.NotNull(prefab.GetComponent<SceneUIAudioBinder>());
            var sources = prefab.GetComponentsInChildren<AudioSource>(true);
            Assert.AreEqual(2, sources.Length);
            foreach (var source in sources) { Assert.AreEqual(0f, source.spatialBlend); Assert.IsFalse(source.playOnAwake); Assert.NotNull(source.outputAudioMixerGroup); }
            Assert.IsEmpty(prefab.GetComponentsInChildren<AudioListener>(true));
        }
        [Test] public void SettingsOpenBlocksNewGameAndBackRestoresMenu()
        {
            root = new GameObject("menu-controller-test");
            var menu = root.AddComponent<MainMenuController>();
            var main = new GameObject("main"); main.transform.SetParent(root.transform);
            var setup = new GameObject("setup"); setup.transform.SetParent(root.transform); setup.SetActive(false);
            var settings = new GameObject("settings"); settings.transform.SetParent(root.transform); settings.SetActive(false);
            Set(menu, "mainMenuPanel", main); Set(menu, "gameSetupPanel", setup); Set(menu, "settingsPanel", settings);
            menu.OnSettingsClicked(); menu.OnNewGameClicked();
            Assert.IsTrue(settings.activeSelf); Assert.IsFalse(setup.activeSelf); Assert.IsFalse(main.activeSelf);
            menu.OnSettingsClosed(); Assert.IsTrue(main.activeSelf); Assert.IsFalse(settings.activeSelf);
        }
        private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
    }
}
#endif
