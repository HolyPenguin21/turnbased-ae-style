using Game.Audio;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class GameSettingsPanelUI : MonoBehaviour
    {
        public event System.Action Closed;
        [SerializeField] private MainMenuController menuController;
        [SerializeField] private Slider masterVolumeSlider;
        [SerializeField] private TMP_Text masterVolumeLabel;
        [SerializeField] private Toggle musicEnabledToggle;
        [SerializeField] private Toggle tutorialHintsToggle;
        [SerializeField] private Slider musicVolumeSlider;
        [SerializeField] private TMP_Text musicVolumeLabel;
        [SerializeField] private Button backButton;
        private GameAudioSettings settings;

        private void OnEnable()
        {
            TutorialHintProgress.Changed += RefreshTutorialHints;
            if (tutorialHintsToggle != null) tutorialHintsToggle.onValueChanged.AddListener(OnTutorialHintsChanged);
            RefreshTutorialHints();
            if (backButton != null) backButton.onClick.AddListener(Close);
            var manager = GameAudioManager.Instance;
            if (manager == null) return;
            settings = manager.Settings;
            settings.Changed += Refresh;
            if (masterVolumeSlider != null) masterVolumeSlider.onValueChanged.AddListener(OnMasterChanged);
            if (musicEnabledToggle != null) musicEnabledToggle.onValueChanged.AddListener(OnMusicEnabled);
            if (musicVolumeSlider != null) musicVolumeSlider.onValueChanged.AddListener(OnMusicChanged);
            Refresh();
            if (masterVolumeSlider != null) masterVolumeSlider.Select();
        }
        private void Refresh()
        {
            if (settings == null) return;
            if (masterVolumeSlider != null) masterVolumeSlider.SetValueWithoutNotify(settings.MasterVolume);
            if (masterVolumeLabel != null) masterVolumeLabel.text = Mathf.RoundToInt(settings.MasterVolume * 100f) + "%";
            if (musicEnabledToggle != null) musicEnabledToggle.SetIsOnWithoutNotify(settings.MusicEnabled);
            if (musicVolumeSlider != null)
            {
                musicVolumeSlider.SetValueWithoutNotify(settings.MusicVolume);
                musicVolumeSlider.interactable = settings.MusicEnabled;
            }
            if (musicVolumeLabel != null) musicVolumeLabel.text = Mathf.RoundToInt(settings.MusicVolume * 100f) + "%";
        }
        private void OnMasterChanged(float value) { if (settings != null) settings.MasterVolume = value; }
        private void OnMusicEnabled(bool value) { if (settings != null) settings.MusicEnabled = value; }
        private void OnMusicChanged(float value) { if (settings != null) settings.MusicVolume = value; }
        private void RefreshTutorialHints()
        {
            if (tutorialHintsToggle != null) tutorialHintsToggle.SetIsOnWithoutNotify(TutorialHintProgress.Enabled);
        }
        private void OnTutorialHintsChanged(bool value) => TutorialHintProgress.Enabled = value;
        private void Update()
        {
            if (GameMenuPanelUI.GameplayInputBlocked) return; // In-game Escape is owned by the menu stack.
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                GameAudioManager.Instance?.PlayClick(); Close();
            }
        }
        public void Close()
        {
            settings?.Save();
            if (menuController != null) menuController.OnSettingsClosed();
            else gameObject.SetActive(false);
            Closed?.Invoke();
        }
        private void OnDisable()
        {
            TutorialHintProgress.Changed -= RefreshTutorialHints;
            if (tutorialHintsToggle != null) tutorialHintsToggle.onValueChanged.RemoveListener(OnTutorialHintsChanged);
            if (settings != null) { settings.Changed -= Refresh; settings.Save(); }
            if (masterVolumeSlider != null) masterVolumeSlider.onValueChanged.RemoveListener(OnMasterChanged);
            if (musicEnabledToggle != null) musicEnabledToggle.onValueChanged.RemoveListener(OnMusicEnabled);
            if (musicVolumeSlider != null) musicVolumeSlider.onValueChanged.RemoveListener(OnMusicChanged);
            if (backButton != null) backButton.onClick.RemoveListener(Close);
            settings = null;
        }
    }
}
