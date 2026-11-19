using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Players;

namespace Game.Progression
{
    // Authored starter changes have their own revision, independent of the profile schema.
    internal sealed class StarterDeckMigration
    {
        private const int CurrentRevision = 4;
        private readonly DeckRules rules;
        private readonly List<string> issues = new List<string>();

        internal StarterDeckMigration(DeckRules rules)
        {
            this.rules = rules;
            Issues = issues.AsReadOnly();
        }

        internal IReadOnlyList<string> Issues { get; }

        internal List<SavedDeck> BuildDecks() => DeckRules.PlayableFactions.Select(BuildDeck).ToList();

        internal SavedDeck BuildDeck(Faction faction)
        {
            var source = rules.Starting.GetDeck(faction);
            var deck = new SavedDeck { isStarter = true, deckId = Guid.NewGuid().ToString("N"),
                name = source?.deckName ?? "Starter", faction = faction };
            if (source?.cards == null) return deck;

            foreach (var entry in source.cards.Where(entry => entry != null && entry.count > 0))
            {
                var card = rules.Starting.ResolveCard(entry.cardKey);
                if (card == null || !rules.Permitted(card, faction))
                {
                    string issue = $"Starting deck '{deck.name}': card '{entry.cardKey}' "
                        + (card == null ? "is not in any card catalog" : "is not allowed for " + faction) + " and was skipped.";
                    if (!issues.Contains(issue))
                    {
                        issues.Add(issue);
                        UnityEngine.Debug.LogWarning(issue, rules.Starting);
                    }
                    continue;
                }
                DeckRules.Entries(deck, DeckRules.Category(card))
                    .Add(new DeckCardEntry { cardKey = card.authoredKey, count = entry.count });
            }
            return deck;
        }

        internal bool NeedsMigration(CollectionProfile profile, IReadOnlyList<SavedDeck> authored) =>
            profile.starterDeckRevision < CurrentRevision
            || authored.Any(starter => !profile.savedDecks.Exists(deck => deck.faction == starter.faction && deck.isStarter)
                || InitialCards(starter).Any(entry => profile.Owned(entry.cardKey) < entry.count));

        internal void Apply(CollectionProfile target, IReadOnlyList<SavedDeck> authored, bool validateNew)
        {
            bool refreshComposition = target.starterDeckRevision < CurrentRevision;
            foreach (var starter in authored)
            {
                GrantInitialCards(target, InitialCards(starter));
                if (validateNew)
                {
                    var validation = rules.Validate(starter, target.Owned);
                    if (!validation.IsValid) throw new InvalidOperationException(string.Join("\n", validation.Errors));
                }
                var selection = target.selectedDeckByFaction.Find(entry => entry.faction == starter.faction);
                var saved = target.savedDecks.Find(deck => deck.faction == starter.faction && deck.isStarter)
                    ?? target.savedDecks.Where(deck => IsLegacyStarter(deck, starter))
                        .OrderByDescending(deck => deck.deckId == selection?.deckId).FirstOrDefault();
                if (saved == null)
                {
                    saved = starter;
                    target.savedDecks.Add(saved);
                }
                else if (refreshComposition || !saved.isStarter)
                {
                    saved.isStarter = true;
                    saved.mainCards = starter.mainCards;
                    saved.equipment = starter.equipment;
                    saved.mutators = starter.mutators;
                }

                if (selection == null)
                    target.selectedDeckByFaction.Add(new DeckSelection { faction = starter.faction, deckId = saved.deckId });
                else if (!target.savedDecks.Exists(deck => deck.faction == starter.faction && deck.deckId == selection.deckId))
                    selection.deckId = saved.deckId;
            }
            target.starterDeckRevision = Math.Max(target.starterDeckRevision, CurrentRevision);
        }

        private static bool IsLegacyStarter(SavedDeck saved, SavedDeck authored) =>
            saved.faction == authored.faction && saved.name == authored.name && SameEntries(saved.mainCards, authored.mainCards)
            // Early defaults had no blueprints. A deck with a different blueprint loadout is a user deck.
            && ((saved.equipment.Count == 0 && saved.mutators.Count == 0)
                || (SameEntries(saved.equipment, authored.equipment) && SameEntries(saved.mutators, authored.mutators)));

        private static bool SameEntries(List<DeckCardEntry> saved, List<DeckCardEntry> authored) =>
            saved.Select(entry => (entry.cardKey, entry.count)).OrderBy(entry => entry.cardKey, StringComparer.Ordinal).ThenBy(entry => entry.count)
                .SequenceEqual(authored.Select(entry => (entry.cardKey, entry.count)).OrderBy(entry => entry.cardKey, StringComparer.Ordinal).ThenBy(entry => entry.count));

        // All playable buildings are initially owned, even when absent from the starter loadout.
        private IEnumerable<DeckCardEntry> InitialCards(SavedDeck starter) =>
            starter.mainCards.Concat(starter.equipment).Concat(starter.mutators)
                .Concat(rules.Cards(starter.faction)
                    .Where(card => (card.cardType == CardType.Base || card.cardType == CardType.Facility)
                        && DeckRules.CopyLimit(card) > 0 && !string.IsNullOrEmpty(card.authoredKey))
                    .Select(card => new DeckCardEntry { cardKey = card.authoredKey, count = 1 }));

        private static void GrantInitialCards(CollectionProfile target, IEnumerable<DeckCardEntry> rows)
        {
            foreach (var entry in rows)
            {
                var owned = target.ownedCards.Find(row => row.cardKey == entry.cardKey);
                if (owned == null) target.ownedCards.Add(new DeckCardEntry { cardKey = entry.cardKey, count = entry.count });
                else owned.count = Math.Max(owned.count, entry.count);
            }
        }
    }
}
