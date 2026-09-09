using System;
using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Factual invalidations only. These flags describe what changed; Strategy maps them to dirty
    // task families. EventState and ResourceSite deliberately stay distinct from Resources:
    // discovering an opportunity is not the same fact as changing the physical H/E/M/T stockpile.
    [Flags]
    internal enum StrategicInvalidationReason
    {
        None = 0,
        ReconKnowledge = 1 << 0,
        Contact = 1 << 1,
        Threat = 1 << 2,
        Actor = 1 << 3,
        Capability = 1 << 4,
        Hand = 1 << 5,
        Resources = 1 << 6,
        Infrastructure = 1 << 7,
        EventState = 1 << 8,
        ResourceSite = 1 << 9,
        External = 1 << 10,
    }

    // Immutable aggregate returned to one consumer. Payload is stored per reason inside the
    // registry, so consuming Contact cannot erase actor/hex evidence still needed by another
    // task family.
    internal sealed class StrategicInvalidation
    {
        internal readonly StrategicInvalidationReason Reasons;
        internal readonly int RegistryVersion;
        internal readonly IReadOnlyCollection<int> ActorIds;
        internal readonly IReadOnlyCollection<int> ContactIds;
        internal readonly IReadOnlyCollection<HexCoord> Hexes;
        internal readonly AiHandData Hand;

        internal bool Any => Reasons != StrategicInvalidationReason.None;

        internal StrategicInvalidation(StrategicInvalidationReason reasons, int version,
            IReadOnlyCollection<int> actorIds, IReadOnlyCollection<int> contactIds,
            IReadOnlyCollection<HexCoord> hexes, AiHandData hand)
        {
            Reasons = reasons;
            RegistryVersion = version;
            ActorIds = actorIds ?? Array.Empty<int>();
            ContactIds = contactIds ?? Array.Empty<int>();
            Hexes = hexes ?? Array.Empty<HexCoord>();
            Hand = hand;
        }

        internal static StrategicInvalidation Empty =>
            new StrategicInvalidation(StrategicInvalidationReason.None, 0,
                Array.Empty<int>(), Array.Empty<int>(), Array.Empty<HexCoord>(), null);
    }

    // Carries current-turn facts that invalidate conclusions made by a prior strategic pass.
    // This remains the ONE interrupt/invalidation owner. Domain event/reward/map-memory code does
    // not write here; the observation boundary will classify those authoritative changes.
    internal static class StrategicInterruptRegistry
    {
        private sealed class PayloadBucket
        {
            public readonly HashSet<int> ActorIds = new HashSet<int>();
            public readonly HashSet<int> ContactIds = new HashSet<int>();
            public readonly HashSet<HexCoord> Hexes = new HashSet<HexCoord>();

            public void Merge(PayloadBucket other)
            {
                if (other == null) return;
                ActorIds.UnionWith(other.ActorIds);
                ContactIds.UnionWith(other.ContactIds);
                Hexes.UnionWith(other.Hexes);
            }
        }

        private sealed class Entry
        {
            public int Turn;
            public AiHandData Hand;
            public StrategicInvalidationReason Reasons;
            public readonly Dictionary<StrategicInvalidationReason, PayloadBucket> PayloadByReason =
                new Dictionary<StrategicInvalidationReason, PayloadBucket>();
            // Monotonic within a pending-set lifetime. Bumped exactly once by Mark for each
            // non-empty factual delta and once by a partial Consume/ClearDiscovery mutation.
            public int Version;
        }

        private static readonly StrategicInvalidationReason[] SingleReasons =
        {
            StrategicInvalidationReason.ReconKnowledge,
            StrategicInvalidationReason.Contact,
            StrategicInvalidationReason.Threat,
            StrategicInvalidationReason.Actor,
            StrategicInvalidationReason.Capability,
            StrategicInvalidationReason.Hand,
            StrategicInvalidationReason.Resources,
            StrategicInvalidationReason.Infrastructure,
            StrategicInvalidationReason.EventState,
            StrategicInvalidationReason.ResourceSite,
            StrategicInvalidationReason.External,
        };

        private const StrategicInvalidationReason AllReasons =
            StrategicInvalidationReason.ReconKnowledge
            | StrategicInvalidationReason.Contact
            | StrategicInvalidationReason.Threat
            | StrategicInvalidationReason.Actor
            | StrategicInvalidationReason.Capability
            | StrategicInvalidationReason.Hand
            | StrategicInvalidationReason.Resources
            | StrategicInvalidationReason.Infrastructure
            | StrategicInvalidationReason.EventState
            | StrategicInvalidationReason.ResourceSite
            | StrategicInvalidationReason.External;

        private static readonly Dictionary<PlayerSetupData, Entry> ByPlayer =
            new Dictionary<PlayerSetupData, Entry>();

        public static void CaptureTurnContext(PlayerSetupData player, int turn, AiHandData hand)
        {
            if (player == null) return;
            Entry e = GetOrReset(player, turn);
            if (hand != null) e.Hand = hand;
        }

        // The caller invokes Mark only for an observed, non-empty factual delta. A compound reason
        // is one observation and therefore advances Version once, not once per flag.
        internal static void Mark(PlayerSetupData player, int turn,
            StrategicInvalidationReason reasons, IEnumerable<int> actorIds = null,
            IEnumerable<int> contactIds = null, IEnumerable<HexCoord> hexes = null,
            AiHandData hand = null)
        {
            reasons &= AllReasons;
            if (player == null || reasons == StrategicInvalidationReason.None)
                return;

            Entry e = GetOrReset(player, turn);
            if (hand != null) e.Hand = hand;

            var incoming = new PayloadBucket();
            AddPositive(incoming.ActorIds, actorIds);
            AddPositive(incoming.ContactIds, contactIds);
            AddHexes(incoming.Hexes, hexes);

            foreach (StrategicInvalidationReason single in SingleReasons)
            {
                if ((reasons & single) == 0) continue;
                if (!e.PayloadByReason.TryGetValue(single, out PayloadBucket bucket))
                {
                    bucket = new PayloadBucket();
                    e.PayloadByReason[single] = bucket;
                }
                bucket.Merge(incoming);
            }

            e.Reasons |= reasons;
            e.Version++;
        }

        internal static StrategicInvalidation Peek(PlayerSetupData player, int turn)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e)
                || e.Turn != turn || e.Reasons == StrategicInvalidationReason.None)
                return StrategicInvalidation.Empty;
            return Snapshot(e, e.Reasons);
        }

        // Clears only the requested reasons and their own payload buckets. Evidence attached to
        // flags that another task family has not consumed remains intact.
        internal static StrategicInvalidation Consume(PlayerSetupData player, int turn,
            StrategicInvalidationReason mask)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return StrategicInvalidation.Empty;

            StrategicInvalidationReason consumed = e.Reasons & mask & AllReasons;
            if (consumed == StrategicInvalidationReason.None)
                return StrategicInvalidation.Empty;

            StrategicInvalidation snapshot = Snapshot(e, consumed);
            foreach (StrategicInvalidationReason single in SingleReasons)
                if ((consumed & single) != 0)
                    e.PayloadByReason.Remove(single);
            e.Reasons &= ~consumed;
            e.Version++;
            return snapshot;
        }

        // Compatibility wrappers for the existing bounded StrategicReactionPass. Their observable
        // meaning is unchanged: discovered army ids remain TargetIds; hand/capability remain the
        // only follow-up reasons. The additional ReconKnowledge flag is for the future task loop.
        public static void MarkDiscovery(PlayerSetupData player, int turn, IEnumerable<int> armyIds)
        {
            if (player == null || armyIds == null) return;
            Entry e = GetOrReset(player, turn);
            var newIds = new List<int>();
            PayloadBucket existing = null;
            e.PayloadByReason.TryGetValue(StrategicInvalidationReason.Contact, out existing);
            foreach (int id in armyIds)
                if (id > 0 && (existing == null || !existing.ContactIds.Contains(id))
                    && !newIds.Contains(id))
                    newIds.Add(id);
            if (newIds.Count == 0) return;
            Mark(player, turn,
                StrategicInvalidationReason.ReconKnowledge | StrategicInvalidationReason.Contact,
                contactIds: newIds);
        }

        public static void MarkHandOpportunity(PlayerSetupData player, int turn, AiHandData hand) =>
            Mark(player, turn, StrategicInvalidationReason.Hand, hand: hand);

        public static void MarkCapabilityChanged(PlayerSetupData player, int turn, AiHandData hand) =>
            Mark(player, turn, StrategicInvalidationReason.Capability, hand: hand);

        // Current pending-invalidation generation for this turn (0 = no entry / different turn).
        public static int Version(PlayerSetupData player, int turn) =>
            player != null && ByPlayer.TryGetValue(player, out Entry e) && e.Turn == turn
                ? e.Version : 0;

        // Historical name retained for existing call sites. It means any pending invalidation.
        public static bool HasPendingDiscovery(PlayerSetupData player, int turn) => HasPending(player, turn);

        public static bool HasPending(PlayerSetupData player, int turn) =>
            player != null && ByPlayer.TryGetValue(player, out Entry e)
            && e.Turn == turn && e.Reasons != StrategicInvalidationReason.None;

        public static bool HasPendingContactDiscovery(PlayerSetupData player, int turn) =>
            HasAny(player, turn, StrategicInvalidationReason.Contact);

        public static bool HasPendingFollowup(PlayerSetupData player, int turn) =>
            HasAny(player, turn,
                StrategicInvalidationReason.Hand | StrategicInvalidationReason.Capability);

        public static HashSet<int> TargetIds(PlayerSetupData player, int turn)
        {
            var result = new HashSet<int>();
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return result;
            MergePayload(e, StrategicInvalidationReason.Contact, null, result, null);
            return result;
        }

        public static bool TryGetHand(PlayerSetupData player, int turn, out AiHandData hand)
        {
            hand = null;
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return false;
            hand = e.Hand;
            return hand != null;
        }

        // Legacy contact-recursion suppression consumes the two flags emitted together by
        // MarkDiscovery. Other typed facts (EventState, ResourceSite, Resources, etc.) survive.
        public static void ClearDiscovery(PlayerSetupData player, int turn) =>
            Consume(player, turn,
                StrategicInvalidationReason.Contact | StrategicInvalidationReason.ReconKnowledge);

        public static void Clear(PlayerSetupData player, int turn)
        {
            if (player != null && ByPlayer.TryGetValue(player, out Entry e) && e.Turn == turn)
                ByPlayer.Remove(player);
        }

        private static bool HasAny(PlayerSetupData player, int turn, StrategicInvalidationReason mask) =>
            player != null && ByPlayer.TryGetValue(player, out Entry e)
            && e.Turn == turn && (e.Reasons & mask) != 0;

        private static StrategicInvalidation Snapshot(Entry e, StrategicInvalidationReason reasons)
        {
            var actors = new HashSet<int>();
            var contacts = new HashSet<int>();
            var hexes = new HashSet<HexCoord>();
            foreach (StrategicInvalidationReason single in SingleReasons)
                if ((reasons & single) != 0)
                    MergePayload(e, single, actors, contacts, hexes);
            return new StrategicInvalidation(reasons, e.Version,
                SortedArray(actors), SortedArray(contacts), HexArray(hexes), e.Hand);
        }

        private static void MergePayload(Entry e, StrategicInvalidationReason reason,
            HashSet<int> actors, HashSet<int> contacts, HashSet<HexCoord> hexes)
        {
            if (!e.PayloadByReason.TryGetValue(reason, out PayloadBucket bucket))
                return;
            actors?.UnionWith(bucket.ActorIds);
            contacts?.UnionWith(bucket.ContactIds);
            hexes?.UnionWith(bucket.Hexes);
        }

        private static int[] SortedArray(HashSet<int> values)
        {
            if (values == null || values.Count == 0) return Array.Empty<int>();
            var result = new int[values.Count];
            values.CopyTo(result);
            Array.Sort(result);
            return result;
        }

        private static HexCoord[] HexArray(HashSet<HexCoord> values)
        {
            if (values == null || values.Count == 0) return Array.Empty<HexCoord>();
            var result = new HexCoord[values.Count];
            values.CopyTo(result);
            return result;
        }

        private static void AddPositive(HashSet<int> target, IEnumerable<int> source)
        {
            if (source == null) return;
            foreach (int id in source)
                if (id > 0) target.Add(id);
        }

        private static void AddHexes(HashSet<HexCoord> target, IEnumerable<HexCoord> source)
        {
            if (source == null) return;
            foreach (HexCoord hex in source) target.Add(hex);
        }

        private static Entry GetOrReset(PlayerSetupData player, int turn)
        {
            if (!ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
            {
                e = new Entry { Turn = turn };
                ByPlayer[player] = e;
            }
            return e;
        }
    }
}
