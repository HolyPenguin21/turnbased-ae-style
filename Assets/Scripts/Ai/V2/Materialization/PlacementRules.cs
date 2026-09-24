using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using System.Collections.Generic;
using System.Linq;

using Game.Ai;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  PLACEMENT RULES  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  V2-only placement policy that is NOT physical deployment law (reserved garrison slots,
    //  stable airfield enumeration). Ground building legality is owned by
    //  ArmyActions.HasRequiredGroundDeploymentBuilding and is called directly by planning/UI.
    // ===========================================================================================
    public static class PlacementRules
    {
        // Same stricter rule V1 AiManagementPlanner.HasGarrisonDepositRoom enforces — an ordinary
        // card must not fill a garrison's last slots, part of capacity is kept for later
        // operations / reorganisation. Neutral primitive so V1 and V2 stay in step on the number.
        public static bool CanDepositIntoGarrison(ArmyData garrison) =>
            garrison != null && garrison.IsGarrison
            && garrison.Capacity - garrison.Members.Count > AiConfig.garrisonReservedSlots;

        // Pure FEASIBILITY query: "which owned airfield can this aviation card physically be
        // deposited at right now?" A query, not a decision — WHETHER an aviation card is worth
        // playing stays entirely with StrategicCardEvaluator / Phase-B arbitration. Uses only
        // canonical gameplay APIs: CardCostRules (thin wrapper over
        // ArmyActions.EffectiveDeployApCost / card.EffectivePlayResourceCost), the shared
        // AiAirSortiePlanner.OwnedAirfieldHexes primitive (citadel + every airfield-capable Base, in
        // its own stable citadel-first order), and AviationRules.FreeAirfieldCapacity (the exact
        // STORED-container figure ArmyActions.DeployUnitFromCard itself gates on).
        public static bool TryFindAviationPlacement(WorldSnapshot snapshot, PlayerSetupData player,
            PlayerRoot root, CardData card, out HexCoord target, out string reason,
            bool requireCurrentAp = true)
        {
            List<HexCoord> options = EnumerateAviationPlacements(snapshot, player, root, card,
                out reason, requireCurrentAp);
            target = options.Count > 0 ? options[0] : default;
            return options.Count > 0;
        }

        // Pure feasibility owner: enumerate every legal owned airfield slot. Ordering is stable
        // only; it is not a strategic preference. NonCombatCardPlayer compares these placements
        // through the canonical current-objective TaskScore.
        public static List<HexCoord> EnumerateAviationPlacements(WorldSnapshot snapshot,
            PlayerSetupData player, PlayerRoot root, CardData card, out string reason,
            bool requireCurrentAp = true)
        {
            reason = null;
            if (player == null || root == null || card?.Definition == null)
            { reason = "missing args"; return new List<HexCoord>(); }

            int deployApCost = CardCostRules.PlayAp(card);
            if (requireCurrentAp && !root.CanSpendActionPoints(deployApCost))
            { reason = "unaffordable(ap)"; return new List<HexCoord>(); }
            if (!CardCostRules.CanAffordPlay(root, card))
            { reason = "unaffordable(resources)"; return new List<HexCoord>(); }

            List<HexCoord> options = FeasibleAviationAirfields(
                AiAirSortiePlanner.OwnedAirfieldHexes(player),
                hex => AviationRules.FreeAirfieldCapacity(hex, player));
            if (options.Count == 0)
                reason = "noAirfieldSlot";
            return options;
        }

        internal static List<HexCoord> FeasibleAviationAirfields(
            IEnumerable<HexCoord> ownedAirfields,
            System.Func<HexCoord, int> freeCapacity) =>
            (ownedAirfields ?? System.Array.Empty<HexCoord>())
                .Where(hex => freeCapacity != null && freeCapacity(hex) > 0)
                .OrderBy(hex => hex.Q).ThenBy(hex => hex.R)
                .ToList();
    }
}
