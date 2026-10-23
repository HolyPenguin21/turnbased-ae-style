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
        // Shared card order (collection, deck list, Research/Production modal): Buildings, Heroes, Units, Equipment, Mutators.
        public static int SectionOrder(CardDefinition card)
            => card == null ? 5 : card.cardType == CardType.Base || card.cardType == CardType.Facility ? 0
                : card.cardType == CardType.Hero ? 1 : card.cardType == CardType.Unit ? 2
                : Category(card) == DeckCategory.Equipment ? 3 : 4;
        // Equipment and mutators are ordered by production Challenge difficulty (CardDefinition.fate), everything else by AP;
        // the total ResourceCost breaks ties at both levels.
        public static IOrderedEnumerable<T> OrderCards<T>(IEnumerable<T> rows, Func<T, CardDefinition> resolve)
            => rows.OrderBy(row => SectionOrder(resolve(row)))
                .ThenBy(row => SortsByChallenge(resolve(row)) ? resolve(row).fate : resolve(row)?.apCost ?? 0)
                .ThenBy(row => TotalResourceCost(resolve(row)))
                .ThenBy(row => resolve(row)?.deckPointCost ?? 0)
                .ThenBy(row => resolve(row)?.displayName ?? "", StringComparer.Ordinal);
        private static bool SortsByChallenge(CardDefinition card) => card != null && card.cardType == CardType.Equipment;
        private static int TotalResourceCost(CardDefinition card)
        {
            var cost = card?.resourceCost;
            return cost == null ? 0 : cost.human + cost.energy + cost.materials + cost.tech;
        }
        // Equipment and mutators are blueprints: one owned copy and one deck slot unlock unlimited production in a match.
        public static int CopyLimit(CardDefinition card)
        {
            if (card == null) return 0;
            var category = Category(card);
            return category == DeckCategory.Equipment || category == DeckCategory.Mutator || card.cardType == CardType.Hero
                ? Math.Min(card.deckCopyLimit, 1) : card.deckCopyLimit;
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
            // Own catalog, the shared Neutral catalog (opt out per card with deckBuilderExcluded),
            // plus explicit cross-catalog loans from the authored starting source.
            return Starting?.GetCatalog(faction)?.cards.Contains(card) == true
                || Starting?.GetCatalog(Faction.Neutral)?.cards.Contains(card) == true
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
            if (!PlayableFactions.Contains(deck.faction)) result.Errors.Add("Choose a playable faction.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var category in new[] { DeckCategory.Main, DeckCategory.Equipment, DeckCategory.Mutator })
            {
                var entries = Entries(deck, category);
                if (entries == null) { result.Errors.Add("Deck data is incomplete."); continue; }
                foreach (var row in entries)
                {
                    if (row == null || string.IsNullOrWhiteSpace(row.cardKey)) { result.Errors.Add("Deck contains an unreadable card."); continue; }
                    if (!keys.Add(row.cardKey)) result.Errors.Add("Duplicate card in deck: " + (Resolve(row.cardKey)?.displayName ?? "unavailable card"));
                    var card = Resolve(row.cardKey);
                    // Kept in the saved deck so the row returns if the card does; ignored in play (see MatchLoadout).
                    if (card == null) { result.Warnings.Add("An unavailable card was skipped."); continue; }
                    if (Category(card) != category || !Permitted(card, deck.faction)) result.Errors.Add(card.displayName + " is not allowed in this deck.");
                    if (card.deckPointCost < 0 || card.deckCopyLimit < 0) result.Errors.Add("Card data is invalid: " + card.displayName);
                    if (row.count < 0 || row.count > CopyLimit(card) || row.count > owned(row.cardKey))
                        result.Errors.Add(row.count < 0 ? $"{card.displayName}: copy count cannot be negative."
                            : row.count > CopyLimit(card) ? $"Maximum {CopyLimit(card)} copies of {card.displayName} per deck."
                            : $"You own only {owned(row.cardKey)} copies of {card.displayName}.");
                    result.Points += (long)card.deckPointCost * row.count;
                }
            }
            if (result.Points > MaximumPoints) result.Errors.Add("Deck exceeds 100 points.");
            int heroCards = deck.mainCards?.Where(e => e != null && e.count > 0 && Resolve(e.cardKey)?.cardType == CardType.Hero).Sum(e => e.count) ?? 0;
            if (heroCards == 0)
                result.Warnings.Add("No heroes: army command and facility use will be limited.");
            if (deck.mainCards?.Any(e => e != null && e.count > 0 && Resolve(e.cardKey)?.cardType == CardType.Base) != true)
                result.Warnings.Add("No base cards: expansion may be limited.");
            return result;
        }
        // True when one more copy of the card fits: owned copies, copy limit and the deck point cap (via TryChange on a copy).
        public bool CanAdd(SavedDeck draft, string key, Func<string, int> owned)
            => TryChange(CollectionProfile.CopyDeck(draft), key, 1, owned, out _);
        public bool TryChange(SavedDeck draft, string key, int delta, Func<string, int> owned, out string reason)
        {
            reason = null;
            var card = Resolve(key);
            if (card == null || Category(card) == DeckCategory.System) { reason = "This card cannot be added to a deck."; return false; }
            var candidate = CollectionProfile.CopyDeck(draft);
            var rows = Entries(candidate, Category(card));
            var row = rows.Find(e => e.cardKey == key);
            int count = (row?.count ?? 0) + delta;
            if (count < 0) { reason = "No copies of this card remain in the deck."; return false; }
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
