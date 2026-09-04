using Game.Aviation;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;

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
            if (def == null || string.IsNullOrEmpty(def.requiredBuildingAbility))
                return true; // no building requirement -> any owned hex is fine
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
        // AviationSupport.OwnedAirfieldHexes primitive (citadel + every airfield-capable Base, in
        // its own stable citadel-first order), and AviationRules.FreeAirfieldCapacity (the exact
        // STORED-container figure ArmyActions.DeployUnitFromCard itself gates on).
        public static bool TryFindAviationPlacement(WorldSnapshot snapshot, PlayerSetupData player,
            PlayerRoot root, CardData card, out HexCoord target, out string reason)
        {
            target = default;
            reason = null;
            if (player == null || root == null || card?.Definition == null)
            { reason = "missing args"; return false; }

            int deployApCost = CardCostRules.PlayAp(card);
            if (!root.CanSpendActionPoints(deployApCost))
            { reason = "unaffordable(ap)"; return false; }
            if (!AiResourceReservation.CanAffordCardPlay(root, player, card))
            { reason = "unaffordable(resources)"; return false; }

            foreach (HexCoord hex in AviationSupport.OwnedAirfieldHexes(player))
                if (AviationRules.FreeAirfieldCapacity(hex, player) > 0)
                { target = hex; return true; }

            reason = "noAirfieldSlot";
            return false;
        }
    }
}
