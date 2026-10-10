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
    [Serializable] public sealed class CollectionProfile
    {
        public int schemaVersion = 1;
        public string profileId = Guid.NewGuid().ToString("N");
        public List<DeckCardEntry> ownedCards = new List<DeckCardEntry>();
        public List<SavedDeck> savedDecks = new List<SavedDeck>();
        public List<DeckSelection> selectedDeckByFaction = new List<DeckSelection>();
        public List<PendingReward> pendingRewards = new List<PendingReward>();
        public List<string> claimedMatchIds = new List<string>();

        public int Owned(string key) => ownedCards.Find(e => e.cardKey == key)?.count ?? 0;
        public CollectionProfile Copy() => JsonUtility.FromJson<CollectionProfile>(JsonUtility.ToJson(this));
        public static SavedDeck CopyDeck(SavedDeck deck) => JsonUtility.FromJson<SavedDeck>(JsonUtility.ToJson(deck));
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
