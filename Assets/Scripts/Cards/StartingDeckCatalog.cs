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
        // AI decks only: blueprints (equipment / mutators) the AI may produce without limit.
        // Never drawn into the hand; they gate what the AI's Production/Research may offer.
        public List<DeckCardEntry> equipment = new List<DeckCardEntry>();
        public List<DeckCardEntry> mutators = new List<DeckCardEntry>();
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

        // Decks AI players draw from. Every AI player picks one at random among its faction's
        // decks once, when the match is prepared (PickAiDeckName), so several can be authored.
        public List<StartingDeck> aiDecks = new List<StartingDeck>();

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
        public List<CardDefinition> BuildDeckPool(Faction faction) => BuildPool(GetDeck(faction));

        public IEnumerable<StartingDeck> GetAiDecks(Faction faction) =>
            aiDecks.Where(d => d != null && d.faction == faction);

        // Random AI deck name for `faction`; null when none is authored (callers then fall back
        // to the faction's starting deck).
        public string PickAiDeckName(Faction faction)
        {
            List<StartingDeck> options = GetAiDecks(faction).ToList();
            return options.Count == 0 ? null : options[Random.Range(0, options.Count)].deckName;
        }

        public StartingDeck GetAiDeck(Faction faction, string deckName)
        {
            List<StartingDeck> options = GetAiDecks(faction).ToList();
            return options.FirstOrDefault(d => d.deckName == deckName)
                ?? (options.Count > 0 ? options[Random.Range(0, options.Count)] : null);
        }

        // The pool an AI player draws from: its chosen AI deck, else (no AI deck authored for the
        // faction) the faction's starting deck. Human players keep BuildDeckPool(faction).
        public List<CardDefinition> BuildAiDeckPool(Faction faction, string deckName) =>
            BuildPool(GetAiDeck(faction, deckName) ?? GetDeck(faction));

        // Keys of the AI deck's blueprints; null when the faction has no AI deck (no restriction).
        public HashSet<string> BuildAiBlueprintKeys(Faction faction, string deckName)
        {
            StartingDeck deck = GetAiDeck(faction, deckName);
            if (deck == null) return null;
            var keys = new HashSet<string>();
            foreach (DeckCardEntry e in (deck.equipment ?? new List<DeckCardEntry>()).Concat(deck.mutators ?? new List<DeckCardEntry>()))
                if (e != null && e.count > 0 && !string.IsNullOrEmpty(e.cardKey)) keys.Add(e.cardKey);
            return keys;
        }

        public List<CardDefinition> BuildDeckPool(PlayerSetupData player) =>
            player.IsHuman ? BuildDeckPool(player.Faction) : BuildAiDeckPool(player.Faction, player.AiDeckName);

        private List<CardDefinition> BuildPool(StartingDeck deck)
        {
            var pool = new List<CardDefinition>();
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

