using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

namespace Game.Cards
{
    // A deck row references CardDefinition.authoredKey when available. Legacy
    // "<catalog.displayName>/<card.displayName>" references remain readable while existing
    // cards acquire stable identities; new authored references survive display-name changes.
    [System.Serializable]
    public class DeckCardEntry
    {
        public string cardKey;
        public int count = 1;
    }

    // One named deck a player of `faction` starts the game with — several copies of the same
    // card are several DeckCardEntry rows worth of count, not a separate "duplicates" concept.
    [System.Serializable]
    public class StartingDeck
    {
        public string deckName;
        public Faction faction;
        public List<DeckCardEntry> cards = new List<DeckCardEntry>();
    }

    // Every starting deck in the game, plus every FactionCardCatalog a deck's cards can be drawn
    // from (a faction's own catalog, Neutral, or — once it exists — another faction's, for a
    // card explicitly loaned across factions). A separate asset from FactionCardCatalog, same
    // reasoning as UnitAbilityCatalog living on its own (Assets/Cards/StartingDeckCatalog.asset,
    // right next to it): this is deck-building data, tuned by editing one asset, not per-card
    // design.
    [CreateAssetMenu(fileName = "StartingDeckCatalog", menuName = "Game/Starting Deck Catalog")]
    public class StartingDeckCatalog : ScriptableObject
    {
        public List<FactionCardCatalog> catalogs = new List<FactionCardCatalog>();
        public List<StartingDeck> decks = new List<StartingDeck>();

        // Data-driven initial shared blueprints; they never enter the ordinary draw pool.
        public List<StartingDeck> collectionBlueprints = new List<StartingDeck>();
        public IEnumerable<DeckCardEntry> GetCollectionBlueprints(Faction faction)
            => collectionBlueprints.Find(d => d.faction == faction)?.cards ?? new List<DeckCardEntry>();

        public FactionCardCatalog GetCatalog(Faction faction) =>
            catalogs.FirstOrDefault(c => c != null && c.faction == faction);

        public StartingDeck GetDeck(Faction faction) =>
            decks.FirstOrDefault(d => d != null && d.faction == faction);

        // Reuse the catalog's existing card lookup. Prefer stable identity across catalogs,
        // then accept the legacy qualified display name for decks that have not migrated yet.
        public CardDefinition ResolveCard(string cardKey)
            => FactionCardCatalog.ResolveAcross(catalogs, cardKey, true, this);

        // Strict expansion for a validated custom loadout. Legacy callers retain their safe skip.
        public bool TryBuildDeckPool(IEnumerable<DeckCardEntry> entries,
            out List<CardDefinition> pool, out string error)
        {
            pool = new List<CardDefinition>(); error = null;
            foreach (var entry in entries)
            {
                var card = entry == null ? null : ResolveCard(entry.cardKey);
                if (entry == null || entry.count < 0 || card == null)
                { pool = null; error = "Cannot resolve the complete deck."; return false; }
                AppendCopies(pool, card, entry.count);
            }
            return true;
        }
        private static void AppendCopies(List<CardDefinition> pool, CardDefinition card, int count)
        { for (int i = 0; i < count; i++) pool.Add(card); }

        // Expands `faction`'s deck (card+count rows) into the flat pool CardHandUI/AiHandData
        // draw from without replacement — what deckIndices used to be, just CardDefinitions
        // instead of catalog-relative ints, since a card can now come from any of `catalogs`.
        public List<CardDefinition> BuildDeckPool(Faction faction)
        {
            var pool = new List<CardDefinition>();
            StartingDeck deck = GetDeck(faction);
            if (deck?.cards == null)
                return pool;

            foreach (DeckCardEntry entry in deck.cards)
            {
                if (entry == null || entry.count <= 0)
                    continue;
                CardDefinition card = ResolveCard(entry.cardKey);
                if (card == null)
                {
                    // A missing facility silently removed from the deck makes an entire AI axis
                    // impossible to fulfil. Keep the safe skip, but make the data error explicit.
                    Debug.LogError($"StartingDeckCatalog '{name}', deck '{deck.deckName}': "
                        + $"unresolved cardKey '{entry.cardKey}' (count {entry.count}).", this);
                    continue;
                }
                AppendCopies(pool, card, entry.count);
            }
            return pool;
        }
    }
}

