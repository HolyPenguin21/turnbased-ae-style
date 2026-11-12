#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Profiles with 0/1/2 storms (spec 3.5): independent slots, atomic union snapshot, overlap
    // allowed, penalties never stacked, one storm leaving never clears hexes another still covers.
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
            SandstormRules.SpotCells(new HexCoord(q, r), 0, grown: true).ToArray();

        [Test]
        public void TwoSandstorms_BornOnDifferentHexes_EachFollowsTheSchedule()
        {
            var field = SandstormLifecycleTests.Field(7);
            for (int seed = 0; seed < 30; seed++)
            {
                SandstormState.ResetForMatch();
                SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.TwoSandstorms, seed), null);
                for (int round = 1; round <= 9; round++)
                {
                    SandstormState.AdvanceRound(field, round);
                    var views = SandstormState.Snapshot.Instances;
                    Assert.That(views.Select(v => v.SlotId), Is.EqualTo(new[] { 0, 1 }));
                    int expected = new[] { 4, 7, 7, 7, 7, 7, 0, 0, 4 }[round - 1];
                    Assert.That(views.Select(v => v.Body.Count), Is.EqualTo(new[] { expected, expected }));
                    Assert.That(SandstormState.Snapshot.Cells.Count, Is.LessThanOrEqualTo(2 * expected));
                    if (round == 1 || round == 9)
                        Assert.That(views[0].Body.Intersect(views[1].Body), Is.Empty, "seed " + seed);
                }
            }
        }

        [Test]
        public void FirstSlot_IsTheSameStormWithOrWithoutASecondSlot()
        {
            var field = SandstormLifecycleTests.Field(7);
            List<string> Trace(MatchWeatherProfile profile)
            {
                SandstormState.ResetForMatch();
                SandstormState.Initialize(new MatchWeatherConditions(profile, 1234), null);
                var trace = new List<string>();
                for (int round = 1; round <= 17; round++)
                {
                    SandstormState.AdvanceRound(field, round);
                    trace.Add(string.Join(",", SandstormState.Snapshot.Instances[0].Body));
                }
                return trace;
            }
            Assert.That(Trace(MatchWeatherProfile.TwoSandstorms), Is.EqualTo(Trace(MatchWeatherProfile.OneSandstorm)),
                "the second slot's random stream never feeds the first");
        }

        [Test]
        public void OverlappingStorms_UnionCountsEachHexOnce()
        {
            var a = new SandstormInstanceView(0, SandstormPhase.Active, 2, 0, Spot(0, 0));
            var b = new SandstormInstanceView(1, SandstormPhase.Active, 3, 0, Spot(1, 0));
            var snapshot = new SandstormSnapshot(MatchWeatherProfile.TwoSandstorms, 1, new[] { b, a });
            Assert.That(snapshot.Cells.Count, Is.EqualTo(10), "two adjacent spots share four hexes");
            Assert.That(snapshot.Contains(new HexCoord(1, 0)), Is.True);
            Assert.That(snapshot.Instances.Select(i => i.SlotId), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(snapshot.Cells, Is.Ordered.Using((IComparer<HexCoord>)Comparer<HexCoord>.Create(SandstormRules.CompareCoords)));
        }

        [Test]
        public void OneStormLeaving_KeepsHexesTheOtherStillCovers()
        {
            // Slot 0 ends this round and sits exactly where slot 1 (age 2 -> 3, its own random
            // stream) drifts to: after the tick slot 0 is gone, yet every one of its hexes is
            // still stormy because slot 1 now covers it. Slot 1's old hexes it left are cleared.
            var field = SandstormLifecycleTests.Field(7);
            var topology = new SandstormRules.Topology(field);
            SandstormSlotData mover = Active(1, 2, 1, Spot(0, 0));
            var random = new SandstormRandom(unchecked((ulong)mover.random));
            Assume.That(SandstormRules.TryPlanAdvance(topology, Spot(0, 0), 1, SandstormRules.LastActiveAge - 3, ref random,
                new SandstormRules.ContinuationMemo(), out List<HexCoord> arrival, out _, out _), Is.True);
            var data = new SandstormStateData { profile = (int)MatchWeatherProfile.TwoSandstorms, seed = 8, lastRound = 6 };
            data.slots.Add(Active(0, 5, 0, arrival.ToArray()));
            data.slots.Add(mover);
            SandstormState.RestoreState(field, data);
            SandstormState.AdvanceRound(field, 7);
            Assert.That(SandstormState.Snapshot.Instances[0].Phase, Is.EqualTo(SandstormPhase.Cooldown));
            Assert.That(SandstormState.Snapshot.Instances[1].Body, Is.EqualTo(arrival));
            foreach (var hex in arrival)
                Assert.That(SandstormState.Snapshot.Contains(hex), Is.True, hex.ToString());
            foreach (var hex in Spot(0, 0).Where(h => !arrival.Contains(h)))
                Assert.That(SandstormState.Snapshot.Contains(hex), Is.False, hex.ToString());
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
