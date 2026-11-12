#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Sandstorm schedule (spec 3.1): one tick per full round; six active rounds (spot sizes 4,7,7,7,7,7),
    // two empty rounds, then a new birth. Driven on plain coordinates — no HexMap, no engine.
    public sealed class SandstormLifecycleTests
    {
        internal static List<HexCoord> Field(int radius) =>
            HexGridMath.HexesInRange(new HexCoord(0, 0), radius).ToList();

        [SetUp] public void SetUp() => SandstormState.ResetForMatch();
        [TearDown] public void TearDown() => SandstormState.ResetForMatch();

        internal static string Dump(SandstormStateData data) =>
            $"{data.profile}/{data.seed}/{data.lastRound}|" + string.Join(";", data.slots.Select(s =>
                $"{s.id}:{s.phase}:{s.age}:{s.course}:{s.nextSpawnRound}:{s.random}:"
                + string.Join(",", s.bodyQ.Select((q, i) => q + "/" + s.bodyR[i]))));

        [Test]
        public void OneSandstorm_FollowsTheNineRoundSchedule()
        {
            var field = Field(6);
            SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.OneSandstorm, 42), null);
            var sizes = new List<int>();
            for (int round = 1; round <= 9; round++)
            {
                Assert.That(SandstormState.AdvanceRound(field, round), Is.True);
                sizes.Add(SandstormState.Snapshot.Instances.Single().Body.Count);
            }
            Assert.That(sizes, Is.EqualTo(new[] { 4, 7, 7, 7, 7, 7, 0, 0, 4 }));
            Assert.That(SandstormState.LastTickDiagnostics, Is.Empty);
        }

        [Test]
        public void Phases_MatchTheScheduleIncludingBothEmptyRounds()
        {
            var field = Field(6);
            SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.OneSandstorm, 7), null);
            var phases = new List<SandstormPhase>();
            var ages = new List<int>();
            for (int round = 1; round <= 9; round++)
            {
                SandstormState.AdvanceRound(field, round);
                var view = SandstormState.Snapshot.Instances.Single();
                phases.Add(view.Phase);
                ages.Add(view.Age);
            }
            Assert.That(phases, Is.EqualTo(new[]
            {
                SandstormPhase.Active, SandstormPhase.Active, SandstormPhase.Active, SandstormPhase.Active,
                SandstormPhase.Active, SandstormPhase.Active, SandstormPhase.Cooldown, SandstormPhase.Cooldown,
                SandstormPhase.Active,
            }));
            Assert.That(ages.Take(6), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5 }));
            Assert.That(ages[8], Is.EqualTo(0), "round 9 is the next birth");
        }

        [Test]
        public void Clear_HasNoSlotsAndNeverSpawns()
        {
            var field = Field(6);
            SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.Clear, 3), null);
            for (int round = 1; round <= 20; round++)
            {
                SandstormState.AdvanceRound(field, round);
                Assert.That(SandstormState.Snapshot.Instances, Is.Empty);
                Assert.That(SandstormState.Snapshot.HasStorm, Is.False);
                Assert.That(SandstormState.Snapshot.Profile, Is.EqualTo(MatchWeatherProfile.Clear));
            }
            Assert.That(SandstormState.CaptureState().slots, Is.Empty);
        }

        [Test]
        public void SameRoundAgain_ChangesNothing_AndEarlierRoundIsRejected()
        {
            var field = Field(6);
            SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.TwoSandstorms, 11), null);
            for (int round = 1; round <= 3; round++) SandstormState.AdvanceRound(field, round);
            string before = Dump(SandstormState.CaptureState());
            var snapshot = SandstormState.Snapshot;
            Assert.That(SandstormState.AdvanceRound(field, 3), Is.False);
            Assert.That(Dump(SandstormState.CaptureState()), Is.EqualTo(before));
            Assert.That(SandstormState.Snapshot, Is.SameAs(snapshot));
            Assert.Throws<InvalidOperationException>(() => SandstormState.AdvanceRound(field, 2));
            Assert.That(Dump(SandstormState.CaptureState()), Is.EqualTo(before));
        }

        [Test]
        public void SameSeedAndMap_GiveTheSameStorm()
        {
            var field = Field(7);
            string Run()
            {
                SandstormState.ResetForMatch();
                SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.TwoSandstorms, 99), null);
                for (int round = 1; round <= 12; round++) SandstormState.AdvanceRound(field, round);
                return Dump(SandstormState.CaptureState());
            }
            // Enumeration order of the coordinates must not matter.
            string first = Run();
            field.Reverse();
            Assert.That(Run(), Is.EqualTo(first));
        }

        [Test]
        public void NotInitialized_TickIsANoOp_AndResetDropsSubscribers()
        {
            Assert.That(SandstormState.AdvanceRound(Field(6), 1), Is.False);
            int calls = 0;
            SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.OneSandstorm, 1), null);
            SandstormState.Changed += _ => calls++;
            SandstormState.AdvanceRound(Field(6), 1);
            Assert.That(calls, Is.EqualTo(1));
            SandstormState.ResetForMatch();
            Assert.That(SandstormState.IsInitialized, Is.False);
            Assert.That(SandstormState.Snapshot.HasStorm, Is.False);
            SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.OneSandstorm, 1), null);
            SandstormState.AdvanceRound(Field(6), 1);
            Assert.That(calls, Is.EqualTo(1), "a new match never notifies the previous match's subscribers");
        }

        [Test]
        public void OneNotificationPerTick_ForBothInstances()
        {
            var field = Field(7);
            SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.TwoSandstorms, 5), null);
            var seen = new List<SandstormSnapshot>();
            SandstormState.Changed += s => seen.Add(s);
            SandstormState.AdvanceRound(field, 1);
            Assert.That(seen.Count, Is.EqualTo(1));
            Assert.That(seen[0].Instances.Count(i => i.Phase == SandstormPhase.Active), Is.EqualTo(2),
                "both births are published together");
        }
    }
}
#endif
