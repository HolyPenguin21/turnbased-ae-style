using System.Collections.Generic;
using Game.Players;

namespace Game.Progression
{
    // Owner decision D2 (2026-11-11): one local account, one reward per match.
    //   Victory — the sole survivor is a human (that human's faction).
    //   Defeat  — the LAST living human was eliminated (that human's faction).
    //   An earlier human elimination publishes nothing; the match goes on.
    // With one human this is exactly the old rule (their elimination = defeat at once).
    // Draws stay outside this policy (no non-campaign reward, see RewardService).
    public sealed class MatchAccountOutcome
    {
        private bool published;
        public bool HasPublished => published;

        public ParticipantResult? OnEliminated(string matchId, PlayerSetupData player, IEnumerable<PlayerSetupData> players)
        {
            if (published || player == null || !player.IsHuman) return null;
            if (players != null)
                foreach (var p in players)
                    if (p != null && !ReferenceEquals(p, player) && p.IsHuman && !p.IsNeutral && !p.IsEliminated)
                        return null;
            published = true;
            return new ParticipantResult(matchId, player, MatchOutcome.Defeat);
        }

        public ParticipantResult? OnMatchFinished(string matchId, IReadOnlyList<PlayerSetupData> survivors)
        {
            if (published || survivors == null || survivors.Count != 1) return null;
            var winner = survivors[0];
            if (winner == null || !winner.IsHuman) return null;
            published = true;
            return new ParticipantResult(matchId, winner, MatchOutcome.Victory);
        }

        public void Reset() => published = false;
    }
}
