using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Units
{
    // Repair cost/eligibility/spend for a wounded unit or hero — the unit analogue of
    // BaseViewerModalUI.RepairBase, except this one actually costs AP/resources (nothing damages
    // a building yet, so that one never needed to). Pure static, called identically from
    // ArmyViewerModalUI (human click) and Game.Ai.AiManagementPlanner's own RepairUnit task/routine,
    // so the two paths can never drift on affordability or the actual spend — same "shared
    // static helper" shape as Map.ArmyActions.
    public static class UnitRepair
    {
        public static bool IsWounded(UnitData unit) => unit != null && unit.HitPointsCurrent < unit.HitPointsMax;

        // The player's own Base only — not an ally's, not neutral/enemy (the project owner's own
        // call). A hex with no building, or a building that isn't Base-tagged (e.g. a bare
        // hero-built resource site), never qualifies.
        public static bool CanRepairAt(HexCoord hex, PlayerSetupData owner)
        {
            BuildingData building = BuildingRegistry.FindAt(hex);
            return building != null && building.Owner == owner && building.IsBase;
        }

        // Half of what the unit originally cost to play, rounded down per resource independently
        // (the project owner's own spec — e.g. a 2AP/1 Human hero repairs for 1AP; a 5AP/2H/3E/4M
        // unit repairs for 2AP/1H/1E/2M). Full heal only, no partial-damage scaling — the cost is
        // fixed regardless of how wounded the unit currently is.
        public static int ApCost(UnitData unit) => unit.ApCost / 2;

        public static ResourceCost ResourceCost(UnitData unit) => new ResourceCost
        {
            human = (unit.OriginalResourceCost?.human ?? 0) / 2,
            energy = (unit.OriginalResourceCost?.energy ?? 0) / 2,
            materials = (unit.OriginalResourceCost?.materials ?? 0) / 2,
            tech = (unit.OriginalResourceCost?.tech ?? 0) / 2,
        };

        // Checks AP then resources separately (rather than one combined check) so a caller can
        // report which one was actually short — same convention as ArmyActions.DeployUnitFromCard
        // and every other ShowSpawnHint call site in this project.
        public static bool TryRepair(UnitData unit, HexCoord hex, PlayerRoot root, out string failReason)
        {
            failReason = null;
            if (unit == null || root == null || !IsWounded(unit) || !CanRepairAt(hex, unit.Owner))
            {
                failReason = "Cannot repair this unit here.";
                return false;
            }

            int apCost = ApCost(unit);
            ResourceCost cost = ResourceCost(unit);
            if (!root.CanSpendActionPoints(apCost))
            {
                failReason = $"Not enough action points to repair {unit.Name}.";
                return false;
            }
            if (!cost.CanAfford(root))
            {
                failReason = $"Not enough resources to repair {unit.Name}.";
                return false;
            }

            root.SpendActionPoints(apCost);
            cost.PayFrom(root);
            unit.HitPointsCurrent = unit.HitPointsMax;
            return true;
        }
    }
}
