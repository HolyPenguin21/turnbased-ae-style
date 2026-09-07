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
    //    -> command/leadership defects -> strongest-first EffectiveArmyPower profile
    //    -> canonical AiPower composition -> operation count.
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
            // §9 — count of heroless/support-led viable field formations a benched combat hero
            // could take over. Ranked as a formation-quality term, below command-capacity waste
            // and above generic strength/composition.
            public readonly int FormationDefect;
            // Threat-agnostic local combat readiness. Occupied viable mutable field formations are
            // sorted strongest-first. We compare the first force, then the second, etc. This makes
            // 110+40 beat 70+60+20 without consulting any enemy/raid state. Empty reusable shells
            // are deliberately absent from the profile.
            public readonly IReadOnlyList<float> FormationStrengths;
            public readonly float NegComposition;
            public readonly int Operations;

            public Outcome(int gd, int legal, int singles, int nonViable, int commandWaste,
                int formationDefect, IReadOnlyList<float> formationStrengths, float negComp, int operations)
            {
                GarrisonDeficit = gd;
                Legality = legal;
                Singletons = singles;
                NonViable = nonViable;
                CommandCapacityWaste = commandWaste;
                FormationDefect = formationDefect;
                FormationStrengths = formationStrengths ?? System.Array.Empty<float>();
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
                c = FormationDefect.CompareTo(o.FormationDefect); if (c != 0) return c;

                c = CompareFormationProfiles(FormationStrengths, o.FormationStrengths);
                if (c != 0) return c;

                if (NegComposition < o.NegComposition - FloatEps) return -1;
                if (NegComposition > o.NegComposition + FloatEps) return 1;
                return Operations.CompareTo(o.Operations);
            }

            private static int CompareFormationProfiles(IReadOnlyList<float> a, IReadOnlyList<float> b)
            {
                int common = System.Math.Min(a?.Count ?? 0, b?.Count ?? 0);
                for (int i = 0; i < common; i++)
                {
                    float av = a[i];
                    float bv = b[i];
                    if (av > bv + FloatEps) return -1; // stronger is better
                    if (av < bv - FloatEps) return 1;
                }

                // A trailing weaker formation is not automatically better or worse merely because
                // it exists. Structural defects were already compared above; composition and move
                // count below decide otherwise-equal prefixes. This keeps empty reusable shells
                // neutral instead of forcing the planner to seed them.
                return 0;
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
