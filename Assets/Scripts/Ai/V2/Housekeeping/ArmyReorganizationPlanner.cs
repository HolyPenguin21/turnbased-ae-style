using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ARMY REORGANIZATION PLANNER — the day pass of Housekeeping (the task layout).
    // ===========================================================================================
    //  Pure and deterministic: a snapshot of one hex in, a plan out; HousekeepingExecutor applies
    //  it. Every accepted candidate strictly improves this lexicographic tuple:
    //    garrison floor -> legality -> operator exposure -> redundant hero / preparation slot waste
    //    -> singletons -> non-viable formations -> commander mismatch -> formation defect
    //    -> strongest-first field formation power profile -> composition -> operation count.
    //  Zero AP. The defence of the hex is the night fold's (HousekeepingNightFold), which reuses
    //  this planner's virtual roster (VState).
    // ===========================================================================================
    public static partial class ArmyReorganizationPlanner
    {
        internal const float FloatEps = 0.001f;

        private readonly struct Outcome
        {
            public readonly int GarrisonDeficit;
            public readonly int Legality;
            // Operators and Support heroes exposed outside the local garrison or a hex bench.
            public readonly int OperatorExposure;
            // Heroes that only occupy a fighter slot (preparation host and free field armies), plus
            // the preparation host's roster gap. Ranked above singletons and viability: a hero
            // leaving for a free local container is the fix, never a reason to keep the fist short.
            public readonly int PreparationSlotWaste;
            public readonly int Singletons;
            public readonly int NonViable;
            // Multi-hero containers not led by their best legal commander (CommanderMismatch).
            public readonly int CommanderMismatch;
            // Heroless or Support-led viable field formations a benched combat hero could lead.
            public readonly int FormationDefect;
            // The field formations' power, strongest first. Higher is better.
            public readonly IReadOnlyList<float> FormationStrengths;
            public readonly float NegComposition;
            public readonly int Operations;

            public Outcome(int gd, int legal, int operatorExposure, int preparationSlotWaste,
                int singles, int nonViable, int commandWaste, int formationDefect,
                IReadOnlyList<float> formationStrengths, float negComp, int operations)
            {
                GarrisonDeficit = gd;
                Legality = legal;
                OperatorExposure = operatorExposure;
                PreparationSlotWaste = preparationSlotWaste;
                Singletons = singles;
                NonViable = nonViable;
                CommanderMismatch = commandWaste;
                FormationDefect = formationDefect;
                FormationStrengths = formationStrengths ?? System.Array.Empty<float>();
                NegComposition = negComp;
                Operations = operations;
            }

            public int CompareTo(in Outcome o)
            {
                int c = GarrisonDeficit.CompareTo(o.GarrisonDeficit); if (c != 0) return c;
                c = Legality.CompareTo(o.Legality); if (c != 0) return c;
                c = OperatorExposure.CompareTo(o.OperatorExposure); if (c != 0) return c;
                c = PreparationSlotWaste.CompareTo(o.PreparationSlotWaste); if (c != 0) return c;
                c = Singletons.CompareTo(o.Singletons); if (c != 0) return c;
                c = NonViable.CompareTo(o.NonViable); if (c != 0) return c;
                c = CommanderMismatch.CompareTo(o.CommanderMismatch); if (c != 0) return c;
                c = FormationDefect.CompareTo(o.FormationDefect); if (c != 0) return c;

                c = CompareFormationProfiles(FormationStrengths, o.FormationStrengths);
                if (c != 0) return c;

                if (NegComposition < o.NegComposition - FloatEps) return -1;
                if (NegComposition > o.NegComposition + FloatEps) return 1;
                return Operations.CompareTo(o.Operations);
            }

            // Stronger first wins; a trailing weaker formation stays neutral, so an empty reusable
            // shell never creates pressure to seed itself.
            private static int CompareFormationProfiles(IReadOnlyList<float> a, IReadOnlyList<float> b)
            {
                int common = System.Math.Min(a.Count, b.Count);
                for (int i = 0; i < common; i++)
                {
                    if (a[i] > b[i] + FloatEps) return -1;
                    if (a[i] < b[i] - FloatEps) return 1;
                }
                return 0;
            }
        }

        // The virtual roster of one hex: every container's members after the operations so far.
        internal sealed class VState
        {
            public readonly Dictionary<int, List<ReorgUnit>> Roster = new Dictionary<int, List<ReorgUnit>>();
            public readonly Dictionary<int, ReorgContainer> Meta = new Dictionary<int, ReorgContainer>();
            public readonly List<PlannedTransfer> Transfers = new List<PlannedTransfer>();
            // A unit moves at most once per plan (the executor rejects a second move).
            public readonly HashSet<int> MovedUnitKeys = new HashSet<int>();
            public readonly List<ReorgThreatBenchmark> ThreatBenchmarks = new List<ReorgThreatBenchmark>();

            public VState Clone()
            {
                var v = new VState();
                v.ThreatBenchmarks.AddRange(ThreatBenchmarks);
                foreach (var kv in Meta) v.Meta[kv.Key] = kv.Value;
                foreach (var kv in Roster) v.Roster[kv.Key] = new List<ReorgUnit>(kv.Value);
                v.Transfers.AddRange(Transfers);
                v.MovedUnitKeys.UnionWith(MovedUnitKeys);
                return v;
            }
        }

        internal static VState StateOf(LocalForceGroup group)
        {
            var state = new VState();
            state.ThreatBenchmarks.AddRange(group.ThreatBenchmarks);
            foreach (ReorgContainer c in group.Containers)
            {
                state.Meta[c.ArmyId] = c;
                state.Roster[c.ArmyId] = new List<ReorgUnit>(c.Units);
            }
            return state;
        }

        // The day plan of one hex, giving frames back between candidates (FramePacer). The group is
        // a snapshot, so a split plan equals an unsplit one.
        internal static System.Collections.IEnumerator PlanRoutine(LocalForceGroup group, FramePacer pacer,
            ReorganizationPlan plan)
        {
            plan.Q = group?.Q ?? 0;
            plan.R = group?.R ?? 0;
            if (group == null)
                yield break;

            VState state = StateOf(group);
            Outcome initial = Evaluate(state);
            plan.BeforeFormationStrengths.AddRange(initial.FormationStrengths);

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
                    if (pacer != null && pacer.Expired)
                        yield return pacer.Checkpoint();
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

            Outcome final = Evaluate(state);
            plan.AfterFormationStrengths.AddRange(final.FormationStrengths);
            plan.Transfers.AddRange(state.Transfers);
            foreach (var kv in state.Roster)
                plan.ExpectedMembership[kv.Key] = kv.Value.Select(u => u.Key).ToList();
        }

        private static bool IsFieldContainer(ReorgContainer c) => c != null && c.IsMutableGround;
    }
}
