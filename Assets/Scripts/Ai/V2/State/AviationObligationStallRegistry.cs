using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AVIATION OBLIGATION STALL REGISTRY  (Recon audit B1)
    // ===========================================================================================
    //  A mandatory air obligation (Recon flight recovery, a multi-turn rebase continuation) is
    //  admitted before any strategic mission in the typed loop. When its one step this turn could
    //  not make progress (no reachable owned-airfield step, move rejected), trying it again this
    //  turn reproduces the same refusal — and the loop used to stop ALL mission execution for the
    //  turn instead. The actor is recorded here for the rest of the turn: the obligation finders
    //  skip it, so the loop moves on to missions, and StrategicSpendability stops protecting an
    //  activation that cannot be spent this turn. The next turn re-tries it from scratch.
    // ===========================================================================================
    internal static class AviationObligationStallRegistry
    {
        private sealed class Entry
        {
            public int Turn = int.MinValue;
            public readonly HashSet<int> ActorIds = new HashSet<int>();
        }

        private static readonly Dictionary<PlayerSetupData, Entry> ByPlayer =
            new Dictionary<PlayerSetupData, Entry>();

        public static void Clear() => ByPlayer.Clear();

        public static void MarkStalled(PlayerSetupData player, int turn, int armyId)
        {
            if (player == null)
                return;
            Entry e = For(player, turn);
            e.ActorIds.Add(armyId);
        }

        public static bool IsStalled(PlayerSetupData player, int turn, int armyId) =>
            player != null && ByPlayer.TryGetValue(player, out Entry e)
            && e.Turn == turn && e.ActorIds.Contains(armyId);

        private static Entry For(PlayerSetupData player, int turn)
        {
            if (!ByPlayer.TryGetValue(player, out Entry e))
                ByPlayer[player] = e = new Entry();
            if (e.Turn != turn)
            {
                e.Turn = turn;
                e.ActorIds.Clear();
            }
            return e;
        }
    }
}
