using System;
using System.Collections.Generic;
using System.Linq;
using Game.Players;
using Game.Progression;

namespace Game.Cards
{
    public enum DeckCategory { Main, Equipment, Mutator, System }
    public sealed class DeckValidation
    {
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public long Points;
        public bool IsValid => Errors.Count == 0;
    }

    // Collection eligibility and composition rules only; no payment, draw, or match mutations.
    public sealed class DeckRules
    {
        public const int MaximumPoints = 100;
        public static readonly Faction[] PlayableFactions = { Faction.IronConcord, Faction.Ashen, Faction.Vessels };
        public readonly StartingDeckCatalog Starting;
        public readonly ResearchProductionCatalog Research;
        public DeckRules(StartingDeckCatalog starting, ResearchProductionCatalog research)
        { Starting = starting; Research = research; }

        public static DeckCategory Category(CardDefinition card)
        {
            if (card == null || card.deckBuilderExcluded || card.cardType == CardType.Tactic) return DeckCategory.System;
            if (card.cardType == CardType.Equipment)
                return card.attachmentSlot == AttachmentSlot.Mutator ? DeckCategory.Mutator
                    : card.attachmentSlot == AttachmentSlot.Equipment ? DeckCategory.Equipment : DeckCategory.System;
            return card.cardType == CardType.Hero || card.cardType == CardType.Unit || card.cardType == CardType.Base || card.cardType == CardType.Facility ? DeckCategory.Main : DeckCategory.System;
        }
        public CardDefinition Resolve(string key)
        {
            var card = Starting?.ResolveCard(key) ?? Research?.ResolveCard(key);
            return card != null && card.authoredKey == key ? card : null;
        }
        public bool Permitted(CardDefinition card, Faction faction)
        {
            if (!PlayableFactions.Contains(faction) || Category(card) == DeckCategory.System) return false;
            if (Category(card) != DeckCategory.Main)
                return Research != null && (Research.ResolveFor(ResearchProductionMode.Production, faction).Contains(card)
                    || Research.ResolveFor(ResearchProductionMode.Research, faction).Contains(card));
            // Own catalog plus explicit cross-catalog loans from the authored starting source.
            // Neutral alone never means that every neutral unit is playable.
            return Starting?.GetCatalog(faction)?.cards.Contains(card) == true
                || Starting?.GetDeck(faction)?.cards.Any(e => e != null && Starting.ResolveCard(e.cardKey) == card) == true;
        }
        public IEnumerable<CardDefinition> Cards(Faction faction)
        {
            return (Starting?.catalogs ?? new List<FactionCardCatalog>())
                .Concat(Research?.cardCatalogs ?? new List<FactionCardCatalog>())
                .Where(c => c != null).SelectMany(c => c.cards).Where(c => c != null).Distinct()
                .Where(c => Permitted(c, faction));
        }
        public static List<DeckCardEntry> Entries(SavedDeck deck, DeckCategory category)
            => category == DeckCategory.Main ? deck.mainCards : category == DeckCategory.Equipment ? deck.equipment : deck.mutators;
        public DeckValidation Validate(SavedDeck deck, Func<string, int> owned)
        {
            var result = new DeckValidation();
            if (deck == null) { result.Errors.Add("No deck selected."); return result; }
            if (!PlayableFactions.Contains(deck.faction)) result.Errors.Add("Invalid faction.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var category in new[] { DeckCategory.Main, DeckCategory.Equipment, DeckCategory.Mutator })
            {
                var entries = Entries(deck, category);
                if (entries == null) { result.Errors.Add("Missing deck category."); continue; }
                foreach (var row in entries)
                {
                    if (row == null || string.IsNullOrWhiteSpace(row.cardKey)) { result.Errors.Add("Missing card identity."); continue; }
                    if (!keys.Add(row.cardKey)) result.Errors.Add("Duplicate card row: " + row.cardKey);
                    var card = Resolve(row.cardKey);
                    if (card == null) { result.Errors.Add("Obsolete card reference: " + row.cardKey); continue; }
                    if (Category(card) != category || !Permitted(card, deck.faction)) result.Errors.Add("Forbidden card: " + card.displayName);
                    if (card.deckPointCost < 0 || card.deckCopyLimit < 0) result.Errors.Add("Invalid card metadata: " + row.cardKey);
                    if (row.count < 0 || row.count > card.deckCopyLimit || row.count > owned(row.cardKey))
                        result.Errors.Add("Invalid quantity: " + card.displayName);
                    result.Points += (long)card.deckPointCost * row.count;
                }
            }
            if (result.Points > MaximumPoints) result.Errors.Add("Deck exceeds 100 points.");
            if (deck.mainCards?.Any(e => e != null && e.count > 0 && Resolve(e.cardKey)?.cardType == CardType.Hero) != true)
                result.Warnings.Add("No heroes: this deck may not field armies or operate facilities.");
            if (deck.mainCards?.Any(e => e != null && e.count > 0 && Resolve(e.cardKey)?.cardType == CardType.Base) != true)
                result.Warnings.Add("No base cards: expansion may be limited.");
            return result;
        }
        public bool TryChange(SavedDeck draft, string key, int delta, Func<string, int> owned, out string reason)
        {
            reason = null;
            var card = Resolve(key);
            if (card == null || Category(card) == DeckCategory.System) { reason = "Unknown or system card."; return false; }
            var candidate = CollectionProfile.CopyDeck(draft);
            var rows = Entries(candidate, Category(card));
            var row = rows.Find(e => e.cardKey == key);
            int count = (row?.count ?? 0) + delta;
            if (count < 0) { reason = "Quantity cannot be negative."; return false; }
            if (row == null) { row = new DeckCardEntry { cardKey = key, count = 0 }; rows.Add(row); }
            row.count = count;
            if (count == 0) rows.Remove(row);
            var validation = Validate(candidate, owned);
            // Removing cards must remain possible when an update made the old deck invalid.
            if (delta > 0 && !validation.IsValid) { reason = string.Join("\n", validation.Errors); return false; }
            draft.mainCards = candidate.mainCards; draft.equipment = candidate.equipment; draft.mutators = candidate.mutators;
            return true;
        }
    }
}
