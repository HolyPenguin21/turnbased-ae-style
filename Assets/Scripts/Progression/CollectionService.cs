using System;
using System.Collections.Generic;
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

    public sealed partial class CollectionService
    {
        private readonly StarterDeckMigration starters;
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
            CardDefinition chosen = SelectCollectionReward();
            if (chosen == null) return true;
            if (!Transact(p => GrantCollectionCopy(p, chosen), out error)) return false;
            acquired = chosen;
            return true;
        }
        // Replay of a pre-event match snapshot must receive exactly the original hand card,
        // without permanently granting another copy. Receipts are never pruned while matches
        // can be reloaded; reaching the storage limit rejects a new claim safely.
        public bool TryGrantRandomCollectionCard(string matchId, string eventId, out CardDefinition acquired, out string error)
        {
            acquired = null;
            error = null;
            if (!CollectionProfileStore.ValidRewardIdentity(matchId) || !CollectionProfileStore.ValidRewardIdentity(eventId))
            { error = "Cannot save this event reward without a valid match and event identity."; return false; }
            var receipt = profile.eventRewards.Find(r => r.matchId == matchId && r.eventId == eventId);
            if (receipt != null)
            {
                if (string.IsNullOrEmpty(receipt.cardKey)) return true;
                acquired = Rules.Resolve(receipt.cardKey);
                if (acquired != null) return true;
                error = "The saved event reward card is unavailable. Restore its card catalog and try again.";
                return false;
            }
            if (profile.eventRewards.Count >= CollectionProfileStore.MaximumEventRewards)
            { error = "The collection cannot store any more event rewards."; return false; }
            CardDefinition chosen = SelectCollectionReward();
            if (!Transact(p =>
            {
                if (chosen != null) GrantCollectionCopy(p, chosen);
                p.eventRewards.Add(new EventRewardReceipt { matchId = matchId, eventId = eventId, cardKey = chosen?.authoredKey ?? string.Empty });
            }, out error)) return false;
            acquired = chosen;
            return true;
        }
        private CardDefinition SelectCollectionReward()
        {
            var candidates = DeckRules.PlayableFactions.SelectMany(Rules.Cards)
                .Where(c => !string.IsNullOrWhiteSpace(c.authoredKey) && Rules.Resolve(c.authoredKey) == c
                    && Owned(c.authoredKey) < DeckRules.CopyLimit(c))
                .GroupBy(c => c.authoredKey).Select(g => g.First()).ToList();
            return candidates.Count == 0 ? null : candidates[collectionRewardRandom.Next(candidates.Count)];
        }
        private static void GrantCollectionCopy(CollectionProfile target, CardDefinition card)
        {
            if (target.Owned(card.authoredKey) >= DeckRules.CopyLimit(card))
                throw new InvalidOperationException("Collection copy limit reached for this card.");
            var row = target.ownedCards.Find(e => e.cardKey == card.authoredKey);
            if (row == null) target.ownedCards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = 1 });
            else row.count++;
        }
        public CollectionService(DeckRules rules, CollectionProfileStore store, CollectionProfile initial)
        {
            Rules = rules;
            this.store = store;
            profile = initial.Copy();
            starters = new StarterDeckMigration(rules);
            ClampBlueprintCopies(profile);
        }
        // Profiles saved before the one-copy blueprint rule may hold several copies of equipment/mutators.
        private void ClampBlueprintCopies(CollectionProfile target)
        {
            if (Rules == null) return;
            void Clamp(IEnumerable<DeckCardEntry> rows)
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
        private bool Transact(Action<CollectionProfile> edit, out string error)
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
        public List<DeckSummary> DeckSummaries(Faction faction)
        {
            var list = new List<DeckSummary>();
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
        public List<SavedDeck> Decks(Faction faction)
            => profile.savedDecks.Where(d => d.faction == faction).Select(CollectionProfile.CopyDeck).ToList();
        public IReadOnlyList<string> StarterIssues => starters.Issues;
        public SavedDeck Starter(Faction faction) => starters.BuildDeck(faction);

        public CollectionProfile CreateStarterProfile()
        {
            var fresh = new CollectionProfile();
            starters.Apply(fresh, starters.BuildDecks(), validateNew: true);
            return fresh;
        }

        public bool InitializeStarters(out string error) =>
            MigrateStarters(validateNew: profile.starterDeckRevision == 0 && profile.savedDecks.Count == 0, out error);

        public bool EnsureStarterDecks(out string error) => MigrateStarters(validateNew: false, out error);

        private bool MigrateStarters(bool validateNew, out string error)
        {
            error = null;
            var authored = starters.BuildDecks();
            if (!starters.NeedsMigration(profile, authored)) return true;
            return Transact(candidate => starters.Apply(candidate, authored, validateNew), out error);
        }

#if UNITY_EDITOR
        public bool FillCollectionForEditor(out int raised, out string error)
        {
            int changed = 0;
            bool saved = Transact(p =>
            {
                foreach (var card in DeckRules.PlayableFactions.SelectMany(Rules.Cards).Distinct())
                {
                    int limit = DeckRules.CopyLimit(card);
                    if (limit <= 0 || p.Owned(card.authoredKey) >= limit) continue;
                    var owned = p.ownedCards.Find(entry => entry.cardKey == card.authoredKey);
                    if (owned == null) p.ownedCards.Add(new DeckCardEntry { cardKey = card.authoredKey, count = limit });
                    else owned.count = limit;
                    changed++;
                }
            }, out error);
            raised = saved ? changed : 0;
            return saved;
        }
#endif

        public SavedDeck StarterDeck(Faction faction)
        {
            var deck = profile.savedDecks.Find(d => d.faction == faction && d.isStarter);
            return deck == null ? null : CollectionProfile.CopyDeck(deck);
        }
        public bool IsStarter(string id) => profile.savedDecks.Any(d => d.deckId == id && d.isStarter);
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
