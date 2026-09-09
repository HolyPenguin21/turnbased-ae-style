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
    // Physical rule: what a player's per-turn resource income actually is, computed read-only
    // from the live map/buildings/armies. AI-neutral — it answers "how much does this hex layout
    // produce for this player", never "should the AI care". Extracted from the former
    // Game.Ai.AiGoalScorer (ARCH-01) so both the AI and any gameplay code share one algorithm.
    //
    // IncomeFor is an exact mirror of GameTurnController.CollectResourceIncome/
    // CollectArmyIncomeAt's own per-hex algorithm, filtered to a single player's share:
    //   1. hexYield = HexResourceCalculator.GetEffectiveYield(terrain, hex bonus) — real yield.
    //   2. the hex's own building (if any) takes the first cut, capped at both its own
    //      CollectedAmount(type) and whatever the hex actually yields.
    //   3. whatever's left goes to armies on the hex with a matching CollectX unit, grouped by
    //      owner, but ONLY an owner with no engageable enemy also on the hex (BattleInitiator.
    //      FindEnemyAt) — same "no stealth yet" contest rule the real turn processor enforces.
    // `map` is GameSession's own single shared HexMap (terrain lookup); the same instance works
    // for computing any player's income.
    public static class IncomeProjection
    {
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

        public static int IncomeFor(PlayerSetupData player, ResourceType type, HexMap map)
        {
            if (player == null || map == null)
                return 0;

            string ability = UnitAbilities.CollectAbilityFor(type);
            var hexes = new HashSet<HexCoord>();
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
                hexes.Add(building.Hex);
            foreach (HexCoord hex in ArmyRegistry.AllOccupiedHexes())
                hexes.Add(hex);

            int total = 0;
            foreach (HexCoord hex in hexes)
            {
                if (!map.TryGetTerrainAt(hex, out TerrainTypeEntry entry))
                    continue;

                ResourceYields hexYield = HexResourceCalculator.GetEffectiveYield(entry, HexResourceBonusRegistry.GetBonus(hex));
                int hexAmount = hexYield.Get(type);
                if (hexAmount <= 0)
                    continue;

                int remaining = hexAmount;
                BuildingData onHex = BuildingRegistry.FindAt(hex);
                if (onHex != null && onHex.Owner != null)
                {
                    int buildingCollected = BuildingCollection(
                        hexAmount, onHex.CollectedAmount(type));
                    if (buildingCollected > 0)
                    {
                        if (onHex.Owner == player)
                            total += buildingCollected;
                        remaining -= buildingCollected;
                    }
                }
                if (remaining <= 0)
                    continue;

                foreach (IGrouping<PlayerSetupData, ArmyData> ownerArmies in ArmyRegistry.AllAt(hex).GroupBy(a => a.Owner))
                {
                    if (remaining <= 0)
                        break;
                    PlayerSetupData owner = ownerArmies.Key;
                    if (owner == null)
                        continue;
                    if (BattleInitiator.FindEnemyAt(hex, owner) != null)
                        continue; // contested — the real turn processor grants nothing here either
                    int unitCount = ownerArmies.Sum(a => a.Members.Count(u => u.HasAbility(ability)));
                    if (unitCount <= 0)
                        continue;
                    int granted = Mathf.Min(unitCount, remaining);
                    if (owner == player)
                        total += granted;
                    remaining -= granted;
                }
            }

            // UnitAbilities.Produce* — flat +1 per in-play carrier (non-Prison army member, owned
            // Base, or Facility), a mirror of GameTurnController.GrantProduceResourceIncome. Kept
            // separate from the hex-yield collection above, exactly as that grant is separate from
            // CollectResourceIncome, so this projection still matches the real per-turn number.
            string produceAbility = UnitAbilities.ProduceAbilityFor(type);
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (army.IsPrison)
                    continue;
                total += army.Members.Count(u => u.HasAbility(produceAbility));
            }
            foreach (BuildingData building in BuildingRegistry.AllBuildings())
            {
                if (building.Owner != player)
                    continue;
                if (building.HasAbility(produceAbility))
                    total++;
                foreach (FacilityData facility in building.FacilitySlots)
                    if (facility != null && facility.HasAbility(produceAbility))
                        total++;
            }
            return total;
        }
    }
}
