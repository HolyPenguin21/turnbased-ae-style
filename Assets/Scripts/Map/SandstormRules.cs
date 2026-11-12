using System;
using System.Collections.Generic;
using Game.HexGrid;

namespace Game.Map
{
    // SplitMix64: a tiny serializable stream, identical on every platform, one per storm slot.
    // Never UnityEngine.Random — a preview or forecast must not shift the map, a battle or the
    // other storm.
    public struct SandstormRandom
    {
        public ulong State;

        public SandstormRandom(ulong state) { State = state; }

        public static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public ulong NextULong()
        {
            ulong value = Mix(State);
            State += 0x9E3779B97F4A7C15UL;
            return value;
        }

        public int Next(int exclusiveMax)
        {
            if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
            return (int)(NextULong() % (ulong)exclusiveMax);
        }

        // Each slot draws from its own stream: simulating one storm never consumes another's.
        public static SandstormRandom ForSlot(long weatherSeed, int slotId) =>
            new SandstormRandom(Mix(unchecked((ulong)weatherSeed) ^ Mix(unchecked((ulong)(slotId + 1) * 0xD1B54A32D192ED03UL))));
    }

    // Pure sandstorm geometry and schedule: which hexes may host a storm, how a round spot is
    // born at half size (its centre and the trailing half of its ring), grows to the full spot
    // (the centre and its whole ring) and drifts 2-3 hexes per full round with at most one
    // 60-degree bend. No armies, renderer, Unity or global random.
    public static class SandstormRules
    {
        // The full spot: the centre and its six neighbours.
        public const int SpotSize = 7;
        // The birth spot: the centre and the three ring hexes behind it (course+2..course+4).
        public const int BirthSize = 4;
        // Ages 0..5: the birth round plus five later rounds — six active rounds in all.
        public const int LastActiveAge = 5;
        // Empty rounds after a storm disappears before the slot may give birth again.
        public const int CooldownRounds = 2;
        public const int ShortAdvance = 2;
        public const int LongAdvance = 3;

        public static int ConfiguredCount(MatchWeatherProfile profile)
        {
            switch (profile)
            {
                case MatchWeatherProfile.Clear: return 0;
                case MatchWeatherProfile.OneSandstorm: return 1;
                case MatchWeatherProfile.TwoSandstorms: return 2;
                default: throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown weather profile.");
            }
        }

        public static HexCoord Step(HexCoord hex, int direction)
        {
            (int dq, int dr) = HexGridMath.NeighborDirectionsByEdge[direction];
            return new HexCoord(hex.Q + dq, hex.R + dr);
        }

        public static int Rotate(int direction, int delta) => ((direction + delta) % 6 + 6) % 6;

        public static int DirectionBetween(HexCoord from, HexCoord to)
        {
            for (int d = 0; d < 6; d++)
                if (Step(from, d).Equals(to)) return d;
            return -1;
        }

        // ---- Where a storm may be ---------------------------------------------------------------
        // The outer playable row is found from the real topology: a map hex missing any of its
        // six neighbours. On the standard hexagonal field this is exactly the ring FieldRadius.
        // A spot centre must keep its whole ring off that row (CanHoldSpot).
        public sealed class Topology
        {
            private readonly HashSet<HexCoord> _all;
            private readonly HashSet<HexCoord> _interior;
            private readonly HashSet<HexCoord> _spotCentres;
            public IReadOnlyList<HexCoord> SortedInterior { get; }

            public Topology(IEnumerable<HexCoord> mapCoords)
            {
                _all = new HashSet<HexCoord>(mapCoords ?? Array.Empty<HexCoord>());
                _interior = new HashSet<HexCoord>();
                foreach (HexCoord hex in _all)
                    if (AllNeighbours(_all, hex)) _interior.Add(hex);
                _spotCentres = new HashSet<HexCoord>();
                foreach (HexCoord hex in _interior)
                    if (AllNeighbours(_interior, hex)) _spotCentres.Add(hex);
                var sorted = new List<HexCoord>(_interior);
                sorted.Sort(CompareCoords);
                SortedInterior = sorted;
            }

            private static bool AllNeighbours(HashSet<HexCoord> set, HexCoord hex)
            {
                for (int d = 0; d < 6; d++)
                    if (!set.Contains(Step(hex, d))) return false;
                return true;
            }

            public bool Contains(HexCoord hex) => _all.Contains(hex);
            public bool IsInterior(HexCoord hex) => _interior.Contains(hex);
            public bool CanHoldSpot(HexCoord centre) => _spotCentres.Contains(centre);
        }

        // Stable coordinate order before any random pick (never dictionary/hash order).
        public static int CompareCoords(HexCoord a, HexCoord b) =>
            a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R);

        // ---- Spot shape -------------------------------------------------------------------------
        // Ring hexes first, the centre last (Body[Count - 1] is always the centre). Full: ring
        // directions 0..5. Birth: the trailing half course+2..course+4, so the spot grows
        // forward on its first update.
        public static List<HexCoord> SpotCells(HexCoord centre, int course, bool grown)
        {
            var cells = new List<HexCoord>(grown ? SpotSize : BirthSize);
            if (grown)
                for (int d = 0; d < 6; d++) cells.Add(Step(centre, d));
            else
                for (int k = 2; k <= 4; k++) cells.Add(Step(centre, Rotate(course, k)));
            cells.Add(centre);
            return cells;
        }

        public static HexCoord CentreOf(IReadOnlyList<HexCoord> body) => body[body.Count - 1];

        // ---- One round's advance ------------------------------------------------------------------
        // `Steps` centre moves; the first `BendAt` keep the course, the rest turn once by 60 degrees
        // (BendSign -1/+1) and stay on the new course. Straight: BendAt == Steps, BendSign 0.
        public readonly struct AdvancePlan
        {
            public readonly int Steps;
            public readonly int BendAt;
            public readonly int BendSign;

            public AdvancePlan(int steps, int bendAt, int bendSign)
            {
                Steps = steps;
                BendAt = bendAt;
                BendSign = bendSign;
            }

            public bool Straight => BendSign == 0;
            public int DirectionOf(int course, int step) => step < BendAt ? course : Rotate(course, BendSign);
            public int FinalCourse(int course) => Straight ? course : Rotate(course, BendSign);
            public override string ToString() => Straight ? $"{Steps} straight" : $"{Steps} bend@{BendAt}{(BendSign > 0 ? "+" : "-")}60";
        }

        private static readonly AdvancePlan[][] Plans = { BuildPlans(ShortAdvance), BuildPlans(LongAdvance) };

        // Fixed order: the straight plan first, then every single-bend plan.
        private static AdvancePlan[] BuildPlans(int steps)
        {
            var plans = new List<AdvancePlan> { new AdvancePlan(steps, steps, 0) };
            for (int bendAt = 0; bendAt < steps; bendAt++)
            {
                plans.Add(new AdvancePlan(steps, bendAt, -1));
                plans.Add(new AdvancePlan(steps, bendAt, +1));
            }
            return plans.ToArray();
        }

        public static IReadOnlyList<AdvancePlan> PlansFor(int steps) =>
            steps == ShortAdvance ? Plans[0] : steps == LongAdvance ? Plans[1]
            : throw new ArgumentOutOfRangeException(nameof(steps));

        // Centre trace: every centre passed must hold the whole spot inside the interior, so the
        // spot never sweeps over the outer row.
        private static bool TryTraceCentre(Topology topology, HexCoord centre, int course, AdvancePlan plan,
            out HexCoord newCentre)
        {
            newCentre = centre;
            for (int i = 0; i < plan.Steps; i++)
            {
                newCentre = Step(newCentre, plan.DirectionOf(course, i));
                if (!topology.CanHoldSpot(newCentre)) return false;
            }
            return HexGridMath.Distance(centre, newCentre) == plan.Steps;
        }

        // The full spot after one plan: the centre drifts and the whole spot moves with it.
        // Returns false (outputs unused) on any violated rule.
        public static bool TryAdvance(Topology topology, IReadOnlyList<HexCoord> body, int course,
            AdvancePlan plan, out List<HexCoord> newBody, out int newCourse)
        {
            newBody = null;
            newCourse = course;
            if (topology == null || body == null || body.Count == 0) return false;
            if (!TryTraceCentre(topology, CentreOf(body), course, plan, out HexCoord centre)) return false;
            newCourse = plan.FinalCourse(course);
            newBody = SpotCells(centre, newCourse, grown: true);
            return true;
        }

        // The first update after birth: the half spot grows to the full spot around the same
        // centre (that update's advance follows).
        public static bool TryExpand(Topology topology, HexCoord centre, int course, out List<HexCoord> body)
        {
            body = topology != null && topology.CanHoldSpot(centre) ? SpotCells(centre, course, grown: true) : null;
            return body != null;
        }

        // Memo for one evaluation pass (one tick): (centre, course, updates left) -> continuation
        // exists. Pure: never draws random numbers, never reserves future hexes.
        public sealed class ContinuationMemo
        {
            internal readonly Dictionary<(HexCoord, int, int), bool> Known = new Dictionary<(HexCoord, int, int), bool>();
            public int Evaluations { get; internal set; }
        }

        // Can a storm whose centre stands on `centre` facing `course` still perform `remaining`
        // advances (2-3 steps, at most one 60-degree bend each)? Other storms are ignored: later
        // overlaps are allowed.
        public static bool HasContinuation(Topology topology, HexCoord centre, int course, int remaining,
            ContinuationMemo memo)
        {
            if (remaining <= 0) return true;
            var key = (centre, course, remaining);
            if (memo != null && memo.Known.TryGetValue(key, out bool cached)) return cached;
            if (memo != null) memo.Evaluations++;
            bool found = false;
            foreach (int steps in new[] { ShortAdvance, LongAdvance })
            {
                foreach (AdvancePlan plan in PlansFor(steps))
                {
                    if (!TryTraceCentre(topology, centre, course, plan, out HexCoord next)) continue;
                    if (HasContinuation(topology, next, plan.FinalCourse(course), remaining - 1, memo))
                    {
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }
            if (memo != null) memo.Known[key] = found;
            return found;
        }

        // A birth is legal on `hex` with `course` when the full spot fits around it and all
        // five later active updates have a legal continuation from there.
        public static bool IsValidBirth(Topology topology, HexCoord hex, int course, ContinuationMemo memo) =>
            topology != null && topology.CanHoldSpot(hex)
            && HasContinuation(topology, hex, course, LastActiveAge, memo);

        // Courses (ascending) a birth on `hex` may take.
        public static List<int> BirthCourses(Topology topology, HexCoord hex, ContinuationMemo memo)
        {
            var courses = new List<int>();
            for (int d = 0; d < 6; d++)
                if (IsValidBirth(topology, hex, d, memo)) courses.Add(d);
            return courses;
        }

        // Body invariants: exactly the birth spot (grown == false) or the full spot around the
        // last cell, in SpotCells order, with the full spot inside the interior.
        public static bool IsValidBody(Topology topology, IReadOnlyList<HexCoord> body, int course, bool grown)
        {
            if (topology == null || body == null || body.Count == 0) return false;
            HexCoord centre = CentreOf(body);
            if (!topology.CanHoldSpot(centre)) return false;
            List<HexCoord> expected = SpotCells(centre, course, grown);
            if (expected.Count != body.Count) return false;
            for (int i = 0; i < body.Count; i++)
                if (!expected[i].Equals(body[i])) return false;
            return true;
        }

        // One round's advance for a spot with `remainingAfter` updates still to come after this
        // one. Desired length 2 or 3 with equal probability; the other length when the desired
        // one has no legal plan. Within a length, the straight plan wins whenever it keeps a
        // continuation; otherwise a uniform pick among the legal bends. False = no legal plan:
        // the caller keeps the spot in place (diagnosable exception, never the routine result).
        public static bool TryPlanAdvance(Topology topology, IReadOnlyList<HexCoord> body, int course,
            int remainingAfter, ref SandstormRandom random, ContinuationMemo memo,
            out List<HexCoord> newBody, out int newCourse, out AdvancePlan chosen)
        {
            newBody = null;
            newCourse = course;
            chosen = default;
            int desired = random.Next(2) == 0 ? ShortAdvance : LongAdvance;
            int[] order = desired == ShortAdvance ? new[] { ShortAdvance, LongAdvance } : new[] { LongAdvance, ShortAdvance };
            foreach (int steps in order)
            {
                var legal = new List<(AdvancePlan Plan, List<HexCoord> Body, int Course)>();
                foreach (AdvancePlan plan in PlansFor(steps))
                {
                    if (!TryAdvance(topology, body, course, plan, out List<HexCoord> candidate, out int candidateCourse))
                        continue;
                    if (!HasContinuation(topology, CentreOf(candidate), candidateCourse, remainingAfter, memo))
                        continue;
                    legal.Add((plan, candidate, candidateCourse));
                }
                if (legal.Count == 0) continue;
                int pick = legal[0].Plan.Straight ? 0 : random.Next(legal.Count);
                chosen = legal[pick].Plan;
                newBody = legal[pick].Body;
                newCourse = legal[pick].Course;
                return true;
            }
            return false;
        }
    }
}
