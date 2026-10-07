using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // Immutable factual publication. Observed knowledge may become dirty without a V2 mutation;
    // a rollback/no-op carries HasMutation=false and never advances world freshness.
    internal readonly struct WorldDelta
    {
        internal readonly bool HasMutation;
        internal readonly StrategicInvalidationReason DirtyFacts;
        internal readonly IReadOnlyCollection<int> ActorIds;
        internal readonly IReadOnlyCollection<int> ContactIds;
        internal readonly IReadOnlyCollection<HexCoord> Hexes;
        internal readonly AiHandData Hand;

        internal WorldDelta(bool hasMutation, StrategicInvalidationReason dirtyFacts,
            IEnumerable<int> actorIds = null, IEnumerable<int> contactIds = null,
            IEnumerable<HexCoord> hexes = null, AiHandData hand = null)
        {
            HasMutation = hasMutation;
            DirtyFacts = dirtyFacts;
            ActorIds = actorIds?.ToArray();
            ContactIds = contactIds?.ToArray();
            Hexes = hexes?.ToArray();
            Hand = hand;
        }
    }

    // One revision policy, using the existing invalidation storage without mirrored flags.
    // Legacy commit endpoints use CommitMutation; observed facts use Publish (no second bump).
    internal static class WorldDeltaLifecycle
    {
        internal static int Current { get; private set; }
        // Only synchronous canonical transactions may use this scope. Never retain it across
        // a coroutine yield: other game actions must keep their own revision boundary.
        [ThreadStatic] private static MutationTransaction _transaction;
        internal static MutationTransaction BeginTransaction() => new MutationTransaction();

        internal sealed class MutationTransaction : IDisposable
        {
            private readonly MutationTransaction _parent;
            private readonly List<(PlayerSetupData player, int turn, WorldDelta delta)> _facts =
                new List<(PlayerSetupData, int, WorldDelta)>();
            private bool _ended;
            internal MutationTransaction() { _parent = _transaction; _transaction = this; }
            internal void Stage(PlayerSetupData player, int turn, WorldDelta delta) =>
                _facts.Add((player, turn, delta));
            internal int Commit(PlayerSetupData player, int turn, WorldDelta committedDelta)
            {
                if (_ended || _transaction != this)
                    throw new InvalidOperationException("World mutation transactions must close in order.");
                _transaction = _parent; _ended = true;
                // The canonical owner reports whether the transaction actually committed.
                // Child stamps are discarded on rollback or no-op, not interpreted as success.
                if (!committedDelta.HasMutation) return Current;
                Stage(player, turn, committedDelta);
                if (_parent != null)
                {
                    foreach (var fact in _facts) _parent.Stage(fact.player, fact.turn, fact.delta);
                    return Current;
                }
                ++Current;
                foreach (var fact in _facts)
                    StrategicInterruptRegistry.Record(fact.player, fact.turn, fact.delta.DirtyFacts,
                        fact.delta.ActorIds, fact.delta.ContactIds, fact.delta.Hexes, fact.delta.Hand);
                return Current;
            }
            public void Dispose()
            {
                if (_ended) return;
                if (_transaction != this)
                    throw new InvalidOperationException("World mutation transactions must close in order.");
                _transaction = _parent; _ended = true;
            }
        }

        internal static int Apply(PlayerSetupData player, int turn, WorldDelta delta)
        {
            if (_transaction != null)
            {
                _transaction.Stage(player, turn, delta);
                return Current;
            }
            if (delta.HasMutation) ++Current;
            StrategicInterruptRegistry.Record(player, turn, delta.DirtyFacts,
                delta.ActorIds, delta.ContactIds, delta.Hexes, delta.Hand);
            return Current;
        }

        internal static int CommitMutation(bool committed = true) =>
            Apply(null, -1, new WorldDelta(committed, StrategicInvalidationReason.None));

        internal static void Publish(PlayerSetupData player, int turn,
            StrategicInvalidationReason reasons, IEnumerable<int> actorIds = null,
            IEnumerable<int> contactIds = null, IEnumerable<HexCoord> hexes = null,
            AiHandData hand = null) =>
            Apply(player, turn, new WorldDelta(false, reasons, actorIds, contactIds, hexes, hand));
    }
}
