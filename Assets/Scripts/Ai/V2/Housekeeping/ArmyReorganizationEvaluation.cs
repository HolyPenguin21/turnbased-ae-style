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
            int garrisonDeficit = 0;
            int legality = 0;
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
                            formationStrengths.Add(FormationReadiness(s, units));
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
                    formationStrengths.Add(FormationReadiness(s, units));
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
            return new Outcome(garrisonDeficit, legality, singles, nonViable, commandWaste,
                formationDefect, formationStrengths, -composition, s.Transfers.Count);
        }

        private static float FormationReadiness(VState state, IReadOnlyList<ReorgUnit> units)
        {
            if (state.ThreatBenchmarks.Count == 0)
                return ReorgViability.EffectivePower(units);

            var defenders = units
                .Where(u => u != null && !u.IsHero)
                .Select(u => u.CombatProfile)
                .ToList();
            if (defenders.Count == 0)
                return 0f;

            float worstPressure = 0f;
            foreach (ReorgThreatBenchmark threat in state.ThreatBenchmarks)
            {
                // A force that cannot penetrate every remaining defender cannot clear the army.
                // WorthIt's draw=0.5 convention is useful for generic comparison but must not turn
                // physical non-penetration into a fictitious 50% successful capture here.
                float success = WorthIt.CanDamageAll(threat.Members, defenders, state.HexDefenseBonus)
                    ? WorthIt.WinChance(threat.Members, defenders, state.HexDefenseBonus)
                    : 0f;
                float pressure = success / (1f + Math.Max(0, threat.EffectiveEta));
                if (pressure > worstPressure)
                    worstPressure = pressure;
            }
            return 1f - worstPressure;
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
