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
    // ArmyViewerModalUI (human click) and Strategy V2's Phase-B maintenance,
    // so the two paths can never drift on affordability or the actual spend — same "shared
    // static helper" shape as Map.ArmyActions.
    public static class UnitRepair
    {
        public static event System.Action<ArmyData, UnitData> Repaired;
        public static bool IsWounded(UnitData unit) => unit != null && unit.HitPointsCurrent > 0
            && unit.HitPointsCurrent < unit.HitPointsMax;

        public static bool IsRepairSourceMember(UnitData unit) => unit != null
            && unit.HitPointsCurrent > 0 && !unit.IsPrisoner && !unit.IsAviation
            && unit.HasAbility(UnitAbilities.Repair);

        // Read live membership, never stamp a hex or maintain a parallel source registry.
        // Garrisoned ground mechanics work too; aviation/carrier containers and prisons do not.
        public static int RepairSourceCount(ArmyData army)
        {
            if (army == null || army.Owner == null || army.Owner.IsEliminated
                || army.IsPrison || army.IsAirfield || army.IsCarrierContainer)
                return 0;
            int count = 0;
            foreach (UnitData member in army.Members)
                if (IsRepairSourceMember(member) && member.Owner == army.Owner)
                    count++;
            return count;
        }

        // A repair point is owner-scoped, independent of Base/Barracks identity. A mechanic
        // serves every own army on its hex without granting deployment or infrastructure rights.
        public static bool CanRepairAt(HexCoord hex, PlayerSetupData owner)
        {
            if (owner == null || owner.IsEliminated)
                return false;
            BuildingData building = BuildingRegistry.FindAt(hex);
            if (building != null && building.Owner == owner && building.IsBase)
                return true;
            foreach (ArmyData army in ArmyRegistry.AllAt(hex))
                if (army.Owner == owner && RepairSourceCount(army) > 0)
                    return true;
            return false;
        }

        // Shared target legality for the UI, AI candidates and the final transaction. All live
        // unit types retain their existing repair rule, including heroes and stored aircraft.
        public static bool CanRepair(UnitData unit, ArmyData army) => IsWounded(unit)
            && !unit.IsPrisoner && army != null && !army.IsPrison && ArmyRegistry.IsRegistered(army)
            && unit.Owner == army.Owner && army.Members.Contains(unit)
            && CanRepairAt(army.Hex, unit.Owner);

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
            ArmyData army = unit != null ? ArmyRegistry.FindArmyContaining(unit) : null;
            if (root == null || unit == null || root.Setup != unit.Owner
                || army == null || !army.Hex.Equals(hex) || !CanRepair(unit, army))
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
            EquipmentSystem.ReconcileResourceRefill(unit, EquipmentStat.HitPoints);
            // Repair does not move the army or alter its vision footprint. Its visible HP does
            // change, however: publish the finished mutation at the unit's actual live hex so
            // an already-watching AI refreshes EnemySightings without a full vision recompute.
            VisionSystem.NotifyContentChanged(army.Hex);
            Repaired?.Invoke(army, unit);
            return true;
        }
    }
}
