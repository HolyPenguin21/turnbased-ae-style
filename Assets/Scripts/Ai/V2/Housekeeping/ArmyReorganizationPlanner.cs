using System.Collections.Generic;
using System.Linq;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ARMY REORGANIZATION PLANNER  (Strategy V2 — HousekeepingManager, step 8C)
    // ===========================================================================================
    //  PURE, DETERMINISTIC. Snapshot first, plan second, mutate later in HousekeepingExecutor.
    //  Every accepted candidate strictly improves this lexicographic tuple:
    //    garrison safety -> legality -> operator exposure -> redundant hero/preparation slot waste
    //    -> singleton count -> non-viable count
    //    -> command/leadership defects -> strongest-first EffectiveArmyPower profile
    //    -> canonical AiPower composition -> operation count.
    //  Candidate generation is zero-AP: housekeeping holds no AP of its own.
    // ===========================================================================================
    public static partial class ArmyReorganizationPlanner
    {
        private const float FloatEps = 0.001f;

        private readonly struct Outcome
        {
            public readonly int GarrisonDeficit;
            public readonly int Legality;
            // Required Research/Production operators exposed outside the local garrison.
            public readonly int OperatorExposure;
            // Heroes that only occupy a fighter slot: preparation's contract and free field
            // armies. Above the singleton/viability terms: a lone hero leaving the host for
            // a free local container is the fix, never a reason to keep the fist short of slots.
            public readonly int PreparationSlotWaste;
            public readonly int Singletons;
            public readonly int NonViable;
            // §7 — number of mutable multi-hero containers not led by the one commander
            // evaluation's best legal hero (HeroRoleEvaluator, see CommanderMismatch). Ranked as a
            // formation-quality term: above generic strength/composition, below the hard
            // legality/singleton/viability invariants.
            public readonly int CommanderMismatch;
            // §9 — count of heroless/support-led viable field formations a benched combat hero
            // could take over. Ranked as a formation-quality term, below the commander mismatch
            // and above generic strength/composition.
            public readonly int FormationDefect;
            // Defensive-readiness profile. With deployed enemies it is threat-first:
            // worst threat's whole-hex hold (every formation in fight order, wounds carried —
            // WorthIt.EstimateHexControl), its first real contact defender, its second line, then the next
            // threat, each using group-local ETA. Without a benchmark it falls back to the
            // strongest-first canonical AiPower formation profile. Higher is always better.
            private readonly LazyProfile _formationStrengths;
            public IReadOnlyList<float> FormationStrengths => _formationStrengths.Value;
            public readonly float NegComposition;
            public readonly int Operations;

            public Outcome(int gd, int legal, int operatorExposure, int preparationSlotWaste,
                int singles, int nonViable, int commandWaste, int formationDefect,
                LazyProfile formationStrengths, float negComp, int operations)
            {
                GarrisonDeficit = gd;
                Legality = legal;
                OperatorExposure = operatorExposure;
                PreparationSlotWaste = preparationSlotWaste;
                Singletons = singles;
                NonViable = nonViable;
                CommanderMismatch = commandWaste;
                FormationDefect = formationDefect;
                _formationStrengths = formationStrengths ?? new LazyProfile(System.Array.Empty<float>());
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

                c = CompareFormationProfiles(FormationStrengths, o.FormationStrengths,
                    _formationStrengths.Tolerances, o._formationStrengths.Tolerances);
                if (c != 0) return c;

                if (NegComposition < o.NegComposition - FloatEps) return -1;
                if (NegComposition > o.NegComposition + FloatEps) return 1;
                return Operations.CompareTo(o.Operations);
            }

            private static int CompareFormationProfiles(IReadOnlyList<float> a, IReadOnlyList<float> b,
                IReadOnlyList<float> toleranceA, IReadOnlyList<float> toleranceB)
            {
                int common = System.Math.Min(a?.Count ?? 0, b?.Count ?? 0);
                for (int i = 0; i < common; i++)
                {
                    float av = a[i];
                    float bv = b[i];
                    float eps = System.Math.Max(FloatEps, System.Math.Max(
                        toleranceA != null && i < toleranceA.Count ? toleranceA[i] : 0f,
                        toleranceB != null && i < toleranceB.Count ? toleranceB[i] : 0f));
                    if (av > bv + eps) return -1; // stronger is better
                    if (av < bv - eps) return 1;
                }

                // Threat-backed profiles have a fixed three entries per contactable threat;
                // the variable-length case is the no-threat AiPower fallback. There, a trailing
                // weaker formation remains neutral so an empty reusable shell never creates
                // pressure to seed itself.
                return 0;
            }
        }

        // The readiness profile of one Outcome, built at most once and only when asked
        // (the threat-backed profile runs WorthIt.EstimateHexControl per threat).
        private sealed class LazyProfile
        {
            // Per-entry equality tolerance (null or 0 = FloatEps): the whole-hex readiness is a
            // Monte Carlo estimate, so a difference inside its noise is no improvement.
            internal readonly struct Data
            {
                public readonly IReadOnlyList<float> Values;
                public readonly IReadOnlyList<float> Tolerances;
                public Data(IReadOnlyList<float> values, IReadOnlyList<float> tolerances)
                {
                    Values = values ?? System.Array.Empty<float>();
                    Tolerances = tolerances;
                }
            }

            private System.Func<Data> _build;
            private Data? _data;

            public LazyProfile(IReadOnlyList<float> value) => _data = new Data(value, null);
            public LazyProfile(System.Func<Data> build) => _build = build;

            private Data Get()
            {
                if (!_data.HasValue)
                {
                    _data = _build();
                    _build = null;
                }
                return _data.Value;
            }

            public IReadOnlyList<float> Value => Get().Values;
            public IReadOnlyList<float> Tolerances => Get().Tolerances;
        }

        private sealed class VState
        {
            public readonly Dictionary<int, List<ReorgUnit>> Roster = new Dictionary<int, List<ReorgUnit>>();
            public readonly Dictionary<int, ReorgContainer> Meta = new Dictionary<int, ReorgContainer>();
            public readonly List<PlannedTransfer> Transfers = new List<PlannedTransfer>();
            // Executor rejects a unit moved twice in one plan; planning owns the same invariant.
            public readonly HashSet<int> MovedUnitKeys = new HashSet<int>();
            public readonly List<ReorgThreatBenchmark> ThreatBenchmarks = new List<ReorgThreatBenchmark>();
            public float HexDefenseBonus;
            public Game.Combat.BattleEnvironment Environment;

            public VState Clone()
            {
                var v = new VState { HexDefenseBonus = HexDefenseBonus, Environment = Environment };
                v.ThreatBenchmarks.AddRange(ThreatBenchmarks);
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
            // No pacer: the routine never yields, so one MoveNext loop runs it to the end.
            System.Collections.IEnumerator steps = PlanRoutine(group, null, plan);
            while (steps.MoveNext()) { }
            return plan;
        }

        // The same plan, giving frames back between candidate evaluations (FramePacer): under a
        // deployed enemy every candidate runs the whole-hex simulation, so one hex can take far
        // more than a frame (Kryll (0,4) T31: ~140 candidates). Nothing mutates the world between
        // AI frames, and the group is a snapshot, so a split plan equals an unsplit one.
        internal static System.Collections.IEnumerator PlanRoutine(LocalForceGroup group, FramePacer pacer,
            ReorganizationPlan plan)
        {
            plan.Q = group?.Q ?? 0;
            plan.R = group?.R ?? 0;
            if (group == null || !group.WorthPlanning())
            {
                if (group != null)
                    foreach (ReorgContainer c in group.Containers)
                        plan.ExpectedMembership[c.ArmyId] = c.Units.Select(u => u.Key).ToList();
                yield break;
            }

            var state = new VState { HexDefenseBonus = group.HexDefenseBonus, Environment = group.Environment };
            state.ThreatBenchmarks.AddRange(group.ThreatBenchmarks);
            foreach (ReorgContainer c in group.Containers)
            {
                state.Meta[c.ArmyId] = c;
                state.Roster[c.ArmyId] = new List<ReorgUnit>(c.Units);
            }

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
