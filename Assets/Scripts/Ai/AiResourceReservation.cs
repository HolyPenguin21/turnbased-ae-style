using System;
using Game.Cards;
using Game.Economy;
using Game.Map;
using Game.Players;

namespace Game.Ai
{
    // The reservation-aware "what can I actually still spend" read every AI spend path routes
    // through. Post-ARCH-01 there is no V1 task bookkeeping: the real PlayerRoot stockpile is the
    // authoritative physical pool, and Strategy V2's own atomic claims are surfaced through the
    // V2ExtraReservation hook (installed by the V2 pipeline). A reservation never removes anything
    // from PlayerRoot — it is purely an accounting subtraction applied here.
    public static class AiResourceReservation
    {
        private static readonly ResourceType[] AllTypes =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        // The V2 pipeline installs this so every spend path that already calls Available() nets
        // out AP/Energy set aside for a planned-but-unlaunched action without re-implementing the
        // lookup. Null when no V2 reservation is active.
        public static Func<PlayerSetupData, ResourceType, int> V2ExtraReservation;

        public static void Clear() => V2ExtraReservation = null;

        public static int Available(PlayerRoot root, PlayerSetupData player, ResourceType type)
        {
            if (root == null)
                return 0;
            int v2Reserved = V2ExtraReservation != null ? Math.Max(0, V2ExtraReservation(player, type)) : 0;
            return Math.Max(0, root.GetResource(type) - v2Reserved);
        }

        public static bool CanAfford(PlayerRoot root, PlayerSetupData player, ResourceCost cost)
        {
            if (root == null || cost == null)
                return false;
            foreach (ResourceType type in AllTypes)
                if (Available(root, player, type) < cost.Get(type))
                    return false;
            return true;
        }

        // Reservation-aware affordability of a specific hand-card instance's play-time resource
        // cost. A null cost (a Research/Production card — already paid at Create) is always
        // affordable, unlike CanAfford's own null -> false which guards an unwired cost.
        public static bool CanAffordCardPlay(PlayerRoot root, PlayerSetupData player, CardData card)
        {
            ResourceCost cost = CardCostRules.PlayResources(card);
            return cost == null || CanAfford(root, player, cost);
        }
    }
}
