using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Game.Audio
{
    public sealed class GameAudioManager : MonoBehaviour
    {
        public static GameAudioManager Instance { get; private set; }
        public GameAudioSettings Settings { get; } = new GameAudioSettings();
        [SerializeField] private AudioSource uiSource;
        [SerializeField] private AudioSource musicSource;
        [SerializeField] private AudioMixer mixer;
        [SerializeField] private AudioClip clickClip;
        [SerializeField] private AudioClip infoClip;
        [SerializeField] private List<AudioClip> musicTracks = new List<AudioClip>();
        private readonly List<AudioClip> playlist = new List<AudioClip>();
        private int lastTrack = -1;
        private int lastInfoFrame = -1;
        private int lastClickFrame = -1;
        private bool ready;
        private bool musicPaused;
        private Coroutine musicRoutine;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            var existing = Object.FindAnyObjectByType<GameAudioManager>();
            if (existing != null)
            {
                existing.Initialize();
                existing.StartCoroutine(existing.RestartExistingService());
                return;
            }
            var prefab = Resources.Load<GameObject>("Audio/GameAudio");
            if (prefab == null) { Debug.LogError("Missing Resources/Audio/GameAudio audio service prefab."); return; }
            Object.Instantiate(prefab);
        }
        private void Awake() => Initialize();
        private void Initialize()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            if (Instance == this) return;
            Instance = this;
            DontDestroyOnLoad(gameObject);
            ready = false; musicPaused = false; lastTrack = -1; lastInfoFrame = -1; lastClickFrame = -1;
            if (musicRoutine != null) StopCoroutine(musicRoutine);
            musicRoutine = null;
            Settings.Changed -= ApplySettings;
            Settings.Load();
            Settings.Changed += ApplySettings;
            playlist.Clear();
            if (uiSource != null) uiSource.Stop();
            if (musicSource != null) musicSource.Stop();
            ConfigureSource(uiSource); ConfigureSource(musicSource);
            if (musicSource != null) musicSource.volume = 0.04f;
            foreach (var track in musicTracks) if (track != null) playlist.Add(track);
        }
        private static void ConfigureSource(AudioSource source)
        {
            if (source == null) return;
            source.playOnAwake = false; source.loop = false; source.spatialBlend = 0f;
            source.panStereo = 0f; source.pitch = 1f; source.dopplerLevel = 0f;
            source.reverbZoneMix = 0f; source.mute = true;
        }
        private void Start()
        {
            if (Instance != this) return;
            BeginPlayback();
        }
        private IEnumerator RestartExistingService()
        {
            // With both domain and scene reload disabled, Awake/Start won't run again.
            yield return null;
            if (Instance != this) yield break;
            GetComponent<SceneUIAudioBinder>()?.ResetBindings();
            BeginPlayback();
        }
        private void BeginPlayback()
        {
            ready = true; ApplySettings();
            if (musicRoutine != null) StopCoroutine(musicRoutine);
            musicRoutine = StartCoroutine(PlayMusic());
        }
        public void ApplySettings()
        {
            if (!ready) return; // AudioMixer.SetFloat is valid from Start onwards.
            if (mixer == null || uiSource == null || musicSource == null ||
                !mixer.SetFloat("MasterVolumeDb", ToDb(Settings.MasterVolume)) ||
                !mixer.SetFloat("MusicVolumeDb", ToDb(Settings.MusicVolume)))
            {
                Debug.LogError("GameAudio needs both sources and the MasterVolumeDb/MusicVolumeDb mixer parameters.");
                ready = false;
                if (uiSource != null) uiSource.mute = true;
                if (musicSource != null) musicSource.mute = true;
                return;
            }
            if (uiSource != null)
            {
                uiSource.volume = 1f;
                uiSource.mute = Settings.MasterVolume == 0f;
            }
            if (musicSource == null) return;
            musicSource.volume = 0.04f;
            musicSource.mute = Settings.MasterVolume == 0f || Settings.MusicVolume == 0f;
            if (!Settings.MusicEnabled && !musicPaused)
            {
                musicSource.Pause(); musicPaused = true;
            }
            else if (Settings.MusicEnabled && musicPaused)
            {
                musicSource.UnPause(); musicPaused = false;
            }
        }
        private static float ToDb(float value) => value <= 0f ? -80f : Mathf.Max(-80f, 20f * Mathf.Log10(value));
        public void PlayClick()
        {
            if (!ready || lastClickFrame == Time.frameCount) return;
            lastClickFrame = Time.frameCount; PlayUI(clickClip);
        }
        public void PlayInfo()
        {
            if (!ready || lastInfoFrame == Time.frameCount) return;
            lastInfoFrame = Time.frameCount; PlayUI(infoClip);
        }
        private void PlayUI(AudioClip clip)
        {
            if (ready && uiSource != null && clip != null && Settings.MasterVolume > 0f)
                uiSource.PlayOneShot(clip);
        }
        private IEnumerator PlayMusic()
        {
            var wait = new WaitForSecondsRealtime(0.25f);
            while (ready && Instance == this && musicSource != null && playlist.Count > 0)
            {
                if (Settings.MusicEnabled && !musicSource.isPlaying && !musicPaused)
                {
                    int next = 0;
                    if (playlist.Count > 1)
                    {
                        // Map one draw onto all indices except the previously played one.
                        next = Random.Range(0, playlist.Count - (lastTrack >= 0 ? 1 : 0));
                        if (lastTrack >= 0 && next >= lastTrack) next++;
                    }
                    lastTrack = next; musicSource.clip = playlist[next]; musicSource.Play();
                }
                yield return wait;
            }
        }
        private void OnApplicationFocus(bool focused) { if (!focused && Instance == this) Settings.Save(); }
        private void OnApplicationPause(bool paused) { if (paused && Instance == this) Settings.Save(); }
        private void OnApplicationQuit() { if (Instance == this) Settings.Save(); }
        private void OnDestroy()
        {
            Settings.Changed -= ApplySettings;
            if (Instance == this) Instance = null;
        }
    }
}
