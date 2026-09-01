using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ARMY REORGANIZATION PLANNER  (Strategy V2 — HousekeepingManager, step 8C)
    // ===========================================================================================
    //  PURE, DETERMINISTIC. Snapshot first, plan second, mutate later in HousekeepingExecutor.
    //  Every accepted candidate strictly improves this lexicographic tuple:
    //    garrison safety -> legality -> singleton count -> non-viable count
    //    -> weakest viable EffectiveArmyPower -> canonical AiPower composition -> operation count.
    //  Candidate generation is zero-AP only while housekeepingApReserve == 0.
    // ===========================================================================================
    public static partial class ArmyReorganizationPlanner
    {
        private const float FloatEps = 0.001f;

        private readonly struct Outcome
        {
            public readonly int GarrisonDeficit;
            public readonly int Legality;
            public readonly int Singletons;
            public readonly int NonViable;
            // §7 — sum over mutable multi-hero containers of (best hero CommandRating − first hero
            // CommandRating). Zero when every such container is already led by its highest-capacity
            // hero. Ranked as a formation-quality term: above generic strength/composition, below
            // the hard legality/singleton/viability invariants.
            public readonly int CommandCapacityWaste;
            public readonly float NegMinStrength;
            public readonly float NegComposition;
            public readonly int Operations;

            public Outcome(int gd, int legal, int singles, int nonViable, int commandWaste,
                float negMin, float negComp, int operations)
            {
                GarrisonDeficit = gd;
                Legality = legal;
                Singletons = singles;
                NonViable = nonViable;
                CommandCapacityWaste = commandWaste;
                NegMinStrength = negMin;
                NegComposition = negComp;
                Operations = operations;
            }

            public int CompareTo(in Outcome o)
            {
                int c = GarrisonDeficit.CompareTo(o.GarrisonDeficit); if (c != 0) return c;
                c = Legality.CompareTo(o.Legality); if (c != 0) return c;
                c = Singletons.CompareTo(o.Singletons); if (c != 0) return c;
                c = NonViable.CompareTo(o.NonViable); if (c != 0) return c;
                c = CommandCapacityWaste.CompareTo(o.CommandCapacityWaste); if (c != 0) return c;
                if (NegMinStrength < o.NegMinStrength - FloatEps) return -1;
                if (NegMinStrength > o.NegMinStrength + FloatEps) return 1;
                if (NegComposition < o.NegComposition - FloatEps) return -1;
                if (NegComposition > o.NegComposition + FloatEps) return 1;
                return Operations.CompareTo(o.Operations);
            }
        }

        private sealed class VState
        {
            public readonly Dictionary<int, List<ReorgUnit>> Roster = new Dictionary<int, List<ReorgUnit>>();
            public readonly Dictionary<int, ReorgContainer> Meta = new Dictionary<int, ReorgContainer>();
            public readonly List<PlannedTransfer> Transfers = new List<PlannedTransfer>();
            // Executor rejects a unit moved twice in one plan; planning owns the same invariant.
            public readonly HashSet<int> MovedUnitKeys = new HashSet<int>();

            public VState Clone()
            {
                var v = new VState();
                foreach (var kv in Meta) v.Meta[kv.Key] = kv.Value;
                foreach (var kv in Roster) v.Roster[kv.Key] = new List<ReorgUnit>(kv.Value);
                v.Transfers.AddRange(Transfers);
                v.MovedUnitKeys.UnionWith(MovedUnitKeys);
                return v;
            }
        }

        public static ReorganizationPlan Plan(LocalForceGroup group)
        {
            var plan = new ReorganizationPlan { Q = group?.Q ?? 0, R = group?.R ?? 0 };
            if (group == null || !group.WorthPlanning())
            {
                if (group != null)
                    foreach (ReorgContainer c in group.Containers)
                        plan.ExpectedMembership[c.ArmyId] = c.Units.Select(u => u.Key).ToList();
                return plan;
            }

            var state = new VState();
            foreach (ReorgContainer c in group.Containers)
            {
                state.Meta[c.ArmyId] = c;
                state.Roster[c.ArmyId] = new List<ReorgUnit>(c.Units);
            }

            int iterations = 0;
            while (iterations++ < AiConfigV2.housekeepingMaxPlanIterationsPerHex)
            {
                Outcome current = Evaluate(state);
                VState best = null;
                Outcome bestOutcome = current;

                foreach (VState candidate in EnumerateCandidates(state))
                {
                    Outcome o = Evaluate(candidate);
                    if (o.Legality > 0)
                        continue;
                    if (o.CompareTo(bestOutcome) < 0)
                    {
                        best = candidate;
                        bestOutcome = o;
                    }
                }

                if (best == null)
                    break;

                state.Roster.Clear();
                foreach (var kv in best.Roster) state.Roster[kv.Key] = kv.Value;
                state.Transfers.Clear();
                state.Transfers.AddRange(best.Transfers);
                state.MovedUnitKeys.Clear();
                state.MovedUnitKeys.UnionWith(best.MovedUnitKeys);
            }

            plan.Transfers.AddRange(state.Transfers);
            foreach (var kv in state.Roster)
                plan.ExpectedMembership[kv.Key] = kv.Value.Select(u => u.Key).ToList();
            return plan;
        }

        private static bool IsFieldContainer(ReorgContainer c) => c != null && c.IsMutableGround;
    }
}
