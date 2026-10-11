#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiPowerOptimizationTests
    {
        private static AiPower.PowerUnit Body(float p, int range, params UnitTypeTag[] tags) =>
            new AiPower.PowerUnit(p, tags, range, false);
        private static AiPower.PowerUnit Hero(int cap, float p = 0) =>
            new AiPower.PowerUnit(p, new[] { UnitTypeTag.Hero, UnitTypeTag.Bio }, 1, true, cap);

        [Test]
        public void EqualCommanders_DoNotRepeatBodySearch_AndKeepFirstIdentity()
        {
            var pool = Enumerable.Range(0, 40).Select(i => Body(10 + i, 1 + i % 2,
                (UnitTypeTag)(i % 8))).Concat(Enumerable.Range(0, 12).Select(i => Hero(8))).ToList();
            int reads = 0;
            float actual = AiPower.PeakStackOf(Enumerable.Range(0, pool.Count).ToList(),
                i => { reads++; return pool[i]; }, out List<int> roster);
            float expected = LegacyPeak(pool, out List<int> expectedRoster);
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(roster, Is.EqualTo(expectedRoster));
            TestContext.WriteLine($"candidate reads: {reads}");
            Assert.That(reads, Is.LessThan(1000), "12 equal commanders must share one body search");
        }

        [Test]
        public void CompositionQuality_DoesNotAllocatePerCandidate()
        {
            var units = new[] { Body(12, 1, UnitTypeTag.Bio, UnitTypeTag.Infantry),
                Body(20, 3, UnitTypeTag.Armored), Hero(8) };
            var bytes = AllocatedBytes();
            for (int i = 0; i < 10; i++) AiPower.CompositionQuality(units);
            long before = bytes();
            for (int i = 0; i < 100; i++) AiPower.CompositionQuality(units);
            long allocated = bytes() - before;
            TestContext.WriteLine($"100 composition evaluations allocated: {allocated}");
            Assert.That(allocated, Is.LessThan(128));
        }

        internal static Func<long> AllocatedBytes()
        {
            var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            if (method == null) Assert.Ignore("Runtime does not expose per-thread allocation counters");
            return (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
        }

        [Test]
        public void OptimizedPeaksAndNestedCeilings_MatchLegacyIncludingRosterTies()
        {
            var rng = new Random(20261008);
            for (int sample = 0; sample < 500; sample++)
            {
                var map = new List<AiPower.PowerUnit>();
                var bodies = new List<AiPower.PowerUnit>();
                var heroes = new List<AiPower.PowerUnit>();
                for (int i = 0, n = rng.Next(20); i < n; i++)
                    map.Add(Body(rng.Next(80), rng.Next(1, 5), (UnitTypeTag)rng.Next(9),
                        (UnitTypeTag)rng.Next(9)));
                for (int i = 0, n = rng.Next(8); i < n; i++) map.Add(Hero(rng.Next(10), rng.Next(2)));
                for (int i = 0, n = rng.Next(12); i < n; i++)
                    bodies.Add(Body(rng.Next(80), rng.Next(1, 5), (UnitTypeTag)rng.Next(9)));
                for (int i = 0, n = rng.Next(8); i < n; i++) heroes.Add(Hero(rng.Next(10), rng.Next(2)));
                var withBodies = map.Concat(bodies).ToList();
                var full = withBodies.Concat(heroes).ToList();
                float field = LegacyPeak(map, out _);
                float unitsRaw = LegacyPeak(withBodies, out _);
                float totalRaw = LegacyPeak(full, out _);
                var actual = AiPower.NestedPotentials(map, bodies, heroes);
                Assert.That(actual.Field, Is.EqualTo(field), $"sample {sample}");
                Assert.That(actual.Units, Is.EqualTo(Math.Max(field, unitsRaw)));
                Assert.That(actual.Total, Is.EqualTo(Math.Max(actual.Units, totalRaw)));
                float expectedPeak = LegacyPeak(full, out List<int> expectedRoster);
                float actualPeak = AiPower.PeakStackOf(Enumerable.Range(0, full.Count).ToList(),
                    i => full[i], out List<int> actualRoster);
                Assert.That(actualPeak, Is.EqualTo(expectedPeak));
                Assert.That(actualRoster, Is.EqualTo(expectedRoster));
            }
        }

        [Test]
        public void CompositionQuality_UnknownEnumValuesRemainDistinct()
        {
            var units = new[] { Body(10, 1, (UnitTypeTag)65, (UnitTypeTag)(-1)),
                Body(10, 2, UnitTypeTag.Hero, (UnitTypeTag)65) };
            Assert.That(AiPower.CompositionQuality(units), Is.EqualTo(LegacyQuality(units)));
        }

        [Test]
        public void Peak_FractionalPowerAndUnknownTagsPreserveLegacyRounding()
        {
            var pool = new List<AiPower.PowerUnit> { Hero(5), Hero(5), Hero(8),
                Body(.1f, 1, UnitTypeTag.Bio), Body(.2f, 2, UnitTypeTag.Infantry),
                Body(.3f, 1, UnitTypeTag.Armored), Body(16777216f, 2, UnitTypeTag.Vehicle),
                Body(1f, 1, (UnitTypeTag)65), Body(.33333334f, 2, (UnitTypeTag)(-1)) };
            float expected = LegacyPeak(pool, out List<int> roster);
            float actual = AiPower.PeakStackOf(Enumerable.Range(0, pool.Count).ToList(),
                i => pool[i], out List<int> result);
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(result, Is.EqualTo(roster));
        }

        private static float LegacyQuality(IReadOnlyCollection<AiPower.PowerUnit> units)
        {
            if (units.Count == 0) return 0;
            var tags = new HashSet<UnitTypeTag>();
            bool front = false, reach = false;
            foreach (var u in units)
            {
                if (u.IsHero) continue;
                foreach (var t in u.Tags) if (t != UnitTypeTag.Hero) tags.Add(t);
                front |= u.Range <= 1; reach |= u.Range > 1;
            }
            float coverage = Math.Min(1f, tags.Count / (float)Math.Max(1, AiConfigV2.compoTypeCoverageTarget));
            float balance = front && reach ? 1 : front || reach ? .5f : 0;
            float weights = AiConfigV2.compoWeightTypeCoverage + AiConfigV2.compoWeightRangeBalance;
            return weights < .0001f ? 0 : (AiConfigV2.compoWeightTypeCoverage * coverage
                + AiConfigV2.compoWeightRangeBalance * balance) / weights;
        }

        private static float LegacyPower(IReadOnlyCollection<AiPower.PowerUnit> units) =>
            units.Sum(u => u.BasePower) * (AiConfigV2.compoFloor
                + (1 - AiConfigV2.compoFloor) * LegacyQuality(units));

        private static List<int> LegacyCompose(List<AiPower.PowerUnit> pool, List<int> bodies,
            int cap, int hero = -1)
        {
            var pick = hero < 0 ? new List<int>() : new List<int> { hero };
            var remaining = new List<int>(bodies);
            while (pick.Count < Math.Max(1, cap) && remaining.Count > 0)
            {
                int best = -1; float score = float.NegativeInfinity;
                for (int i = 0; i < remaining.Count; i++)
                {
                    var candidate = pick.Concat(new[] { remaining[i] }).Select(j => pool[j]).ToList();
                    float value = LegacyPower(candidate);
                    if (value > score) { score = value; best = i; }
                }
                if (best < 0) break;
                pick.Add(remaining[best]); remaining.RemoveAt(best);
            }
            return pick;
        }

        private static float LegacyPeak(List<AiPower.PowerUnit> pool, out List<int> roster)
        {
            var bodies = Enumerable.Range(0, pool.Count).Where(i => !pool[i].IsHero).ToList();
            roster = LegacyCompose(pool, bodies, 2);
            float best = LegacyPower(roster.Select(i => pool[i]).ToList());
            for (int i = 0; i < pool.Count; i++)
            {
                if (!pool[i].IsHero || pool[i].CommandRating < 1) continue;
                var candidate = LegacyCompose(pool, bodies, pool[i].CommandRating, i);
                float value = LegacyPower(candidate.Select(j => pool[j]).ToList());
                if (value > best) { best = value; roster = candidate; }
            }
            return best;
        }
    }
}
#endif
