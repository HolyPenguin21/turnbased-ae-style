using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;

namespace Game.Progression
{
    // Persistent reward transactions only. Outcome is supplied by the existing turn controller.
    public sealed class RewardService
    {
        private readonly CollectionService collection;
        private readonly Random random = new Random();
        public RewardService(CollectionService collection) { this.collection = collection; }
        public bool Record(ParticipantResult result, out string error)
        {
            error = null;
            if (result.Player == null || !result.Player.IsHuman || result.Outcome == MatchOutcome.Draw || string.IsNullOrWhiteSpace(result.MatchId)) return true;
            var current = collection.Snapshot;
            if (current.claimedMatchIds.Contains(result.MatchId) || current.pendingRewards.Any(r => r.matchId == result.MatchId)) return true;
            var keys = collection.Rules.Cards(result.Faction)
                .Where(c => c.deckCopyLimit > current.Owned(c.authoredKey)).Select(c => c.authoredKey).Distinct().ToList();
            // Uniform sampling without replacement; no weighting by count or rarity.
            for (int i = keys.Count - 1; i > 0; i--) { int j = random.Next(i + 1); var k = keys[i]; keys[i] = keys[j]; keys[j] = k; }
            var pending = new PendingReward { matchId = result.MatchId, faction = result.Faction, outcome = result.Outcome,
                offeredKeys = keys.Take(result.Outcome == MatchOutcome.Victory ? 5 : 1).ToList() };
            // A defeat receipt remains pending for display, but its grant is committed once.
            return collection.Transact(p =>
            {
                p.pendingRewards.Add(pending);
                if (result.Outcome == MatchOutcome.Defeat) Apply(p, pending, pending.offeredKeys);
            }, out error);
        }
        public bool Claim(string matchId, IEnumerable<string> selected, out string error)
        {
            var keys = selected?.ToList() ?? new List<string>();
            return collection.Transact(p =>
            {
                var reward = p.pendingRewards.Find(r => r.matchId == matchId);
                if (reward == null) throw new InvalidOperationException("Reward is not pending.");
                if (reward.claimed || p.claimedMatchIds.Contains(matchId)) return;
                int required = Math.Min(2, reward.offeredKeys.Count);
                if (keys.Count != required || keys.Distinct().Count() != keys.Count || keys.Any(k => !reward.offeredKeys.Contains(k)))
                    throw new InvalidOperationException("Select the required distinct offered cards.");
                Apply(p, reward, keys);
            }, out error);
        }
        private void Apply(CollectionProfile p, PendingReward reward, IEnumerable<string> keys)
        {
            var granted = keys.ToList();
            foreach (string key in granted)
            {
                var card = collection.Rules.Resolve(key);
                if (card == null || !collection.Rules.Permitted(card, reward.faction) || p.Owned(key) >= card.deckCopyLimit)
                    throw new InvalidOperationException("Reward is no longer eligible: " + key);
            }
            foreach (string key in granted)
            {
                var row = p.ownedCards.Find(e => e.cardKey == key);
                if (row == null) { row = new DeckCardEntry { cardKey = key, count = 0 }; p.ownedCards.Add(row); }
                row.count++;
            }
            reward.acquiredKeys = granted; reward.claimed = true; p.claimedMatchIds.Add(reward.matchId);
        }
        public bool Dismiss(string matchId, out string error) => collection.Transact(p =>
        {
            var reward = p.pendingRewards.Find(r => r.matchId == matchId);
            if (reward != null && !reward.claimed) throw new InvalidOperationException("Confirm the reward first.");
            p.pendingRewards.RemoveAll(r => r.matchId == matchId);
        }, out error);
    }
}
