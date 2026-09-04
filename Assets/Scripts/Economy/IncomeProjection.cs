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
                    int buildingCollected = Mathf.Min(onHex.CollectedAmount(type), remaining);
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
            return total;
        }
    }
}
