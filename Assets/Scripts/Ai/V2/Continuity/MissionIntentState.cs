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
        private int _baseExpansionEligibleTurn = -1;
        private int _baseExpansionLastReconciledTurn = -1;
        private CardData _baseExpansionCard;
        private HexCoord? _baseExpansionTarget;
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
            if (armyId != 0)
                _reconTrimmedActorIds.Add(armyId);
        }

        internal IReadOnlyCollection<int> ReconActorsTrimmedThisTurn(int turn)
        {
            EnsureReconTrimTurn(turn);
            return _reconTrimmedActorIds;
        }

        internal bool IsStagedBaseExpansion(CardData card, HexCoord? target) =>
            card == _baseExpansionCard && target.HasValue
            && target.Equals(_baseExpansionTarget);

        public int BaseExpansionWaitTurns { get; private set; }

        internal bool IsBaseExpansionDeliverySuppressed(int turn, CardData card, HexCoord? target) =>
            turn < _baseExpansionSuppressedUntilTurn
            && card == _baseExpansionSuppressedCard
            && target.HasValue
            && target.Equals(_baseExpansionSuppressedTarget);

        // A structurally valid site may still be operationally impossible for every materialized
        // builder. Count only consecutive, canonical delivery-gate failures for the exact staged
        // project. Once the ordinary commitment stall window is exhausted, briefly suppress that
        // project so Demand can compare other sites instead of manufacturing urgency forever.
        internal bool RecordBaseExpansionDeliveryFailure(int turn, CardData card, HexCoord? target)
        {
            if (card == null || !target.HasValue)
                return false;
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
            ResetBaseExpansionWait();
            return true;
        }

        // Pre-intent continuity for a legal Base opportunity. A Base mission cannot own a durable
        // actor before winning allocation, but repeated portfolio deferral must still survive into
        // the next turn. Kept in the existing continuity state rather than a second manager.
        internal float MarkBaseExpansionCandidate(int turn, CardData card,
            HexCoord? target, bool structurallyEligible)
        {
            if (!structurallyEligible || card == null || !target.HasValue)
            {
                ResetBaseExpansionWait();
                return 0f;
            }
            if (_baseExpansionCard != card
                || !_baseExpansionTarget.HasValue
                || !_baseExpansionTarget.Value.Equals(target.Value))
            {
                ResetBaseExpansionWait();
                _baseExpansionCard = card;
                _baseExpansionTarget = target;
            }
            _baseExpansionEligibleTurn = turn;
            return BaseExpansionWaitTurns * AiConfigV2.economyBaseUrgencyPerDeferredTurn;
        }

        internal void ReconcileBaseExpansionWait(int turn,
            IReadOnlyList<MissionTurnOutcome> outcomes)
        {
            bool completed = (outcomes ?? System.Array.Empty<MissionTurnOutcome>()).Any(o =>
                IsStagedBaseExpansionOutcome(o)
                && (o.EconomyBuildCompleted
                    || (o.Outcome == ExecutionOutcome.Completed && o.ObjectiveSatisfied)));
            bool invalidated = (outcomes ?? System.Array.Empty<MissionTurnOutcome>()).Any(o =>
                IsStagedBaseExpansionOutcome(o)
                && (o.StructuralFailure
                    || o.ProvisionFailureKindValue == ProvisionFailureKind.TargetInvalidated));
            if (completed || invalidated || _baseExpansionEligibleTurn != turn)
            {
                ResetBaseExpansionWait();
                return;
            }
            if (_baseExpansionLastReconciledTurn == turn)
                return;
            _baseExpansionLastReconciledTurn = turn;
            BaseExpansionWaitTurns++;
        }

        private static bool IsBaseExpansionOutcome(MissionTurnOutcome outcome)
        {
            if (outcome == null || outcome.MissionKind != MissionKind.Economy)
                return false;
            if (outcome.HasEconomyPayload
                && outcome.EconomyTarget.Kind == EconomyTaskKind.FoundBase)
                return true;
            return outcome.Proposal?.Target is EconomyMissionTarget proposed
                && proposed.Kind == EconomyTaskKind.FoundBase;
        }

        private bool IsStagedBaseExpansionOutcome(MissionTurnOutcome outcome)
        {
            if (!IsBaseExpansionOutcome(outcome) || !_baseExpansionTarget.HasValue)
                return false;
            EconomyMissionTarget target;
            if (outcome.HasEconomyPayload)
                target = outcome.EconomyTarget;
            else if (outcome.Proposal?.Target is EconomyMissionTarget proposed)
                target = proposed;
            else
                return false;
            return target.TargetHex.Equals(_baseExpansionTarget.Value)
                && (_baseExpansionCard == null || target.BuildCard == _baseExpansionCard);
        }

        private void ResetBaseExpansionWait()
        {
            BaseExpansionWaitTurns = 0;
            _baseExpansionEligibleTurn = -1;
            _baseExpansionLastReconciledTurn = -1;
            _baseExpansionCard = null;
            _baseExpansionTarget = null;
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

