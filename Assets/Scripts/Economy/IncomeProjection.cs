using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Economy
{
    // Single physical resource-income owner. The turn processor consumes the grants from
    // ForEachHexCollectionGrant; IncomeFor observes those SAME grants without mutating roots.
    // Economy's strategic weighting and observer-specific enemy knowledge belong to Analysis,
    // not here. Building-first, finite yield and army-owner order are decided exactly once.
    public static class IncomeProjection
    {
        private static readonly ResourceType[] AllResourceTypes =
            { ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech };

        // Canonical per-building slice used by both the real income projection and every
        // pre-build marginal-value check. Keeping the cap here prevents AI/UI legality from
        // drifting away from the turn processor's finite per-hex resource pool.
        public static int BuildingCollection(int effectiveHexYield, int collectionCapacity)
            => Mathf.Min(Mathf.Max(0, effectiveHexYield), Mathf.Max(0, collectionCapacity));

        public static int MarginalBuildingCollection(int effectiveHexYield,
            int currentCollectionCapacity, int additionalCollectionCapacity = 1)
        {
            int before = BuildingCollection(effectiveHexYield, currentCollectionCapacity);
            int after = BuildingCollection(effectiveHexYield,
                currentCollectionCapacity + Mathf.Max(0, additionalCollectionCapacity));
            return Mathf.Max(0, after - before);
        }

        public static int OwnerCollectionAtHex(int effectiveHexYield,
            int buildingCollectionCapacity, int ownerArmyCollectorCount,
            bool ownerArmiesCanCollect)
        {
            int building = BuildingCollection(effectiveHexYield, buildingCollectionCapacity);
            int remaining = Mathf.Max(0, effectiveHexYield - building);
            int army = ownerArmiesCanCollect
                ? Mathf.Min(Mathf.Max(0, ownerArmyCollectorCount), remaining)
                : 0;
            return building + army;
        }

        // Net persistent income gained by adding building collection capacity. This intentionally
        // accounts for own Collect units already consuming the remainder: moving the same unit of
        // finite hex yield from an army to a Facility is not economic growth.
        public static int MarginalOwnerCollectionAtHex(int effectiveHexYield,
            int currentBuildingCollectionCapacity, int additionalBuildingCollectionCapacity,
            int ownerArmyCollectorCount, bool ownerArmiesCanCollect)
        {
            int before = OwnerCollectionAtHex(effectiveHexYield,
                currentBuildingCollectionCapacity, ownerArmyCollectorCount, ownerArmiesCanCollect);
            int after = OwnerCollectionAtHex(effectiveHexYield,
                currentBuildingCollectionCapacity + Mathf.Max(0, additionalBuildingCollectionCapacity),
                ownerArmyCollectorCount, ownerArmiesCanCollect);
            return Mathf.Max(0, after - before);
        }

        // The ONE allocator for real grants and read-only projection. Pass an optional resource
        // filter when querying one type, but never implement the allocation a second time.
        // `credit` gets the exact registered PlayerRoot that was checked while allocating;
        // no recipient without an account can consume a slice of the finite resource pool.
        // Order is the gameplay order: occupied hexes, resource type, building, then armies
        // grouped by their owner in ArmyRegistry's enumeration order. No world state is
        // mutated here; the real turn's callback is the only place that credits resources.
        public static void ForEachHexCollectionGrant(HexMap map,
            Action<PlayerRoot, ResourceType, int> credit, ResourceType? onlyType = null)
        {
            if (map == null || credit == null)
                return;

            var hexes = new HashSet<HexCoord>();
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
                hexes.Add(building.Hex);
            foreach (HexCoord hex in ArmyRegistry.AllOccupiedHexes())
                hexes.Add(hex);

            foreach (HexCoord hex in hexes)
            {
                if (!map.TryGetTerrainAt(hex, out TerrainTypeEntry entry))
                    continue;
                ResourceYields hexYield = HexResourceCalculator.GetEffectiveYield(
                    entry, HexResourceBonusRegistry.GetBonus(hex));
                if (!hexYield.HasAnyYield)
                    continue;

                BuildingData building = BuildingRegistry.FindAt(hex);
                PlayerRoot buildingRoot = building?.Owner != null
                    ? PlayerRootRegistry.FindFor(building.Owner) : null;
                foreach (ResourceType type in AllResourceTypes)
                {
                    if (onlyType.HasValue && type != onlyType.Value)
                        continue;
                    int hexAmount = hexYield.Get(type);
                    if (hexAmount <= 0)
                        continue;
                    int remaining = hexAmount;
                    if (buildingRoot != null)
                    {
                        int buildingCollected = BuildingCollection(hexAmount,
                            building.CollectedAmount(type));
                        if (buildingCollected > 0)
                        {
                            credit(buildingRoot, type, buildingCollected);
                            remaining -= buildingCollected;
                        }
                    }
                    if (remaining <= 0)
                        continue;

                    string ability = UnitAbilities.CollectAbilityFor(type);
                    foreach (IGrouping<PlayerSetupData, ArmyData> ownerArmies in
                             ArmyRegistry.AllAt(hex).GroupBy(a => a.Owner))
                    {
                        if (remaining <= 0)
                            break;
                        PlayerSetupData owner = ownerArmies.Key;
                        if (owner == null || BattleInitiator.FindEnemyAt(hex, owner) != null)
                            continue;
                        int unitCount = ownerArmies.Sum(a =>
                            a.Members.Count(u => u.HasAbility(ability)));
                        if (unitCount <= 0)
                            continue;
                        PlayerRoot ownerRoot = PlayerRootRegistry.FindFor(owner);
                        if (ownerRoot == null)
                            continue;
                        int granted = Mathf.Min(unitCount, remaining);
                        credit(ownerRoot, type, granted);
                        remaining -= granted;
                    }
                }
            }
        }

        // One carrier count for Produce (turn grants and IncomeFor), not a parallel enumeration.
        // ApBonus retains its separately labelled UI breakdown, but cannot define Produce rules.
        public static int CountInPlayAbilitySources(PlayerSetupData player, string ability)
        {
            if (player == null || string.IsNullOrEmpty(ability))
                return 0;
            int sources = 0;
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army.IsPrison)
                    continue;
                foreach (UnitData unit in army.Members)
                    if (unit.HasAbility(ability))
                        sources++;
            }
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner != player)
                    continue;
                if (building.HasAbility(ability))
                    sources++;
                foreach (FacilityData facility in building.FacilitySlots)
                    if (facility != null && facility.HasAbility(ability))
                        sources++;
            }
            return sources;
        }

        public static int IncomeFor(PlayerSetupData player, ResourceType type, HexMap map)
        {
            // The actual turn never credits a player without a registered account. Projection
            // observes the allocator's output but must not mutate a single PlayerRoot.
            PlayerRoot root = player != null ? PlayerRootRegistry.FindFor(player) : null;
            if (root == null || map == null)
                return 0;

            int total = 0;
            ForEachHexCollectionGrant(map, (recipient, grantType, amount) =>
            {
                if (object.ReferenceEquals(recipient, root) && grantType == type)
                    total += amount;
            }, onlyType: type);
            return total + CountInPlayAbilitySources(player, UnitAbilities.ProduceAbilityFor(type));
        }
    }
}
