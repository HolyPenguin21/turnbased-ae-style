using System.Collections.Generic;
using System.Collections;
using UnityEngine;

[RequireComponent(typeof(AudioSource))]
public class PersistentMusic : MonoBehaviour
{
    private static PersistentMusic instance;

    [Header("Звуки интерфейса")]
    [SerializeField] private AudioClip buttonClickSound;

    // Этот метод мы будем вызывать при нажатии на кнопку
    public void PlayButtonSound()
    {
        if (buttonClickSound != null && audioSource != null)
        {
            // PlayOneShot проигрывает звук один раз поверх текущей музыки
            audioSource.PlayOneShot(buttonClickSound);
        }
    }

    [Header("Настройки музыки")]
    [SerializeField] private List<AudioClip> musicTracks = new List<AudioClip>();

    private AudioSource audioSource;
    private int lastPlayedIndex = -1;
    private Coroutine musicRoutine;

    void Awake()
    {
        // Логика Одиночки (Singleton)
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);

            // Получаем компонент AudioSource
            audioSource = GetComponent<AudioSource>();
            // Отключаем loop, так как мы сами управляем треками в Update
            audioSource.loop = false;
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Start()
    {
        // Музыка не требует проверки в каждом кадре: следующий трек известен по длительности
        // текущего. Корутинa просыпается только на границе композиции.
        if (instance == this)
            musicRoutine = StartCoroutine(PlayMusicRoutine());
    }

    private IEnumerator PlayMusicRoutine()
    {
        while (instance == this && musicTracks.Count > 0)
        {
            PlayRandomTrack();
            if (audioSource == null || audioSource.clip == null)
                yield break;
            yield return new WaitForSeconds(audioSource.clip.length);
        }
        musicRoutine = null;
    }

    private void PlayRandomTrack()
    {
        if (musicTracks.Count == 0) return;

        // Если в списке всего 1 трек, просто играем его
        if (musicTracks.Count == 1)
        {
            audioSource.clip = musicTracks[0];
            audioSource.Play();
            return;
        }

        int nextIndex;

        // Выбираем случайный индекс, пока он совпадает с предыдущим
        do
        {
            nextIndex = Random.Range(0, musicTracks.Count);
        }
        while (nextIndex == lastPlayedIndex);

        // Запоминаем текущий индекс и включаем музыку
        lastPlayedIndex = nextIndex;
        audioSource.clip = musicTracks[nextIndex];
        audioSource.Play();
    }
}
