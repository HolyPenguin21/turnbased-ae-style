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

        internal static int Apply(PlayerSetupData player, int turn, WorldDelta delta)
        {
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
