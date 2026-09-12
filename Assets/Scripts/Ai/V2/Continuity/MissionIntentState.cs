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

        public IReadOnlyCollection<MissionIntent> All => _intents.Values;
        public int Count => _intents.Count;
        public bool TryGet(MissionIntentKey k, out MissionIntent i) => _intents.TryGetValue(k, out i);
        public void Put(MissionIntent i) => _intents[i.IntentKey] = i;
        public void Remove(MissionIntentKey k) => _intents.Remove(k);

        public int BaseExpansionWaitTurns { get; private set; }

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
