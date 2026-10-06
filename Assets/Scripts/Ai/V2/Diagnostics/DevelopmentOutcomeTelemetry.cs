using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // Per-player whole-game tally of what Research/Production actually delivered, so a playtest
    // log answers "did development buy anything" with one line instead of a timeline replay.
    // Diagnostics only: nothing reads these counters back into a decision.
    internal static class DevelopmentOutcomeTelemetry
    {
        internal sealed class Tally
        {
            public int ChallengesWon, ChallengesLost, Attached, FacilitiesBuilt, Skipped;
        }

        private static readonly Dictionary<PlayerSetupData, Tally> ByPlayer =
            new Dictionary<PlayerSetupData, Tally>();

        internal static void ClearAll() => ByPlayer.Clear();

        internal static Tally Get(PlayerSetupData player)
        {
            if (player == null) return new Tally();
            if (!ByPlayer.TryGetValue(player, out Tally t))
                ByPlayer[player] = t = new Tally();
            return t;
        }

        // One creation-stage execution; attachment may happen on a later turn.
        internal static void RecordUpgrade(PlayerSetupData player, int turn,
            bool executed, bool challengeWon)
        {
            Tally t = Get(player);
            if (!executed) t.Skipped++;
            else
            {
                if (challengeWon) t.ChallengesWon++; else t.ChallengesLost++;
            }
            Log(player, turn, t);
        }

        internal static void RecordAttachment(PlayerSetupData player, int turn, Game.Cards.CardData card)
        {
            if (card == null || !card.ResearchProductionCreated) return;
            Tally t = Get(player);
            t.Attached++;
            Log(player, turn, t);
        }

        internal static void RecordFacilityBuilt(PlayerSetupData player, int turn)
        {
            Tally t = Get(player);
            t.FacilitiesBuilt++;
            Log(player, turn, t);
        }

        private static void Log(PlayerSetupData player, int turn, Tally t) =>
            AiDebugLog.Write($"[AI][V2][Dev][Outcome] {player?.Nickname} T{turn} total: "
                + $"challengesWon={t.ChallengesWon} challengesLost={t.ChallengesLost} "
                + $"upgradesAttached={t.Attached} facilitiesBuilt={t.FacilitiesBuilt} skipped={t.Skipped}");
    }
}
