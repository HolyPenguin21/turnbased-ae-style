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
            public readonly HashSet<int> ActorIds = new HashSet<int>();
        }

        internal static void EndTurn(PlayerSetupData player, int turn)
        {
            ByPlayer.Remove(player, turn);
        }

        private static readonly TurnScopedByPlayer<Entry> ByPlayer =
            new TurnScopedByPlayer<Entry>(() => new Entry());

        public static void Clear() => ByPlayer.Clear();

        public static void MarkStalled(PlayerSetupData player, int turn, int armyId)
        {
            if (player == null)
                return;
            ByPlayer.GetOrReset(player, turn).ActorIds.Add(armyId);
        }

        public static bool IsStalled(PlayerSetupData player, int turn, int armyId) =>
            ByPlayer.Peek(player, turn) is Entry e && e.ActorIds.Contains(armyId);
    }
}
