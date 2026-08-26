using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

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

        // Every repaired unit costs exactly one AP. Its resource part is rolled ONCE when the
        // card enters play: ceil(half) of its original resource units, sampled without
        // replacement, so the shown cost never changes for that individual card.
        public static int ApCost(UnitData unit) => unit != null ? 1 : 0;

        public static ResourceCost ResourceCost(UnitData unit)
        {
            InitializeRepairCost(unit);
            return unit?.RepairResourceCost ?? new ResourceCost();
        }

        // Called by SpawnUnit as soon as a card becomes a live unit. The null guard also keeps
        // old runtime-created units and display snapshots safe if they predate this field.
        public static void InitializeRepairCost(UnitData unit)
        {
            if (unit == null || unit.RepairResourceCost != null)
                return;

            var available = new List<ResourceType>();
            AddUnits(available, ResourceType.Human, unit.OriginalResourceCost?.human ?? 0);
            AddUnits(available, ResourceType.Energy, unit.OriginalResourceCost?.energy ?? 0);
            AddUnits(available, ResourceType.Materials, unit.OriginalResourceCost?.materials ?? 0);
            AddUnits(available, ResourceType.Tech, unit.OriginalResourceCost?.tech ?? 0);

            var repairCost = new ResourceCost();
            int picks = (available.Count + 1) / 2;
            for (int i = 0; i < picks; i++)
            {
                int index = Random.Range(0, available.Count);
                AddOne(repairCost, available[index]);
                available.RemoveAt(index);
            }
            unit.RepairResourceCost = repairCost;
        }

        private static void AddUnits(List<ResourceType> target, ResourceType type, int amount)
        {
            for (int i = 0; i < amount; i++)
                target.Add(type);
        }

        private static void AddOne(ResourceCost cost, ResourceType type)
        {
            switch (type)
            {
                case ResourceType.Human: cost.human++; break;
                case ResourceType.Energy: cost.energy++; break;
                case ResourceType.Materials: cost.materials++; break;
                case ResourceType.Tech: cost.tech++; break;
            }
        }

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
