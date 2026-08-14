using System.Collections.Generic;
using System.Linq;
using Game.Players;
using UnityEngine;

namespace Game.Cards
{
    // One card+count row in a StartingDeck. cardKey identifies the card across ALL of
    // StartingDeckCatalog.catalogs (a card's own id is only unique within its own catalog, see
    // FactionCardCatalog) as "<catalog.displayName>/<card.displayName>" — drawn via a dropdown
    // (DeckCardEntryDrawer, Assets/Editor) the same way CardDefinition.grantedAbilities gets one
    // from [AbilityTag], not hand-typed.
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

        public FactionCardCatalog GetCatalog(Faction faction) =>
            catalogs.FirstOrDefault(c => c != null && c.faction == faction);

        public StartingDeck GetDeck(Faction faction) =>
            decks.FirstOrDefault(d => d != null && d.faction == faction);

        // Scans `catalogs` for the card named by cardKey ("<catalog.displayName>/<card.
        // displayName>") — null if the catalog or the card inside it can no longer be found (a
        // stale key left over from a rename/removal), same "just skip it" fallback the deck pool
        // builder below relies on.
        public CardDefinition ResolveCard(string cardKey)
        {
            if (string.IsNullOrEmpty(cardKey))
                return null;

            foreach (FactionCardCatalog catalog in catalogs)
            {
                if (catalog == null)
                    continue;
                string prefix = catalog.displayName + "/";
                if (!cardKey.StartsWith(prefix))
                    continue;
                string cardName = cardKey.Substring(prefix.Length);
                CardDefinition match = catalog.cards.FirstOrDefault(c => c != null && c.displayName == cardName);
                if (match != null)
                    return match;
            }
            return null;
        }

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
                CardDefinition card = ResolveCard(entry?.cardKey);
                if (card == null)
                    continue;
                for (int i = 0; i < entry.count; i++)
                    pool.Add(card);
            }
            return pool;
        }
    }
}
