using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;

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
    }
}
