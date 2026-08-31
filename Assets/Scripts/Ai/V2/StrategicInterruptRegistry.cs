using System.Collections.Generic;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Carries current-turn facts that invalidate strategic conclusions already made earlier in the
    // pipeline. Enemy/neutral discovery is one source; a late hand/capability mutation can also make
    // a previously suppressed mission executable. All reasons feed the same ONE bounded reaction
    // pass before housekeeping — no recursive replanning and no second planner implementation.
    internal static class StrategicInterruptRegistry
    {
        [System.Flags]
        private enum Reason
        {
            None = 0,
            Discovery = 1 << 0,
            HandOpportunity = 1 << 1,
            CapabilityChanged = 1 << 2,
        }

        private sealed class Entry
        {
            public int Turn;
            public AiHandData Hand;
            public Reason Reasons;
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
            if (e.DiscoveredArmyIds.Count > 0)
                e.Reasons |= Reason.Discovery;
        }

        public static void MarkHandOpportunity(PlayerSetupData player, int turn, AiHandData hand)
        {
            if (player == null)
                return;
            Entry e = GetOrReset(player, turn);
            if (hand != null)
                e.Hand = hand;
            e.Reasons |= Reason.HandOpportunity;
        }

        public static void MarkCapabilityChanged(PlayerSetupData player, int turn, AiHandData hand)
        {
            if (player == null)
                return;
            Entry e = GetOrReset(player, turn);
            if (hand != null)
                e.Hand = hand;
            e.Reasons |= Reason.CapabilityChanged;
        }

        // Historical name retained because StrategicManager/StrategicReactionPass already meet at
        // this seam. Semantics are now deliberately broader: ANY strategic invalidation requests the
        // same bounded reaction. TargetIds remains empty when the reason was not a contact discovery.
        public static bool HasPendingDiscovery(PlayerSetupData player, int turn)
        {
            return player != null
                && ByPlayer.TryGetValue(player, out Entry e)
                && e.Turn == turn
                && e.Reasons != Reason.None;
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