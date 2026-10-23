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
        private static CollectionProfile Read(string file)
        {
            var profile = JsonUtility.FromJson<CollectionProfile>(File.ReadAllText(file));
            ValidateShape(profile);
            return profile;
        }
        public static void ValidateShape(CollectionProfile p)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.profileId)) throw new InvalidDataException("Missing profile identity.");
            if (p.schemaVersion > 1) throw new NotSupportedException("This profile needs a newer game version.");
            if (p.schemaVersion != 1) throw new InvalidDataException("Unsupported profile schema.");
            if (p.ownedCards == null || p.savedDecks == null || p.selectedDeckByFaction == null || p.pendingRewards == null || p.claimedMatchIds == null)
                throw new InvalidDataException("Incomplete profile.");
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
        public void Save(CollectionProfile profile)
        {
            ValidateShape(profile);
            AtomicJsonFile.Save(path, System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(profile, true)));
        }
    }
}
