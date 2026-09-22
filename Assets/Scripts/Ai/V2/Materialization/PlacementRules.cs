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
    //  Self-contained "may this card legally deploy at this hex" building check — the same rule
    //  CardHandUI.IsValidDropTarget enforces for a human drag-drop (an OWN building at the hex
    //  granting the card's requiredBuildingAbility, Barracks in practice). Kept here so V2 does
    //  not reach into V1 AiManagementPlanner for it.
    // ===========================================================================================
    public static class PlacementRules
    {
        public static bool HasRequiredBuilding(PlayerSetupData player, HexCoord hex, CardDefinition def)
        {
            // For ground Unit/Hero cards, an empty ability is NOT a wildcard. Human
            // CardHandUI.IsValidDropTarget rejects it; the previous AI-only exemption admitted
            // a card the live human path would never allow. Aviation uses its independent owned
            // airfield placement rule and is intentionally not routed through this predicate.
            if (player == null || def == null || string.IsNullOrEmpty(def.requiredBuildingAbility))
                return false;
            BuildingData b = BuildingRegistry.FindAt(hex);
            return b != null && b.Owner == player && b.HasAbility(def.requiredBuildingAbility);
        }

        // Same stricter rule V1 AiManagementPlanner.HasGarrisonDepositRoom enforces — an ordinary
        // card must not fill a garrison's last slots, part of capacity is kept for later
        // operations / reorganisation. Neutral primitive so V1 and V2 stay in step on the number.
        public static bool CanDepositIntoGarrison(ArmyData garrison) =>
            garrison != null && garrison.IsGarrison
            && garrison.Capacity - garrison.Members.Count > AiConfig.garrisonReservedSlots;

        // AI-MGR-01 final closure §1 — pure FEASIBILITY query: "which owned airfield can this
        // aviation card physically be deposited at right now?" Ported verbatim from V1
        // AiManagementPlanner.FindAviationPlacement so the V2 non-combat lane no longer reaches into
        // a V1 Level-1 planner for a placement decision. This is a query, not a decision — WHETHER
        // an aviation card is worth playing stays entirely with StrategicCardEvaluator / Phase-B
        // arbitration. Uses only canonical gameplay APIs: AiCardCost (thin wrapper over
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
            if (!AiResourceReservation.CanAffordCardPlay(root, player, card))
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
