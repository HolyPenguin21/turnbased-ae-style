using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  STRATEGIC GENERATION BUDGET  (AI-MGR-02 §P0)
    // ===========================================================================================
    //  ONE authoritative per-(player, turn) counter of Research/Production Challenge ATTEMPTS.
    //  maxGenerationActionsPerTurn is enforced against this, so Phase A demand fulfilment and
    //  every end-of-turn tempo re-entry (main pass, bounded reaction round, Housekeeping re-run)
    //  share the same budget even when they carry a fresh MaterializationReservation. Turn-keyed:
    //  a stale entry from a previous turn reads as 0, so no explicit reset is needed.
    // ===========================================================================================
    internal static class StrategicGenerationBudget
    {
        private sealed class Entry { public int Turn = -1; public int Used; }

        private static readonly Dictionary<PlayerSetupData, Entry> ByPlayer =
            new Dictionary<PlayerSetupData, Entry>();

        public static int Used(PlayerSetupData player, int turn)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return 0;
            return e.Used;
        }

        public static void Record(PlayerSetupData player, int turn, int count = 1)
        {
            if (player == null || count <= 0)
                return;
            if (!ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                ByPlayer[player] = e = new Entry { Turn = turn, Used = 0 };
            e.Used += count;
        }
    }
}
