using System;
using UnityEngine;

namespace Game.UI
{
    // Explicit values are persisted. Append new topics without renumbering existing ones.
    public enum TutorialHintId { Draw = 0, EndTurn = 1, CreateArmy = 2, Fate = 3, Aircraft = 4 }

    public static class TutorialHintProgress
    {
        private const string EnabledKey = "Rustmarch.Tutorial.Enabled";
        private const string SeenKey = "Rustmarch.Tutorial.Seen.v1";
        public static event Action Changed;
        public static bool Enabled
        {
            get => PlayerPrefs.GetInt(EnabledKey, 1) != 0;
            set
            {
                if (Enabled == value) return;
                PlayerPrefs.SetInt(EnabledKey, value ? 1 : 0);
                PlayerPrefs.Save();
                Changed?.Invoke();
            }
        }
        public static bool HasSeen(TutorialHintId id) =>
            (PlayerPrefs.GetInt(SeenKey, 0) & (1 << (int)id)) != 0;

        public static void Complete(TutorialHintId id)
        {
            if (HasSeen(id)) return;
            PlayerPrefs.SetInt(SeenKey, PlayerPrefs.GetInt(SeenKey, 0) | (1 << (int)id));
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Changed = null;
    }
}
