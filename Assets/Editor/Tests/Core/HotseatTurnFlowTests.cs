#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Map;
using Game.Players;
using Game.Progression;
using Game.Turns;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // Hotseat turn flow on a real GameTurnController (engine-bound: runs inside Unity).
    public sealed class HotseatTurnFlowTests
    {
        private List<PlayerSetupData> previousPlayers;
        private GameObject go;

        [SetUp] public void SetUp() { previousPlayers = GameSession.Players; LocalSeat.Reset(); }
        [TearDown] public void TearDown()
        {
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
            GameSession.Players = previousPlayers; LocalSeat.Reset(); VisionSystem.CurrentViewer = null;
            typeof(GameSession).GetProperty("RewardsEligible").SetValue(null, false);
        }

        private static PlayerSetupData Human(string id) => new PlayerSetupData { PlayerId = id, Nickname = id, IsHuman = true };
        private static PlayerSetupData Ai(string id) => new PlayerSetupData { PlayerId = id, Nickname = id };

        private GameTurnController Controller()
        {
            go = new GameObject("hotseat turn test");
            // Unconfigured reward UI: the controller does not attach a live profile writer.
            go.AddComponent<Game.UI.CollectionRewardUI>();
            return go.AddComponent<GameTurnController>();
        }

        [Test] public void FirstHumanFallingPublishesNothingAndTheOtherHumansWinIsTheReward()
        {
            var a = Human("a"); var b = Human("b"); var c = Ai("c");
            GameSession.Players = new List<PlayerSetupData> { a, b, c };
            typeof(GameSession).GetProperty("RewardsEligible").SetValue(null, true);
            var turns = Controller();
            var results = new List<ParticipantResult>();
            turns.ParticipantFinished += r => results.Add(r);
            var eliminate = typeof(GameTurnController).GetMethod("EliminatePlayer", BindingFlags.NonPublic | BindingFlags.Instance);

            eliminate.Invoke(turns, new object[] { a });
            Assert.That(results, Is.Empty, "The reward screen would return to the menu and end the match for b.");
            Assert.That(turns.Phase, Is.Not.EqualTo(StrategicTurnPhase.Finished));

            eliminate.Invoke(turns, new object[] { c });
            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results[0].Player, Is.SameAs(b));
            Assert.That(results[0].Outcome, Is.EqualTo(MatchOutcome.Victory));
        }

        [Test] public void LastHumanFallingIsTheOnlyDefeat()
        {
            var a = Human("a"); var b = Human("b"); var c = Ai("c"); var d = Ai("d");
            GameSession.Players = new List<PlayerSetupData> { a, b, c, d };
            typeof(GameSession).GetProperty("RewardsEligible").SetValue(null, true);
            var turns = Controller();
            var results = new List<ParticipantResult>();
            turns.ParticipantFinished += r => results.Add(r);
            var eliminate = typeof(GameTurnController).GetMethod("EliminatePlayer", BindingFlags.NonPublic | BindingFlags.Instance);
            eliminate.Invoke(turns, new object[] { a });
            eliminate.Invoke(turns, new object[] { b });
            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results[0].Player, Is.SameAs(b));
            Assert.That(results[0].Outcome, Is.EqualTo(MatchOutcome.Defeat));
            eliminate.Invoke(turns, new object[] { c });
            Assert.That(results.Count, Is.EqualTo(1), "An AI winning after the defeat publishes nothing more.");
        }

        [Test] public void HotseatResumeSeatsTheSavedSecondHumanAndRendersTheirFog()
        {
            var a = Human("a"); var b = Human("b"); var c = Ai("c");
            GameSession.Players = new List<PlayerSetupData> { a, b, c };
            var turns = Controller();
            var state = new GameTurnState { Round = 3, PlayerOrder = new[] { "a", "c", "b" }, CurrentPlayerIndex = 2,
                Completed = new[] { new PlayerTurnCount { PlayerId = "a", CompletedTurns = 3 },
                    new PlayerTurnCount { PlayerId = "b", CompletedTurns = 2 }, new PlayerTurnCount { PlayerId = "c", CompletedTurns = 3 } },
                GreetingsShown = new[] { "a", "b" }, PendingMessages = Array.Empty<PlayerTurnMessages>() };
            turns.ResumeGame(state);
            Assert.That(turns.CurrentPlayer, Is.SameAs(b));
            // No popup panel in this scene: the curtain degrades to an immediate seat.
            Assert.That(LocalSeat.Current, Is.SameAs(b));
            Assert.That(VisionSystem.CurrentViewer, Is.SameAs(b));
            Assert.That(turns.TurnConfirmed, Is.True);
        }
    }
}
#endif
