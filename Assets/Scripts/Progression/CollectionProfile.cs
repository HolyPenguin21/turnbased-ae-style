using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Players;

namespace Game.Progression
{
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
        public CollectionProfile Copy()
        {
            var copy = (CollectionProfile)MemberwiseClone();
            copy.ownedCards = DeckCardEntry.CopyEntries(ownedCards);
            copy.savedDecks = savedDecks?.ConvertAll(CopyDeck);
            copy.selectedDeckByFaction = selectedDeckByFaction?.ConvertAll(selection => selection == null ? null :
                new DeckSelection { faction = selection.faction, deckId = selection.deckId });
            copy.pendingRewards = pendingRewards?.ConvertAll(CopyReward);
            copy.claimedMatchIds = claimedMatchIds == null ? null : new List<string>(claimedMatchIds);
            copy.eventRewards = eventRewards?.ConvertAll(receipt => receipt == null ? null :
                new EventRewardReceipt { matchId = receipt.matchId, eventId = receipt.eventId, cardKey = receipt.cardKey });
            return copy;
        }
        public static SavedDeck CopyDeck(SavedDeck deck) => deck?.Copy();
        internal static PendingReward CopyReward(PendingReward reward) => reward == null ? null : new PendingReward
        {
            matchId = reward.matchId,
            faction = reward.faction,
            outcome = reward.outcome,
            offeredKeys = reward.offeredKeys == null ? null : new List<string>(reward.offeredKeys),
            acquiredKeys = reward.acquiredKeys == null ? null : new List<string>(reward.acquiredKeys),
            claimed = reward.claimed,
        };
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
