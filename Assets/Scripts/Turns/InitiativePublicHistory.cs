using System.Collections.Generic;
using Game.Players;

namespace Game.Turns
{
    // The ONLY cross-player information the Initiative AI is allowed to use about opponents when
    // it plans this round's purchases: what everyone visibly ended up with last time. No hidden
    // stockpile, card or reservation read — just the public bonus-dice count each player showed
    // on the table after the previous initiative phase resolved.
    //
    // GameTurnController.OnTurnOrderResolved records one entry per player every turn, AFTER the
    // roll, BEFORE next turn's ResetBonusInitiativeDice wipes the counts. Cleared on a new game.
    public static class InitiativePublicHistory
    {
        private const int MaxSamples = 6;

        private static readonly Dictionary<PlayerSetupData, List<int>> BoughtByPlayer =
            new Dictionary<PlayerSetupData, List<int>>();

        public static void Clear() => BoughtByPlayer.Clear();

        public static void RecordFinalBonusDice(PlayerSetupData player, int boughtBonusDice)
        {
            if (player == null)
                return;
            if (!BoughtByPlayer.TryGetValue(player, out List<int> samples))
                BoughtByPlayer[player] = samples = new List<int>();
            samples.Add(boughtBonusDice < 0 ? 0 : boughtBonusDice);
            if (samples.Count > MaxSamples)
                samples.RemoveRange(0, samples.Count - MaxSamples);
        }

        // Expected bonus dice this opponent will buy this round. No history => assume 0 (the five
        // free base dice only). With history, use a short trailing average rather than a one-off
        // spike; the result is public-behaviour prediction, never hidden-state inspection.
        public static int EstimatedBonusDice(PlayerSetupData player)
        {
            if (player == null || !BoughtByPlayer.TryGetValue(player, out List<int> samples) || samples.Count == 0)
                return 0;

            int sum = 0;
            foreach (int s in samples)
                sum += s;
            int avg = (sum + samples.Count / 2) / samples.Count;
            if (avg < 0)
                avg = 0;
            if (avg > InitiativeRules.MaxBonusDice)
                avg = InitiativeRules.MaxBonusDice;
            return avg;
        }
    }
}
