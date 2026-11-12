#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Sandstorm geometry invariants: only interior hexes; a centre plus some of its neighbours,
    // sized by age (1,3,5,6,5,3); the centre drifts one hex per round or lulls in place, turning
    // by at most 60 degrees; the shape changes from round to round; look-ahead never drives the
    // centre into a dead end. Geometry depends only on the coordinates, so the standard field
    // radii cover both biomes.
    public sealed class SandstormGeometryTests
    {
        [SetUp] public void SetUp() => SandstormState.ResetForMatch();
        [TearDown] public void TearDown() => SandstormState.ResetForMatch();

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
                        Assert.That(view.Body.Count, Is.EqualTo(SandstormRules.SizeAt(view.Age)), at);
                        Assert.That(SandstormRules.IsValidBody(topology, view.Body, view.Age), Is.True, at);
                        Assert.That(view.Body.All(topology.IsInterior), Is.True, at);
                        if (view.Age > 0 && previous != null)
                        {
                            int moved = HexGridMath.Distance(SandstormRules.CentreOf(previous.Body), SandstormRules.CentreOf(view.Body));
                            Assert.That(moved, Is.InRange(0, 1), at);
                            Assert.That(SandstormRules.TurnDistance(previous.Course, view.Course), Is.LessThanOrEqualTo(1), at);
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
        public void StormsLull_LingerAndChangeShape()
        {
            var field = SandstormLifecycleTests.Field(7);
            int lulls = 0, drifts = 0, lingered = 0;
            // At the peak (age 3, six hexes) one neighbour is missing; a fixed shape would always
            // miss the same one relative to the course.
            var missingTurns = new HashSet<int>();
            for (int seed = 0; seed < 100; seed++)
            {
                SandstormState.ResetForMatch();
                SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.OneSandstorm, seed), null);
                SandstormInstanceView previous = null;
                for (int round = 1; round <= 6; round++)
                {
                    SandstormState.AdvanceRound(field, round);
                    SandstormInstanceView view = SandstormState.Snapshot.Instances.Single();
                    if (previous != null && view.Age >= 2)
                    {
                        HexCoord from = SandstormRules.CentreOf(previous.Body), to = SandstormRules.CentreOf(view.Body);
                        if (from.Equals(to)) lulls++; else drifts++;
                        // A hex that is neither the new centre nor the old centre but stays stormy lingered.
                        if (!from.Equals(to) && previous.Body.Any(h => !h.Equals(from) && !h.Equals(to) && view.Body.Contains(h))) lingered++;
                    }
                    if (view.Age == 3)
                    {
                        HexCoord centre = SandstormRules.CentreOf(view.Body);
                        for (int d = 0; d < 6; d++)
                            if (!view.Body.Contains(SandstormRules.Step(centre, d)))
                                missingTurns.Add(SandstormRules.TurnDistance(d, view.Course));
                    }
                    previous = view;
                }
            }
            Assert.That(lulls, Is.InRange(40, 140), "about one round in five is a lull");
            Assert.That(drifts, Is.GreaterThan(lulls));
            Assert.That(lingered, Is.GreaterThan(0), "some hexes stay behind a drifting storm");
            Assert.That(missingTurns.Count, Is.GreaterThan(1), "the shape is not fixed");
        }

        [Test]
        public void Shape_FillsFromTheFront_AndSkipsBlockedHexes()
        {
            var centre = new HexCoord(0, 0);
            var random = new SandstormRandom(1);
            Assert.That(SandstormRules.ChooseShape(centre, 0, 2, null, null, ref random),
                Is.EqualTo(SandstormRules.BodyOf(centre, new[] { 0 })));
            Assert.That(SandstormRules.ChooseShape(centre, 0, 4, null, null, ref random),
                Is.EqualTo(SandstormRules.BodyOf(centre, new[] { 0, 1, 5 })));
            var blocked = new HashSet<HexCoord> { SandstormRules.Step(centre, 0), SandstormRules.Step(centre, 1) };
            List<HexCoord> shape = SandstormRules.ChooseShape(centre, 0, 6, null, blocked, ref random);
            Assert.That(shape.Count, Is.EqualTo(5));
            Assert.That(shape.Intersect(blocked), Is.Empty);
            // A shrinking storm never grows past its size even when every old hex lingers.
            List<HexCoord> full = SandstormRules.BodyOf(centre, new[] { 0, 1, 2, 3, 4, 5 });
            for (ulong seed = 0; seed < 50; seed++)
            {
                var r = new SandstormRandom(seed);
                Assert.That(SandstormRules.ChooseShape(centre, 0, 3, full, null, ref r).Count, Is.EqualTo(3));
            }
        }

        [Test]
        public void SmallMap_LookAheadRejectsADriftIntoADeadEnd()
        {
            // Small field: storm centres within radius 3. A centre on (2,0) drifting along (1,0)
            // could step straight to (3,0), from where no one-hex drift with <= 60 degrees exists.
            var topology = new SandstormRules.Topology(SandstormLifecycleTests.Field(5));
            Assert.That(topology.CanHoldSpot(new HexCoord(3, 0)), Is.True, "the straight step is locally legal");
            Assert.That(SandstormRules.HasContinuation(topology, new HexCoord(3, 0), 0, 1, new SandstormRules.ContinuationMemo()), Is.False);
            for (ulong seed = 0; seed < 200; seed++)
            {
                var random = new SandstormRandom(SandstormRandom.Mix(seed));
                var kind = SandstormRules.ChooseDrift(topology, new HexCoord(2, 0), 0, 1, null, ref random,
                    new SandstormRules.ContinuationMemo(), out HexCoord next, out _);
                Assert.That(kind, Is.Not.EqualTo(SandstormRules.DriftKind.Stuck), "seed " + seed);
                Assert.That(next, Is.Not.EqualTo(new HexCoord(3, 0)), "seed " + seed);
            }
        }

        [Test]
        public void Drift_NeverEntersAnotherStormsCentre()
        {
            var topology = new SandstormRules.Topology(SandstormLifecycleTests.Field(7));
            var blocked = new[] { new HexCoord(1, 0) };
            for (ulong seed = 0; seed < 200; seed++)
            {
                var random = new SandstormRandom(SandstormRandom.Mix(seed));
                SandstormRules.ChooseDrift(topology, new HexCoord(0, 0), 0, 2, blocked, ref random,
                    new SandstormRules.ContinuationMemo(), out HexCoord next, out _);
                Assert.That(next, Is.Not.EqualTo(blocked[0]), "seed " + seed);
            }
        }

        [Test]
        public void EveryPlan_MovesTheCentreOneHex_TurningAtMostSixtyDegrees()
        {
            Assert.That(SandstormRules.Plans.Count, Is.EqualTo(3));
            foreach (var plan in SandstormRules.Plans)
            {
                HexCoord centre = new HexCoord(0, 0);
                for (int i = 0; i < plan.Steps; i++) centre = SandstormRules.Step(centre, plan.DirectionOf(0, i));
                Assert.That(HexGridMath.Distance(new HexCoord(0, 0), centre), Is.EqualTo(1), plan.ToString());
                Assert.That(SandstormRules.TurnDistance(0, plan.FinalCourse(0)), Is.LessThanOrEqualTo(1), plan.ToString());
            }
        }

        [Test]
        public void NoBirthPossible_WaitsWithoutHangingOrLeavingTheMap()
        {
            // Radius 2: only (0,0) can hold a storm, and it has nowhere to drift.
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
        public void NoLegalDrift_StaysInPlace_AndStillAges()
        {
            // Radius 2 field: only (0,0) can hold a storm, so every one-hex drift leaves the interior.
            var field = SandstormLifecycleTests.Field(2);
            var body = SandstormRules.BodyOf(new HexCoord(0, 0), new[] { 0, 1, 2, 3 });
            var topology = new SandstormRules.Topology(field);
            Assume.That(SandstormRules.IsValidBody(topology, body, 2), Is.True);
            var data = new SandstormStateData { profile = (int)MatchWeatherProfile.OneSandstorm, seed = 1, lastRound = 4 };
            var slot = new SandstormSlotData { id = 0, phase = (int)SandstormPhase.Active, age = 2, course = 5, random = 77 };
            foreach (var h in body) { slot.bodyQ.Add(h.Q); slot.bodyR.Add(h.R); }
            data.slots.Add(slot);
            SandstormState.RestoreState(field, data);
            SandstormState.AdvanceRound(field, 5);
            var view = SandstormState.Snapshot.Instances.Single();
            Assert.That(view.Age, Is.EqualTo(3));
            Assert.That(SandstormRules.CentreOf(view.Body), Is.EqualTo(new HexCoord(0, 0)));
            Assert.That(view.Body.Count, Is.EqualTo(SandstormRules.SizeAt(3)));
        }
    }
}
#endif
