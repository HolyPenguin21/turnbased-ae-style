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
            float minStrength = float.MaxValue;
            float composition = 0f;
            bool anyViable = false;

            foreach (int id in s.Meta.Keys.OrderBy(x => x))
            {
                ReorgContainer meta = s.Meta[id];
                List<ReorgUnit> units = s.Roster[id];

                if (ReorgViability.Capacity(units, meta.IsGarrison) < units.Count)
                    legality++;

                if (meta.IsGarrison)
                {
                    int nonHero = units.Count(u => !u.IsHero);
                    garrisonDeficit += Math.Max(0, meta.GarrisonNonHeroFloor - nonHero);
                    if (units.Count == 0 && meta.GarrisonNonHeroFloor > 0)
                        garrisonDeficit++;
                    continue;
                }

                // EmptyReusableArmy is a physical STARTING role. If the virtual plan fills that
                // shell, it must immediately be evaluated like every other mutable field army.
                if (!IsFieldContainer(meta) || !meta.CanChangeComposition || units.Count == 0)
                    continue;

                if (!meta.SingletonExempt && ReorgViability.IsSingletonShape(units))
                    singles++;

                if (ReorgViability.IsViable(units))
                {
                    anyViable = true;
                    float p = ReorgViability.EffectivePower(units);
                    if (p < minStrength)
                        minStrength = p;
                    composition += ReorgViability.CompositionQuality(units);
                }
                else if (!meta.SingletonExempt)
                {
                    nonViable++;
                }
            }

            float negMin = anyViable ? -minStrength : 0f;
            return new Outcome(garrisonDeficit, legality, singles, nonViable,
                negMin, -composition, s.Transfers.Count);
        }
    }
}
