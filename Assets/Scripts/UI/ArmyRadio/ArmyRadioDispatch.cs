using System;
using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.UI
{
    public enum ArmyRadioCategory
    {
        BattleStarted, BattleWon, Retreat, HeavyLosses, EnemyBaseDiscovered,
        BaseCaptured, HeroCaptured, EventDiscovered, TerritoryExplored,
        ReinforcementsArrived, BaseFounded, AviationSortie, AviationReturned,
        RepairsCompleted, EquipmentReceived,
        Contact, Waiting, Moving, Garrison, Airborne, Stealth, Damaged, Ready, Atmosphere
    }

    // A small presentation buffer, not a gameplay event bus. Live facts stay in their owners.
    public sealed class ArmyRadioCandidate
    {
        public readonly ArmyRadioCategory Category;
        public readonly ArmyData Speaker;
        public readonly PlayerSetupData Viewer;
        public readonly object Subject;
        public readonly ArmyData RelatedArmy;
        public readonly HexCoord Hex;
        public readonly float CreatedAt, ExpiresAt;
        public readonly int Priority;
        public ArmyRadioCandidate(ArmyRadioCategory category, ArmyData speaker, PlayerSetupData viewer,
            object subject, HexCoord hex, float now, float ttl, ArmyData relatedArmy = null)
        {
            Category = category; Speaker = speaker; Viewer = viewer; Subject = subject; Hex = hex; RelatedArmy = relatedArmy;
            CreatedAt = now; ExpiresAt = now + Math.Max(0f, ttl); Priority = ArmyRadioDispatch.Priority(category);
        }
        private static int EventFamily(ArmyRadioCategory category) => category == ArmyRadioCategory.BattleWon
            || category == ArmyRadioCategory.Retreat || category == ArmyRadioCategory.HeavyLosses
                ? (int)ArmyRadioCategory.BattleWon : (int)category;
        public bool SameEvent(ArmyRadioCandidate other) => other != null && EventFamily(Category) == EventFamily(other.Category)
            && ReferenceEquals(Speaker, other.Speaker) && ReferenceEquals(Viewer, other.Viewer)
            && ReferenceEquals(Subject, other.Subject)
            && (EventFamily(Category) == (int)ArmyRadioCategory.BattleWon || Hex.Equals(other.Hex));
    }

    public sealed class ArmyRadioDispatch
    {
        private readonly List<ArmyRadioCandidate> _pending = new List<ArmyRadioCandidate>();
        private readonly List<ArmyRadioCandidate> _decided = new List<ArmyRadioCandidate>();
        private readonly List<string> _phrases = new List<string>();
        private readonly Dictionary<ArmyRadioCategory, float> _lastCategory = new Dictionary<ArmyRadioCategory, float>();
        public int PendingCount => _pending.Count;
        public int DecisionCount => _decided.Count;
        public int PhraseHistoryCount => _phrases.Count;
        public static bool Operational(ArmyRadioCategory category) => category <= ArmyRadioCategory.EquipmentReceived;
        public static int Priority(ArmyRadioCategory category)
        {
            switch (category)
            {
                case ArmyRadioCategory.BattleStarted: case ArmyRadioCategory.BattleWon:
                case ArmyRadioCategory.Retreat: case ArmyRadioCategory.HeavyLosses:
                case ArmyRadioCategory.EnemyBaseDiscovered: case ArmyRadioCategory.BaseCaptured:
                case ArmyRadioCategory.HeroCaptured: return 1;
                case ArmyRadioCategory.RepairsCompleted: case ArmyRadioCategory.EquipmentReceived:
                    return 3;
                case ArmyRadioCategory.Atmosphere: return 5;
                default: return Operational(category) ? 2 : 4;
            }
        }
        public static float Chance(ArmyRadioCategory category, ArmyRadioChatterSettings settings)
            => Operational(category) ? (Priority(category) == 1 ? settings.criticalChance : settings.operationalChance)
                : category == ArmyRadioCategory.Atmosphere ? settings.ambientChance : settings.situationalChance;
        public static bool Accept(double roll, float chance, int recentTransmissions)
            => roll >= 0d && roll < Math.Max(0f, Math.Min(1f, chance)) / (1f + .25f * recentTransmissions);

        public bool Add(ArmyRadioCandidate candidate, float now, int capacity)
        {
            if (candidate == null || !Operational(candidate.Category) || candidate.ExpiresAt <= now) return false;
            Prune(now, null);
            foreach (var item in _pending) if (item.SameEvent(candidate)) return false;
            foreach (var item in _decided) if (item.SameEvent(candidate)) return false;
            capacity = Math.Max(1, Math.Min(64, capacity));
            while (_pending.Count > capacity)
            {
                int index = WorstIndex(); RememberDecision(_pending[index]); _pending.RemoveAt(index);
            }
            if (_pending.Count == capacity)
            {
                int worst = WorstIndex();
                if (!Better(candidate, _pending[worst], -1)) { RememberDecision(candidate); return false; }
                RememberDecision(_pending[worst]); _pending.RemoveAt(worst);
            }
            _pending.Add(candidate); return true;
        }
        public void Prune(float now, Func<ArmyRadioCandidate, bool> valid)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (now >= _pending[i].ExpiresAt || (valid != null && !valid(_pending[i])))
                { RememberDecision(_pending[i]); _pending.RemoveAt(i); }
            for (int i = _decided.Count - 1; i >= 0; i--)
                if (now >= _decided[i].ExpiresAt + 30f) _decided.RemoveAt(i);
        }
        public bool CategoryReady(ArmyRadioCategory category, float now, float gap) =>
            !_lastCategory.TryGetValue(category, out float last) || now - last >= Math.Max(1f, gap);
        public bool TryTake(float now, int lastSpeakerId, float categoryGap,
            Func<ArmyRadioCandidate, bool> valid, Func<ArmyRadioCandidate, bool> ready, out ArmyRadioCandidate best)
        {
            Prune(now, valid); best = null;
            int chosen = -1;
            for (int i = 0; i < _pending.Count; i++)
            {
                var item = _pending[i];
                if (!CategoryReady(item.Category, now, categoryGap) || !ready(item)) continue;
                if (best == null || Better(item, best, lastSpeakerId)) { best = item; chosen = i; }
            }
            if (chosen < 0) return false;
            _pending.RemoveAt(chosen); RememberDecision(best);
            return true; // Decision is consumed BEFORE its one probability roll or UI placement.
        }
        private int WorstIndex()
        {
            int worst = 0;
            for (int i = 1; i < _pending.Count; i++) if (Better(_pending[worst], _pending[i], -1)) worst = i;
            return worst;
        }
        private void RememberDecision(ArmyRadioCandidate candidate)
        {
            if (_decided.Count >= 64) _decided.RemoveAt(0);
            _decided.Add(candidate);
        }
        public static bool Better(ArmyRadioCandidate a, ArmyRadioCandidate b, int lastSpeakerId)
        {
            if (a.Priority != b.Priority) return a.Priority < b.Priority;
            if (a.CreatedAt != b.CreatedAt) return a.CreatedAt > b.CreatedAt;
            int aId = a.Speaker?.Id ?? -1, bId = b.Speaker?.Id ?? -1;
            if ((aId <= lastSpeakerId) != (bId <= lastSpeakerId)) return aId > lastSpeakerId;
            return aId < bId;
        }
        public bool Recent(string phrase) => _phrases.Contains(phrase);
        public void Shown(ArmyRadioCategory category, string phrase, float now, int historySize)
        {
            _lastCategory[category] = now;
            historySize = Math.Max(1, Math.Min(64, historySize));
            while (_phrases.Count >= historySize) _phrases.RemoveAt(0);
            _phrases.Add(phrase);
        }
        public void Clear()
        { _pending.Clear(); _decided.Clear(); _phrases.Clear(); _lastCategory.Clear(); }
    }
}
