#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Sandstorm geometry invariants (spec 3.2/3.3): only interior hexes, a connected body of 1
    // then 5 unique hexes, 2-3 head steps per round, course change <= 60 degrees, look-ahead that
    // never drives the head into a dead end. Geometry depends only on the coordinates, so the
    // standard field radii cover both biomes.
    public sealed class SandstormGeometryTests
    {
        [SetUp] public void SetUp() => SandstormState.ResetForMatch();
        [TearDown] public void TearDown() => SandstormState.ResetForMatch();

        private static int TurnDistance(int a, int b)
        {
            int d = ((a - b) % 6 + 6) % 6;
            return d > 3 ? 6 - d : d;
        }

        [TestCase(5)] [TestCase(6)] [TestCase(7)] [TestCase(8)]
        public void OuterRow_IsTheOuterRingOfTheStandardField(int radius)
        {
            var field = SandstormLifecycleTests.Field(radius);
            var topology = new SandstormRules.Topology(field);
            foreach (HexCoord hex in field)
                Assert.That(topology.IsInterior(hex), Is.EqualTo(HexGridMath.Distance(hex, new HexCoord(0, 0)) < radius), hex.ToString());
        }

        [TestCase(5)] [TestCase(6)] [TestCase(7)] [TestCase(8)]
        public void HundredSeeds_TwoFullCycles_KeepEveryInvariant(int radius)
        {
            var field = SandstormLifecycleTests.Field(radius);
            var topology = new SandstormRules.Topology(field);
            for (int seed = 0; seed < 100; seed++)
            {
                SandstormState.ResetForMatch();
                SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.OneSandstorm, seed * 7919L + radius), null);
                SandstormInstanceView previous = null;
                for (int round = 1; round <= 17; round++)
                {
                    SandstormState.AdvanceRound(field, round);
                    string at = $"r{radius} seed {seed} round {round}";
                    Assert.That(SandstormState.LastTickDiagnostics, Is.Empty, at);
                    SandstormInstanceView view = SandstormState.Snapshot.Instances.Single();
                    if (view.Phase == SandstormPhase.Active)
                    {
                        Assert.That(view.Body.Count, Is.EqualTo(view.Age == 0 ? 1 : SandstormRules.BodyLength), at);
                        Assert.That(SandstormRules.IsValidBody(topology, view.Body), Is.True, at);
                        if (view.Age > 0 && previous != null)
                        {
                            HexCoord oldHead = previous.Body[previous.Body.Count - 1];
                            int moved = HexGridMath.Distance(oldHead, view.Body[view.Body.Count - 1]);
                            Assert.That(moved, Is.InRange(2, 3), at);
                            Assert.That(TurnDistance(previous.Course, view.Course), Is.LessThanOrEqualTo(1), at);
                            // The body is the head's trail: every earlier cell that is still part of
                            // the body moved toward the tail, nothing jumped.
                            if (previous.Body.Count == SandstormRules.BodyLength)
                                Assert.That(view.Body.Take(SandstormRules.BodyLength - moved),
                                    Is.EqualTo(previous.Body.Skip(moved)), at);
                        }
                        if (view.Age == 1)
                        {
                            // Birth hex stays on the body line behind the new head.
                            Assert.That(previous.Body.Single(), Is.EqualTo(view.Body[view.Body.Count - 1 - HexGridMath.Distance(previous.Body.Single(), view.Body.Last())]), at);
                        }
                    }
                    else
                    {
                        Assert.That(view.Body, Is.Empty, at);
                    }
                    previous = view;
                }
            }
        }

        [Test]
        public void SmallMap_LookAheadRejectsAnAdvanceIntoADeadEnd()
        {
            // Small field: interior radius 4. A head at (1,0) moving along (1,0) could step
            // straight to (4,0), from where no 2-3 hex advance with <= 60 degrees exists.
            var topology = new SandstormRules.Topology(SandstormLifecycleTests.Field(5));
            var body = new List<HexCoord> { new HexCoord(-3, 0), new HexCoord(-2, 0), new HexCoord(-1, 0), new HexCoord(0, 0), new HexCoord(1, 0) };
            Assert.That(SandstormRules.TryAdvance(topology, body, 0, new SandstormRules.AdvancePlan(3, 3, 0), out var dead, out int deadCourse), Is.True,
                "the straight 3-step plan is locally legal");
            Assert.That(SandstormRules.HasContinuation(topology, dead.Last(), deadCourse, 1, new SandstormRules.ContinuationMemo()), Is.False);
            for (ulong seed = 0; seed < 200; seed++)
            {
                var random = new SandstormRandom(SandstormRandom.Mix(seed));
                Assert.That(SandstormRules.TryPlanAdvance(topology, body, 0, 1, ref random, new SandstormRules.ContinuationMemo(),
                    out var next, out _, out _), Is.True);
                Assert.That(next.Last(), Is.Not.EqualTo(new HexCoord(4, 0)), "seed " + seed);
            }
        }

        [Test]
        public void ThreeStepLoopWithOneHexDisplacement_IsNeverAPlan()
        {
            foreach (int steps in new[] { 2, 3 })
                foreach (var plan in SandstormRules.PlansFor(steps))
                {
                    HexCoord head = new HexCoord(0, 0);
                    for (int i = 0; i < plan.Steps; i++) head = SandstormRules.Step(head, plan.DirectionOf(0, i));
                    Assert.That(HexGridMath.Distance(new HexCoord(0, 0), head), Is.EqualTo(steps), plan.ToString());
                }
        }

        [Test]
        public void NoBirthPossible_WaitsWithoutHangingOrLeavingTheMap()
        {
            // Radius 2: interior radius 1 cannot hold a five-hex body.
            var field = SandstormLifecycleTests.Field(2);
            SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.TwoSandstorms, 1), null);
            for (int round = 1; round <= 4; round++)
            {
                SandstormState.AdvanceRound(field, round);
                Assert.That(SandstormState.Snapshot.Instances.All(i => i.Phase == SandstormPhase.WaitingForSpawn), Is.True);
                Assert.That(SandstormState.Snapshot.HasStorm, Is.False);
                Assert.That(SandstormState.LastTickDiagnostics.Count, Is.EqualTo(2));
            }
            // The wait itself is a valid saved state.
            Assert.DoesNotThrow(() => SandstormState.Validate(SandstormState.CaptureState(), field));
        }

        [Test]
        public void NoLegalAdvance_StaysInPlace_AndStillAges()
        {
            // A body restored on a field that leaves no 2-3 hex advance: diagnosable stall.
            var field = SandstormLifecycleTests.Field(3);
            // Radius 3 field (interior radius 2); head (2,-1) facing (1,-1): every 2-3 hex plan
            // with at most one 60-degree bend leaves the interior.
            var body = new[] { new HexCoord(-2, 1), new HexCoord(-1, 1), new HexCoord(0, 1), new HexCoord(1, 0), new HexCoord(2, -1) };
            var topology = new SandstormRules.Topology(field);
            Assume.That(SandstormRules.IsValidBody(topology, body), Is.True);
            var data = new SandstormStateData { profile = (int)MatchWeatherProfile.OneSandstorm, seed = 1, lastRound = 4 };
            var slot = new SandstormSlotData { id = 0, phase = (int)SandstormPhase.Active, age = 2, course = 5, random = 77 };
            foreach (var h in body) { slot.bodyQ.Add(h.Q); slot.bodyR.Add(h.R); }
            data.slots.Add(slot);
            SandstormState.RestoreState(field, data);
            SandstormState.AdvanceRound(field, 5);
            var view = SandstormState.Snapshot.Instances.Single();
            Assert.That(view.Age, Is.EqualTo(3));
            Assert.That(view.Body, Is.EqualTo(body));
            Assert.That(SandstormState.LastTickDiagnostics, Is.Not.Empty);
        }
    }
}
#endif
