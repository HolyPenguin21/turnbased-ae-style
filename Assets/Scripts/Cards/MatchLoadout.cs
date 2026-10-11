using System;
using System.Collections.Generic;
using Game.Progression;
using Game.Players;

namespace Game.Cards
{
    // Immutable by ownership: mutable DTOs are never exposed or shared with the saved profile.
    public sealed class MatchLoadout
    {
        private readonly SavedDeck deck;
        private readonly Dictionary<string, int> owned = new Dictionary<string, int>();
        public Faction Faction => deck.faction;
        public MatchLoadout(SavedDeck source, DeckRules rules, Func<string, int> ownership)
        {
            var validation = rules.Validate(source, ownership);
            if (!validation.IsValid) throw new InvalidOperationException(string.Join("\n", validation.Errors));
            deck = CollectionProfile.CopyDeck(source);
            foreach (var e in deck.mainCards) owned[e.cardKey] = e.count;
            foreach (var e in deck.equipment) owned[e.cardKey] = e.count;
            foreach (var e in deck.mutators) owned[e.cardKey] = e.count;
        }
        public bool TryBuildPool(DeckRules rules, out List<CardDefinition> pool, out string error)
        {
            var validation = rules.Validate(deck, key => owned.TryGetValue(key, out int n) ? n : 0);
            if (!validation.IsValid) { pool = null; error = string.Join("\n", validation.Errors); return false; }
            return rules.Starting.TryBuildDeckPool(deck.mainCards, out pool, out error);
        }
        internal List<DeckCardEntry> BlueprintEntries()
        {
            var result = new List<DeckCardEntry>();
            foreach (var e in deck.equipment) result.Add(new DeckCardEntry { cardKey = e.cardKey, count = e.count });
            foreach (var e in deck.mutators) result.Add(new DeckCardEntry { cardKey = e.cardKey, count = e.count });
            return result;
        }
    }
    // Runtime quotas have no reference to the profile. Each participant owns a separate instance.
    public sealed class BlueprintQuota
    {
        private readonly HashSet<string> unlocked = new HashSet<string>();
        private readonly HashSet<string> pending = new HashSet<string>();
        public BlueprintQuota(MatchLoadout loadout) { foreach (var e in loadout.BlueprintEntries()) if (e.count > 0) unlocked.Add(e.cardKey); }
        // A blueprint in the deck unlocks unlimited production; only one attempt per card may be in flight.
        public bool Unlocked(string key) => unlocked.Contains(key ?? "");
        public bool Available(string key) => Unlocked(key) && !pending.Contains(key);
        internal bool TryReserve(string key) { if (!Available(key)) return false; return pending.Add(key); }
        internal void Release(string key) { pending.Remove(key); }
    }
    public sealed class ProductionAttempt : IDisposable
    {
        private readonly CardDefinition card;
        private readonly BlueprintQuota quota;
        private bool finished;
        internal ProductionAttempt(CardDefinition card, BlueprintQuota quota) { this.card = card; this.quota = quota; }
        public CardData Complete(bool success)
        {
            if (finished) return null;
            finished = true; quota?.Release(card.authoredKey);
            return success ? ResearchProductionSystem.MintCard(card) : null;
        }
        public void Dispose() { Complete(false); }
    }
}
