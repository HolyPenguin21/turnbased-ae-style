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

    // Pure sandstorm geometry and schedule: which hexes may host a storm, how a body of five
    // hexes ("the slug", tail -> head) is formed behind its birth hex and crawls 2-3 hexes per
    // full round with at most one 60-degree bend. No armies, renderer, Unity or global random.
    public static class SandstormRules
    {
        public const int BodyLength = 5;
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
        public sealed class Topology
        {
            private readonly HashSet<HexCoord> _all;
            private readonly HashSet<HexCoord> _interior;
            public IReadOnlyList<HexCoord> SortedInterior { get; }

            public Topology(IEnumerable<HexCoord> mapCoords)
            {
                _all = new HashSet<HexCoord>(mapCoords ?? Array.Empty<HexCoord>());
                _interior = new HashSet<HexCoord>();
                foreach (HexCoord hex in _all)
                {
                    bool inner = true;
                    for (int d = 0; d < 6 && inner; d++)
                        inner = _all.Contains(Step(hex, d));
                    if (inner) _interior.Add(hex);
                }
                var sorted = new List<HexCoord>(_interior);
                sorted.Sort(CompareCoords);
                SortedInterior = sorted;
            }

            public bool Contains(HexCoord hex) => _all.Contains(hex);
            public bool IsInterior(HexCoord hex) => _interior.Contains(hex);
        }

        // Stable coordinate order before any random pick (never dictionary/hash order).
        public static int CompareCoords(HexCoord a, HexCoord b) =>
            a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R);

        // ---- One round's advance ------------------------------------------------------------------
        // `Steps` head moves; the first `BendAt` keep the course, the rest turn once by 60 degrees
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

        // Head-only trace: every entered hex must be an interior map hex. A trail whose step
        // directions turn by at most 60 degrees at a time cannot revisit any of its last four
        // cells (closing a loop needs six steps of a hexagon), so the head trace alone decides
        // whether the five-hex body stays unique; TryAdvance still checks the whole body.
        private static bool TryTraceHead(Topology topology, HexCoord head, int course, AdvancePlan plan,
            out HexCoord newHead)
        {
            newHead = head;
            for (int i = 0; i < plan.Steps; i++)
            {
                newHead = Step(newHead, plan.DirectionOf(course, i));
                if (!topology.IsInterior(newHead)) return false;
            }
            return HexGridMath.Distance(head, newHead) == plan.Steps;
        }

        // The whole body after one plan: each step adds a hex in front of the head and drops the
        // tail. A hex may be entered while occupied only when it is the tail released by that
        // same step. Returns false (state untouched) on any violated rule.
        public static bool TryAdvance(Topology topology, IReadOnlyList<HexCoord> body, int course,
            AdvancePlan plan, out List<HexCoord> newBody, out int newCourse)
        {
            newBody = null;
            newCourse = course;
            if (topology == null || body == null || body.Count == 0) return false;
            var cells = new List<HexCoord>(body);
            HexCoord oldHead = cells[cells.Count - 1];
            for (int i = 0; i < plan.Steps; i++)
            {
                HexCoord next = Step(cells[cells.Count - 1], plan.DirectionOf(course, i));
                if (!topology.IsInterior(next)) return false;
                // The tail is released by this same step, so only it may be re-entered.
                cells.RemoveAt(0);
                if (cells.Contains(next)) return false;
                cells.Add(next);
            }
            if (HexGridMath.Distance(oldHead, cells[cells.Count - 1]) != plan.Steps
                || !IsValidBody(topology, cells)) return false;
            newBody = cells;
            newCourse = plan.FinalCourse(course);
            return true;
        }

        // The first update after birth: the four earlier body hexes lie straight behind the
        // birth hex along the course; the birth hex stays the head (no extra head moves).
        public static bool TryExpand(Topology topology, HexCoord head, int course, out List<HexCoord> body)
        {
            body = new List<HexCoord>(BodyLength);
            int back = Rotate(course, 3);
            for (int i = BodyLength - 1; i >= 1; i--)
            {
                HexCoord cell = head;
                for (int s = 0; s < i; s++) cell = Step(cell, back);
                if (!topology.IsInterior(cell)) { body = null; return false; }
                body.Add(cell);
            }
            if (!topology.IsInterior(head)) { body = null; return false; }
            body.Add(head);
            return true;
        }

        // Memo for one evaluation pass (one tick): (head, course, updates left) -> continuation
        // exists. Pure: never draws random numbers, never reserves future hexes.
        public sealed class ContinuationMemo
        {
            internal readonly Dictionary<(HexCoord, int, int), bool> Known = new Dictionary<(HexCoord, int, int), bool>();
            public int Evaluations { get; internal set; }
        }

        // Can a storm whose head stands on `head` facing `course` still perform `remaining`
        // advances (2-3 steps, at most one 60-degree bend each)? Other storms are ignored: later
        // overlaps are allowed.
        public static bool HasContinuation(Topology topology, HexCoord head, int course, int remaining,
            ContinuationMemo memo)
        {
            if (remaining <= 0) return true;
            var key = (head, course, remaining);
            if (memo != null && memo.Known.TryGetValue(key, out bool cached)) return cached;
            if (memo != null) memo.Evaluations++;
            bool found = false;
            foreach (int steps in new[] { ShortAdvance, LongAdvance })
            {
                foreach (AdvancePlan plan in PlansFor(steps))
                {
                    if (!TryTraceHead(topology, head, course, plan, out HexCoord newHead)) continue;
                    if (HasContinuation(topology, newHead, plan.FinalCourse(course), remaining - 1, memo))
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

        // A birth is legal on `hex` with `course` when the body can form behind it and all
        // five later active updates have a legal continuation from there.
        public static bool IsValidBirth(Topology topology, HexCoord hex, int course, ContinuationMemo memo) =>
            topology != null && topology.IsInterior(hex) && TryExpand(topology, hex, course, out _)
            && HasContinuation(topology, hex, course, LastActiveAge, memo);

        // Courses (ascending) a birth on `hex` may take.
        public static List<int> BirthCourses(Topology topology, HexCoord hex, ContinuationMemo memo)
        {
            var courses = new List<int>();
            for (int d = 0; d < 6; d++)
                if (IsValidBirth(topology, hex, d, memo)) courses.Add(d);
            return courses;
        }

        // Body invariants: 1 or 5 unique interior hexes, consecutive ones adjacent.
        public static bool IsValidBody(Topology topology, IReadOnlyList<HexCoord> body)
        {
            if (topology == null || body == null || (body.Count != 1 && body.Count != BodyLength)) return false;
            var seen = new HashSet<HexCoord>();
            for (int i = 0; i < body.Count; i++)
            {
                if (!topology.IsInterior(body[i]) || !seen.Add(body[i])) return false;
                if (i > 0 && HexGridMath.Distance(body[i - 1], body[i]) != 1) return false;
            }
            return true;
        }

        // One round's advance for a body with `remainingAfter` updates still to come after this
        // one. Desired length 2 or 3 with equal probability; the other length when the desired
        // one has no legal plan. Within a length, the straight plan wins whenever it keeps a
        // continuation; otherwise a uniform pick among the legal bends. False = no legal plan:
        // the caller keeps the body in place (diagnosable exception, never the routine result).
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
                    if (!HasContinuation(topology, candidate[candidate.Count - 1], candidateCourse, remainingAfter, memo))
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
