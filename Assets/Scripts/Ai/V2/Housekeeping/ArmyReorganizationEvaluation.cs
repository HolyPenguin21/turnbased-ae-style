using System;
using System.Collections.Generic;
using System.Linq;

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
                        benchedCombatCapable += units.Count(u => u != null && u.IsHero
                            && u.HeroRole != HeroOperationalRole.SupportOperator
                            && GarrisonMayRelease(units, u, meta));
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
                    float p = ReorgViability.EffectivePower(units);
                    formationStrengths.Add(p);
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

            // Threat-agnostic concentration objective: strongest legal formation first, then the
            // next strongest from the remainder. The canonical AiPower model remains the only
            // strength/composition source; Housekeeping introduces no enemy-aware tactical scalar.
            formationStrengths.Sort((a, b) => b.CompareTo(a));
            return new Outcome(garrisonDeficit, legality, singles, nonViable, commandWaste,
                formationDefect, formationStrengths, -composition, s.Transfers.Count);
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
