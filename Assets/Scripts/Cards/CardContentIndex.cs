using System;
using System.Collections.Generic;
using System.Linq;

namespace Game.Cards
{
    // An authored identity index for an operation with a fixed content scope. The same
    // identity and ambiguity rules are used by live catalogs whose Inspector lists can change.
    internal sealed class CardContentIndex
    {
        private readonly Dictionary<string, Match> authored = new Dictionary<string, Match>(StringComparer.Ordinal);

        private struct Match
        {
            internal CardDefinition Card;
            internal bool Ambiguous;

            internal void Include(CardDefinition card)
            {
                if (Card == null) Card = card;
                else if (!ReferenceEquals(Card, card)) Ambiguous = true;
            }
        }

        internal CardContentIndex(IEnumerable<CardDefinition> cards)
        {
            foreach (var card in cards)
            {
                if (card == null || string.IsNullOrWhiteSpace(card.authoredKey)) continue;
                authored.TryGetValue(card.authoredKey, out var match);
                match.Include(card);
                authored[card.authoredKey] = match;
            }
        }

        internal CardDefinition Find(string key, out bool ambiguous)
        {
            var match = key != null && authored.TryGetValue(key, out var value) ? value : default;
            ambiguous = match.Ambiguous;
            return ambiguous ? null : match.Card;
        }

        internal static IEnumerable<CardDefinition> Definitions(IEnumerable<FactionCardCatalog> catalogs)
        {
            if (catalogs == null) yield break;
            foreach (var catalog in catalogs)
                if (catalog != null && catalog.cards != null)
                    foreach (var card in catalog.cards)
                        yield return card;
        }

        internal static CardDefinition ResolveSingle(FactionCardCatalog catalog, string key)
        {
            if (string.IsNullOrWhiteSpace(key) || catalog.cards == null) return null;
            return catalog.cards.FirstOrDefault(card => card != null && card.authoredKey == key)
                ?? catalog.cards.FirstOrDefault(card => card != null && card.displayName == key);
        }

        internal static CardDefinition Resolve(IEnumerable<FactionCardCatalog> catalogs, string key, bool allowLegacy,
            out bool ambiguous)
        {
            ambiguous = false;
            if (catalogs == null || string.IsNullOrWhiteSpace(key)) return null;
            var card = ResolveAuthored(Definitions(catalogs), key, out ambiguous);
            return ambiguous ? null : card ?? (allowLegacy ? FindQualified(catalogs, key) : null);
        }

        internal static CardDefinition ResolveAuthored(IEnumerable<CardDefinition> cards, string key, out bool ambiguous)
        {
            var match = new Match();
            foreach (var card in cards)
            {
                if (card == null || card.authoredKey != key) continue;
                match.Include(card);
                if (match.Ambiguous) break;
            }
            ambiguous = match.Ambiguous;
            return ambiguous ? null : match.Card;
        }

        internal CardDefinition Resolve(IEnumerable<FactionCardCatalog> catalogs, string key, bool allowLegacy)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var card = Find(key, out bool ambiguous);
            return ambiguous ? null : card ?? (allowLegacy ? FindQualified(catalogs, key) : null);
        }

        private static CardDefinition FindQualified(IEnumerable<FactionCardCatalog> catalogs, string key)
        {
            if (catalogs == null) return null;
            foreach (var catalog in catalogs)
            {
                if (catalog == null) continue;
                string prefix = catalog.displayName + "/";
                if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var card = ResolveSingle(catalog, key.Substring(prefix.Length));
                if (card != null) return card;
            }
            return null;
        }
    }
}
