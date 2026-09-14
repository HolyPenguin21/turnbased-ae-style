using System;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;

namespace Game.Ai.V2
{
    public static partial class ArmyReorganizationPlanner
    {
        private static Outcome Evaluate(VState s)
        {
            Dictionary<int, float> pressureByContainer = ComputeContactPressureByContainer(s);

            int garrisonDeficit = 0;
            int legality = 0;
            int operatorExposure = 0;
            int singles = 0;
            int nonViable = 0;
            int commandWaste = 0;
            float composition = 0f;
            var formationStrengths = new List<float>();

            // §9 — formation-leadership bookkeeping. A "benched" hero sits in a garrison or a lone
            // hero container; a heroless viable field formation is only a fixable defect when such
            // a hero exists in the same group.
            int benchedCombatCapable = 0;   // CombatLeader or Flexible, available to lead
            int unledViableFields = 0;
            int supportLedWhileCombatBenched = 0;

            foreach (int id in s.Meta.Keys.OrderBy(x => x))
            {
                ReorgContainer meta = s.Meta[id];
                List<ReorgUnit> units = s.Roster[id];

                if (ReorgViability.Capacity(units, meta.IsGarrison) < units.Count)
                    legality++;
                if (!meta.IsGarrison)
                    operatorExposure += units.Count(u => u != null && u.IsDevelopmentOperator);

                // §7 — commander order is a formation-quality concern for every reorderable
                // container, garrison included. Accumulate before the garrison early-out.
                if (meta.CanChangeComposition)
                    commandWaste += CommandCapacityWaste(units);

                if (meta.IsGarrison)
                {
                    int nonHero = units.Count(u => !u.IsHero);
                    garrisonDeficit += Math.Max(0, meta.GarrisonNonHeroFloor - nonHero);
                    if (units.Count == 0 && meta.GarrisonNonHeroFloor > 0)
                        garrisonDeficit++;
                    if (meta.CanChangeComposition)
                    {
                        benchedCombatCapable += units.Count(u => u != null && u.IsHero
                            && u.HeroRole != HeroOperationalRole.SupportOperator
                            && GarrisonMayRelease(units, u, meta));
                        if (nonHero > 0)
                        {
                            formationStrengths.Add(FormationReadiness(s, units, pressureByContainer, id));
                            composition += ReorgViability.CompositionQuality(units);
                        }
                    }
                    continue;
                }

                // EmptyReusableArmy is neutral: once filled it is evaluated as a normal field
                // formation, but while empty it contributes neither a defect nor a zero-strength
                // entry that would pressure the planner to seed it.
                if (!IsFieldContainer(meta) || !meta.CanChangeComposition || units.Count == 0)
                    continue;

                if (!meta.SingletonExempt && ReorgViability.IsSingletonShape(units))
                    singles++;

                bool loneHero = units.Count == 1 && units[0].IsHero;
                if (loneHero && units[0].HeroRole != HeroOperationalRole.SupportOperator)
                    benchedCombatCapable++;

                if (ReorgViability.IsViable(units))
                {
                    formationStrengths.Add(FormationReadiness(s, units, pressureByContainer, id));
                    composition += ReorgViability.CompositionQuality(units);

                    ReorgUnit commander = units.FirstOrDefault(u => u.IsHero);
                    if (commander == null && units.Count(u => !u.IsHero) >= 2)
                        unledViableFields++;
                    else if (commander != null && commander.HeroRole == HeroOperationalRole.SupportOperator)
                        supportLedWhileCombatBenched++;
                }
                else if (!meta.SingletonExempt)
                {
                    nonViable++;
                }
            }

            // Only an unled/support-led formation that a benched combat hero could actually take
            // over is a defect the planner can act on.
            int formationDefect = benchedCombatCapable > 0
                ? Math.Min(unledViableFields + supportLedWhileCombatBenched, benchedCombatCapable)
                : 0;

            // The first value represents the best defender the contact system can expose, then
            // the next layer from the remainder. Every measurable reduction in enemy success can
            // win; there is deliberately no artificial viability gate.
            formationStrengths.Sort((a, b) => b.CompareTo(a));
            return new Outcome(garrisonDeficit, legality, operatorExposure, singles, nonViable,
                commandWaste, formationDefect, formationStrengths, -composition, s.Transfers.Count);
        }

        // §Task4 — per-container readiness against the enemy actually threatening IT, not a
        // scalar worst-case folded across the whole group. `pressureByContainer` is precomputed
        // once per Evaluate() call by ComputeContactPressureByContainer below.
        private static float FormationReadiness(VState state, IReadOnlyList<ReorgUnit> units,
            IReadOnlyDictionary<int, float> pressureByContainer, int containerId)
        {
            if (state.ThreatBenchmarks.Count == 0)
                return ReorgViability.EffectivePower(units);

            if (!units.Any(u => u != null && !u.IsHero))
                return 0f;

            float pressure = pressureByContainer.TryGetValue(containerId, out float p) ? p : 0f;
            return 1f - pressure;
        }

        // Mirrors BattleInitiator's own attacker-specific contact selection instead of scoring
        // every formation against every enemy's worst case independently (the old scalar-collapse
        // bug — two enemy compositions that are each weak against a DIFFERENT one of our formations
        // could get folded/redistributed as if neither mattered). For each enemy benchmark: rank
        // every contact-eligible container by the SAME canonical hardness comparator BattleInitiator
        // uses (BattleInitiator.CompareDefenderHardness), restricted to the non-hero members that
        // specific enemy can actually see (ReorgThreatBenchmark.TargetableUnitKeys). Only the first
        // (real contact) and second (the follow-up contact if the first were cleared) ranked
        // containers absorb this enemy's pressure — every other formation is correctly unaffected
        // by an enemy that would never actually reach it first.
        private static Dictionary<int, float> ComputeContactPressureByContainer(VState state)
        {
            var pressureByContainer = new Dictionary<int, float>();
            if (state.ThreatBenchmarks.Count == 0)
                return pressureByContainer;

            foreach (ReorgThreatBenchmark threat in state.ThreatBenchmarks)
            {
                var ranked = new List<(int containerId, WorthIt.BattleEstimate estimate, List<WorthIt.DefenderProfile> defenders)>();
                foreach (var kv in state.Meta)
                {
                    ReorgContainer meta = kv.Value;
                    if (meta.Role == ReorgPhysicalRole.Aviation || meta.Role == ReorgPhysicalRole.SpecialExcludedContainer)
                        continue;
                    if (!state.Roster.TryGetValue(kv.Key, out List<ReorgUnit> units))
                        continue;

                    var defenders = units.Where(u => u != null && !u.IsHero
                            && threat.TargetableUnitKeys.Contains(u.Key))
                        .Select(u => u.CombatProfile)
                        .ToList();
                    if (defenders.Count == 0)
                        continue; // fully hidden from (or has no combat member visible to) this enemy

                    WorthIt.BattleEstimate estimate = WorthIt.Estimate(threat.Members, defenders, state.HexDefenseBonus);
                    ranked.Add((kv.Key, estimate, defenders));
                }
                if (ranked.Count == 0)
                    continue;

                ranked.Sort((a, b) =>
                    Game.Combat.BattleInitiator.CompareDefenderHardness(a.estimate, a.containerId, b.estimate, b.containerId));

                for (int i = 0; i < ranked.Count && i < 2; i++)
                {
                    (int containerId, WorthIt.BattleEstimate estimate, List<WorthIt.DefenderProfile> defenders) = ranked[i];
                    // Same non-penetration guard the old scalar read already applied: a force that
                    // cannot damage every remaining defender cannot actually clear this container.
                    float success = WorthIt.CanDamageAll(threat.Members, defenders, state.HexDefenseBonus)
                        ? estimate.WinChance
                        : 0f;
                    float pressure = (success / (1f + Math.Max(0, threat.EtaToGroup))) * (i == 0 ? 1f : 0.5f);
                    if (!pressureByContainer.TryGetValue(containerId, out float existing) || pressure > existing)
                        pressureByContainer[containerId] = pressure;
                }
            }
            return pressureByContainer;
        }

        // (best hero CommandRating − current commander's CommandRating), clamped at 0. Roster
        // order here mirrors the live ArmyData.Members order (Analyzer preserves it; a planned
        // commander reorder rewrites it), so units[firstHero] is the container's real commander
        // and ReorgViability.Capacity already reads its CommandRating.
        private static int CommandCapacityWaste(List<ReorgUnit> units)
        {
            int bestCr = 0;
            int firstCr = -1;
            foreach (ReorgUnit u in units)
            {
                if (u == null || !u.IsHero)
                    continue;
                if (firstCr < 0)
                    firstCr = u.CommandRating;
                if (u.CommandRating > bestCr)
                    bestCr = u.CommandRating;
            }
            return firstCr < 0 ? 0 : Math.Max(0, bestCr - firstCr);
        }
    }
}
