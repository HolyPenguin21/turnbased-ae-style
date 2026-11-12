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

    // Pure sandstorm geometry and schedule: which hexes may host a storm, how it lives and how it
    // changes shape. A storm is a centre plus some of its six neighbours (always connected
    // through the centre). Its size follows its age (it gathers strength, peaks and fades), the
    // centre drifts one hex per round (turning by at most 60 degrees) or lulls in place, and the
    // shape is redrawn every round: hexes the storm already covers tend to linger, the rest is
    // filled from the front. Two storms never share a hex. No armies, renderer, Unity or global
    // random.
    public static class SandstormRules
    {
        // Hexes (centre included) by age: birth on one hex, peak at age 3, fading out at age 5.
        private static readonly int[] SizeByAge = { 1, 3, 5, 6, 5, 3 };
        // Ages 0..5: the birth round plus five later rounds — six active rounds in all.
        public const int LastActiveAge = 5;
        // Empty rounds after a storm disappears before the slot may give birth again.
        public const int CooldownRounds = 2;
        // Chance (percent) that a storm lulls in place for a round instead of drifting.
        public const int LullPercent = 20;
        // Chance (percent) that a hex the storm covered stays stormy, when it still touches the centre.
        public const int LingerPercent = 60;

        public static int SizeAt(int age) =>
            age >= 0 && age <= LastActiveAge ? SizeByAge[age] : throw new ArgumentOutOfRangeException(nameof(age));

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

        // 0..3: how many 60-degree turns separate two directions.
        public static int TurnDistance(int a, int b)
        {
            int d = ((a - b) % 6 + 6) % 6;
            return d > 3 ? 6 - d : d;
        }

        public static int DirectionBetween(HexCoord from, HexCoord to)
        {
            for (int d = 0; d < 6; d++)
                if (Step(from, d).Equals(to)) return d;
            return -1;
        }

        // ---- Where a storm may be ---------------------------------------------------------------
        // The outer playable row is found from the real topology: a map hex missing any of its
        // six neighbours. On the standard hexagonal field this is exactly the ring FieldRadius.
        // A storm centre must keep its whole ring off that row (CanHoldSpot).
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

        // ---- Body ---------------------------------------------------------------------------------
        // Canonical body: the covered neighbours in ascending direction order, the centre last
        // (Body[Count - 1] is always the centre).
        public static List<HexCoord> BodyOf(HexCoord centre, IEnumerable<int> directions)
        {
            var dirs = new List<int>(directions ?? Array.Empty<int>());
            dirs.Sort();
            var cells = new List<HexCoord>(dirs.Count + 1);
            foreach (int d in dirs) cells.Add(Step(centre, d));
            cells.Add(centre);
            return cells;
        }

        public static HexCoord CentreOf(IReadOnlyList<HexCoord> body) => body[body.Count - 1];

        // Body invariants: the centre holds a whole ring inside the interior; the other cells are
        // distinct neighbours of the centre in ascending direction order; 1 hex at birth, then
        // 1..SizeAt(age) hexes (fewer only where another storm stood in the way).
        public static bool IsValidBody(Topology topology, IReadOnlyList<HexCoord> body, int age)
        {
            if (topology == null || body == null || body.Count == 0 || age < 0 || age > LastActiveAge) return false;
            if (age == 0 ? body.Count != 1 : body.Count > SizeAt(age)) return false;
            HexCoord centre = CentreOf(body);
            if (!topology.CanHoldSpot(centre)) return false;
            int previous = -1;
            for (int i = 0; i < body.Count - 1; i++)
            {
                int d = DirectionBetween(centre, body[i]);
                if (d <= previous) return false;
                previous = d;
            }
            return true;
        }

        // ---- One round's drift --------------------------------------------------------------------
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

        // A drifting round moves the centre by exactly one hex: straight, or turned by 60 degrees.
        public const int AdvanceSteps = 1;

        // Fixed order: the straight plan first, then both 60-degree turns.
        public static readonly IReadOnlyList<AdvancePlan> Plans = new[]
        {
            new AdvancePlan(AdvanceSteps, AdvanceSteps, 0),
            new AdvancePlan(AdvanceSteps, 0, -1),
            new AdvancePlan(AdvanceSteps, 0, +1),
        };

        // Centre trace: every centre passed must hold a whole ring inside the interior, so the
        // storm never reaches the outer row.
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

        // Memo for one evaluation pass (one tick): (centre, course, updates left) -> continuation
        // exists. Pure: never draws random numbers, never reserves future hexes.
        public sealed class ContinuationMemo
        {
            internal readonly Dictionary<(HexCoord, int, int), bool> Known = new Dictionary<(HexCoord, int, int), bool>();
            public int Evaluations { get; internal set; }
        }

        // Can a storm whose centre stands on `centre` facing `course` still drift `remaining`
        // rounds in a row (one hex each, turning by at most 60 degrees)? Lulls only make this
        // easier; other storms are ignored here (they are avoided round by round).
        public static bool HasContinuation(Topology topology, HexCoord centre, int course, int remaining,
            ContinuationMemo memo)
        {
            if (remaining <= 0) return true;
            var key = (centre, course, remaining);
            if (memo != null && memo.Known.TryGetValue(key, out bool cached)) return cached;
            if (memo != null) memo.Evaluations++;
            bool found = false;
            foreach (AdvancePlan plan in Plans)
            {
                if (!TryTraceCentre(topology, centre, course, plan, out HexCoord next)) continue;
                if (HasContinuation(topology, next, plan.FinalCourse(course), remaining - 1, memo))
                {
                    found = true;
                    break;
                }
            }
            if (memo != null) memo.Known[key] = found;
            return found;
        }

        // A birth is legal on `hex` with `course` when a whole ring fits around it and all five
        // later active updates could drift from there.
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

        public enum DriftKind
        {
            Moved = 1,
            // A random lull: the storm stays and only reshapes.
            Lull = 2,
            // No legal drift (edge or dead end): stays this round — diagnosable, never routine.
            Stuck = 3,
            // Every otherwise legal drift enters another storm's centre: waits for it to move on.
            Blocked = 4,
        }

        // The centre's move for one round with `remainingAfter` updates still to come. A lull
        // with LullPercent chance; otherwise the straight step whenever it keeps a continuation,
        // else a uniform pick among the legal turns. `blocked` = other storms' centres, which a
        // centre never enters. Random draws: the lull roll, then (only for a turn) the pick.
        public static DriftKind ChooseDrift(Topology topology, HexCoord centre, int course, int remainingAfter,
            ICollection<HexCoord> blocked, ref SandstormRandom random, ContinuationMemo memo,
            out HexCoord newCentre, out int newCourse)
        {
            newCentre = centre;
            newCourse = course;
            if (random.Next(100) < LullPercent) return DriftKind.Lull;
            var legal = new List<(AdvancePlan Plan, HexCoord Centre)>();
            bool blockedByStorm = false;
            foreach (AdvancePlan plan in Plans)
            {
                if (!TryTraceCentre(topology, centre, course, plan, out HexCoord next)) continue;
                if (!HasContinuation(topology, next, plan.FinalCourse(course), remainingAfter, memo)) continue;
                if (blocked != null && blocked.Contains(next)) { blockedByStorm = true; continue; }
                legal.Add((plan, next));
            }
            if (legal.Count == 0) return blockedByStorm ? DriftKind.Blocked : DriftKind.Stuck;
            int pick = legal[0].Plan.Straight ? 0 : random.Next(legal.Count);
            newCentre = legal[pick].Centre;
            newCourse = legal[pick].Plan.FinalCourse(course);
            return DriftKind.Moved;
        }

        // The storm's shape this round: `size` hexes around `centre`. Hexes of `previous` that
        // still touch the centre linger with LingerPercent chance each (a random few drop when
        // the storm is shrinking); the rest is filled nearest to the course first (ahead, then
        // the front sides, the rear sides, behind), ties in random order. `blocked` hexes (other
        // storms) are never taken, so the storm may come out smaller next to another one.
        public static List<HexCoord> ChooseShape(HexCoord centre, int course, int size,
            IReadOnlyList<HexCoord> previous, ICollection<HexCoord> blocked, ref SandstormRandom random)
        {
            int want = Math.Max(0, size - 1);
            var chosen = new List<int>(want);
            if (previous != null)
                foreach (HexCoord cell in previous)
                {
                    int d = DirectionBetween(centre, cell);
                    if (d < 0 || (blocked != null && blocked.Contains(cell))) continue;
                    if (random.Next(100) < LingerPercent) chosen.Add(d);
                }
            while (chosen.Count > want) chosen.RemoveAt(random.Next(chosen.Count));
            for (int turns = 0; turns <= 3 && chosen.Count < want; turns++)
            {
                var ring = new List<int>(2);
                for (int d = 0; d < 6; d++)
                {
                    if (TurnDistance(d, course) != turns || chosen.Contains(d)) continue;
                    if (blocked != null && blocked.Contains(Step(centre, d))) continue;
                    ring.Add(d);
                }
                while (ring.Count > 0 && chosen.Count < want)
                {
                    int k = random.Next(ring.Count);
                    chosen.Add(ring[k]);
                    ring.RemoveAt(k);
                }
            }
            return BodyOf(centre, chosen);
        }
    }
}
