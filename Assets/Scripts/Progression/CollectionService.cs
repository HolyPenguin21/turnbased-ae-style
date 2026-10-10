using System;
using System.Linq;
using Game.Cards;
using Game.Players;

namespace Game.Progression
{
    public sealed class CollectionService
    {
        public DeckRules Rules { get; }
        private readonly CollectionProfileStore store;
        private CollectionProfile profile;
        public event Action Changed;
        public CollectionProfile Snapshot => profile.Copy();
        public int Owned(string key) => profile.Owned(key);
        public CollectionService(DeckRules rules, CollectionProfileStore store, CollectionProfile initial)
        { Rules = rules; this.store = store; profile = initial.Copy(); }
        public bool Transact(Action<CollectionProfile> edit, out string error)
        {
            error = null;
            try
            {
                var next = profile.Copy(); edit(next); store.Save(next); profile = next;
            }
            catch (Exception ex) { error = ex.Message; return false; }
            Changed?.Invoke();
            return true;
        }
        public SavedDeck DefaultDeck(Faction faction)
        {
            string selected = profile.selectedDeckByFaction.Find(s => s.faction == faction)?.deckId;
            var deck = selected != null ? profile.savedDecks.Find(d => d.faction == faction && d.deckId == selected)
                : profile.savedDecks.Find(d => d.faction == faction);
            return deck == null ? null : CollectionProfile.CopyDeck(deck);
        }
        public SavedDeck Starter(Faction faction)
        {
            var deck = new SavedDeck { deckId = Guid.NewGuid().ToString("N"), name = Rules.Starting.GetDeck(faction)?.deckName ?? "Starter", faction = faction };
            var source = Rules.Starting.GetDeck(faction);
            if (source?.cards != null)
                foreach (var e in source.cards.Where(e => e != null && e.count > 0))
                {
                    var card = Rules.Starting.ResolveCard(e.cardKey);
                    if (card == null || !Rules.Permitted(card, faction)) throw new InvalidOperationException("Invalid starter reference: " + e.cardKey);
                    DeckRules.Entries(deck, DeckRules.Category(card)).Add(new DeckCardEntry { cardKey = card.authoredKey, count = e.count });
                }
            foreach (var e in Rules.Starting.GetCollectionBlueprints(faction))
            {
                var card = Rules.Resolve(e.cardKey);
                if (card == null || DeckRules.Category(card) == DeckCategory.Main || !Rules.Permitted(card, faction))
                    throw new InvalidOperationException("Invalid initial blueprint: " + e.cardKey);
                DeckRules.Entries(deck, DeckRules.Category(card)).Add(new DeckCardEntry { cardKey = e.cardKey, count = e.count });
            }
            return deck;
        }
        public void InitializeStarters(CollectionProfile target)
        {
            foreach (var faction in DeckRules.PlayableFactions)
            {
                var starter = Starter(faction);
                GrantInitialCards(target, starter.mainCards.Concat(starter.equipment).Concat(starter.mutators));
                var validation = Rules.Validate(starter, target.Owned);
                if (!validation.IsValid) throw new InvalidOperationException(string.Join("\n", validation.Errors));
                target.savedDecks.Add(starter);
                target.selectedDeckByFaction.Add(new DeckSelection { faction = faction, deckId = starter.deckId });
            }
        }
        // Catalog updates grant new starter blueprints without changing any saved composition.
        // This also keeps the existing Starter action usable for profiles created before the update.
        public bool EnsureStarterBlueprintOwnership(out string error)
        {
            var rows = DeckRules.PlayableFactions.SelectMany(Rules.Starting.GetCollectionBlueprints).ToList();
            error = null;
            if (rows.All(e => Owned(e.cardKey) >= e.count)) return true;
            return Transact(p => GrantInitialCards(p, rows), out error);
        }
        private static void GrantInitialCards(CollectionProfile target, System.Collections.Generic.IEnumerable<DeckCardEntry> rows)
        {
            foreach (var e in rows)
            {
                var owned = target.ownedCards.Find(o => o.cardKey == e.cardKey);
                if (owned == null) target.ownedCards.Add(new DeckCardEntry { cardKey = e.cardKey, count = e.count });
                else owned.count = Math.Max(owned.count, e.count); // shared definitions seeded once
            }
        }
        public bool SaveDeck(SavedDeck draft, out string error)
        {
            var validation = Rules.Validate(draft, Owned);
            if (!validation.IsValid) { error = string.Join("\n", validation.Errors); return false; }
            if (string.IsNullOrWhiteSpace(draft.name) || string.IsNullOrWhiteSpace(draft.deckId)) { error = "Deck needs a name and identity."; return false; }
            return Transact(p =>
            {
                p.savedDecks.RemoveAll(d => d.deckId == draft.deckId);
                p.savedDecks.Add(CollectionProfile.CopyDeck(draft));
            }, out error);
        }
        public bool DeleteDeck(string id, out string error) => Transact(p =>
        { p.savedDecks.RemoveAll(d => d.deckId == id); p.selectedDeckByFaction.RemoveAll(s => s.deckId == id); }, out error);
        public bool SelectDeck(SavedDeck deck, out string error)
        {
            if (deck == null) { error = "Select a saved deck."; return false; }
            var saved = profile.savedDecks.Find(d => d.deckId == deck.deckId && d.faction == deck.faction);
            if (saved == null) { error = "Select a saved deck."; return false; }
            var check = Rules.Validate(saved, Owned);
            if (!check.IsValid) { error = string.Join("\n", check.Errors); return false; }
            return Transact(p => { p.selectedDeckByFaction.RemoveAll(s => s.faction == deck.faction); p.selectedDeckByFaction.Add(new DeckSelection { faction = deck.faction, deckId = deck.deckId }); }, out error);
        }
    }

}
