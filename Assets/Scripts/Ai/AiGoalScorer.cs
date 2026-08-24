using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Ai
{
    // Shared per-actor Economy scoring primitive (IncomeBehindBonus), read by both
    // BuildFacilityTask/ResourcesScrapTask's own ScoreHex. Pure read-only logic, same
    // stateless-static style as BattleAi — never mutates ArmyRegistry/BuildingRegistry, only reads
    // them.
    //
    // Used to also hold a competitive PickBest(actor)-across-AiGoalKind layer (ExpandEconomy vs.
    // Defend/Destroy/Hunt) — removed 2026: Defend/Destroy/Hunt never got an AiTaskCategory/
    // planner built for them (see AiTaskCategory's own class comment), and the one surviving
    // AiGoalKind (ExpandEconomy) was only ever reachable through a Debug.Log call
    // (GameTurnController's old LogAiGoal) that duplicated — and could disagree with — the score
    // AiEconomyPlanner.TryStartEconomyCandidates computes for real. Also used to hold
    // ScoreExpandEconomyHex — a per-hex proximity-to-nearest-own-hex term BuildFacilityTask/
    // ResourcesScrapTask's own ScoreHex both called into — removed 2026-08-17: the project owner's
    // own call that Экономика's degradation-by-distance should read the same way Разведка/Агрессия
    // already do (citadelDistancePenalty, straight distance from the citadel specifically), not a
    // separate "nearest owned hex, hard scan-radius cutoff" formula; BuildFacilityTask now computes
    // that itself, and ResourcesScrapTask dropped distance from its own scoring entirely (just
    // ability + known hex, no distance term at all — see its own class comment). Re-add a
    // goal-level competitive scorer once Оборона/Атака get real task categories to compete against
    // (AI_ARCHITECTURE.html roadmap Phase 2), rather than as a log-only stub.
    public static class AiGoalScorer
    {
        // The doc's own one documented cheat slice for 2.2 Экономика: "сравнивает свой income с
        // остальными игроками (без учёта видимости) и старается не отставать" — compares the
        // actor's own per-turn INCOME (not current stockpile — see TotalIncome's own comment on
        // why a stockpile comparison used to misfire) against the rest of the field (ignoring
        // visibility on purpose, unlike everything else in this file) and boosts Экономика's
        // urgency the further behind the actor is. Project owner's own 2026-08-23 correction — a
        // player sitting on a large one-off resource windfall (an event bonus, a raided stockpile)
        // used to read as "not behind" here even with zero actual production, so Экономика never
        // felt urgent for them despite having no real income at all.
        public static float IncomeBehindBonus(PlayerSetupData actor, HexMap map)
        {
            PlayerRoot ownRoot = PlayerRootRegistry.FindFor(actor);
            if (ownRoot == null || GameSession.Players == null)
                return 0f;

            List<int> otherTotals = GameSession.Players
                .Where(p => p != actor && !p.IsEliminated)
                .Select(p => TotalIncome(p, map))
                .ToList();
            if (otherTotals.Count == 0)
                return 0f;

            float avgOther = (float)otherTotals.Average();
            int ownTotal = TotalIncome(actor, map);
            if (avgOther <= ownTotal)
                return 0f;

            float deficitRatio = Mathf.Clamp01((avgOther - ownTotal) / Mathf.Max(1f, avgOther));
            return deficitRatio * 20f; // same order of magnitude as the per-hex proximity term above
        }

        private static readonly ResourceType[] AllResourceTypes =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        // Per-turn income for a single resource type — an exact mirror of GameTurnController.
        // CollectResourceIncome/CollectArmyIncomeAt's own per-hex algorithm (2026-08-24 follow-up
        // fix, "IncomeFor не всегда равен реальному доходу", project owner's own report), not the
        // earlier simplified version this replaces (uncapped BuildingData.CollectedAmount plus a
        // flat +1/collector, no yield cap, no building-first priority, no contest check — which
        // could read a facility/collector as producing more than the hex's own actual yield ever
        // allows, or credit a collector sharing a hex with an engageable enemy that the real game
        // would give nothing that turn). Same three-step real algorithm, applied read-only and
        // filtered to just `player`'s own share instead of crediting every owner:
        //   1. hexYield = HexResourceCalculator.GetEffectiveYield(terrain, hex bonus) — real yield.
        //   2. the hex's own building (if any) takes the first cut, capped at both its own
        //      CollectedAmount(type) and whatever the hex actually yields.
        //   3. whatever's left goes to armies on the hex with a matching CollectX unit, grouped by
        //      owner, but ONLY an owner with no engageable enemy also on the hex (BattleInitiator.
        //      FindEnemyAt) — same "no stealth yet" contest rule the real turn processor enforces.
        // `map` is required (terrain lookup) — GameSession's own single shared HexMap, not
        // per-player, so the same instance works for computing any player's own income.
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

        private static int TotalIncome(PlayerSetupData player, HexMap map) => AllResourceTypes.Sum(t => IncomeFor(player, t, map));

        // "экономика не теряет приоритет после насыщения" fix (2026-08-24, project owner's own
        // report) — true once EVERY one of the 4 resource types clears `perTypeThreshold` on its
        // own (not just the combined total — see IncomeFor's own comment), the local signal
        // BuildFacilityTask.TravelScore/AiEconomyPlanner.TryStartEconomyCandidates use to shave
        // their own travel-tier score down once Economy has genuinely stopped being urgent, rather
        // than always starting from the same high base regardless of how saturated the player
        // already is. Deliberately local/instantaneous (no Strategic Assessment, no memory of past
        // turns) — the moment any one type dips back below threshold (a facility lost, a collector
        // pulled off), this flips back on its own next call.
        public static bool HasMatureEconomy(PlayerSetupData player, int perTypeThreshold, HexMap map) =>
            AllResourceTypes.All(t => IncomeFor(player, t, map) >= perTypeThreshold);
    }
}
