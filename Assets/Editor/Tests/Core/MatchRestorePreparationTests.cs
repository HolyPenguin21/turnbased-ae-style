#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Campaign;
using Game.Players;
using Game.Setup;
using Game.Turns;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class MatchRestorePreparationTests
    {
        [Test]
        public void ResumeKeepsRoundAndCompletedTurnsWithoutStartingNewTurn()
        {
            var previous = GameSession.Players;
            var human = new PlayerSetupData { PlayerId = "human", IsHuman = true };
            var ai = new PlayerSetupData { PlayerId = "ai" };
            var go = new GameObject("resume test");
            try
            {
                GameSession.Players = new List<PlayerSetupData> {human, ai};
                var turns = go.AddComponent<GameTurnController>();
                var state = new GameTurnState {Round = 7, PlayerOrder = new[]{"ai", "human"}, CurrentPlayerIndex=1,
                    Completed = new[]{new PlayerTurnCount{PlayerId="ai",CompletedTurns=7},new PlayerTurnCount{PlayerId="human",CompletedTurns=6}},
                    GreetingsShown = new[]{"human"}, PendingMessages=Array.Empty<PlayerTurnMessages>()};
                int roundStarts = 0;
                turns.TurnStarted += _ => roundStarts++;
                turns.ResumeGame(state);
                Assert.That(turns.TurnNumber, Is.EqualTo(7));
                Assert.That(turns.CurrentPlayer, Is.SameAs(human));
                Assert.That(turns.TurnConfirmed, Is.True);
                Assert.That(turns.CompletedTurnsFor(human), Is.EqualTo(6));
                Assert.That(roundStarts, Is.Zero, "Resume must not execute a round-start lifecycle.");
            }
            finally {UnityEngine.Object.DestroyImmediate(go);GameSession.Players=previous;}
        }
        [Test]
        public void InvalidPreparedRestoreDoesNotReplaceTheCurrentSession()
        {
            var previous = GameSession.Players;
            var current = new List<PlayerSetupData> {new PlayerSetupData {PlayerId="current",IsHuman=true}};
            try
            {
                GameSession.Players = current;
                string matchId = GameSession.MatchId;
                Assert.That(GameSession.TryPrepareRestore(null, null, out string error), Is.False);
                Assert.That(error, Is.Not.Empty);
                Assert.That(GameSession.Players, Is.SameAs(current));
                Assert.That(GameSession.MatchId, Is.EqualTo(matchId));
            }
            finally {GameSession.Players=previous;}
        }

        [TestCase("valid", true)]
        [TestCase("differentCampaign", false)]
        [TestCase("differentMatch", false)]
        [TestCase("differentDeck", false)]
        [TestCase("recordedResult", false)]
        [TestCase("ownershipApplied", false)]
        [TestCase("automatic", false)]
        public void CampaignRestoreRequiresTheSameUnfinishedManualOperation(string change, bool expected)
        {
            var context = new CampaignMatchContext {CampaignId="campaign",OperationId="op",MatchId="match",SelectedHumanDeckId="deck",
                HumanFaction=Faction.IronConcord,AttackerFaction=Faction.IronConcord,DefenderFaction=Faction.Ashen,SourceRegionId=1,TargetRegionId=2};
            var op = new CampaignOperation {OperationId="op",MatchId="match",SelectedHumanDeckId="deck",Manual=true,
                AttackerFaction=Faction.IronConcord,DefenderFaction=Faction.Ashen,SourceRegionId=1,TargetRegionId=2};
            var state = new CampaignState {CampaignId="campaign",HumanFaction=Faction.IronConcord,PendingOperation=op};
            switch (change)
            {
                case "differentCampaign":state.CampaignId="other";break;
                case "differentMatch":op.MatchId="other";break;
                case "differentDeck":op.SelectedHumanDeckId="other";break;
                case "recordedResult":op.ResultRecorded=true;break;
                case "ownershipApplied":op.OwnershipApplied=true;break;
                case "automatic":op.Manual=false;break;
            }
            Assert.That(CampaignMatchBridge.ValidateRestoreContext(state,context,out _),Is.EqualTo(expected));
        }

        [Test]
        public void SavePolicyAllowsSettledTurnButRejectsEachPendingOperation()
        {
            var ready = new MatchSaveConditions {HasMatch=true,HumanTurn=true,TurnConfirmed=true};
            Assert.That(MatchSaveEligibility.UnavailableReason(ready), Is.Null);
            foreach (var field in typeof(MatchSaveConditions).GetFields(BindingFlags.Instance|BindingFlags.Public))
            {
                object changed = ready;
                field.SetValue(changed, !(bool)field.GetValue(changed));
                Assert.That(MatchSaveEligibility.UnavailableReason((MatchSaveConditions)changed), Is.Not.Null, field.Name);
            }
        }
    }
}
#endif
