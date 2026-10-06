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

        internal EconomyLifecycleState Economy { get; } = new EconomyLifecycleState();
        internal DevelopmentLifecycleState Development { get; } = new DevelopmentLifecycleState();
        private ReconTurnState Recon(int turn) => ReconTurnStateStore.For((object)Owner ?? this, turn);

        // Compatibility adapters for existing fixtures/callers. No storage or policy here.
        internal void RememberGeneratedDevelopmentOperator(CardData card, HexCoord site,
            ResearchProductionMode mode, int turn) =>
            Development.RememberGeneratedDevelopmentOperator(card, site, mode, turn);
        internal IReadOnlyList<CardData> ReconcileGeneratedDevelopmentOperators(int turn,
            Func<CardData, HexCoord, ResearchProductionMode, bool> stillNeeded) =>
            Development.ReconcileGeneratedDevelopmentOperators(turn, stillNeeded);
        internal bool TryConsumeReconLaneTrim(int turn) => Recon(turn).TryConsumeReconLaneTrim(turn);
        internal void MarkReconActorTrimmed(int turn, int armyId) => Recon(turn).MarkReconActorTrimmed(turn, armyId);
        internal IReadOnlyCollection<int> ReconActorsTrimmedThisTurn(int turn) => Recon(turn).ReconActorsTrimmedThisTurn(turn);
        internal IReadOnlyCollection<int> ReconGroundActorsUsedThisTurn(int turn) => Recon(turn).ReconGroundActorsUsedThisTurn(turn);
        internal void MarkReconGroundActorUsed(int turn, int armyId) => Recon(turn).MarkReconGroundActorUsed(turn, armyId);
        internal bool IsBaseExpansionDeliverySuppressed(int turn, CardData card, HexCoord? target) => Economy.IsBaseExpansionDeliverySuppressed(turn, card, target);
        internal bool RecordBaseExpansionDeliveryFailure(int turn, CardData card, HexCoord? target) => Economy.RecordBaseExpansionDeliveryFailure(turn, card, target);
        internal void RecordBaseExpansionDeliveryProgress(int turn, CardData card, HexCoord? target) => Economy.RecordBaseExpansionDeliveryProgress(turn, card, target);
        internal void RecordExtractionDeliveryProgress(int turn, ResourceType? resourceType, HexCoord target) => Economy.RecordExtractionDeliveryProgress(turn, resourceType, target);
        internal bool IsExtractionDeliverySuppressed(int turn, ResourceType? resourceType, HexCoord target) => Economy.IsExtractionDeliverySuppressed(turn, resourceType, target);
        internal bool RecordExtractionDeliveryFailure(int turn, ResourceType? resourceType, HexCoord target) => Economy.RecordExtractionDeliveryFailure(turn, resourceType, target);

    }

    public static class MissionIntentRegistry
    {
        private static readonly Dictionary<PlayerSetupData, MissionIntentState> ByPlayer =
            new Dictionary<PlayerSetupData, MissionIntentState>();

        // Read-only pre-turn access: initiative may inspect existing commitments without
        // creating a continuity state or advancing any of its clocks.
        public static MissionIntentState Peek(PlayerSetupData player) =>
            player != null && ByPlayer.TryGetValue(player, out MissionIntentState s) ? s : null;

        public static MissionIntentState GetOrCreate(PlayerSetupData player)
        {
            if (player == null)
                return new MissionIntentState();
            if (!ByPlayer.TryGetValue(player, out MissionIntentState s))
                ByPlayer[player] = s = new MissionIntentState(player);
            return s;
        }

        public static void Clear()
        {
            AiTurnSession.ClearAll();
            ByPlayer.Clear();
            ReconTurnStateStore.ClearAll();
        }
    }
}

