using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Players;

namespace Game.Map
{
    // T belongs to the eventual event codec: authored definition/card keys, player identities,
    // chosen rewards and every runtime flag must be encoded there. This is an owner boundary,
    // not a complete event save format.
    [Serializable] public sealed class HexEventEntryState<T>
    {
        public HexCoord Hex;
        public T Payload;
    }

    public static partial class HexEventRegistry
    {
        // Give the codec a detached runtime copy. Authored definitions/cards and player
        // identities remain shared references for the codec to encode, not mutable snapshots.
        public static List<HexEventEntryState<T>> CaptureEntries<T>(Func<HexCoord, Entry, T> captureEntry)
        {
            if (captureEntry == null) throw new ArgumentNullException(nameof(captureEntry));
            var result = new List<HexEventEntryState<T>>();
            foreach (var pair in ByHex)
                result.Add(new HexEventEntryState<T> { Hex = pair.Key, Payload = captureEntry(pair.Key, CopyEntry(pair.Value)) });
            return result;
        }

        // Decode and detach all runtime values before mutation. No Set/MarkSkipped/MarkConsumed:
        // restoring saved flags must not discover events, claim rewards or notify observers.
        public static void RestoreEntries<T>(IEnumerable<HexEventEntryState<T>> state, Func<HexCoord, T, Entry> restoreEntry)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (restoreEntry == null) throw new ArgumentNullException(nameof(restoreEntry));
            var replacement = new Dictionary<HexCoord, Entry>();
            foreach (var entry in state)
            {
                if (entry == null || replacement.ContainsKey(entry.Hex))
                    throw new ArgumentException("Missing or duplicate hex event entry.", nameof(state));
                replacement.Add(entry.Hex, CopyEntry(restoreEntry(entry.Hex, entry.Payload)));
            }
            ByHex.Clear();
            foreach (var pair in replacement) ByHex.Add(pair.Key, pair.Value);
        }

        private static Entry CopyEntry(Entry source)
        {
            if (source?.ResolvedGuardMembers == null || source.SelectedRewards == null
                || source.ResolvedCardRewards == null || source.DiscoveredBy == null || source.KnownBy == null)
                throw new ArgumentException("An event codec must provide an entry with complete runtime collections.");
            var rewardCopies = new Dictionary<RewardEntry, RewardEntry>();
            var copy = new Entry
            {
                Definition = source.Definition, Consumed = source.Consumed, GuardArmyName = source.GuardArmyName,
                GuardOwner = source.GuardOwner, Triggered = source.Triggered, ConsumedBy = source.ConsumedBy,
                CollectionRewardPending = source.CollectionRewardPending, CollectionRewardError = source.CollectionRewardError,
                Skipped = source.Skipped,
                ResolvedGuardMembers = new List<(CardDefinition card, int count)>(source.ResolvedGuardMembers),
                DiscoveredBy = new HashSet<PlayerSetupData>(source.DiscoveredBy), KnownBy = new HashSet<PlayerSetupData>(source.KnownBy),
            };
            foreach (var reward in source.SelectedRewards) copy.SelectedRewards.Add(CopyReward(reward, rewardCopies));
            foreach (var pair in source.ResolvedCardRewards) copy.ResolvedCardRewards.Add((CopyReward(pair.reward, rewardCopies), pair.card));
            return copy;
        }

        private static RewardEntry CopyReward(RewardEntry source, Dictionary<RewardEntry, RewardEntry> copies)
        {
            if (source == null) return null;
            if (copies.TryGetValue(source, out var existing)) return existing;
            var copy = new RewardEntry
            {
                type = source.type, cardKey = source.cardKey,
                resources = source.resources == null ? null : new ResourceYields
                {
                    human = source.resources.human, energy = source.resources.energy,
                    materials = source.resources.materials, tech = source.resources.tech,
                },
            };
            copies.Add(source, copy);
            return copy;
        }
    }
}
