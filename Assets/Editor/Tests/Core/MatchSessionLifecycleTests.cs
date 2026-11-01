#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Campaign;
using Game.Core;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Terrain;
using NUnit.Framework;
namespace Game.EditorTests
{
    public sealed class MatchSessionLifecycleTests
    {
        [SetUp] public void SetUp() => MatchStateInventory.EndMatch();
        [TearDown] public void TearDown() => MatchStateInventory.EndMatch();
        [Test] public void PreparedLaunchDoesNotShareSetupDraftOrRuntimeState()
        {
            var draftPlayer = new PlayerSetupData { Nickname="draft", Faction=Faction.Ashen, PlayerId="old", IsEliminated=true, CitadelHexQ=1, CitadelHexR=2 };
            var draft = new List<PlayerSetupData> { draftPlayer };
            Assert.That(GameSession.TryPrepareMatch(draft,null,out var error),Is.True,error);
            Assert.That(GameSession.Players,Has.Count.EqualTo(1));
            var runtime=GameSession.Players[0];
            Assert.That(runtime,Is.Not.SameAs(draftPlayer));Assert.That(runtime.PlayerId,Is.Not.EqualTo("old"));
            Assert.That(runtime.IsEliminated,Is.False);Assert.That(runtime.CitadelHexQ,Is.Null);
            draftPlayer.Nickname="changed";draft.Clear();
            Assert.That(runtime.Nickname,Is.EqualTo("draft"));Assert.That(GameSession.Players,Has.Count.EqualTo(1));
        }
        [Test] public void RejectedLaunchPreservesActiveSessionInputs()
        {
            var players=new List<PlayerSetupData>{new PlayerSetupData {PlayerId="live"}};
            var campaign=new CampaignMatchContext {MatchId="live-match"};
            GameSession.Players=players;GameSession.SetCampaignContext(campaign);GameSession.SelectedMapSize=MapSize.Small;
            Assert.That(GameSession.TryPrepareMatch(new List<PlayerSetupData>{new PlayerSetupData{IsHuman=true},new PlayerSetupData{IsHuman=true}},null,out _),Is.False);
            Assert.That(GameSession.MatchId,Is.EqualTo("live-match"));Assert.That(GameSession.CampaignContext,Is.SameAs(campaign));
            Assert.That(GameSession.Players,Is.SameAs(players));Assert.That(GameSession.SelectedMapSize,Is.EqualTo(MapSize.Small));
        }
    }
}
#endif
