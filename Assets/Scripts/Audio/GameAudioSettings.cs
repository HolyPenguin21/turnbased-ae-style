using System;
using UnityEngine;

namespace Game.Audio
{
    public sealed class GameAudioSettings
    {
        private const string MasterKey = "Game.Audio.MasterVolume";
        private const string EnabledKey = "Game.Audio.MusicEnabled";
        private const string MusicKey = "Game.Audio.MusicVolume";
        private float masterVolume = 1f;
        private float musicVolume = 0.5f;
        private bool musicEnabled = true;
        public event Action Changed;

        public float MasterVolume { get => masterVolume; set { float next = ValidVolume(value, 1f); if (masterVolume == next) return; masterVolume = next; Store(); } }
        public float MusicVolume { get => musicVolume; set { float next = ValidVolume(value, 0.5f); if (musicVolume == next) return; musicVolume = next; Store(); } }
        public bool MusicEnabled { get => musicEnabled; set { if (musicEnabled == value) return; musicEnabled = value; Store(); } }

        public void Load()
        {
            masterVolume = ValidVolume(PlayerPrefs.GetFloat(MasterKey, 1f), 1f);
            musicVolume = ValidVolume(PlayerPrefs.GetFloat(MusicKey, 0.5f), 0.5f);
            musicEnabled = PlayerPrefs.GetInt(EnabledKey, 1) != 0;
        }
        public void Save() => PlayerPrefs.Save();
        private void Store()
        {
            PlayerPrefs.SetFloat(MasterKey, masterVolume);
            PlayerPrefs.SetFloat(MusicKey, musicVolume);
            PlayerPrefs.SetInt(EnabledKey, musicEnabled ? 1 : 0);
            Changed?.Invoke();
        }
        private static float ValidVolume(float value, float fallback) => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp01(value);
    }
}
