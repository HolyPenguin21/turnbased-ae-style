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
        [Test] public void ExitCancelsOperationsBeforeWorldReset()
        {
            var player=new PlayerSetupData();var home=new Game.HexGrid.HexCoord(0,0);
            var building=new BuildingData{Owner=player,Hex=home,IsBase=true};
            var army=new ArmyData{Owner=player,Hex=home};army.Members.Add(new Game.Units.UnitData{Owner=player});
            BuildingRegistry.Register(home,building);ArmyRegistry.Register(army);
            var decision=new MatchDecision();var operation=new ArmyMovementOperation(army,new[]{home},_=>1);
            bool cancelled=false,staleRejected=false,present=false;
            operation.Completed+=result=>{cancelled=result.Kind==ArmyMovementEnd.Cancelled;staleRejected=!decision.TryComplete();present=ReferenceEquals(BuildingRegistry.FindAt(home),building);};
            MatchStateInventory.EndMatch();
            Assert.That(cancelled,Is.True);Assert.That(staleRejected,Is.True);Assert.That(present,Is.True);Assert.That(ArmyRegistry.AllAt(home),Is.Empty);
        }
        [Test] public void InitializeWorldIsIdempotentAndDirectReloadCannotReuseRuntimeRoster()
        {
            Assert.That(GameSession.TryPrepareMatch(new List<PlayerSetupData>{new PlayerSetupData()},null,out _),Is.True);
            GameSession.InitializeWorld(123);var home=new Game.HexGrid.HexCoord(0,0);
            var building=new BuildingData{Owner=GameSession.Players[0],Hex=home,IsBase=true};BuildingRegistry.Register(home,building);
            long generation=GameSession.Generation;GameSession.InitializeWorld(123);
            Assert.That(GameSession.Generation,Is.EqualTo(generation));Assert.That(BuildingRegistry.FindAt(home),Is.SameAs(building));
            GameSession.MarkRunning();GameSession.InitializeWorld(124);
            Assert.That(BuildingRegistry.FindAt(home),Is.Null);Assert.That(GameSession.Players,Is.Empty);Assert.That(GameSession.IsCurrent(generation),Is.False);
        }
        [Test] public void PreparedRequestCanReinitializeTheSameScene()
        {
            Assert.That(GameSession.TryPrepareMatch(new List<PlayerSetupData>{new PlayerSetupData()},null,out _),Is.True);
            GameSession.InitializeWorld(123);var home=new Game.HexGrid.HexCoord(0,0);
            BuildingRegistry.Register(home,new BuildingData{Owner=GameSession.Players[0],Hex=home,IsBase=true});long prior=GameSession.Generation;
            Assert.That(GameSession.TryPrepareMatch(new List<PlayerSetupData>{new PlayerSetupData()},null,out _),Is.True);GameSession.InitializeWorld(123);
            Assert.That(BuildingRegistry.FindAt(home),Is.Null);Assert.That(GameSession.Generation,Is.GreaterThan(prior));
        }
        [Test] public void DecisionCompletionIsOneShotAndCannotCrossSessionExit()
        {
            var completed=new MatchDecision();Assert.That(completed.TryComplete(),Is.True);Assert.That(completed.TryComplete(),Is.False);
            var old=new MatchDecision();MatchStateInventory.EndMatch();
            Assert.That(GameSession.TryPrepareMatch(new List<PlayerSetupData>{new PlayerSetupData()},null,out _),Is.True);
            Assert.That(old.TryComplete(),Is.False);Assert.That(new MatchDecision().TryComplete(),Is.True);
        }
        [Test] public void WorldResetReleasesEncounterBindings()
        {
            var home=new Game.HexGrid.HexCoord(0,0);var hunter=new ArmyData{Owner=new PlayerSetupData(),Hex=home};var target=new ArmyData{Owner=new PlayerSetupData(),Hex=home};
            var prior=new Game.Combat.BattleState(home,hunter,target,new Game.Combat.BattleGrid(),1);MatchStateInventory.ResetForNewMatch();
            var lookup=typeof(Game.Combat.BattleEncounterCoordinator).GetMethod("GetLegacyEncounter",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            Assert.That(lookup.Invoke(null,new object[]{hunter,target}),Is.Not.SameAs(prior));
        }
        [TestCase(false,false,0)][TestCase(false,true,1)][TestCase(true,false,2)][TestCase(true,true,2)]
        public void ContinueRoutesAllFourPresenceCombinations(bool saves,bool campaign,int expected)
        {
            var route=typeof(Game.UI.MainMenuController).GetMethod("ContinueAction",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
            Assert.That(route.Invoke(null,new object[]{saves,campaign}),Is.EqualTo(expected));
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
