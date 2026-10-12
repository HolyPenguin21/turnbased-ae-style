using System.Collections.Generic;
using UnityEngine;

namespace Game.UI
{
    // Unscaled presentation clocks: fading never extends a message's lifetime.
    public sealed class ArmyRadioTiming
    {
        private readonly float _started, _expires, _fadeIn, _fadeOut;
        private float _dismissed = float.PositiveInfinity, _dismissAlpha;
        public ArmyRadioTiming(float now, float duration, float fadeIn, float fadeOut)
        {
            _started = now; _expires = now + Mathf.Max(2f, duration);
            _fadeIn = Mathf.Max(.05f, fadeIn); _fadeOut = Mathf.Max(.05f, fadeOut);
        }
        public bool IsDismissing => !float.IsPositiveInfinity(_dismissed);
        public bool Finished(float now) => now >= Mathf.Min(_expires, _dismissed + _fadeOut);
        public float Alpha(float now)
        {
            float natural = Mathf.Min(Mathf.Clamp01((now - _started) / _fadeIn), Mathf.Clamp01((_expires - now) / _fadeOut));
            return IsDismissing ? Mathf.Min(natural, _dismissAlpha * Mathf.Clamp01(1f - (now - _dismissed) / _fadeOut)) : natural;
        }
        public void Dismiss(float now)
        {
            if (IsDismissing) return;
            _dismissAlpha = Alpha(now); _dismissed = now;
        }
    }

    public sealed class ArmyRadioCadence
    {
        private readonly Dictionary<int, float> _lastSpoke = new Dictionary<int, float>();
        private float _nextAllowed;
        private readonly Queue<float> _transmissions = new Queue<float>();
        public int RecentCount(float now)
        {
            while (_transmissions.Count > 0 && now - _transmissions.Peek() >= 60f) _transmissions.Dequeue();
            return _transmissions.Count;
        }
        public bool ChannelReady(float now, int limit) => now >= _nextAllowed && DensityReady(now, limit);
        public bool DensityReady(float now, int limit, int slots = 1) => RecentCount(now) + slots <= Mathf.Clamp(limit, 1, 6);
        // A failed probability decision closes this opportunity, without cooling an army.
        public void Quiet(float now, float gap) => _nextAllowed = now + Mathf.Max(1f, gap);
        public ArmyRadioCadence(float now) { _nextAllowed = now + 2f; }
        public bool CanSpeak(int id, float now, float cooldown) => now >= _nextAllowed && ArmyReady(id, now, cooldown);
        public bool ArmyReady(int id, float now, float cooldown) =>
            !_lastSpoke.TryGetValue(id, out float previous) || now - previous >= Mathf.Max(10f, cooldown);
        public void Spoke(int id, float now, float gap)
        {
            _lastSpoke[id] = now; _nextAllowed = now + Mathf.Max(1f, gap);
            RecentCount(now);
            if (_transmissions.Count >= 6) _transmissions.Dequeue();
            _transmissions.Enqueue(now);
        }
        // A dialogue reserves both participants, but its one scheduled reply bypasses the global gap.
        public void Reserve(int id, float now) => _lastSpoke[id] = now;
        public void Forget(int id) => _lastSpoke.Remove(id);
        // Viewer changes discard speaker scope, never the channel's rolling density or gap.
        public void ResetSpeakers() => _lastSpoke.Clear();
    }
}
