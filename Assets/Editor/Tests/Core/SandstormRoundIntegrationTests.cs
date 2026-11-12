#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Reflection;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using Game.Turns;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // The weather tick lives in GameTurnController.ProceedWithNewTurn: once per FULL round,
    // after the previous round's delayed battles, before every TurnStarted subscriber and the
    // round's preparation; never on a finished match. Unity only (MonoBehaviours).
    public sealed class SandstormRoundIntegrationTests
    {
        private GameObject _root;
        private GameTurnController _turns;
        private HexMap _map;

        [SetUp] public void SetUp()
        {
            SandstormState.ResetForMatch();
            _root = new GameObject("sandstorm-round");
            _root.SetActive(false);
            _map = _root.AddComponent<HexMap>();
            var plain = new TerrainTypeEntry { terrainName = "Plain", moveCost = 1 };
            var data = new Dictionary<HexCoord, TerrainTypeEntry>();
            foreach (HexCoord h in HexGridMath.HexesInRange(new HexCoord(0, 0), 6)) data[h] = plain;
            _map.SetData(6, 1f, data);
            _turns = _root.AddComponent<GameTurnController>();
            typeof(GameTurnController).GetField("map", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_turns, _map);
            SandstormState.Initialize(new MatchWeatherConditions(MatchWeatherProfile.OneSandstorm, 77), _map);
        }

        [TearDown] public void TearDown()
        {
            SandstormState.ResetForMatch();
            Object.DestroyImmediate(_root);
        }

        private void NewRound() =>
            typeof(GameTurnController).GetMethod("ProceedWithNewTurn", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_turns, null);

        [Test]
        public void EveryTurnStartedSubscriberSeesTheNewRoundsWeather()
        {
            var seen = new List<(int Round, int WeatherRound, int Size)>();
            _turns.TurnStarted += round => seen.Add((round, SandstormState.LastAppliedRound,
                SandstormState.Snapshot.Instances[0].Body.Count));
            for (int i = 0; i < 9; i++) NewRound();
            Assert.That(seen.ConvertAll(s => s.WeatherRound), Is.EqualTo(seen.ConvertAll(s => s.Round)));
            Assert.That(seen.ConvertAll(s => s.Size), Is.EqualTo(new[] { 1, 5, 5, 5, 5, 5, 0, 0, 1 }));
            Assert.That(_map.Weather, Is.SameAs(SandstormState.Snapshot), "the map publishes the same snapshot");
        }

        [Test]
        public void FinishedMatch_NeverTicks()
        {
            NewRound();
            typeof(GameTurnController).GetField("_gameOver", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_turns, true);
            NewRound();
            Assert.That(SandstormState.LastAppliedRound, Is.EqualTo(1));
        }
    }
}
#endif
