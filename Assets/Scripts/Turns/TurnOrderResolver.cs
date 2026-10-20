using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Map;
using Game.Players;

namespace Game.Turns
{
    public sealed class InitiativeRollRound
    {
        public readonly Dictionary<PlayerSetupData, TokenRollResult> Rolls;

        public InitiativeRollRound(Dictionary<PlayerSetupData, TokenRollResult> rolls)
        {
            Rolls = rolls;
        }
    }

    public sealed class TurnOrderResolution
    {
        public readonly List<PlayerSetupData> Order;
        public readonly Dictionary<PlayerSetupData, TokenRollResult> FinalRolls;
        public readonly List<InitiativeRollRound> Rounds;

        public TurnOrderResolution(List<PlayerSetupData> order,
            Dictionary<PlayerSetupData, TokenRollResult> finalRolls,
            List<InitiativeRollRound> rounds)
        {
            Order = order;
            FinalRolls = finalRolls;
            Rounds = rounds;
        }
    }

    // Decides player turn order at the start of every turn: everyone rolls a tokens pool (see
    // TokenRollResult), highest score goes first. Ties are broken by rerolling all of that
    // player's tokens again — but only for the tied players, repeated (recursively, since a
    // reroll can itself produce a smaller tie) until every slot is resolved.
    //
    // Pool size is InitiativeRules.BaseTokens plus whatever that player bought this turn (see
    // PlayerRoot.BonusInitiativeTokens) — the base count is the shared gameplay contract now, not
    // a local const, so the human buy UI, the AI planner and this roll can't disagree on it.
    public static class TurnOrderResolver
    {
        // Exposed for TokenRowUI, so the popup can size each player's tokens slots correctly
        // before the roll happens (not just once ShowRoll gives back the actual results).
        public static int TokenCountFor(PlayerSetupData player)
        {
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            return InitiativeRules.BaseTokens + (root != null ? root.BonusInitiativeTokens : 0);
        }

        // finalRolls comes back with, for every player, the roll that actually decided their
        // slot — their first roll if they were never tied, or whichever reroll broke the tie
        // they were in.
        public static List<PlayerSetupData> Resolve(List<PlayerSetupData> players, out Dictionary<PlayerSetupData, TokenRollResult> finalRolls)
        {
            TurnOrderResolution resolution = Resolve(players);
            finalRolls = resolution.FinalRolls;
            return resolution.Order;
        }

        public static TurnOrderResolution Resolve(List<PlayerSetupData> players)
        {
            var rounds = new List<InitiativeRollRound>();
            var finalRolls = new Dictionary<PlayerSetupData, TokenRollResult>();
            List<PlayerSetupData> order = ResolveGroup(players, finalRolls, rounds,
                group => RollGroup(group));
            return new TurnOrderResolution(order, finalRolls, rounds);
        }

        // Deterministic seam used by EditMode tests. Each queued dictionary represents
        // one visible roll round: first every player, then only whichever tied subgroup rerolls.
        internal static TurnOrderResolution ResolveRecordedRolls(List<PlayerSetupData> players,
            Queue<Dictionary<PlayerSetupData, TokenRollResult>> recordedRounds)
        {
            var rounds = new List<InitiativeRollRound>();
            var finalRolls = new Dictionary<PlayerSetupData, TokenRollResult>();
            List<PlayerSetupData> order = ResolveGroup(players, finalRolls, rounds, group =>
            {
                Dictionary<PlayerSetupData, TokenRollResult> recorded = recordedRounds.Dequeue();
                var selected = new Dictionary<PlayerSetupData, TokenRollResult>();
                foreach (PlayerSetupData player in group)
                    selected[player] = recorded[player];
                return selected;
            });
            return new TurnOrderResolution(order, finalRolls, rounds);
        }

        private static List<PlayerSetupData> ResolveGroup(List<PlayerSetupData> group,
            Dictionary<PlayerSetupData, TokenRollResult> finalRolls,
            List<InitiativeRollRound> rounds,
            System.Func<List<PlayerSetupData>, Dictionary<PlayerSetupData, TokenRollResult>> rollGroup)
        {
            Dictionary<PlayerSetupData, TokenRollResult> rolls = rollGroup(group);
            rounds.Add(new InitiativeRollRound(rolls));
            foreach (PlayerSetupData player in group)
                finalRolls[player] = rolls[player];

            var ordered = new List<PlayerSetupData>();
            foreach (IGrouping<int, PlayerSetupData> scoreGroup in group.GroupBy(p => rolls[p].Score).OrderByDescending(g => g.Key))
            {
                List<PlayerSetupData> tied = scoreGroup.ToList();
                if (tied.Count == 1)
                    ordered.Add(tied[0]);
                else
                    ordered.AddRange(ResolveGroup(tied, finalRolls, rounds, rollGroup));
            }
            return ordered;
        }

        private static Dictionary<PlayerSetupData, TokenRollResult> RollGroup(List<PlayerSetupData> group)
        {
            var rolls = new Dictionary<PlayerSetupData, TokenRollResult>();
            foreach (PlayerSetupData player in group)
                rolls[player] = RollTokens(TokenCountFor(player));
            return rolls;
        }

        // Same 50/50-per-die mechanic as every other "Challenge" in the game (see
        // Game.Combat.ChallengeResolver) — reused here rather than duplicated, even though an
        // initiative roll isn't an attacker/defender pair, just one player's pool at a time.
        private static TokenRollResult RollTokens(int count)
        {
            return new TokenRollResult(ChallengeResolver.RollTokens(count));
        }
    }
}
