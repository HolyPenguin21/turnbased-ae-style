using System;
using System.Linq;
using Game.Cards;
using Game.Players;

namespace Game.Progression
{
    public readonly struct DeckSummary
    {
        public readonly string DeckId, Name;
        public readonly bool IsStarter;
        public DeckSummary(string deckId, string name, bool isStarter) { DeckId = deckId; Name = name; IsStarter = isStarter; }
    }

    public sealed class CollectionService
    {
        private const int CurrentStarterDeckRevision = 2;
        public DeckRules Rules { get; }
        private readonly CollectionProfileStore store;
        private CollectionProfile profile;
        public event Action Changed;
        public CollectionProfile Snapshot => profile.Copy();
        public int Owned(string key) => profile.Owned(key);
        private readonly Random collectionRewardRandom = new Random();

        // Library rewards share collection eligibility/copy limits with match rewards, but draw
        // uniformly across ALL playable factions (including shared Neutral cards once per key).
        // This transaction never creates/claims a match receipt or edits any saved deck.
        public bool TryGrantRandomCollectionCard(out CardDefinition acquired, out string error)
        {
            acquired = null;
            error = null;
            var candidates = DeckRules.PlayableFactions.SelectMany(Rules.Cards)
                .Where(c => !string.IsNullOrWhiteSpace(c.authoredKey) && Rules.Resolve(c.authoredKey) == c
                    && Owned(c.authoredKey) < DeckRules.CopyLimit(c))
                .GroupBy(c => c.authoredKey).Select(g => g.First()).ToList();
            if (candidates.Count == 0) return true;
            CardDefinition chosen = candidates[collectionRewardRandom.Next(candidates.Count)];
            if (!Transact(p =>
            {
                if (p.Owned(chosen.authoredKey) >= DeckRules.CopyLimit(chosen))
                    throw new InvalidOperationException("Collection copy limit reached for this card.");
                var row = p.ownedCards.Find(e => e.cardKey == chosen.authoredKey);
                if (row == null) p.ownedCards.Add(new DeckCardEntry { cardKey = chosen.authoredKey, count = 1 });
                else row.count++;
            }, out error)) return false;
            acquired = chosen;
            return true;
        }
        public CollectionService(DeckRules rules, CollectionProfileStore store, CollectionProfile initial)
        { Rules = rules; this.store = store; profile = initial.Copy(); ClampBlueprintCopies(profile); }
        // Profiles saved before the one-copy blueprint rule may hold several copies of equipment/mutators.
        private void ClampBlueprintCopies(CollectionProfile target)
        {
            if (Rules == null) return;
            void Clamp(System.Collections.Generic.IEnumerable<DeckCardEntry> rows)
            {
                foreach (var e in rows)
                {
                    var card = Rules.Resolve(e.cardKey);
                    int limit = DeckRules.CopyLimit(card);
                    bool capped = card != null && (DeckRules.Category(card) != DeckCategory.Main || card.cardType == CardType.Hero);
                    if (limit > 0 && capped && e.count > limit) e.count = limit;
                }
            }
            Clamp(target.ownedCards);
            foreach (var deck in target.savedDecks) { Clamp(deck.mainCards); Clamp(deck.equipment); Clamp(deck.mutators); }
        }
        public bool Transact(Action<CollectionProfile> edit, out string error)
        {
            error = null;
            try
            {
                var next = profile.Copy(); edit(next); store.Save(next); profile = next;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning(ex);
                error = ex is InvalidOperationException ? ex.Message : "Could not save your collection. Try again.";
                return false;
            }
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
        // Lightweight list for pickers: avoids copying the whole profile just to read deck names.
        public System.Collections.Generic.List<DeckSummary> DeckSummaries(Faction faction)
        {
            var list = new System.Collections.Generic.List<DeckSummary>();
            foreach (var d in profile.savedDecks) if (d.faction == faction) list.Add(new DeckSummary(d.deckId, d.name, d.isStarter));
            return list;
        }
        public SavedDeck GetDeck(string id)
        {
            var deck = profile.savedDecks.Find(d => d.deckId == id);
            return deck == null ? null : CollectionProfile.CopyDeck(deck);
        }
        public SavedDeck GetDeck(string id, Faction faction)
        {
            var deck = profile.savedDecks.Find(d => d.deckId == id && d.faction == faction);
            return deck == null ? null : CollectionProfile.CopyDeck(deck);
        }
        // Copies of one faction's saved decks in profile order, without copying the whole profile.
        public System.Collections.Generic.List<SavedDeck> Decks(Faction faction)
            => profile.savedDecks.Where(d => d.faction == faction).Select(CollectionProfile.CopyDeck).ToList();
        // Authored data errors found while building starters; the row is skipped so the collection stays usable.
        private readonly System.Collections.Generic.List<string> starterIssues = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.IReadOnlyList<string> StarterIssues => starterIssues;
        public SavedDeck Starter(Faction faction)
        {
            var deck = new SavedDeck { isStarter = true, deckId = Guid.NewGuid().ToString("N"), name = Rules.Starting.GetDeck(faction)?.deckName ?? "Starter", faction = faction };
            var source = Rules.Starting.GetDeck(faction);
            if (source?.cards != null)
                foreach (var e in source.cards.Where(e => e != null && e.count > 0))
                {
                    var card = Rules.Starting.ResolveCard(e.cardKey);
                    if (card == null || !Rules.Permitted(card, faction))
                    {
                        string issue = $"Starting deck '{deck.name}': card '{e.cardKey}' "
                            + (card == null ? "is not in any card catalog" : "is not allowed for " + faction) + " and was skipped.";
                        if (!starterIssues.Contains(issue)) { starterIssues.Add(issue); UnityEngine.Debug.LogWarning(issue, Rules.Starting); }
                        continue;
                    }
                    DeckRules.Entries(deck, DeckRules.Category(card)).Add(new DeckCardEntry { cardKey = card.authoredKey, count = e.count });
                }
            return deck;
        }
        public void InitializeStarters(CollectionProfile target)
        {
            foreach (var faction in DeckRules.PlayableFactions)
            {
                var starter = Starter(faction);
                GrantInitialCards(target, InitialCards(starter));
                var validation = Rules.Validate(starter, target.Owned);
                if (!validation.IsValid) throw new InvalidOperationException(string.Join("\n", validation.Errors));
                target.savedDecks.Add(starter);
                target.selectedDeckByFaction.Add(new DeckSelection { faction = faction, deckId = starter.deckId });
            }
            target.starterDeckRevision = CurrentStarterDeckRevision;
        }
        public SavedDeck StarterDeck(Faction faction)
        {
            var deck = profile.savedDecks.Find(d => d.faction == faction && d.isStarter);
            return deck == null ? null : CollectionProfile.CopyDeck(deck);
        }
        public bool IsStarter(string id) => profile.savedDecks.Any(d => d.deckId == id && d.isStarter);
        // Refresh protected starters once per balance revision, retaining their identities and names.
        // Unmarked legacy defaults still match by name AND composition; custom decks stay separate.
        public bool EnsureStarterDecks(out string error)
        {
            var starters = DeckRules.PlayableFactions.Select(Starter).ToList();
            bool refreshComposition = profile.starterDeckRevision < CurrentStarterDeckRevision;
            error = null;
            if (!refreshComposition && starters.All(s => profile.savedDecks.Any(d => d.faction == s.faction && d.isStarter)
                && InitialCards(s).All(e => Owned(e.cardKey) >= e.count))) return true;
            return Transact(p =>
            {
                foreach (var starter in starters)
                {
                    GrantInitialCards(p, InitialCards(starter));
                    var saved = p.savedDecks.Find(d => d.faction == starter.faction && d.isStarter)
                        ?? p.savedDecks.Where(d => d.faction == starter.faction && d.name == starter.name
                            && d.mainCards.Count == starter.mainCards.Count
                            && d.mainCards.All(e => starter.mainCards.Any(s => s.cardKey == e.cardKey && s.count == e.count)))
                            .OrderByDescending(d => p.selectedDeckByFaction.Any(s => s.faction == starter.faction && s.deckId == d.deckId)).FirstOrDefault();
                    if (saved == null) { saved = starter; p.savedDecks.Add(saved); }
                    else if (refreshComposition || !saved.isStarter)
                    {
                        saved.isStarter = true;
                        saved.mainCards = starter.mainCards;
                        saved.equipment = starter.equipment;
                        saved.mutators = starter.mutators;
                    }
                    var selection = p.selectedDeckByFaction.Find(s => s.faction == starter.faction);
                    if (selection == null) p.selectedDeckByFaction.Add(new DeckSelection { faction = starter.faction, deckId = saved.deckId });
                    else if (!p.savedDecks.Any(d => d.faction == starter.faction && d.deckId == selection.deckId)) selection.deckId = saved.deckId;
                }
                p.starterDeckRevision = Math.Max(p.starterDeckRevision, CurrentStarterDeckRevision);
            }, out error);
        }
        // All playable buildings are owned initially, even when absent from the starter loadout.
        // Both new profiles and upgrades use the same grant; rewards and existing copies survive.
        private System.Collections.Generic.IEnumerable<DeckCardEntry> InitialCards(SavedDeck starter)
            => starter.mainCards.Concat(starter.equipment).Concat(starter.mutators)
                .Concat(Rules.Cards(starter.faction)
                    .Where(c => (c.cardType == CardType.Base || c.cardType == CardType.Facility)
                        && DeckRules.CopyLimit(c) > 0 && !string.IsNullOrEmpty(c.authoredKey))
                    .Select(c => new DeckCardEntry { cardKey = c.authoredKey, count = 1 }));
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
            if (string.IsNullOrWhiteSpace(draft.name) || string.IsNullOrWhiteSpace(draft.deckId))
            {
                error = string.IsNullOrWhiteSpace(draft.name) ? "Enter a deck name." : "Cannot save this deck. Create a new deck.";
                return false;
            }
            bool starter = IsStarter(draft.deckId) || draft.isStarter;
            if (starter && profile.savedDecks.Any(d => d.isStarter && d.faction == draft.faction && d.deckId != draft.deckId))
            { error = "This faction already has a starter deck."; return false; }
            return Transact(p =>
            {
                p.savedDecks.RemoveAll(d => d.deckId == draft.deckId);
                var saved = CollectionProfile.CopyDeck(draft);
                saved.isStarter = starter;
                p.savedDecks.Add(saved);
            }, out error);
        }
        public bool DeleteDeck(string id, out string error)
        {
            if (IsStarter(id)) { error = "Starter decks cannot be deleted."; return false; }
            return Transact(p =>
            { p.savedDecks.RemoveAll(d => d.deckId == id); p.selectedDeckByFaction.RemoveAll(s => s.deckId == id); }, out error);
        }
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
