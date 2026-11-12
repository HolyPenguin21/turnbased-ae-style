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

        private static HexCoord[] Line(int q0, int r, int length) =>
            Enumerable.Range(0, length).Select(i => new HexCoord(q0 + i, r)).ToArray();

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
                    int expected = new[] { 1, 5, 5, 5, 5, 5, 0, 0, 1 }[round - 1];
                    Assert.That(views.Select(v => v.Body.Count), Is.EqualTo(new[] { expected, expected }));
                    Assert.That(SandstormState.Snapshot.Cells.Count, Is.LessThanOrEqualTo(2 * expected));
                    if (round == 1 || round == 9)
                        Assert.That(views[0].Body.Single(), Is.Not.EqualTo(views[1].Body.Single()), "seed " + seed);
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
            var a = new SandstormInstanceView(0, SandstormPhase.Active, 2, 0, Line(-2, 0, 5));
            var b = new SandstormInstanceView(1, SandstormPhase.Active, 3, 0, Line(0, 0, 5));
            var snapshot = new SandstormSnapshot(MatchWeatherProfile.TwoSandstorms, 1, new[] { b, a });
            Assert.That(snapshot.Cells.Count, Is.EqualTo(7));
            Assert.That(snapshot.Contains(new HexCoord(1, 0)), Is.True);
            Assert.That(snapshot.Instances.Select(i => i.SlotId), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(snapshot.Cells, Is.Ordered.Using((IComparer<HexCoord>)Comparer<HexCoord>.Create(SandstormRules.CompareCoords)));
        }

        [Test]
        public void OneStormLeaving_KeepsHexesTheOtherStillCovers()
        {
            var field = SandstormLifecycleTests.Field(7);
            // Slot 0 ends this round; slot 1's head sits on a hex slot 0 also covers. After the tick
            // slot 0 is gone and slot 1's old head is still part of its body (it moved 2-3).
            var shared = new HexCoord(0, 0);
            var data = new SandstormStateData { profile = (int)MatchWeatherProfile.TwoSandstorms, seed = 8, lastRound = 6 };
            data.slots.Add(Active(0, 5, 0, Line(-2, 0, 5)));
            data.slots.Add(Active(1, 2, 1, new HexCoord(0, -4), new HexCoord(0, -3), new HexCoord(0, -2), new HexCoord(0, -1), shared));
            SandstormState.RestoreState(field, data);
            Assert.That(SandstormState.Snapshot.Contains(shared), Is.True);
            SandstormState.AdvanceRound(field, 7);
            Assert.That(SandstormState.Snapshot.Instances[0].Phase, Is.EqualTo(SandstormPhase.Cooldown));
            Assert.That(SandstormState.Snapshot.Instances[1].Body, Has.Member(shared));
            Assert.That(SandstormState.Snapshot.Contains(shared), Is.True);
            foreach (var hex in Line(-2, 0, 5).Where(h => !SandstormState.Snapshot.Instances[1].Body.Contains(h)))
                Assert.That(SandstormState.Snapshot.Contains(hex), Is.False, hex.ToString());
        }

        [Test]
        public void PauseOfOneSlot_DoesNotDisturbTheOther()
        {
            var field = SandstormLifecycleTests.Field(7);
            var data = new SandstormStateData { profile = (int)MatchWeatherProfile.TwoSandstorms, seed = 8, lastRound = 10 };
            data.slots.Add(new SandstormSlotData { id = 0, phase = (int)SandstormPhase.Cooldown, nextSpawnRound = 12, random = 5 });
            data.slots.Add(Active(1, 2, 0, Line(-3, 0, 5)));
            SandstormState.RestoreState(field, data);
            SandstormState.AdvanceRound(field, 11);
            Assert.That(SandstormState.Snapshot.Instances[0].Phase, Is.EqualTo(SandstormPhase.Cooldown));
            Assert.That(SandstormState.Snapshot.Instances[1].Age, Is.EqualTo(3));
            SandstormState.AdvanceRound(field, 12);
            Assert.That(SandstormState.Snapshot.Instances[0].Phase, Is.EqualTo(SandstormPhase.Active));
            Assert.That(SandstormState.Snapshot.Instances[0].Age, Is.EqualTo(0));
            Assert.That(SandstormState.Snapshot.Instances[1].Age, Is.EqualTo(4));
            Assert.That(SandstormState.Snapshot.Instances[1].Body, Has.No.Member(SandstormState.Snapshot.Instances[0].Body.Single()),
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
