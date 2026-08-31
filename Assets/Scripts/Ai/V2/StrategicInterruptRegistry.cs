using System.Collections.Generic;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Carries only current-turn execution facts across existing pipeline seams. Scout discovery is
    // a strategic interrupt because the whole point of Recon is to reveal actionable information.
    // The interrupt is bounded to one reaction pass and is cleared before housekeeping completes.
    internal static class StrategicInterruptRegistry
    {
        private sealed class Entry
        {
            public int Turn;
            public AiHandData Hand;
            public readonly HashSet<int> DiscoveredArmyIds = new HashSet<int>();
        }

        private static readonly Dictionary<PlayerSetupData, Entry> ByPlayer =
            new Dictionary<PlayerSetupData, Entry>();

        public static void CaptureTurnContext(PlayerSetupData player, int turn, AiHandData hand)
        {
            if (player == null)
                return;
            Entry e = GetOrReset(player, turn);
            if (hand != null)
                e.Hand = hand;
        }

        public static void MarkDiscovery(PlayerSetupData player, int turn, IEnumerable<int> armyIds)
        {
            if (player == null || armyIds == null)
                return;
            Entry e = GetOrReset(player, turn);
            foreach (int id in armyIds)
                if (id > 0)
                    e.DiscoveredArmyIds.Add(id);
        }

        public static bool HasPendingDiscovery(PlayerSetupData player, int turn)
        {
            return player != null
                && ByPlayer.TryGetValue(player, out Entry e)
                && e.Turn == turn
                && e.DiscoveredArmyIds.Count > 0;
        }

        public static HashSet<int> TargetIds(PlayerSetupData player, int turn)
        {
            if (!HasPendingDiscovery(player, turn))
                return new HashSet<int>();
            return new HashSet<int>(ByPlayer[player].DiscoveredArmyIds);
        }

        public static bool TryGetHand(PlayerSetupData player, int turn, out AiHandData hand)
        {
            hand = null;
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return false;
            hand = e.Hand;
            return hand != null;
        }

        public static void Clear(PlayerSetupData player, int turn)
        {
            if (player != null && ByPlayer.TryGetValue(player, out Entry e) && e.Turn == turn)
                ByPlayer.Remove(player);
        }

        private static Entry GetOrReset(PlayerSetupData player, int turn)
        {
            if (!ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
            {
                e = new Entry { Turn = turn };
                ByPlayer[player] = e;
            }
            return e;
        }
    }
}
