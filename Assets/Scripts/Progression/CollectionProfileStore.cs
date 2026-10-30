using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Core;
using UnityEngine;

namespace Game.Progression
{
    // Atomic disk boundary. The service publishes in-memory changes only after this succeeds.
    public sealed class CollectionProfileStore
    {
        public const int MaximumProfileBytes = 16 * 1024 * 1024;
        public const int MaximumEventRewards = 100000;
        public const int MaximumRewardIdentityLength = 256;
        private readonly string path;
        public CollectionProfileStore(string directory) { path = Path.Combine(directory, "collection-v1.json"); }
        public CollectionProfile Load(out string notice)
        {
            var profile = AtomicJsonFile.Load(path, Read, "Collection restored from backup: ", out notice);
            if (!string.IsNullOrEmpty(notice))
            {
                Debug.LogWarning(notice);
                notice = "Collection restored from backup.";
            }
            return profile;
        }
        // A missing v2 ledger must not become an innocent empty list. Unity's JsonUtility always
        // allocates list fields, so the key itself is checked in the file text.
        private static readonly System.Text.RegularExpressions.Regex ReceiptLedgerKey =
            new System.Text.RegularExpressions.Regex(@"""eventRewards""\s*:");
        private static CollectionProfile Read(string file)
        {
            if (new FileInfo(file).Length > MaximumProfileBytes) throw new InvalidDataException("Collection file too large.");
            string json = File.ReadAllText(file);
            var profile = JsonUtility.FromJson<CollectionProfile>(json);
            if (profile != null && profile.schemaVersion == 2
                && !ReceiptLedgerKey.IsMatch(json))
                throw new InvalidDataException("Missing event reward ledger.");
            // v1 predates event receipts. Existing ownership is retained without inventing or
            // replaying historical grants; only future event claims acquire durable receipts.
            if (profile != null && profile.schemaVersion == 1)
            {
                if (profile.eventRewards == null) profile.eventRewards = new List<EventRewardReceipt>();
                profile.schemaVersion = 2;
            }
            ValidateShape(profile);
            return profile;
        }
        public static void ValidateShape(CollectionProfile p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.profileId)) throw new InvalidDataException("Missing profile identity.");
            if (p.schemaVersion > 2) throw new NotSupportedException("This profile needs a newer game version.");
            if (p.schemaVersion != 2) throw new InvalidDataException("Unsupported profile schema.");
            if (p.ownedCards == null || p.savedDecks == null || p.selectedDeckByFaction == null || p.pendingRewards == null || p.claimedMatchIds == null || p.eventRewards == null)
                throw new InvalidDataException("Incomplete profile.");
            if (p.eventRewards.Count > MaximumEventRewards) throw new InvalidDataException("Too many event reward receipts.");
            var eventIdentities = new HashSet<(string, string)>();
            foreach (var receipt in p.eventRewards)
                if (receipt == null || !ValidRewardIdentity(receipt.matchId) || !ValidRewardIdentity(receipt.eventId)
                    || (!string.IsNullOrEmpty(receipt.cardKey) && string.IsNullOrWhiteSpace(receipt.cardKey))
                    || !eventIdentities.Add((receipt.matchId, receipt.eventId)))
                    throw new InvalidDataException("Invalid event reward receipts.");
            var keys = new HashSet<string>();
            foreach (var e in p.ownedCards)
                if (e == null || string.IsNullOrWhiteSpace(e.cardKey) || e.count < 0 || !keys.Add(e.cardKey))
                    throw new InvalidDataException("Invalid ownership entries.");
            if (p.savedDecks.Any(d => d == null || string.IsNullOrWhiteSpace(d.deckId) || d.mainCards == null || d.equipment == null || d.mutators == null
                || d.mainCards.Concat(d.equipment).Concat(d.mutators).Any(e => e == null || string.IsNullOrWhiteSpace(e.cardKey)))
                || p.savedDecks.Select(d => d.deckId).Distinct().Count() != p.savedDecks.Count)
                throw new InvalidDataException("Invalid deck identities.");
            if (p.selectedDeckByFaction.Any(s => s == null || string.IsNullOrWhiteSpace(s.deckId))
                || p.selectedDeckByFaction.Select(s => s.faction).Distinct().Count() != p.selectedDeckByFaction.Count)
                throw new InvalidDataException("Invalid deck selections.");
            if (p.claimedMatchIds.Any(string.IsNullOrWhiteSpace) || p.claimedMatchIds.Distinct().Count() != p.claimedMatchIds.Count)
                throw new InvalidDataException("Invalid claimed match identities.");
            if (p.pendingRewards.Any(r => r == null || string.IsNullOrWhiteSpace(r.matchId) || r.offeredKeys == null || r.acquiredKeys == null
                || r.offeredKeys.Any(string.IsNullOrWhiteSpace) || r.acquiredKeys.Any(string.IsNullOrWhiteSpace)
                || r.offeredKeys.Distinct().Count() != r.offeredKeys.Count || r.acquiredKeys.Distinct().Count() != r.acquiredKeys.Count
                || r.acquiredKeys.Any(k => !r.offeredKeys.Contains(k))
                || (r.outcome != MatchOutcome.Victory && r.outcome != MatchOutcome.Defeat)
                || r.offeredKeys.Count > (r.outcome == MatchOutcome.Victory ? 5 : 1)
                || r.acquiredKeys.Count > (r.outcome == MatchOutcome.Victory ? 2 : 1)
                || r.claimed != p.claimedMatchIds.Contains(r.matchId) || (!r.claimed && r.acquiredKeys.Count > 0))
                || p.pendingRewards.Select(r => r.matchId).Distinct().Count() != p.pendingRewards.Count)
                throw new InvalidDataException("Invalid pending rewards.");
        }
        public static bool ValidRewardIdentity(string value)
            => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumRewardIdentityLength;
        public void Save(CollectionProfile profile)
        {
            ValidateShape(profile);
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(profile, true));
            if (bytes.Length > MaximumProfileBytes) throw new InvalidDataException("Collection file too large.");
            AtomicJsonFile.Save(path, bytes);
        }
    }
}
