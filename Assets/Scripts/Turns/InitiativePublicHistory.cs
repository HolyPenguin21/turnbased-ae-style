using System;
using System.Collections.Generic;
using Game.Ai;
using Game.Players;

namespace Game.Turns
{
    // The ONLY cross-player information the Initiative AI is allowed to use about opponents when
    // it plans this round's purchases: what everyone visibly ended up with last time. No hidden
    // stockpile, card or reservation read — just the public bonus-tokens count each player showed
    // on the table after the previous initiative phase resolved.
    //
    // GameTurnController.OnTurnOrderResolved records one entry per player every turn, AFTER the
    // roll, BEFORE next turn's ResetBonusInitiativeTokens wipes the counts. Cleared on a new game.
    public static partial class InitiativePublicHistory
    {
        private const int MaxSamples = 6;

        private static readonly Dictionary<PlayerSetupData, List<int>> BoughtByPlayer =
            new Dictionary<PlayerSetupData, List<int>>();

        // Detached public observations, preserved by match-local player identity. Restore is
        // direct: re-recording would truncate or manufacture the historical sample sequence.
        internal static IReadOnlyDictionary<string, IReadOnlyList<int>> CapturePersistentState(
            Func<PlayerSetupData, string> playerId) =>
            AiPersistenceCollections.Capture(BoughtByPlayer, playerId,
                samples => AiPersistenceCollections.FreezeList(samples));

        internal static void RestorePersistentState(IReadOnlyDictionary<string, IReadOnlyList<int>> state,
            Func<string, PlayerSetupData> player) =>
            AiPersistenceCollections.Restore(BoughtByPlayer, state, player, samples => new List<int>(samples));

        public static void Clear() => BoughtByPlayer.Clear();

        public static void RecordFinalBonusTokens(PlayerSetupData player, int boughtBonusTokens)
        {
            if (player == null)
                return;
            if (!BoughtByPlayer.TryGetValue(player, out List<int> samples))
                BoughtByPlayer[player] = samples = new List<int>();
            samples.Add(boughtBonusTokens < 0 ? 0 : boughtBonusTokens);
            if (samples.Count > MaxSamples)
                samples.RemoveRange(0, samples.Count - MaxSamples);
        }

        // Expected bonus tokens this opponent will buy this round. No history => assume 0 (the five
        // free base tokens only). With history, use a short trailing average rather than a one-off
        // spike; the result is public-behaviour prediction, never hidden-state inspection.
        public static int EstimatedBonusTokens(PlayerSetupData player)
        {
            if (player == null || !BoughtByPlayer.TryGetValue(player, out List<int> samples) || samples.Count == 0)
                return 0;

            int sum = 0;
            foreach (int s in samples)
                sum += s;
            int avg = (sum + samples.Count / 2) / samples.Count;
            if (avg < 0)
                avg = 0;
            if (avg > InitiativeRules.MaxBonusTokens)
                avg = InitiativeRules.MaxBonusTokens;
            return avg;
        }
    }
}
