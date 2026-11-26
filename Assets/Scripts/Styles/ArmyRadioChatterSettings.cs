using System;
using UnityEngine;

namespace Game.Styles
{
    [Serializable]
    public sealed class ArmyRadioChatterSettings
    {
        public bool enabled = true;
        [Min(5f)] public float ambientInterval = 18f;
        [Min(1f)] public float minimumGap = 3f;
        [Min(10f)] public float armyCooldown = 45f;
        [Min(2f)] public float displaySeconds = 5f;
        [Min(1f)] public float waitingSeconds = 12f;
        [Range(1, 3)] public int maximumMessages = 3;
        [Min(.05f)] public float fadeInSeconds = .18f;
        [Min(.05f)] public float fadeOutSeconds = .28f;
        [Min(10f)] public float jokeIdleSeconds = 60f;
        [Range(0f, 1f)] public float jokeChance = .3f;
        [Range(0f, 1f)] public float dialogueChance = .25f;
        [Min(.5f)] public float replyDelay = 2f;
        [Header("Radio dispatch")]
        [Range(0f, 1f)] public float criticalChance = .6f;
        [Range(0f, 1f)] public float operationalChance = .3f;
        [Range(0f, 1f)] public float situationalChance = .2f;
        [Range(0f, 1f)] public float ambientChance = .1f;
        [Range(1, 6)] public int maximumPerMinute = 3;
        [Range(1, 64)] public int maximumPendingCandidates = 24;
        [Min(1f)] public float operationalTtl = 10f;
        [Min(1f)] public float minorEventTtl = 6f;
        [Min(1f)] public float categoryCooldown = 25f;
        [Range(1, 64)] public int recentPhraseHistorySize = 16;
    }
}
