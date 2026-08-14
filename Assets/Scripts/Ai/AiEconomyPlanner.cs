using System;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai
{
    // Экономика · Задача 1's own "who does this task" half — pure and read-only, same style as
    // AiScoutPlanner. Target-hex selection itself stays in AiGoalScorer.ScoreExpandEconomy (no
    // point re-deriving that scoring math here); this only picks the ACTOR and the resource TYPE
    // once a target hex is already chosen.
    public static class AiEconomyPlanner
    {
        // See AiConfig.maxBuildAttempts — moved there so it's tunable without recompiling.
        public static int MaxBuildAttempts => AiConfig.Current.maxBuildAttempts;

        // "герой с армией или без (разведчик)" — the project owner's own Задача 1 spec: any
        // hero-led army qualifies (bare, Recce-carrying, or already escorted — only being a hero
        // at all matters, since only a hero can build an extraction facility), nearest to
        // `targetHex` wins. Deliberately NOT restricted to idle armies — AiTurnController's own
        // preemption path (see its class comment) is what lets this reach into an army another
        // task already claimed, per the owner's own "нужно брать ближайшего героя ... его
        // текущее задание можно отложить" call.
        public static ArmyData FindNearestHero(PlayerSetupData player, HexCoord targetHex)
        {
            ArmyData best = null;
            int bestDistance = int.MaxValue;
            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                if (!AiArmyRoles.IsHeroLed(army))
                    continue;
                int distance = HexGridMath.Distance(army.Hex, targetHex);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = army;
                }
            }
            return best;
        }

        // Whichever ResourceType the hex's own bonus yields the most of — ties broken by enum
        // order, which is fine since a hex only ever carries one meaningful bonus in practice.
        // Null if the hex carries no bonus at all (shouldn't happen for a target ScoreExpandEconomy
        // itself picked, but callers shouldn't assume that without checking).
        public static ResourceType? DominantResourceType(HexCoord hex)
        {
            ResourceYields bonus = HexResourceBonusRegistry.GetBonus(hex);
            if (bonus == null)
                return null;

            ResourceType best = ResourceType.Human;
            int bestAmount = 0;
            foreach (ResourceType type in (ResourceType[])Enum.GetValues(typeof(ResourceType)))
            {
                int amount = bonus.Get(type);
                if (amount > bestAmount)
                {
                    bestAmount = amount;
                    best = type;
                }
            }
            return bestAmount > 0 ? best : (ResourceType?)null;
        }

        // Экономика · Задача 2's own "who does this task" half — a unit carrying the matching
        // CollectX ability (Game.Map.BuildingAbilities.CollectHuman/Energy/Materials/Tech, same
        // tag a Facility's own ability uses — see GameTurnController.CollectArmyIncomeAt, the
        // passive per-turn payout this task's whole point is to go stand on) already alone in its
        // own army — no detach needed, just walk it there. `pool` so an army another task already
        // claimed this step is never double-booked (see AiResourcePool's own class comment).
        public static ArmyData FindNearestSoloCollector(PlayerSetupData player, HexCoord targetHex, ResourceType type,
            AiResourcePool pool)
        {
            string ability = BuildingAbilities.CollectAbilityFor(type);
            ArmyData best = null;
            int bestDistance = int.MaxValue;
            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (army.IsGarrison || army.IsPrison || army.Members.Count != 1 || !army.Members[0].HasAbility(ability))
                    continue;
                int distance = HexGridMath.Distance(army.Hex, targetHex);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = army;
                }
            }
            return best;
        }

        // Экономика · Задача 2's own prerequisite step whenever no ready solo collector exists yet
        // (see FindNearestSoloCollector) — the project owner's own two ways to end up with an army
        // holding just the collector: two of the player's own armies already share a hex and
        // "reform" so the collector's escort moves OUT into the other one (MergeTarget != null,
        // Source itself becomes the solo army — nothing new spawns), or Source is sitting at the
        // player's own base/garrison hex and the collector alone splits OUT into a freshly created
        // army instead (MergeTarget == null — same shape as AiManagementPlanner.
        // FindGarrisonOverflow's own split, just for one specific unit rather than the overflow
        // tail). `pool` so a source mid-Разведка/Экономика this step is never touched.
        public readonly struct CollectorDetachPlan
        {
            public readonly ArmyData Source;
            public readonly UnitData Unit;
            public readonly ArmyData MergeTarget;

            public CollectorDetachPlan(ArmyData source, UnitData unit, ArmyData mergeTarget)
            {
                Source = source;
                Unit = unit;
                MergeTarget = mergeTarget;
            }
        }

        public static CollectorDetachPlan? FindCollectorDetachPlan(PlayerSetupData player, ResourceType type,
            HexCoord garrisonHex, AiResourcePool pool)
        {
            string ability = BuildingAbilities.CollectAbilityFor(type);
            foreach (ArmyData source in pool.AvailableArmies())
            {
                if (source.IsPrison)
                    continue;
                UnitData unit = source.Members.FirstOrDefault(m => m.HasAbility(ability));
                if (unit == null || source.Members.Count == 1)
                    continue; // already solo — FindNearestSoloCollector's own job, not a detach

                int escortCount = source.Members.Count - 1;
                ArmyData mergeTarget = ArmyRegistry.AllForOwner(player).FirstOrDefault(a =>
                    a != source && !a.IsPrison && a.Hex.Equals(source.Hex) && a.Capacity - a.Members.Count >= escortCount);
                if (mergeTarget != null)
                    return new CollectorDetachPlan(source, unit, mergeTarget);

                if (source.Hex.Equals(garrisonHex))
                    return new CollectorDetachPlan(source, unit, null);
            }
            return null;
        }
    }
}
