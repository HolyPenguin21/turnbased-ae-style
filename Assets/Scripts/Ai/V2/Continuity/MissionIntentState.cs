using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // MissionIntentState (per-player durable intent store) + MissionIntentRegistry (per-player lookup).
    // File-split (mechanical, no behaviour change) from MissionIntent.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 3. Independent standalone types,
    // not a partial class.
    public sealed class MissionIntentState
    {
        private readonly Dictionary<MissionIntentKey, MissionIntent> _intents =
            new Dictionary<MissionIntentKey, MissionIntent>();
        // The player this store belongs to (null for a detached store). Lets a Continuity
        // transition that only holds the state release that player's turn-scoped reservations.
        internal PlayerSetupData Owner { get; }

        public MissionIntentState() { }
        internal MissionIntentState(PlayerSetupData owner) { Owner = owner; }

        public IReadOnlyCollection<MissionIntent> All => _intents.Values;
        public int Count => _intents.Count;
        public bool TryGet(MissionIntentKey k, out MissionIntent i) => _intents.TryGetValue(k, out i);
        public void Put(MissionIntent i) => _intents[i.IntentKey] = i;
        public void Remove(MissionIntentKey k) => _intents.Remove(k);

        // Challenge mints a physical Hero before the destination factory can deploy it.
        // This per-player intent store survives turns; AP/resource reservations do not.
        private readonly Dictionary<CardData, (HexCoord Site, ResearchProductionMode Mode, int Turn)>
            _generatedDevelopmentOperators =
                new Dictionary<CardData, (HexCoord, ResearchProductionMode, int)>();

        internal void RememberGeneratedDevelopmentOperator(CardData card, HexCoord site,
            ResearchProductionMode mode, int turn)
        {
            if (card == null) return;
            // Only one card may be earmarked for each facility/role at once.
            foreach (CardData previous in _generatedDevelopmentOperators
                .Where(x => x.Value.Site.Equals(site) && x.Value.Mode == mode)
                .Select(x => x.Key).ToList())
                _generatedDevelopmentOperators.Remove(previous);
            _generatedDevelopmentOperators[card] = (site, mode, turn);
        }

        // Infrastructure provides current structural eligibility; State owns identity,
        // finite age and removal. An AP shortage is NOT a reason to discard a valid card.
        internal IReadOnlyList<CardData> ReconcileGeneratedDevelopmentOperators(int turn,
            Func<CardData, HexCoord, ResearchProductionMode, bool> stillNeeded)
        {
            foreach (var claim in _generatedDevelopmentOperators.ToList())
                if (turn < claim.Value.Turn
                    || turn - claim.Value.Turn > System.Math.Max(1, AiConfigV2.commitmentStallTurns)
                    || stillNeeded == null
                    || !stillNeeded(claim.Key, claim.Value.Site, claim.Value.Mode))
                    _generatedDevelopmentOperators.Remove(claim.Key);
            return _generatedDevelopmentOperators.Keys.ToList();
        }

        private int _reconTrimTurn = -1;
        private int _reconTrimCount;
        private readonly HashSet<int> _reconTrimmedActorIds = new HashSet<int>();

        private void EnsureReconTrimTurn(int turn)
        {
            if (_reconTrimTurn == turn)
                return;
            _reconTrimTurn = turn;
            _reconTrimCount = 0;
            _reconTrimmedActorIds.Clear();
        }

        internal bool TryConsumeReconLaneTrim(int turn)
        {
            EnsureReconTrimTurn(turn);
            if (_reconTrimCount >= AiConfigV2.maxReconLaneTrimPerTurn)
                return false;
            _reconTrimCount++;
            return true;
        }

        internal void MarkReconActorTrimmed(int turn, int armyId)
        {
            EnsureReconTrimTurn(turn);
            // ArmyId 0 is a legitimate actor identity. Callers reach this method only after
            // PreferredMoverArmyId.HasValue, so absence is represented by nullable ownership,
            // never by a numeric sentinel.
            _reconTrimmedActorIds.Add(armyId);
        }

        internal IReadOnlyCollection<int> ReconActorsTrimmedThisTurn(int turn)
        {
            EnsureReconTrimTurn(turn);
            return _reconTrimmedActorIds;
        }

        // Bounded delivery-failure streaks. A structurally valid site may still be operationally
        // impossible for every builder: count only CONSECUTIVE-turn delivery-gate failures of the
        // exact project and, once the ordinary commitment stall window is exhausted, briefly
        // suppress that project so Demand compares other sites instead of repeating it. One
        // counter per project — Base by (card, site), Extraction by (resource, site) — because
        // several Economy builds can be active (and stuck) at once; a single shared slot let two
        // stuck projects reset each other's streak forever (Economy audit B4).
        private sealed class DeliveryFailureStreaks<TKey>
        {
            private readonly Dictionary<TKey, (int Turn, int Count)> _failures =
                new Dictionary<TKey, (int, int)>();
            private readonly Dictionary<TKey, int> _suppressedUntilTurn = new Dictionary<TKey, int>();

            public bool IsSuppressed(int turn, TKey key) =>
                _suppressedUntilTurn.TryGetValue(key, out int until) && turn < until;

            public bool Record(int turn, TKey key)
            {
                if (IsSuppressed(turn, key))
                    return true;
                bool hasRecord = _failures.TryGetValue(key, out (int Turn, int Count) rec);
                bool consecutiveTurn = hasRecord && (rec.Turn == turn || rec.Turn == turn - 1);
                int count = consecutiveTurn ? rec.Count : 0;
                if (!hasRecord || rec.Turn != turn)
                    count++;
                _failures[key] = (turn, count);

                if (count < System.Math.Max(1, AiConfigV2.commitmentStallTurns))
                    return false;

                _suppressedUntilTurn[key] = turn
                    + System.Math.Max(1, AiConfigV2.allocatorRejectCooldownTurns) + 1;
                _failures.Remove(key);
                return true;
            }
        }

        private readonly DeliveryFailureStreaks<(CardData, HexCoord)> _baseDeliveryFailures =
            new DeliveryFailureStreaks<(CardData, HexCoord)>();
        private readonly DeliveryFailureStreaks<(ResourceType?, HexCoord)> _extractionDeliveryFailures =
            new DeliveryFailureStreaks<(ResourceType?, HexCoord)>();

        internal bool IsBaseExpansionDeliverySuppressed(int turn, CardData card, HexCoord? target) =>
            card != null && target.HasValue
            && _baseDeliveryFailures.IsSuppressed(turn, (card, target.Value));

        internal bool RecordBaseExpansionDeliveryFailure(int turn, CardData card, HexCoord? target) =>
            card != null && target.HasValue
            && _baseDeliveryFailures.Record(turn, (card, target.Value));

        internal bool IsExtractionDeliverySuppressed(int turn, ResourceType? resourceType, HexCoord target) =>
            _extractionDeliveryFailures.IsSuppressed(turn, (resourceType, target));

        internal bool RecordExtractionDeliveryFailure(int turn, ResourceType? resourceType, HexCoord target) =>
            _extractionDeliveryFailures.Record(turn, (resourceType, target));

    }

    public static class MissionIntentRegistry
    {
        private static readonly Dictionary<PlayerSetupData, MissionIntentState> ByPlayer =
            new Dictionary<PlayerSetupData, MissionIntentState>();

        public static MissionIntentState GetOrCreate(PlayerSetupData player)
        {
            if (player == null)
                return new MissionIntentState();
            if (!ByPlayer.TryGetValue(player, out MissionIntentState s))
                ByPlayer[player] = s = new MissionIntentState(player);
            return s;
        }

        public static void Clear() => ByPlayer.Clear();
    }
}

