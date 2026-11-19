using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Players;

namespace Game.Progression
{
    // Match rewards use the same save-before-publish boundary as decks and event receipts.
    public sealed class RewardService
    {
        private readonly CollectionService collection;

        public RewardService(CollectionService collection) => this.collection = collection;

        public bool Record(ParticipantResult result, out string error) =>
            collection.RecordMatchReward(result, out error);

        public List<string> AvailableOffers(PendingReward reward) => collection.AvailableMatchOffers(reward);

        public bool Claim(string matchId, IEnumerable<string> selected, out string error) =>
            collection.ClaimMatchReward(matchId, selected, out error);

        public bool Dismiss(string matchId, out string error) => collection.DismissMatchReward(matchId, out error);

        internal static int RequiredSelectionCount(int available) => Math.Min(2, available);

        internal PendingReward FindPending(string matchId) => collection.FindPendingMatchReward(matchId);
        internal PendingReward FirstPending() => collection.FirstPendingMatchReward();
        internal bool HasDismissed(string matchId) => collection.HasDismissedMatchReward(matchId);
    }

    public sealed partial class CollectionService
    {
        private readonly Random matchRewardRandom = new Random();

        internal PendingReward FindPendingMatchReward(string matchId) =>
            CollectionProfile.CopyReward(profile.pendingRewards.Find(reward => reward.matchId == matchId));

        internal PendingReward FirstPendingMatchReward() =>
            CollectionProfile.CopyReward(profile.pendingRewards.FirstOrDefault());

        internal bool HasDismissedMatchReward(string matchId) => profile.claimedMatchIds.Contains(matchId)
            && !profile.pendingRewards.Exists(reward => reward.matchId == matchId);

        internal bool RecordMatchReward(ParticipantResult result, out string error)
        {
            error = null;
            if (result.Player == null || !result.Player.IsHuman || result.Outcome == MatchOutcome.Draw || string.IsNullOrWhiteSpace(result.MatchId)) return true;
            // Replaying an earlier ordinary save keeps the offer already issued for that MatchId.
            if (profile.claimedMatchIds.Contains(result.MatchId) || profile.pendingRewards.Any(reward => reward.matchId == result.MatchId))
                return true;
            var keys = Rules.Cards(result.Faction)
                .Where(c => CanGrant(c, result.Faction, profile)).Select(c => c.authoredKey).Distinct().ToList();
            // Uniform sampling without replacement; no weighting by count or rarity.
            for (int i = keys.Count - 1; i > 0; i--) { int j = matchRewardRandom.Next(i + 1); var k = keys[i]; keys[i] = keys[j]; keys[j] = k; }
            var pending = new PendingReward { matchId = result.MatchId, faction = result.Faction, outcome = result.Outcome,
                offeredKeys = keys.Take(result.Outcome == MatchOutcome.Victory ? 5 : 1).ToList() };
            // A defeat receipt remains pending for display, but its grant is committed once.
            return Transact(p =>
            {
                p.pendingRewards.Add(pending);
                if (result.Outcome == MatchOutcome.Defeat) ApplyMatchGrant(p, pending, pending.offeredKeys);
            }, out error);
        }
        // Keep the original persisted offer identities; updates never substitute random cards.
        internal List<string> AvailableMatchOffers(PendingReward reward)
            => AvailableOffers(reward, profile);
        private List<string> AvailableOffers(PendingReward reward, CollectionProfile profile)
            => reward.offeredKeys.Where(key => CanGrant(Rules.Resolve(key), reward.faction, profile)).ToList();
        private bool CanGrant(CardDefinition card, Faction faction, CollectionProfile profile)
            => card != null && Rules.Permitted(card, faction) && profile.Owned(card.authoredKey) < DeckRules.CopyLimit(card);
        internal bool ClaimMatchReward(string matchId, IEnumerable<string> selected, out string error)
        {
            error = null;
            if (profile.claimedMatchIds.Contains(matchId)) return true;
            var keys = selected?.ToList() ?? new List<string>();
            return Transact(p =>
            {
                var reward = p.pendingRewards.Find(r => r.matchId == matchId);
                if (reward == null) throw new InvalidOperationException("This reward is no longer available.");
                var available = AvailableOffers(reward, p);
                int required = RewardService.RequiredSelectionCount(available.Count);
                if (keys.Count != required || keys.Distinct().Count() != keys.Count || keys.Any(k => !available.Contains(k)))
                    throw new InvalidOperationException($"Choose {required} different reward cards.");
                ApplyMatchGrant(p, reward, keys);
            }, out error);
        }
        private void ApplyMatchGrant(CollectionProfile p, PendingReward reward, List<string> keys)
        {
            foreach (string key in keys)
            {
                var card = Rules.Resolve(key);
                if (!CanGrant(card, reward.faction, p))
                    throw new InvalidOperationException("This reward card is no longer available.");
                GrantCollectionCopy(p, card);
            }
            reward.acquiredKeys = new List<string>(keys);
            reward.claimed = true;
            p.claimedMatchIds.Add(reward.matchId);
        }

        internal bool DismissMatchReward(string matchId, out string error)
        {
            error = null;
            var reward = profile.pendingRewards.Find(entry => entry.matchId == matchId);
            if (reward == null) return true;
            if (!reward.claimed)
            {
                error = "Confirm the reward first.";
                return false;
            }
            return Transact(p => p.pendingRewards.RemoveAll(entry => entry.matchId == matchId), out error);
        }
    }
}
