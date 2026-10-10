using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            notice = null;
            if (!File.Exists(path) && !File.Exists(path + ".bak")) return null;
            try { return Read(path); }
            catch (Exception ex)
            {
                // Future schemas must never be replaced by an older client's backup.
                if (ex is NotSupportedException) throw;
                notice = "Profile recovered from backup: " + ex.Message;
                var restored = Read(path + ".bak");
                if (File.Exists(path)) File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                return restored;
            }
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
            if (p.selectedDeckByFaction.Any(s => s == null)) throw new InvalidDataException("Invalid deck selections.");
            if (p.pendingRewards.Any(r => r == null || string.IsNullOrWhiteSpace(r.matchId) || r.offeredKeys == null || r.acquiredKeys == null)
                || p.pendingRewards.Select(r => r.matchId).Distinct().Count() != p.pendingRewards.Count)
                throw new InvalidDataException("Invalid pending rewards.");
        }
        public void Save(CollectionProfile profile)
        {
            ValidateShape(profile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(profile, true));
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
