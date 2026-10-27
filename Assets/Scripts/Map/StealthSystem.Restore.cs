using System;
using System.Collections.Generic;
using Game.Players;
using Game.Units;

namespace Game.Map
{
    [Serializable] public sealed class DetectionState
    {
        public int UnitId; public string ObserverId; public long Expiry;
    }
    [Serializable] public sealed class DetectionNoticesState
    {
        public string PlayerId; public List<string> Notices = new List<string>();
    }
    [Serializable] public sealed class StealthState
    {
        public List<DetectionState> Detections = new List<DetectionState>();
        public List<DetectionNoticesState> Notices = new List<DetectionNoticesState>();
    }
    public static partial class StealthSystem
    {
        public static StealthState CaptureState(Action<UnitData> includeReferencedUnit = null)
        {
            var result = new StealthState();
            foreach (var unit in _detected)
            {
                includeReferencedUnit?.Invoke(unit.Key);
                foreach (var observer in unit.Value)
                    result.Detections.Add(new DetectionState { UnitId = unit.Key.RuntimeId, ObserverId = StateReference.PlayerId(observer.Key), Expiry = observer.Value });
            }
            foreach (var observer in _detectionNotices)
                result.Notices.Add(new DetectionNoticesState { PlayerId = StateReference.PlayerId(observer.Key), Notices = new List<string>(observer.Value) });
            return result;
        }
        // Hidden/toggle flags belong to UnitData's entity state. Never call MarkDetected here:
        // it would renew the expiry against the current clock and announce a new detection.
        public static void RestoreState(StealthState state, Func<int, UnitData> resolveUnit, Func<string, PlayerSetupData> resolvePlayer)
        {
            if (state?.Detections == null || state.Notices == null) throw new ArgumentException("Missing stealth state.");
            var detected = new Dictionary<UnitData, Dictionary<PlayerSetupData, long>>();
            var notices = new Dictionary<PlayerSetupData, List<string>>();
            foreach (var entry in state.Detections)
            {
                if (entry == null || entry.Expiry < 0) throw new ArgumentException("Invalid detection.");
                var unit = StateReference.Unit(entry.UnitId, resolveUnit); var observer = StateReference.Player(entry.ObserverId, resolvePlayer);
                if (!detected.TryGetValue(unit, out var observers)) detected.Add(unit, observers = new Dictionary<PlayerSetupData, long>());
                if (observers.ContainsKey(observer)) throw new ArgumentException("Duplicate detection.");
                observers.Add(observer, entry.Expiry);
            }
            foreach (var entry in state.Notices)
            {
                if (entry?.Notices == null) throw new ArgumentException("Invalid detection notices.");
                var observer = StateReference.Player(entry.PlayerId, resolvePlayer);
                if (notices.ContainsKey(observer)) throw new ArgumentException("Duplicate detection notice owner.");
                notices.Add(observer, new List<string>(entry.Notices));
            }
            _detected.Clear(); _detectionNotices.Clear();
            foreach (var pair in detected) _detected.Add(pair.Key, pair.Value);
            foreach (var pair in notices) _detectionNotices.Add(pair.Key, pair.Value);
        }
    }
}
