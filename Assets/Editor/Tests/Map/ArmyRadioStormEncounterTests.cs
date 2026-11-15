#if UNITY_INCLUDE_TESTS
using System.Reflection;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Turns;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class ArmyRadioStormEncounterTests
    {
        private static void Set(object target, string field, object value) => target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static void Call(object target, string method, params object[] args) => target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);

        [Test]
        public void RoundWeatherWaitsForPresentationThenReportsEachContinuousExposureOnlyOnce()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear();
            VisionSystem.Configure(null); SandstormState.ResetForMatch();
            var field = SandstormLifecycleTests.Field(6);
            var conditions = new MatchWeatherConditions(MatchWeatherProfile.OneSandstorm, 42);
            // Find this deterministic birth before attaching the radio to the fresh match.
            SandstormState.Initialize(conditions, null);
            SandstormState.AdvanceRound(field, 1);
            HexCoord exposed = SandstormState.Snapshot.Cells[0];
            SandstormState.ResetForMatch(); SandstormState.Initialize(conditions, null);
            bool debugReveal = VisionSystem.DebugRevealAll;
            var go = new GameObject("Radio storm encounter test");
            try
            {
                var turns = go.AddComponent<GameTurnController>();
                var radio = go.AddComponent<ArmyRadioChatterController>();
                var owner = new PlayerSetupData { IsHuman = true };
                VisionSystem.CurrentViewer = owner;
                var army = new ArmyData { Owner = owner, Hex = exposed };
                army.Members.Add(new UnitData { Owner = owner, HitPointsCurrent = 5, HitPointsMax = 5 });
                ArmyRegistry.Register(army);
                Set(radio, "_turns", turns); Set(radio, "_viewer", owner);
                Set(radio, "_settings", new ArmyRadioChatterSettings());
                Set(radio, "_camera", go.AddComponent<Camera>());
                Set(radio, "_cadence", new ArmyRadioCadence(Time.unscaledTime));
                Call(radio, "ClearOperations"); Set(radio, "_primeObservation", false);
                Call(radio, "Subscribe");
                var dispatch = (ArmyRadioDispatch)typeof(ArmyRadioChatterController)
                    .GetField("_dispatch", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(radio);

                Assert.That(turns.CanPresentArmyRadio, Is.False);
                SandstormState.AdvanceRound(field, 1);
                Call(radio, "Update");
                Assert.That(dispatch.PendingCount, Is.Zero, "weather must not start its TTL behind initiative/confirmation");
                dispatch.Prune(Time.unscaledTime + 30f, null);
                typeof(GameTurnController).GetProperty("CurrentPlayer").SetValue(turns, owner);
                typeof(GameTurnController).GetProperty("TurnConfirmed").SetValue(turns, true);
                Assert.That(turns.CanPresentArmyRadio, Is.True);
                Set(radio, "_nextObservation", 0f); Call(radio, "Update");
                Assert.That(dispatch.PendingCount, Is.EqualTo(1), "first presentation sample still reports the current storm");
                Set(radio, "_nextObservation", 0f); Call(radio, "Update");
                Assert.That(dispatch.PendingCount, Is.EqualTo(1));
                Assert.That(dispatch.TryTake(Time.unscaledTime, -1, 1f, _ => true, _ => true, out var report), Is.True);

                var mover = go.AddComponent<ArmyController>(); mover.SetData(army); army.Controller = mover;
                typeof(ArmyController).GetProperty("IsMoving").SetValue(mover, true);
                ArmyRegistry.MoveArmy(army, new HexCoord(20, 0));
                ArmyRegistry.MoveArmy(army, exposed);
                Set(radio, "_nextObservation", 0f); Call(radio, "Update");
                Assert.That(dispatch.PendingCount, Is.Zero, "a moving marker cannot start a report's freshness");
                typeof(ArmyController).GetProperty("IsMoving").SetValue(mover, false);
                Set(radio, "_nextObservation", 0f); Call(radio, "Update");
                Assert.That(dispatch.PendingCount, Is.EqualTo(1), "the first stationary sample reports the encounter");
                var shortLived = new ArmyData { Owner = owner, Hex = new HexCoord(20, 0) };
                shortLived.Members.Add(new UnitData { Owner = owner, HitPointsCurrent = 5, HitPointsMax = 5 });
                ArmyRegistry.Register(shortLived); ArmyRegistry.MoveArmy(shortLived, exposed);
                var exposures = (System.Collections.IDictionary)typeof(ArmyRadioChatterController)
                    .GetField("_stormContacts", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(radio);
                Assert.That(exposures.Contains(shortLived), Is.True);
                ArmyRegistry.Unregister(shortLived);
                Set(radio, "_nextObservation", 0f); Call(radio, "Update");
                Assert.That(exposures.Contains(shortLived), Is.False, "an army removed before its first sample must not retain exposure state");
                Assert.That(dispatch.TryTake(Time.unscaledTime, -1, 1f, _ => true, _ => true, out report), Is.True);
                Assert.That(report.Category, Is.EqualTo(ArmyRadioCategory.GroundSandstorm));
                Set(radio, "_nextObservation", 0f); Call(radio, "Update");
                Assert.That(dispatch.PendingCount, Is.Zero, "continuous exposure must not retry a consumed decision");

                ArmyRegistry.MoveArmy(army, new HexCoord(20, 0));
                ArmyRegistry.MoveArmy(army, exposed);
                Assert.That(dispatch.PendingCount, Is.EqualTo(1), "a real re-entry is a new encounter");
                ArmyRegistry.MoveArmy(army, new HexCoord(20, 0));
                ArmyRegistry.MoveArmy(army, exposed);
                Assert.That(dispatch.PendingCount, Is.EqualTo(1), "an old pending encounter must not revive after a quick re-entry");
                Assert.That(dispatch.TryTake(Time.unscaledTime, -1, 1f, _ => true, _ => true, out report), Is.True);

                typeof(GameTurnController).GetProperty("TurnConfirmed").SetValue(turns, false);
                ArmyRegistry.MoveArmy(army, new HexCoord(20, 0));
                ArmyRegistry.MoveArmy(army, exposed);
                Assert.That(dispatch.PendingCount, Is.Zero, "movement entry must also defer freshness behind a blocking popup");
                dispatch.Prune(Time.unscaledTime + 30f, null);
                typeof(GameTurnController).GetProperty("TurnConfirmed").SetValue(turns, true);
                Set(radio, "_nextObservation", 0f); Call(radio, "Update");
                Assert.That(dispatch.PendingCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(go);
                ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear();
                SandstormState.ResetForMatch(); VisionSystem.DebugRevealAll = debugReveal;
            }
        }
    }
}
#endif
