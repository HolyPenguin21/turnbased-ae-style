using Game.Audio;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Game.UI
{
    public sealed class GameSettingsPanelUI : MonoBehaviour
    {
        [SerializeField] private MainMenuController menuController;
        [SerializeField] private Slider masterVolumeSlider;
        [SerializeField] private TMP_Text masterVolumeLabel;
        [SerializeField] private Toggle musicEnabledToggle;
        [SerializeField] private Slider musicVolumeSlider;
        [SerializeField] private TMP_Text musicVolumeLabel;
        [SerializeField] private Button backButton;
        private GameAudioSettings settings;

        private void OnEnable()
        {
            var manager = GameAudioManager.Instance;
            if (manager == null) return;
            settings = manager.Settings;
            settings.Changed += Refresh;
            if (masterVolumeSlider != null) masterVolumeSlider.onValueChanged.AddListener(OnMasterChanged);
            if (musicEnabledToggle != null) musicEnabledToggle.onValueChanged.AddListener(OnMusicEnabled);
            if (musicVolumeSlider != null) musicVolumeSlider.onValueChanged.AddListener(OnMusicChanged);
            if (backButton != null) backButton.onClick.AddListener(Close);
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
        private void Update()
        {
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
        }
        private void OnDisable()
        {
            if (settings != null) { settings.Changed -= Refresh; settings.Save(); }
            if (masterVolumeSlider != null) masterVolumeSlider.onValueChanged.RemoveListener(OnMasterChanged);
            if (musicEnabledToggle != null) musicEnabledToggle.onValueChanged.RemoveListener(OnMusicEnabled);
            if (musicVolumeSlider != null) musicVolumeSlider.onValueChanged.RemoveListener(OnMusicChanged);
            if (backButton != null) backButton.onClick.RemoveListener(Close);
            settings = null;
        }
    }
}
