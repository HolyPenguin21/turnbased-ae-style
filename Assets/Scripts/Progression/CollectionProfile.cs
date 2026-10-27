using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Players;
using UnityEngine;

namespace Game.Progression
{
    [Serializable] public sealed class SavedDeck
    {
        public string deckId;
        public bool isStarter;
        public string name;
        public Faction faction;
        public List<DeckCardEntry> mainCards = new List<DeckCardEntry>();
        public List<DeckCardEntry> equipment = new List<DeckCardEntry>();
        public List<DeckCardEntry> mutators = new List<DeckCardEntry>();
    }
    [Serializable] public sealed class DeckSelection
    {
        public Faction faction;
        public string deckId;
    }
    [Serializable] public sealed class PendingReward
    {
        public string matchId;
        public Faction faction;
        public MatchOutcome outcome;
        public List<string> offeredKeys = new List<string>();
        public List<string> acquiredKeys = new List<string>();
        public bool claimed;
    }
    // Permanent grant and its exact current-match result are committed in one profile write.
    // An empty cardKey records that no eligible collection card remained for this event.
    [Serializable] public sealed class EventRewardReceipt
    {
        public string matchId;
        public string eventId;
        public string cardKey;
    }
    [Serializable] public sealed class CollectionProfile
    {
        public int schemaVersion = 2;
        // Separate from the storage schema: authored starter balance updates apply once.
        public int starterDeckRevision;
        public string profileId = Guid.NewGuid().ToString("N");
        public List<DeckCardEntry> ownedCards = new List<DeckCardEntry>();
        public List<SavedDeck> savedDecks = new List<SavedDeck>();
        public List<DeckSelection> selectedDeckByFaction = new List<DeckSelection>();
        public List<PendingReward> pendingRewards = new List<PendingReward>();
        public List<string> claimedMatchIds = new List<string>();
        public List<EventRewardReceipt> eventRewards = new List<EventRewardReceipt>();

        public int Owned(string key) => ownedCards.Find(e => e.cardKey == key)?.count ?? 0;
        public CollectionProfile Copy() => JsonUtility.FromJson<CollectionProfile>(JsonUtility.ToJson(this));
        // Shared by profile editing and immutable match loadouts. Plain value copies avoid
        // coupling a runtime snapshot to native JSON serialization or a mutable profile list.
        public static SavedDeck CopyDeck(SavedDeck deck) => deck == null ? null : new SavedDeck
        {
            deckId = deck.deckId, isStarter = deck.isStarter, name = deck.name, faction = deck.faction,
            mainCards = CopyDeckEntries(deck.mainCards), equipment = CopyDeckEntries(deck.equipment),
            mutators = CopyDeckEntries(deck.mutators),
        };
        private static List<DeckCardEntry> CopyDeckEntries(List<DeckCardEntry> entries) => entries?.ConvertAll(
            entry => entry == null ? null : new DeckCardEntry {cardKey=entry.cardKey,count=entry.count});
    }
    public enum MatchOutcome { Victory, Defeat, Draw }
    public readonly struct ParticipantResult
    {
        public readonly string MatchId;
        public readonly PlayerSetupData Player;
        public readonly Faction Faction;
        public readonly MatchOutcome Outcome;
        public ParticipantResult(string matchId, PlayerSetupData player, MatchOutcome outcome)
        { MatchId = matchId; Player = player; Faction = player.Faction; Outcome = outcome; }
    }
}
