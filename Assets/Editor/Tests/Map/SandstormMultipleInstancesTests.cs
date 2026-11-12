#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Profiles with 0/1/2 storms (spec 3.5): independent slots and random streams, atomic union
    // snapshot, two storms never share a hex (so the map never draws two storm sprites on one
    // hex), a storm leaving clears only the hexes nobody else covers.
    public sealed class SandstormMultipleInstancesTests
    {
        [SetUp] public void SetUp() => SandstormState.ResetForMatch();
        [TearDown] public void TearDown() => SandstormState.ResetForMatch();

        private static SandstormSlotData Active(int id, int age, int course, params HexCoord[] body)
        {
            var slot = new SandstormSlotData { id = id, phase = (int)SandstormPhase.Active, age = age, course = course, random = 1000 + id };
            foreach (var h in body) { slot.bodyQ.Add(h.Q); slot.bodyR.Add(h.R); }
            return slot;
        }

        private static HexCoord[] Spot(int q, int r) =>
            SandstormRules.BodyOf(new HexCoord(q, r), new[] { 0, 1, 3, 5 }).ToArray();

        private static void AssertApart(IReadOnlyList<SandstormInstanceView> views, string at)
        {
            var seen = new HashSet<HexCoord>();
            foreach (var view in views)
                foreach (var hex in view.Body)
                    Assert.That(seen.Add(hex), Is.True, $"{at}: {hex} covered by two storms");
        }

        [TestCase(5)] [TestCase(7)]
        public void TwoSandstorms_FollowTheSchedule_AndNeverShareAHex(int radius)
        {
            var field = SandstormLifecycleTests.Field(radius);
            var topology = new SandstormRules.Topology(field);
            for (int seed = 0; seed < 60; seed++)
            {
                SandstormState.ResetForMatch();
                SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.TwoSandstorms, seed), null);
                for (int round = 1; round <= 17; round++)
                {
                    SandstormState.AdvanceRound(field, round);
                    string at = $"r{radius} seed {seed} round {round}";
                    var views = SandstormState.Snapshot.Instances;
                    Assert.That(views.Select(v => v.SlotId), Is.EqualTo(new[] { 0, 1 }), at);
                    AssertApart(views, at);
                    foreach (var view in views.Where(v => v.Phase == SandstormPhase.Active))
                    {
                        Assert.That(SandstormRules.IsValidBody(topology, view.Body, view.Age), Is.True, at);
                        // Next to the other storm a shape may come out smaller, never larger.
                        Assert.That(view.Body.Count, Is.InRange(1, SandstormRules.SizeAt(view.Age)), at);
                    }
                    Assert.DoesNotThrow(() => SandstormState.Validate(SandstormState.CaptureState(), field), at);
                }
            }
        }

        [Test]
        public void TwoSandstorms_SizesFollowTheAgeWhenApart()
        {
            var field = SandstormLifecycleTests.Field(7);
            int checkedRounds = 0;
            for (int seed = 0; seed < 30; seed++)
            {
                SandstormState.ResetForMatch();
                SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.TwoSandstorms, seed), null);
                for (int round = 1; round <= 9; round++)
                {
                    SandstormState.AdvanceRound(field, round);
                    var views = SandstormState.Snapshot.Instances;
                    int expected = new[] { 1, 3, 5, 6, 5, 3, 0, 0, 1 }[round - 1];
                    if (views.Any(v => v.Phase != SandstormPhase.Active)
                        || HexGridMath.Distance(SandstormRules.CentreOf(views[0].Body), SandstormRules.CentreOf(views[1].Body)) < 3)
                        continue;
                    Assert.That(views.Select(v => v.Body.Count), Is.EqualTo(new[] { expected, expected }), $"seed {seed} round {round}");
                    checkedRounds++;
                }
            }
            Assert.That(checkedRounds, Is.GreaterThan(30));
        }

        [Test]
        public void FirstSlot_IsTheSameStormWithOrWithoutASecondSlot_UntilTheyMeet()
        {
            // The second slot's random stream never feeds the first; only its presence (a centre
            // the first may not enter, hexes the first's shape may not take) can, and only once
            // the centres (both drifting one hex) can end up within two hexes of each other.
            var field = SandstormLifecycleTests.Field(7);
            List<(string Body, int Gap)> Trace(MatchWeatherProfile profile)
            {
                SandstormState.ResetForMatch();
                SandstormState.Initialize(new MatchWeatherConditions(profile, 1234), null);
                var trace = new List<(string, int)>();
                for (int round = 1; round <= 17; round++)
                {
                    var before = SandstormState.Snapshot.Instances;
                    int gap = before.Count == 2 && before.All(v => v.Body.Count > 0)
                        ? HexGridMath.Distance(SandstormRules.CentreOf(before[0].Body), SandstormRules.CentreOf(before[1].Body))
                        : int.MaxValue;
                    SandstormState.AdvanceRound(field, round);
                    trace.Add((string.Join(",", SandstormState.Snapshot.Instances[0].Body), gap));
                }
                return trace;
            }
            var two = Trace(MatchWeatherProfile.TwoSandstorms);
            var one = Trace(MatchWeatherProfile.OneSandstorm);
            int compared = 0;
            for (int i = 0; i < two.Count && two[i].Gap > 4; i++, compared++)
                Assert.That(two[i].Body, Is.EqualTo(one[i].Body), "round " + (i + 1));
            Assert.That(compared, Is.GreaterThan(0));
        }

        [Test]
        public void OverlappingViews_UnionCountsEachHexOnce()
        {
            // The snapshot itself stays a plain union (rules never produce an overlap).
            var a = new SandstormInstanceView(0, SandstormPhase.Active, 2, 0, Spot(0, 0));
            var b = new SandstormInstanceView(1, SandstormPhase.Active, 3, 0, Spot(1, 0));
            var snapshot = new SandstormSnapshot(MatchWeatherProfile.TwoSandstorms, 1, new[] { b, a });
            Assert.That(snapshot.Cells.Count, Is.EqualTo(8), "two adjacent spots share two hexes");
            Assert.That(snapshot.Contains(new HexCoord(1, 0)), Is.True);
            Assert.That(snapshot.Instances.Select(i => i.SlotId), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(snapshot.Cells, Is.Ordered.Using((IComparer<HexCoord>)Comparer<HexCoord>.Create(SandstormRules.CompareCoords)));
        }

        [Test]
        public void OneStormLeaving_ClearsOnlyTheHexesNobodyElseCovers()
        {
            var field = SandstormLifecycleTests.Field(7);
            // Age 5: at most three hexes.
            var leaving = SandstormRules.BodyOf(new HexCoord(-2, 0), new[] { 0, 1 }).ToArray();
            var staying = Spot(1, 0);
            var data = new SandstormStateData { profile = (int)MatchWeatherProfile.TwoSandstorms, seed = 8, lastRound = 6 };
            data.slots.Add(Active(0, 5, 0, leaving));
            data.slots.Add(Active(1, 2, 3, staying));
            SandstormState.RestoreState(field, data);
            SandstormState.AdvanceRound(field, 7);
            Assert.That(SandstormState.Snapshot.Instances[0].Phase, Is.EqualTo(SandstormPhase.Cooldown));
            var remaining = SandstormState.Snapshot.Instances[1].Body;
            foreach (var hex in leaving)
                Assert.That(SandstormState.Snapshot.Contains(hex), Is.EqualTo(remaining.Contains(hex)), hex.ToString());
            Assert.That(SandstormState.Snapshot.Cells, Is.EquivalentTo(remaining));
        }

        [Test]
        public void PauseOfOneSlot_DoesNotDisturbTheOther()
        {
            var field = SandstormLifecycleTests.Field(7);
            var data = new SandstormStateData { profile = (int)MatchWeatherProfile.TwoSandstorms, seed = 8, lastRound = 10 };
            data.slots.Add(new SandstormSlotData { id = 0, phase = (int)SandstormPhase.Cooldown, nextSpawnRound = 12, random = 5 });
            data.slots.Add(Active(1, 2, 0, Spot(-2, 0)));
            SandstormState.RestoreState(field, data);
            SandstormState.AdvanceRound(field, 11);
            Assert.That(SandstormState.Snapshot.Instances[0].Phase, Is.EqualTo(SandstormPhase.Cooldown));
            Assert.That(SandstormState.Snapshot.Instances[1].Age, Is.EqualTo(3));
            SandstormState.AdvanceRound(field, 12);
            Assert.That(SandstormState.Snapshot.Instances[0].Phase, Is.EqualTo(SandstormPhase.Active));
            Assert.That(SandstormState.Snapshot.Instances[0].Age, Is.EqualTo(0));
            Assert.That(SandstormState.Snapshot.Instances[1].Age, Is.EqualTo(4));
            Assert.That(SandstormState.Snapshot.Instances[1].Body.Intersect(SandstormState.Snapshot.Instances[0].Body), Is.Empty,
                "a birth never lands on a hex another storm covers");
        }

        [Test]
        public void ConfiguredCount_IsZeroOneTwo_AndZeroIsNotAProfile()
        {
            Assert.That(SandstormRules.ConfiguredCount(MatchWeatherProfile.Clear), Is.EqualTo(0));
            Assert.That(SandstormRules.ConfiguredCount(MatchWeatherProfile.OneSandstorm), Is.EqualTo(1));
            Assert.That(SandstormRules.ConfiguredCount(MatchWeatherProfile.TwoSandstorms), Is.EqualTo(2));
            Assert.That(MatchWeatherConditions.IsValid((MatchWeatherProfile)0), Is.False);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => new MatchWeatherConditions((MatchWeatherProfile)0, 1));
        }
    }
}
#endif
