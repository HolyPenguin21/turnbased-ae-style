#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Weather save/load (spec 8.2): capture/restore reproduces bodies, ages, pauses and random
    // streams without a tick; the continuation equals an uninterrupted run; damaged data is
    // rejected rather than replaced by a new random storm.
    public sealed class SandstormSaveTests
    {
        [SetUp] public void SetUp() => SandstormState.ResetForMatch();
        [TearDown] public void TearDown() => SandstormState.ResetForMatch();

        [TestCase(MatchWeatherProfile.OneSandstorm)]
        [TestCase(MatchWeatherProfile.TwoSandstorms)]
        [TestCase(MatchWeatherProfile.Clear)]
        public void RestoreAtEveryRound_ContinuesExactlyLikeTheUninterruptedRun(MatchWeatherProfile profile)
        {
            var field = SandstormLifecycleTests.Field(6);
            var conditions = new MatchWeatherConditions(profile, 2026);
            var continuous = new List<string>();
            SandstormState.Initialize(conditions, null);
            for (int round = 1; round <= 18; round++)
            {
                SandstormState.AdvanceRound(field, round);
                continuous.Add(SandstormLifecycleTests.Dump(SandstormState.CaptureState()));
            }
            for (int saveAt = 1; saveAt < 18; saveAt++)
            {
                SandstormState.ResetForMatch();
                SandstormState.Initialize(conditions, null);
                for (int round = 1; round <= saveAt; round++) SandstormState.AdvanceRound(field, round);
                SandstormStateData saved = SandstormState.CaptureState();
                SandstormState.ResetForMatch();
                SandstormState.RestoreState(field, saved);
                Assert.That(SandstormLifecycleTests.Dump(SandstormState.CaptureState()), Is.EqualTo(continuous[saveAt - 1]),
                    "restore performs no tick");
                Assert.That(SandstormState.LastAppliedRound, Is.EqualTo(saveAt));
                for (int round = saveAt + 1; round <= 18; round++)
                {
                    SandstormState.AdvanceRound(field, round);
                    Assert.That(SandstormLifecycleTests.Dump(SandstormState.CaptureState()), Is.EqualTo(continuous[round - 1]),
                        $"save at {saveAt}, round {round}");
                }
            }
        }

        [Test]
        public void TwoStorms_InDifferentPhasesAndOverlapping_RoundTrip()
        {
            var field = SandstormLifecycleTests.Field(7);
            var data = new SandstormStateData { profile = (int)MatchWeatherProfile.TwoSandstorms, seed = 4, lastRound = 9 };
            var a = new SandstormSlotData { id = 0, phase = (int)SandstormPhase.Active, age = 3, course = 0, random = -5 };
            var b = new SandstormSlotData { id = 1, phase = (int)SandstormPhase.Active, age = 0, course = 2, random = 6 };
            foreach (HexCoord h in SandstormRules.SpotCells(new HexCoord(0, 0), 0, grown: true)) { a.bodyQ.Add(h.Q); a.bodyR.Add(h.R); }
            // A half spot inside slot 0's spot: overlap is valid.
            foreach (HexCoord h in SandstormRules.SpotCells(new HexCoord(0, 0), 2, grown: false)) { b.bodyQ.Add(h.Q); b.bodyR.Add(h.R); }
            data.slots.Add(a); data.slots.Add(b);
            SandstormState.RestoreState(field, data);
            Assert.That(SandstormLifecycleTests.Dump(SandstormState.CaptureState()), Is.EqualTo(SandstormLifecycleTests.Dump(data)));
            Assert.That(SandstormState.Snapshot.Cells.Count, Is.EqualTo(SandstormRules.SpotSize));
        }

        private static SandstormStateData ValidOne()
        {
            var data = new SandstormStateData { profile = (int)MatchWeatherProfile.OneSandstorm, seed = 4, lastRound = 9 };
            var slot = new SandstormSlotData { id = 0, phase = (int)SandstormPhase.Active, age = 3, course = 0, random = 9 };
            foreach (HexCoord h in SandstormRules.SpotCells(new HexCoord(0, 0), 0, grown: true)) { slot.bodyQ.Add(h.Q); slot.bodyR.Add(h.R); }
            data.slots.Add(slot);
            return data;
        }

        private static readonly (string Name, Action<SandstormStateData> Damage)[] Damages =
        {
            ("profile 0", d => d.profile = 0),
            ("unknown profile", d => d.profile = 9),
            ("slot count", d => d.slots.Add(new SandstormSlotData { id = 1, phase = (int)SandstormPhase.WaitingForSpawn, nextSpawnRound = 1 })),
            ("clear with a slot", d => d.profile = (int)MatchWeatherProfile.Clear),
            ("duplicate body hex", d => d.slots[0].bodyQ[4] = d.slots[0].bodyQ[2]),
            ("outer row", d => { for (int i = 0; i < d.slots[0].bodyQ.Count; i++) d.slots[0].bodyQ[i] += 5; }),
            ("gap", d => d.slots[0].bodyQ[6] += 1),
            ("size 5", d => { d.slots[0].bodyQ.RemoveRange(0, 2); d.slots[0].bodyR.RemoveRange(0, 2); }),
            ("half spot after birth", d => { d.slots[0].bodyQ.RemoveRange(0, 3); d.slots[0].bodyR.RemoveRange(0, 3); }),
            ("full spot at birth", d => { d.slots[0].age = 0; d.lastRound = 1; }),
            ("age 6", d => d.slots[0].age = 6),
            ("born before round 1", d => d.lastRound = 3),
            ("unknown phase", d => d.slots[0].phase = 0),
            ("pause with a body", d => d.slots[0].phase = (int)SandstormPhase.Cooldown),
            ("q/r mismatch", d => d.slots[0].bodyR.RemoveAt(0)),
            ("missing slots", d => d.slots = null),
        };

        [Test]
        public void DamagedData_IsRejected_WithoutTouchingTheLiveState()
        {
            var field = SandstormLifecycleTests.Field(6);
            Assert.DoesNotThrow(() => SandstormState.Validate(ValidOne(), field));
            SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.TwoSandstorms, 1), null);
            SandstormState.AdvanceRound(field, 1);
            string live = SandstormLifecycleTests.Dump(SandstormState.CaptureState());
            foreach (var (name, damage) in Damages)
            {
                var data = ValidOne();
                damage(data);
                Assert.Throws<ArgumentException>(() => SandstormState.RestoreState(field, data), name);
                Assert.That(SandstormLifecycleTests.Dump(SandstormState.CaptureState()), Is.EqualTo(live), name);
            }
        }

        [Test]
        public void PauseAndWait_AreValidSavedStates()
        {
            var field = SandstormLifecycleTests.Field(6);
            var data = new SandstormStateData { profile = (int)MatchWeatherProfile.TwoSandstorms, seed = 4, lastRound = 7 };
            data.slots.Add(new SandstormSlotData { id = 0, phase = (int)SandstormPhase.Cooldown, nextSpawnRound = 8, random = 1 });
            data.slots.Add(new SandstormSlotData { id = 1, phase = (int)SandstormPhase.WaitingForSpawn, nextSpawnRound = 8, random = 2 });
            Assert.DoesNotThrow(() => SandstormState.Validate(data, field));
            data.slots[0].nextSpawnRound = 10;
            Assert.Throws<ArgumentException>(() => SandstormState.Validate(data, field), "a pause longer than two rounds");
        }
    }
}
#endif
