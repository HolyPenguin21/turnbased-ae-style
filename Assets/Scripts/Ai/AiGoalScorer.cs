using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
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
        public static float IncomeBehindBonus(PlayerSetupData actor)
        {
            PlayerRoot ownRoot = PlayerRootRegistry.FindFor(actor);
            if (ownRoot == null || GameSession.Players == null)
                return 0f;

            List<int> otherTotals = GameSession.Players
                .Where(p => p != actor && !p.IsEliminated)
                .Select(TotalIncome)
                .ToList();
            if (otherTotals.Count == 0)
                return 0f;

            float avgOther = (float)otherTotals.Average();
            int ownTotal = TotalIncome(actor);
            if (avgOther <= ownTotal)
                return 0f;

            float deficitRatio = Mathf.Clamp01((avgOther - ownTotal) / Mathf.Max(1f, avgOther));
            return deficitRatio * 20f; // same order of magnitude as the per-hex proximity term above
        }

        private static readonly ResourceType[] AllResourceTypes =
        {
            ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech,
        };

        // Per-turn income across all 4 resource types, mirroring (in simplified form) what
        // GameTurnController.CollectResourceIncome actually pays out each turn — structural
        // income from every owned building (BuildingData.CollectedAmount, same uncapped-by-hex-
        // yield simplification BuildFacilityTask.HasIncomeSource already leans on) plus 1 per
        // resource type for every unit anywhere on a hex whose bonus actually yields that type and
        // that carries the matching CollectX ability (a ResourcesScrap collector actually parked
        // on its hex — see CollectArmyIncomeAt). Deliberately NOT a full re-derivation (no enemy-
        // contest check, no per-hex remaining-yield cap split across owners) — same "cheat slice"
        // spirit as this method's own caller, just measuring the right thing (rate, not stock).
        private static int TotalIncome(PlayerSetupData player)
        {
            int total = BuildingRegistry.AllBuildings().Where(b => b.Owner == player)
                .Sum(b => AllResourceTypes.Sum(t => b.CollectedAmount(t)));

            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                ResourceYields bonus = HexResourceBonusRegistry.GetBonus(army.Hex);
                if (bonus == null)
                    continue;
                foreach (ResourceType type in AllResourceTypes)
                    if (bonus.Get(type) > 0)
                        total += army.Members.Count(u => u.HasAbility(UnitAbilities.CollectAbilityFor(type)));
            }
            return total;
        }
    }
}
