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
        private int _baseExpansionDeliveryFailureTurn = -1;
        private int _baseExpansionDeliveryFailureCount;
        private CardData _baseExpansionDeliveryFailureCard;
        private HexCoord? _baseExpansionDeliveryFailureTarget;
        private int _baseExpansionSuppressedUntilTurn = -1;
        private CardData _baseExpansionSuppressedCard;
        private HexCoord? _baseExpansionSuppressedTarget;

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

        internal bool IsBaseExpansionDeliverySuppressed(int turn, CardData card, HexCoord? target) =>
            turn < _baseExpansionSuppressedUntilTurn
            && card == _baseExpansionSuppressedCard
            && target.HasValue
            && target.Equals(_baseExpansionSuppressedTarget);

        // A structurally valid site may still be operationally impossible for every materialized
        // builder. Count only consecutive, canonical delivery-gate failures for the exact staged
        // project. Once the ordinary commitment stall window is exhausted, briefly suppress that
        // project so Demand can compare other sites instead of repeating a failed delivery.
        internal bool RecordBaseExpansionDeliveryFailure(int turn, CardData card, HexCoord? target)
        {
            if (card == null || !target.HasValue)
                return false;
            if (IsBaseExpansionDeliverySuppressed(turn, card, target))
                return true;
            bool sameProject = card == _baseExpansionDeliveryFailureCard
                && target.Equals(_baseExpansionDeliveryFailureTarget);
            bool consecutiveTurn = _baseExpansionDeliveryFailureTurn == turn
                || _baseExpansionDeliveryFailureTurn == turn - 1;
            if (!sameProject || !consecutiveTurn)
                _baseExpansionDeliveryFailureCount = 0;
            if (_baseExpansionDeliveryFailureTurn != turn)
                _baseExpansionDeliveryFailureCount++;
            _baseExpansionDeliveryFailureTurn = turn;
            _baseExpansionDeliveryFailureCard = card;
            _baseExpansionDeliveryFailureTarget = target;

            if (_baseExpansionDeliveryFailureCount
                < System.Math.Max(1, AiConfigV2.commitmentStallTurns))
                return false;

            _baseExpansionSuppressedCard = card;
            _baseExpansionSuppressedTarget = target;
            _baseExpansionSuppressedUntilTurn = turn
                + System.Math.Max(1, AiConfigV2.allocatorRejectCooldownTurns) + 1;
            _baseExpansionDeliveryFailureCount = 0;
            return true;
        }

        // R3 (2026-09-17) — same bounded-suppression pattern as Base above, generalized to a key
        // per (resource type, site) because BuildExtraction has no single staged slot: several
        // extraction intents can be durable and suspended at once, unlike Base's one project. Wired
        // from MissionContinuityLayer.AdvanceIntent's capabilityUnavailable branch — the sole call
        // site — so a durable Extraction intent stuck on repeated NoMoverExists/MoverContended
        // cannot be suspended forever with its actor/card reservation never released.
        private readonly Dictionary<(ResourceType?, HexCoord), (int Turn, int Count)>
            _extractionDeliveryFailures = new Dictionary<(ResourceType?, HexCoord), (int, int)>();
        private readonly Dictionary<(ResourceType?, HexCoord), int> _extractionSuppressedUntilTurn =
            new Dictionary<(ResourceType?, HexCoord), int>();

        internal bool IsExtractionDeliverySuppressed(int turn, ResourceType? resourceType, HexCoord target) =>
            _extractionSuppressedUntilTurn.TryGetValue((resourceType, target), out int until) && turn < until;

        internal bool RecordExtractionDeliveryFailure(int turn, ResourceType? resourceType, HexCoord target)
        {
            var key = (resourceType, target);
            if (IsExtractionDeliverySuppressed(turn, resourceType, target))
                return true;
            bool hasRecord = _extractionDeliveryFailures.TryGetValue(key, out (int Turn, int Count) rec);
            bool consecutiveTurn = hasRecord && (rec.Turn == turn || rec.Turn == turn - 1);
            int count = consecutiveTurn ? rec.Count : 0;
            if (!hasRecord || rec.Turn != turn)
                count++;
            _extractionDeliveryFailures[key] = (turn, count);

            if (count < System.Math.Max(1, AiConfigV2.commitmentStallTurns))
                return false;

            _extractionSuppressedUntilTurn[key] = turn
                + System.Math.Max(1, AiConfigV2.allocatorRejectCooldownTurns) + 1;
            _extractionDeliveryFailures.Remove(key);
            return true;
        }

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
                ByPlayer[player] = s = new MissionIntentState();
            return s;
        }

        public static void Clear() => ByPlayer.Clear();
    }
}

