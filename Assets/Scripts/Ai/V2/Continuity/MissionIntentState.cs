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
        public void Remove(MissionIntentKey k)
        {
            var session = AiTurnSession.PeekActive(Owner);
            // Existing continuity rekeys the object before moving its dictionary slot.
            // That is a continuing role, not retirement of a completed waypoint.
            if (_intents.TryGetValue(k, out var intent) && !intent.IntentKey.Equals(k))
                session?.Leases.Rekey(k, intent.IntentKey);
            else session?.Leases.Retire(k);
            _intents.Remove(k);
        }
        internal void Remove(MissionIntentKey k, int turn)
        {
            // Explicit retirement also covers a terminal attempt that never became durable.
            var session = AiTurnSession.Peek(Owner, turn);
            if (session != null) session.Leases.Retire(k);
            else MissionLeaseBook.ReleaseResources(Owner, turn, k);
            _intents.Remove(k);
        }

        // Withdrawn Attack targets: the witness that stops an immediate identical restart
        // (AttackRetreatWitness). Survives the retirement of the intent that wrote it.
        private readonly Dictionary<AttackTargetRef, AttackRetreatWitness> _retreatWitnesses =
            new Dictionary<AttackTargetRef, AttackRetreatWitness>();
        internal bool TryGetRetreatWitness(AttackTargetRef target, out AttackRetreatWitness witness) =>
            _retreatWitnesses.TryGetValue(target, out witness);
        internal void PutRetreatWitness(AttackRetreatWitness witness)
        {
            if (witness != null && witness.Target.HasValue)
                _retreatWitnesses[witness.Target] = witness;
        }
        internal void ClearRetreatWitness(AttackTargetRef target) => _retreatWitnesses.Remove(target);
        internal int RetreatWitnessCount => _retreatWitnesses.Count;
        internal string RetreatWitnessDigest() => _retreatWitnesses.Count == 0 ? "-"
            : string.Join(";", _retreatWitnesses.Values
                .OrderBy(w => w.Target.Hex.Q).ThenBy(w => w.Target.Hex.R)
                .Select(w => $"{w.Target.Hex.Q},{w.Target.Hex.R}:{w.EnemyArmyId}:{w.EnemyFingerprint}:{w.OwnFingerprint}"));

        internal EconomyLifecycleState Economy { get; } = new EconomyLifecycleState();
        internal DevelopmentLifecycleState Development { get; } = new DevelopmentLifecycleState();
        // Same per-player/turn scope the session exposes; detached states use their own identity.
        internal ReconTurnState ReconTurn(int turn) => ReconTurnStateStore.For((object)Owner ?? this, turn);
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

