using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Map;
using Game.Players;

namespace Game.Turns
{
    // Decides player turn order at the start of every turn: everyone rolls a dice pool (see
    // DiceRollResult), highest score goes first. Ties are broken by rerolling all of that
    // player's dice again — but only for the tied players, repeated (recursively, since a
    // reroll can itself produce a smaller tie) until every slot is resolved.
    //
    // Pool size is BaseDiceCount plus whatever that player bought this turn (see
    // PlayerRoot.BonusInitiativeDice) — not a single shared constant any more, since buying
    // extra dice is the whole point of the initiative-buying step this feeds into.
    public static class TurnOrderResolver
    {
        private const int BaseDiceCount = 3;

        // Exposed for DiceRowUI, so the popup can size each player's dice slots correctly
        // before the roll happens (not just once ShowRoll gives back the actual results).
        public static int DiceCountFor(PlayerSetupData player)
        {
            PlayerRoot root = PlayerRootRegistry.FindFor(player);
            return BaseDiceCount + (root != null ? root.BonusInitiativeDice : 0);
        }

        // finalRolls comes back with, for every player, the roll that actually decided their
        // slot — their first roll if they were never tied, or whichever reroll broke the tie
        // they were in.
        public static List<PlayerSetupData> Resolve(List<PlayerSetupData> players, out Dictionary<PlayerSetupData, DiceRollResult> finalRolls)
        {
            finalRolls = new Dictionary<PlayerSetupData, DiceRollResult>();
            return ResolveGroup(players, finalRolls);
        }

        private static List<PlayerSetupData> ResolveGroup(List<PlayerSetupData> group, Dictionary<PlayerSetupData, DiceRollResult> finalRolls)
        {
            var rolls = new Dictionary<PlayerSetupData, DiceRollResult>();
            foreach (PlayerSetupData player in group)
            {
                DiceRollResult roll = RollDice(DiceCountFor(player));
                rolls[player] = roll;
                // Tentative — overwritten below if this player turns out to be tied and has
                // to reroll again at a deeper level.
                finalRolls[player] = roll;
            }

            var ordered = new List<PlayerSetupData>();
            foreach (IGrouping<int, PlayerSetupData> scoreGroup in group.GroupBy(p => rolls[p].Score).OrderByDescending(g => g.Key))
            {
                List<PlayerSetupData> tied = scoreGroup.ToList();
                if (tied.Count == 1)
                    ordered.Add(tied[0]);
                else
                    ordered.AddRange(ResolveGroup(tied, finalRolls));
            }
            return ordered;
        }

        // Same 50/50-per-die mechanic as every other "Challenge" in the game (see
        // Game.Combat.ChallengeResolver) — reused here rather than duplicated, even though an
        // initiative roll isn't an attacker/defender pair, just one player's pool at a time.
        private static DiceRollResult RollDice(int count)
        {
            return new DiceRollResult(ChallengeResolver.RollDice(count));
        }
    }
}
