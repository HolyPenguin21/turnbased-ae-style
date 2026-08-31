using System.Collections.Generic;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // Carries current-turn facts that invalidate conclusions made by the frozen strategic pass.
    // One ordinary reaction round is always allowed; one extra follow-up round is allowed only for
    // hand/capability changes created by that reaction. Contact discovery never recursively chains.
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
            if (player == null) return;
            Entry e = GetOrReset(player, turn);
            if (hand != null) e.Hand = hand;
        }

        public static void MarkDiscovery(PlayerSetupData player, int turn, IEnumerable<int> armyIds)
        {
            if (player == null || armyIds == null) return;
            Entry e = GetOrReset(player, turn);
            foreach (int id in armyIds)
                if (id > 0) e.DiscoveredArmyIds.Add(id);
            if (e.DiscoveredArmyIds.Count > 0)
                e.Reasons |= Reason.Discovery;
        }

        public static void MarkHandOpportunity(PlayerSetupData player, int turn, AiHandData hand)
        {
            if (player == null) return;
            Entry e = GetOrReset(player, turn);
            if (hand != null) e.Hand = hand;
            e.Reasons |= Reason.HandOpportunity;
        }

        public static void MarkCapabilityChanged(PlayerSetupData player, int turn, AiHandData hand)
        {
            if (player == null) return;
            Entry e = GetOrReset(player, turn);
            if (hand != null) e.Hand = hand;
            e.Reasons |= Reason.CapabilityChanged;
        }

        // Historical name retained for existing call sites. It now means ANY pending strategic
        // invalidation, not only contact discovery.
        public static bool HasPendingDiscovery(PlayerSetupData player, int turn) => HasPending(player, turn);

        public static bool HasPending(PlayerSetupData player, int turn) =>
            player != null && ByPlayer.TryGetValue(player, out Entry e)
            && e.Turn == turn && e.Reasons != Reason.None;

        public static bool HasPendingContactDiscovery(PlayerSetupData player, int turn) =>
            player != null && ByPlayer.TryGetValue(player, out Entry e)
            && e.Turn == turn && (e.Reasons & Reason.Discovery) != 0;

        public static bool HasPendingFollowup(PlayerSetupData player, int turn) =>
            player != null && ByPlayer.TryGetValue(player, out Entry e)
            && e.Turn == turn
            && (e.Reasons & (Reason.HandOpportunity | Reason.CapabilityChanged)) != 0;

        public static HashSet<int> TargetIds(PlayerSetupData player, int turn)
        {
            if (!HasPending(player, turn)) return new HashSet<int>();
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

        // Drop only contact-discovery recursion while preserving a hand/capability invalidation that
        // may have been registered in the same round.
        public static void ClearDiscovery(PlayerSetupData player, int turn)
        {
            if (player == null || !ByPlayer.TryGetValue(player, out Entry e) || e.Turn != turn)
                return;
            e.Reasons &= ~Reason.Discovery;
            e.DiscoveredArmyIds.Clear();
            if (e.Reasons == Reason.None)
                ByPlayer.Remove(player);
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
