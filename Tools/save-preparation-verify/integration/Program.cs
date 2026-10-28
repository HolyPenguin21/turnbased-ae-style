using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using Game.Progression;
using Game.Ai;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Turns;
using Game.Units;

internal static class Program
{
    private static int failures,checks;
    private static int Main()
    {
        Run("real staged restore / exact owner facts / real ResumeGame", FullChain);
        Run("invalid detached graph rejected before destructive reset", InvalidBeforeReset);
        foreach (string stage in new[]{"world","knowledge","presentation"})
        {
            string captured=stage;
            Run("injected "+stage+" failure / actual scene helper cleanup / retry",()=>FailedStage(captured));
        }
        Run("actual ResumeGame validation failure / actual helper cleanup", FailedResume);
        Run("coordinator rejects omitted frozen presentation", MissingPresentation);
        Run("prepared request identity cannot be replaced during restore", ChangedRequest);
        Run("existing setup error follows actual helper failure cleanup", ExistingSetupError);
        Run("same saved payload restores twice after successful live mutations", RepeatSavedPayload);
        Run("actual collection receipt survives tactical restores without permanent replay", CollectionReceiptRoundtrip);
        Console.WriteLine(checks+" integration checks, "+failures+" failures");
        Console.WriteLine("Managed logical chain only: native renderer, map authoring, UI and unrelated AI owners are adapters; no Unity scene executed.");
        return failures==0?0:1;
    }
    private static void Run(string name,Action action)
    {
        MatchStateInventory.EndMatch();
        try { action(); checks++; Console.WriteLine("PASS "+name); }
        catch(Exception ex) { checks++; failures++; Console.WriteLine("FAIL "+name+": "+ex); }
        finally { MatchStateInventory.EndMatch(); ProgressionContext.Collection=null; }
    }
    internal static void Check(bool condition,string message) { if(!condition) throw new Exception(message); }
    private static void FullChain()
    {
        var source=DetachedSource.Capture(); var scene=Scene(source);
        var oldArmy=ArmyRegistry.AllAt(source.Hex).Single();
        Check(!ReferenceEquals(source.Human,oldArmy.Owner),"source player graph must be detached from outgoing owners");
        int worldActions=0, visionActions=0,handActions=0,resourceActions=0,turnNotices=0;
        Action<ArmyData,HexCoord,HexCoord> moved=(a,b,c)=>worldActions++;
        Action<PlayerSetupData> vision=_=>visionActions++;
        Action<HexCoord> eventAction=_=>worldActions++;
        ArmyRegistry.ArmyRelocated+=moved; VisionSystem.VisibilityChanged+=vision;
        HexEventRegistry.EventSkipped+=eventAction; HexEventRegistry.EventConsumed+=eventAction;
        source.Human.Hand.HandChanged+=()=>handActions++;
        source.OnRoot=root=>root.ResourcesChanged+=()=>resourceActions++;
        scene.turnController.TurnStateChanged+=()=> {
            Check(!MatchRestoreScope.IsRestoring,"turn bind must run outside quiet scope");
            Check(GameSession.PendingRestore==source,"CompleteRestore must follow ResumeGame event");
            source.AssertWorld(); scene.hexSelectionController.ValidateRememberedVisualCoverage(); turnNotices++;
        };
        try {
            Check(GameSession.TryPrepareRestore(source,scene.gameConfig,out string error),error);
            Check(GameSession.MatchId==source.MatchId && GameSession.PendingRestore==source,"real staging must preserve match identity");
            Check(ArmyRegistry.IsRegistered(oldArmy),"staging must not reset outgoing world");
            Check(scene.RunPreparedRestore(),GameSession.SetupError);
            Check(!ArmyRegistry.IsRegistered(oldArmy),"actual inventory must clear outgoing registered world");
            Check(scene.ConfigureCalls==1,"one scene configuration/reset");
            Check(source.Stages.SequenceEqual(new[]{"validate","validate","world","knowledge","presentation"}),"restore stage ordering: "+string.Join(",",source.Stages));
            Check(GameSession.PendingRestore==null && !MatchRestoreScope.IsRestoring,"complete request and release scope");
            Check(GameSession.MatchId==source.MatchId && GameSession.RewardsEligible,"identity/reward eligibility retained");
            Check(ReferenceEquals(GameSession.FindHumanPlayer(),source.Human),"canonical player reference");
            Check(ReferenceEquals(scene.turnController.CurrentPlayer,source.Human),"actual ResumeGame canonical human");
            Check(scene.turnController.TurnNumber==8 && scene.turnController.TurnConfirmed,"settled human turn resumes");
            Check(scene.turnController.PresentationBinds==1 && turnNotices==1,"one final presentation bind and turn event");
            Check(worldActions==0 && visionActions==0 && handActions==0 && resourceActions==0,"no replayed world/vision/hand/resource events");
            var turn=scene.turnController.CaptureTurnState();
            Check(turn.Round==8 && turn.PlayerOrder.SequenceEqual(new[]{"ai","human"}) && turn.CurrentPlayerIndex==1,"turn order/index preserved");
            Check(turn.Completed.Single(x=>x.PlayerId=="human").CompletedTurns==7 && turn.Completed.Single(x=>x.PlayerId=="ai").CompletedTurns==8,"owner clocks preserved");
            Check(turn.GreetingsShown.SequenceEqual(new[]{"human","ai"}) && turn.PendingMessages.Single().Messages.SequenceEqual(new[]{"queued before save"}),"greetings/pending reports retained without replay");
            source.AssertWorld(); AssertHand(source.Human.Hand,source.Hand);
            var appearance=scene.hexSelectionController.CaptureRememberedVisuals(visual=>new MapVisualSnapshotReference {TypeKey="frozen",SnapshotKey=visual.Appearance});
            Check(appearance.Armies.Single().Appearance.SnapshotKey=="old-roster" && appearance.Buildings.Single().Appearance.SnapshotKey=="old-owner-color","real appearance import retains old frozen keys");
            var aiHand=AiHandRegistry.Peek(source.Ai);
            AssertHand(aiHand,source.AiHand);
            Check(AiHandRegistry.TryGetOwner(aiHand,out var aiOwner) && ReferenceEquals(aiOwner,source.Ai),"AI hand reverse owner rebound");
            Check(AiHandData.AgeInTurns(aiHand.Hand[0],8)==6,"AI acquisition age retained");
            Check(StealthSystem.IsDetectedBy(source.Units[source.HiddenId],source.Human),"detection window retained");
            SetCompleted(scene.turnController,source.Human,8);
            Check(StealthSystem.IsDetectedBy(source.Units[source.HiddenId],source.Human),"not expired one clock early");
            SetCompleted(scene.turnController,source.Human,9);
            Check(!StealthSystem.IsDetectedBy(source.Units[source.HiddenId],source.Human),"expiry not renewed by load");
            var icons=Field<Dictionary<HexCoord,List<ResourceIconVisual>>>(scene.map.GetComponent<MapResourceDisplay>(),"_iconsByHex");
            Check(icons[source.Hex].Any(x=>x.Amount==6),"real resource display rebuilt combined 1+5 yield after bonuses");
            var markers=Field<Dictionary<HexCoord,EventMarkerVisual>>(scene.map.GetComponent<MapEventDisplay>(),"_markersByHex");
            Check(markers.Count==1 && markers[source.Hex].gameObject.active,"real event display rebuilt discovered remembered skipped event");
            Check(new ArmyData().Id>source.Identity.ArmyHighWaterMark && new UnitData().RuntimeId>source.Identity.UnitHighWaterMark
                && new CardData(source.Definitions["a"]).RuntimeId>source.Identity.CardHighWaterMark,"new identities above destroyed/high water saved range");
            source.Armies[source.ArmyId].ClearTransport();
            Check(source.Units[source.PassengerId].AssignedTransportCarrier==null && source.Units[source.PassengerId].TransportArmy==null,"real restored recipient index allows transport cleanup");
        } finally {
            ArmyRegistry.ArmyRelocated-=moved; VisionSystem.VisibilityChanged-=vision;
            HexEventRegistry.EventSkipped-=eventAction; HexEventRegistry.EventConsumed-=eventAction;
        }
    }
    private static void InvalidBeforeReset()
    {
        var source=DetachedSource.Capture(); var scene=Scene(source);
        Check(GameSession.TryPrepareRestore(source,scene.gameConfig,out var error),error);
        var oldPlayers=GameSession.Players; var oldId=GameSession.MatchId;
        var oldArmy=ArmyRegistry.AllAt(source.Hex).Single(); var bad=DetachedSource.Capture(detachedOnly:true);
        bad.InvalidateActivationReference();
        Check(!GameSession.TryPrepareRestore(bad,scene.gameConfig,out error) && error.Contains("activation"),"invalid graph must reject in source validation");
        Check(ReferenceEquals(GameSession.Players,oldPlayers) && GameSession.MatchId==oldId && GameSession.PendingRestore==source,"failed prepare retains previous staged session atomically");
        Check(ArmyRegistry.IsRegistered(oldArmy) && scene.ConfigureCalls==0,"invalid input rejected before real reset");
    }
    private static void FailedStage(string stage)
    {
        var source=DetachedSource.Capture(); source.FailAt=stage; var scene=Scene(source);
        Check(GameSession.TryPrepareRestore(source,scene.gameConfig,out var error),error);
        Check(!scene.RunPreparedRestore(),"injected "+stage+" failure must fail helper");
        AssertFailureClean(scene,source);
        Check(GameSession.SetupError.Contains("injected "+stage),"actual helper preserves stage error");
        var retry=DetachedSource.Capture(); var retryScene=Scene(retry);
        Check(GameSession.TryPrepareRestore(retry,retryScene.gameConfig,out error),error);
        Check(retryScene.RunPreparedRestore(),GameSession.SetupError);
        retry.AssertWorld(); Check(!MatchRestoreScope.IsRestoring,"retry is not left restoring");
    }
    private static void FailedResume()
    {
        var source=DetachedSource.Capture(); source.CorruptTurnAfterPresentation=true; var scene=Scene(source);
        Check(GameSession.TryPrepareRestore(source,scene.gameConfig,out var error),error);
        Check(!scene.RunPreparedRestore(),"actual ResumeGame must reject invalid human position");
        Check(source.Stages.Last()=="presentation" && GameSession.SetupError.Contains("settled human turn"),"failure must come from actual ResumeGame validation after all source phases");
        AssertFailureClean(scene,source);
    }
    private static void MissingPresentation()
    {
        var source=DetachedSource.Capture(); source.OmitPresentation=true; var scene=Scene(source);
        Check(GameSession.TryPrepareRestore(source,scene.gameConfig,out var error),error);
        Check(!scene.RunPreparedRestore(),"real coordinator must reject knowledge without frozen marker coverage");
        Check(GameSession.SetupError.Contains("frozen"),"coverage rejection must reach actual helper"); AssertFailureClean(scene,source);
    }
    private static void ChangedRequest()
    {
        var source=DetachedSource.Capture(); var scene=Scene(source);
        Check(GameSession.TryPrepareRestore(source,scene.gameConfig,out var error),error);
        scene.turnController.TurnStateChanged+=()=>GameSession.ClearMatch();
        Check(!scene.RunPreparedRestore(),"CompleteRestore must detect request changed during ResumeGame");
        Check(GameSession.SetupError.Contains("request changed"),"actual CompleteRestore identity guard");
        AssertFailureClean(scene,source,expectedBinds:1);
    }
    private static void ExistingSetupError()
    {
        var source=DetachedSource.Capture(); var scene=Scene(source);
        Check(GameSession.TryPrepareRestore(source,scene.gameConfig,out var error),error);
        GameSession.SetupError="map authoring rejected";
        Check(!scene.RunPreparedRestore() && scene.ConfigureCalls==0,"pre-existing map error aborts before configure");
        AssertFailureClean(scene,source);
    }
    private static void RepeatSavedPayload()
    {
        var saved=DetachedSource.Capture();
        var first=saved.NewPreparedSource(); var scene=Scene(first);
        Check(GameSession.TryPrepareRestore(first,scene.gameConfig,out var error),error);
        Check(scene.RunPreparedRestore(),GameSession.SetupError); first.AssertWorld();
        var originalHand=first.Human.Hand;
        originalHand.RemoveCard(originalHand.Hand[0]);
        originalHand.TryAddCard(new CardData(first.Definitions["b"]),allowOverflow:true);
        originalHand.SetCurrentTurn(40);
        PlayerRootRegistry.FindFor(first.Human).ActionPoints=99;
        first.Armies[first.ArmyId].ResetActivationForNewTurn();
        first.Units[first.PassengerId].RestoreMovementSpent(0);
        HexEventRegistry.FindAt(first.Hex).Consumed=true;
        HexResourceBonusRegistry.RestoreState(Array.Empty<HexResourceBonusState>());
        StealthSystem.Clear(); VisionSystem.Clear();
        var second=saved.NewPreparedSource(); var secondScene=Scene(second);
        Check(!ReferenceEquals(first.Human,second.Human) && !ReferenceEquals(originalHand,second.Human.Hand),"fresh prepared player/hand objects");
        Check(!ReferenceEquals(first.Hand,second.Hand) && !ReferenceEquals(first.Hand.cards,second.Hand.cards),"mutable saved hand DTOs copied for each decode");
        Check(GameSession.TryPrepareRestore(second,secondScene.gameConfig,out error),error);
        Check(secondScene.RunPreparedRestore(),GameSession.SetupError);
        second.AssertWorld(); AssertHand(second.Human.Hand,saved.Hand);
        foreach(int id in first.Units.Keys) Check(!ReferenceEquals(first.Units[id],second.Units[id]),"fresh canonical unit for each decoded saved identity");
        Check(!ArmyRegistry.IsRegistered(first.Armies[first.ArmyId]) && ArmyRegistry.IsRegistered(second.Armies[second.ArmyId]),"actual reset replaces outgoing registered graph");
        Check(secondScene.turnController.PresentationBinds==1 && GameSession.PendingRestore==null,"second successful load completed normally");
        Check(saved.Hand.currentTurn==8 && saved.Hand.cards.Count==3 && saved.Hand.cards[0].definitionKey=="b","saved payload unaffected by first live mutation");
    }
    private static void CollectionReceiptRoundtrip()
    {
        var saved=DetachedSource.Capture(); // The same pre-Library tactical payload is used for every decode.
        string directory=Path.Combine(Path.GetTempPath(),"restore-profile-receipt-"+Guid.NewGuid().ToString("N"));
        try {
            var award=new CardDefinition {authoredKey="library.original",cardType=CardType.Unit,deckCopyLimit=3};
            var starting=new StartingDeckCatalog();
            starting.catalogs.Add(new FactionCardCatalog {faction=Faction.Ashen,cards=new List<CardDefinition>{award}});
            var store=new CollectionProfileStore(directory);
            var collection=new CollectionService(new DeckRules(starting,new ResearchProductionCatalog()),store,new CollectionProfile());
            ProgressionContext.Collection=collection;
            var first=saved.NewPreparedSource(); var scene=Scene(first);
            Check(GameSession.TryPrepareRestore(first,scene.gameConfig,out var error),error);
            Check(scene.RunPreparedRestore(),GameSession.SetupError);
            Check(collection.TryGrantRandomCollectionCard(saved.MatchId,"hex:0:0",out var granted,out error),error);
            Check(ReferenceEquals(granted,award) && collection.Owned(award.authoredKey)==1,"actual transaction permanently grants one authored card");
            Check(first.Human.Hand.TryAddCard(new CardData(granted),allowOverflow:true),"actual main hand receives original Library award");
            Check(first.Human.Hand.Hand.Count==saved.Hand.cards.Count+1,"one original tactical award");
            Check(store.Load(out _).eventRewards.Single().cardKey==award.authoredKey,"actual store wrote durable exact reward receipt");
            for(int replay=0;replay<2;replay++) {
                var restored=saved.NewPreparedSource(); var restoredScene=Scene(restored);
                Check(GameSession.TryPrepareRestore(restored,restoredScene.gameConfig,out error),error);
                Check(restoredScene.RunPreparedRestore(),GameSession.SetupError); restored.AssertWorld();
                Check(ReferenceEquals(ProgressionContext.Collection,collection),"actual tactical reset preserves same profile service object");
                AssertHand(restored.Human.Hand,saved.Hand);
                Check(restored.Human.Hand.Hand.All(c=>c.Definition.authoredKey!=award.authoredKey),"pre-event restore removes prior tactical award");
                int handCount=restored.Human.Hand.Hand.Count;
                Check(collection.TryGrantRandomCollectionCard(saved.MatchId,"hex:0:0",out var exact,out error),error);
                Check(ReferenceEquals(exact,award),"receipt replay returns exact original authored definition");
                Check(restored.Human.Hand.TryAddCard(new CardData(exact),allowOverflow:true),"actual hand receives replayed original card");
                Check(restored.Human.Hand.Hand.Count==handCount+1 && restored.Human.Hand.Hand.Count(c=>c.Definition.authoredKey==award.authoredKey)==1,"one tactical award per restored pre-event timeline");
                var persisted=store.Load(out _);
                Check(collection.Owned(award.authoredKey)==1 && persisted.Owned(award.authoredKey)==1 && persisted.eventRewards.Count==1,"real receipt prevents second permanent grant across tactical resets");
                Check(collection.Snapshot.pendingRewards.Count==0 && collection.Snapshot.claimedMatchIds.Count==0,"Library never creates match-result receipts");
            }
            // A genuinely reloaded service also sees the durable ledger, independent of memory.
            var reloaded=new CollectionService(collection.Rules,store,store.Load(out _));
            Check(reloaded.TryGrantRandomCollectionCard(saved.MatchId,"hex:0:0",out var diskReplay,out var diskError),diskError);
            Check(ReferenceEquals(diskReplay,award) && reloaded.Owned(award.authoredKey)==1,"disk reload preserves receipt and permanent ownership together");
        } finally {
            ProgressionContext.Collection=null;
            if(Directory.Exists(directory)) Directory.Delete(directory,true);
        }
    }
    private static void AssertFailureClean(CitadelSetupController scene,DetachedSource source,int expectedBinds=0)
    {
        Check(!MatchRestoreScope.IsRestoring,"failure leaked restore scope");
        Check(GameSession.PendingRestore==null && GameSession.MatchId==null && GameSession.Players.Count==0 && !GameSession.RewardsEligible,"actual EndMatch clears staged session");
        Check(!ArmyRegistry.AllOccupiedHexes().Any() && !BuildingRegistry.AllBuildings().Any(),"actual EndMatch clears world registries");
        Check(PlayerRootRegistry.FindFor(source.Human)==null && AiHandRegistry.Peek(source.Ai)==null,"actual root/hand registry cleanup");
        Check(HexEventRegistry.FindAt(source.Hex)==null && HexResourceBonusRegistry.GetBonus(source.Hex)==null,"actual event/bonus cleanup");
        Check(!VisionSystem.HasEverSeen(source.Human,source.Hex) && HumanVisualMemory.CaptureState(_=>"unused").Players.Count==0
            && StealthSystem.CaptureState().Detections.Count==0 && AntiAirState.CaptureState().Prompted.Count==0,"actual knowledge/stealth/AA cleanup");
        Check(scene.turnController.ExitCalls==1 && scene.turnController.PresentationBinds==expectedBinds,"actual helper prepares outgoing controller once");
    }
    private static void AssertHand(PlayerHandData actual,HandState saved)
    {
        var now=actual.CaptureState();
        Check(now.capacity==saved.capacity && now.currentTurn==saved.currentTurn && now.mutationVersion==saved.mutationVersion,"capacity/turn/mutation version changed");
        Check(now.cards.Select(x=>(x.runtimeId,x.definitionKey,x.equipmentKey,x.mutatorKey,x.acquiredTurn,x.researchProductionCreated))
            .SequenceEqual(saved.cards.Select(x=>(x.runtimeId,x.definitionKey,x.equipmentKey,x.mutatorKey,x.acquiredTurn,x.researchProductionCreated))),"hand order/identity/attachments/age changed");
        Check(now.remainingDeckKeys.SequenceEqual(saved.remainingDeckKeys),"draw pool order/multiplicity changed");
    }
    private static CitadelSetupController Scene(DetachedSource source)
    {
        var config=new GameConfig(); var map=new HexMap(); var selection=new HexSelectionController();
        var turns=new GameTurnController {hexSelectionController=selection};
        var resources=new MapResourceDisplay(); var events=new MapEventDisplay();
        resources.Components[typeof(HexMap)]=map; events.Components[typeof(HexMap)]=map;
        map.Components[typeof(MapResourceDisplay)]=resources; map.Components[typeof(MapEventDisplay)]=events;
        SetField(resources,"gameConfig",config); SetField(events,"gameConfig",config);
        return new CitadelSetupController { gameConfig=config,map=map,turnController=turns,hexSelectionController=selection };
    }
    private static T Field<T>(object owner,string name)=>(T)owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(owner);
    private static void SetField(object owner,string name,object value)=>owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(owner,value);
    private static void SetCompleted(GameTurnController turns,PlayerSetupData owner,long value)=>Field<Dictionary<PlayerSetupData,long>>(turns,"_completedTurns")[owner]=value;
}

// Test-only, detached in-memory decoder. It stores primitive/owner DTO facts and recreates a
// canonical entity graph by saved ids. No file codec, gameplay factories, UI or profile grants.
internal sealed class DetachedSource : IMatchRestoreSource
{
    public string MatchId {get;}="saved-match-fixed";
    public bool RewardsEligible=>true;
    public IReadOnlyList<PlayerSetupData> Players {get;private set;}
    public Game.Campaign.CampaignMatchContext CampaignContext=>null;
    public HexMapLayout Map {get;private set;}=new HexMapLayout();
    public GameTurnState Turn {get;private set;}
    public MatchIdentityState Identity {get;private set;}
    public PlayerSetupData Human=>Players[0]; public PlayerSetupData Ai=>Players[1];
    public readonly HexCoord Hex=new HexCoord(0,0),HiddenHex=new HexCoord(4,0);
    public readonly List<string> Stages=new List<string>();
    public readonly Dictionary<string,CardDefinition> Definitions=new Dictionary<string,CardDefinition>();
    public readonly Dictionary<int,UnitData> Units=new Dictionary<int,UnitData>();
    public readonly Dictionary<int,ArmyData> Armies=new Dictionary<int,ArmyData>();
    public HandState Hand,AiHand; public string FailAt; public bool OmitPresentation,CorruptTurnAfterPresentation;
    public Action<PlayerRoot> OnRoot;
    public int ArmyId,TransportId,PassengerId,DepartedId,HiddenId,AirfieldId,DeckId,EnemyArmyId;
    private ArmyActivationState activation; private UnitMovementState passengerMovement;
    private UnitCarrierLinksState carrierLinks; private PlayerEconomyState economy;
    private VisionState fog; private StealthState stealth; private AntiAirTurnState antiAir;
    private HumanVisualMemoryState visualMemory; private RememberedMapVisualState appearances;
    private List<HexResourceBonusState> bonuses; private List<HexEventEntryState<EventFact>> events;
    private sealed class EventFact {
        public bool Consumed,Triggered,Skipped,Pending;
        public string GuardName,GuardOwnerId,Error; public string[] DiscoveredIds,KnownIds;
        public int RewardMaterials;
    }
    private string snapshotName; private int snapshotHp;
    private PlayerSetupData ResolvePlayer(string id)=>Players.SingleOrDefault(p=>p.PlayerId==id);
    private UnitData ResolveUnit(int id)=>Units.TryGetValue(id,out var unit)?unit:null;
    private ArmyData ResolveArmy(int id)=>Armies.TryGetValue(id,out var army)?army:null;
    public static DetachedSource Capture(bool detachedOnly=false)
    {
        var source=new DetachedSource();
        foreach(string key in new[]{"a","b","gear"}) source.Definitions.Add(key,new CardDefinition {authoredKey=key,cardType=CardType.Unit});
        int cardStart=CardData.IdentityHighWaterMark+10;
        source.Hand=new HandState {capacity=2,currentTurn=8,mutationVersion=12,
            cards=new List<CardState> {
                new CardState {runtimeId=cardStart+3,definitionKey="b",equipmentKey="gear",acquiredTurn=4},
                new CardState {runtimeId=cardStart+1,definitionKey="a",acquiredTurn=1},
                new CardState {runtimeId=cardStart+2,definitionKey="a",acquiredTurn=7,researchProductionCreated=true}},
            remainingDeckKeys=new List<string>{"b","a","b"}};
        source.AiHand=new HandState {capacity=4,currentTurn=8,mutationVersion=6,cards=new List<CardState> {
            new CardState {runtimeId=cardStart+4,definitionKey="a",acquiredTurn=2}}, remainingDeckKeys=new List<string>{"a","b","a"}};
        source.Players=new[]{new PlayerSetupData {PlayerId="human",IsHuman=true,Hand=PlayerHandData.FromState(source.Hand,key=>source.Definitions[key])},new PlayerSetupData {PlayerId="ai"}};
        source.Turn=new GameTurnState {Round=8,PlayerOrder=new[]{"ai","human"},CurrentPlayerIndex=1,
            Completed=new[]{new PlayerTurnCount {PlayerId="human",CompletedTurns=7},new PlayerTurnCount {PlayerId="ai",CompletedTurns=8}},
            GreetingsShown=new[]{"human","ai"},PendingMessages=new[]{new PlayerTurnMessages {PlayerId="human",Messages=new[]{"queued before save"}}}};
        // Capture owner facts through the same production boundaries the future codec will use.
        var oldHuman=new PlayerSetupData {PlayerId="human",IsHuman=true}; var oldAi=new PlayerSetupData {PlayerId="ai"};
        var army=new ArmyData {Owner=oldHuman,Hex=source.Hex}; var airfield=new ArmyData {Owner=oldHuman,IsAirfield=true}; var deck=new ArmyData {Owner=oldHuman};
        var transport=new UnitData {Owner=oldHuman,AirfieldCapacity=3}; var passenger=new UnitData {Owner=oldHuman,MoveMax=3,MoveCurrent=2};
        var departed=new UnitData {Owner=oldHuman};
        var hidden=new UnitData {Owner=oldAi,IsHidden=true,HasEnteredStealthThisTurn=true,Name="hidden current",HitPointsCurrent=1};
        source.ArmyId=army.Id; source.AirfieldId=airfield.Id; source.DeckId=deck.Id;
        source.TransportId=transport.RuntimeId; source.PassengerId=passenger.RuntimeId; source.DepartedId=departed.RuntimeId; source.HiddenId=hidden.RuntimeId;
        army.Members.Add(transport); army.Members.Add(passenger); army.Members.Add(departed); army.MarkActivated(); army.Members.Remove(departed);
        source.activation=army.CaptureActivationState();
        passenger.SetTransportCarrier(transport,army); passenger.RestoreMovementSpent(7); source.passengerMovement=passenger.CaptureMovementState();
        transport.CarrierAirfield=airfield; transport.CarrierFlightDeck=deck; source.carrierLinks=transport.CaptureCarrierLinksState();
        // Original live objects deliberately differ from detached source players and later clones.
        var enemy=new ArmyData {Owner=oldAi,Hex=source.HiddenHex,Name="enemy"}; var observed=new UnitData {Owner=oldAi,Name="historical",HitPointsCurrent=3,MoveMax=2}; enemy.Members.Add(observed);
        source.EnemyArmyId=enemy.Id;
        if (!detachedOnly) {
            using(MatchRestoreScope.Begin()) { ArmyRegistry.Register(army); BuildingRegistry.Register(source.Hex,new BuildingData {Owner=oldHuman,Hex=source.Hex}); }
            HumanVisualMemory.ObserveArmy(oldHuman,enemy,source.HiddenHex); HumanVisualMemory.ObserveBuilding(oldHuman,source.HiddenHex,true);
            source.visualMemory=HumanVisualMemory.CaptureState(display=>{source.snapshotName=display.Members[0].Name; source.snapshotHp=display.Members[0].HitPointsCurrent; return "old-sighting";});
            observed.Name="changed behind fog"; observed.HitPointsCurrent=1;
            StealthSystem.CompletedTurnsProvider=_=>7; StealthSystem.ObserverTakingTurnProvider=_=>true; StealthSystem.MarkDetected(hidden,oldHuman);
            source.stealth=StealthSystem.CaptureState(); AntiAirState.RecordPrompted(hidden,enemy.Id,false); source.antiAir=AntiAirState.CaptureState();
            HexResourceBonusRegistry.RestoreState(new[]{new HexResourceBonusState {Hex=source.Hex,Bonus=new ResourceYields {human=5,tech=2}}});
            source.bonuses=HexResourceBonusRegistry.CaptureState();
            var reward=new RewardEntry {resources=new ResourceYields {materials=9}};
            var entry=new HexEventRegistry.Entry {Consumed=false,Triggered=true,Skipped=true,CollectionRewardPending=true,CollectionRewardError="prior store failure",
                GuardArmyName="already defeated",GuardOwner=oldAi,SelectedRewards=new List<RewardEntry>{reward}};
            entry.DiscoveredBy.Add(oldHuman); entry.KnownBy.Add(oldHuman);
            HexEventRegistry.RestoreEntries(new[]{new HexEventEntryState<HexEventRegistry.Entry> {Hex=source.Hex,Payload=entry}},(hex,payload)=>payload);
            source.events=HexEventRegistry.CaptureEntries((hex,payload)=>new EventFact {
                Consumed=payload.Consumed,Triggered=payload.Triggered,Skipped=payload.Skipped,Pending=payload.CollectionRewardPending,
                GuardName=payload.GuardArmyName,GuardOwnerId=payload.GuardOwner.PlayerId,Error=payload.CollectionRewardError,
                DiscoveredIds=payload.DiscoveredBy.Select(p=>p.PlayerId).ToArray(),KnownIds=payload.KnownBy.Select(p=>p.PlayerId).ToArray(),
                RewardMaterials=payload.SelectedRewards.Single().resources.materials });
            var oldRoot=PlayerRoot.Create(oldHuman,"outgoing"); PlayerRootRegistry.Register(oldHuman,oldRoot);
            oldRoot.RestoreEconomyState(new PlayerEconomyState {ActionPoints=4,Human=2,Energy=3,Materials=5,Tech=7,InitiativeContributions=new List<ResourceType>{ResourceType.Human,ResourceType.Tech},LastApFromInitiative=6,LastApBonusSources="saved source"});
            source.economy=oldRoot.CaptureEconomyState();
        }
        source.fog=new VisionState {CurrentViewerId="human",Players=new List<PlayerVisionState> {
            new PlayerVisionState {PlayerId="human",Visible=new List<HexCoord>{source.Hex},Visited=new List<HexCoord>{source.Hex},EverSeen=new List<HexCoord>{source.Hex,source.HiddenHex}},
            new PlayerVisionState {PlayerId="ai",Visible=new List<HexCoord>(),Visited=new List<HexCoord>(),EverSeen=new List<HexCoord>()}}};
        source.appearances=new RememberedMapVisualState {
            Buildings=new List<RememberedBuildingVisualState>{new RememberedBuildingVisualState {ViewerId="human",Hex=source.HiddenHex,Appearance=new MapVisualSnapshotReference {TypeKey="building",SnapshotKey="old-owner-color"}}},
            Armies=new List<RememberedArmyVisualState>{new RememberedArmyVisualState {ViewerId="human",SourceArmyId=enemy.Id,Hex=source.HiddenHex,Appearance=new MapVisualSnapshotReference {TypeKey="army",SnapshotKey="old-roster"}}}};
        source.Identity=MatchIdentityState.Capture(); source.Identity.ArmyHighWaterMark+=50; source.Identity.UnitHighWaterMark+=50; source.Identity.CardHighWaterMark+=50;
        return source;
    }
    // Every call decodes the same saved values into a fresh request and canonical object graph.
    // Copy all mutable DTO containers; no live restored hand/entity/knowledge object is reused.
    public DetachedSource NewPreparedSource()
    {
        var copy=new DetachedSource {
            Map=Map.Copy(), ArmyId=ArmyId,TransportId=TransportId,PassengerId=PassengerId,DepartedId=DepartedId,
            HiddenId=HiddenId,AirfieldId=AirfieldId,DeckId=DeckId,EnemyArmyId=EnemyArmyId,
            Hand=CopyHand(Hand),AiHand=CopyHand(AiHand),snapshotName=snapshotName,snapshotHp=snapshotHp,
            Identity=new MatchIdentityState {ArmyHighWaterMark=Identity.ArmyHighWaterMark,UnitHighWaterMark=Identity.UnitHighWaterMark,CardHighWaterMark=Identity.CardHighWaterMark},
            Turn=new GameTurnState {Round=Turn.Round,PlayerOrder=Turn.PlayerOrder.ToArray(),CurrentPlayerIndex=Turn.CurrentPlayerIndex,
                Completed=Turn.Completed.Select(c=>new PlayerTurnCount {PlayerId=c.PlayerId,CompletedTurns=c.CompletedTurns}).ToArray(),
                GreetingsShown=Turn.GreetingsShown.ToArray(),PendingMessages=Turn.PendingMessages.Select(m=>new PlayerTurnMessages {PlayerId=m.PlayerId,Messages=m.Messages.ToArray()}).ToArray()},
            activation=new ArmyActivationState {HasActivated=activation.HasActivated,CoveredUnitIds=new List<int>(activation.CoveredUnitIds)},
            passengerMovement=new UnitMovementState {PermanentMaximum=passengerMovement.PermanentMaximum,LegacyCurrent=passengerMovement.LegacyCurrent,Spent=passengerMovement.Spent,TransportCarrierId=passengerMovement.TransportCarrierId,TransportArmyId=passengerMovement.TransportArmyId},
            carrierLinks=new UnitCarrierLinksState {AirfieldArmyId=carrierLinks.AirfieldArmyId,FlightDeckArmyId=carrierLinks.FlightDeckArmyId},
            economy=new PlayerEconomyState {ActionPoints=economy.ActionPoints,Human=economy.Human,Energy=economy.Energy,Materials=economy.Materials,Tech=economy.Tech,
                InitiativeContributions=new List<ResourceType>(economy.InitiativeContributions),LastApFromInitiative=economy.LastApFromInitiative,LastApFromPrisonBonus=economy.LastApFromPrisonBonus,LastApFromApBonus=economy.LastApFromApBonus,LastApBonusSources=economy.LastApBonusSources},
            fog=new VisionState {CurrentViewerId=fog.CurrentViewerId,Players=fog.Players.Select(p=>new PlayerVisionState {PlayerId=p.PlayerId,Visible=new List<HexCoord>(p.Visible),Visited=new List<HexCoord>(p.Visited),EverSeen=new List<HexCoord>(p.EverSeen)}).ToList()},
            stealth=new StealthState {Detections=stealth.Detections.Select(d=>new DetectionState {UnitId=d.UnitId,ObserverId=d.ObserverId,Expiry=d.Expiry}).ToList(),Notices=stealth.Notices.Select(n=>new DetectionNoticesState {PlayerId=n.PlayerId,Notices=new List<string>(n.Notices)}).ToList()},
            antiAir=new AntiAirTurnState {FiredUnitIds=new List<int>(antiAir.FiredUnitIds),Prompted=antiAir.Prompted.Select(p=>new AntiAirPromptState {UnitId=p.UnitId,AirArmyId=p.AirArmyId}).ToList(),FacilityGunners=antiAir.FacilityGunners.Select(g=>new FacilityGunnerState {BuildingHex=g.BuildingHex,Slot=g.Slot,UnitId=g.UnitId}).ToList()},
            visualMemory=new HumanVisualMemoryState {ObserverMatch=visualMemory.ObserverMatch,Players=visualMemory.Players.Select(p=>new HumanPlayerMemoryState {PlayerId=p.PlayerId,KnownBuildings=new List<HexCoord>(p.KnownBuildings),ArmySightings=p.ArmySightings.Select(a=>new HumanArmySightingState {SourceArmyId=a.SourceArmyId,Hex=a.Hex,DisplaySnapshotKey=a.DisplaySnapshotKey}).ToList()}).ToList()},
            appearances=new RememberedMapVisualState {
                Buildings=appearances.Buildings.Select(b=>new RememberedBuildingVisualState {ViewerId=b.ViewerId,Hex=b.Hex,Appearance=CopyAppearance(b.Appearance)}).ToList(),
                Armies=appearances.Armies.Select(a=>new RememberedArmyVisualState {ViewerId=a.ViewerId,SourceArmyId=a.SourceArmyId,Hex=a.Hex,Appearance=CopyAppearance(a.Appearance)}).ToList()},
            bonuses=bonuses.Select(b=>new HexResourceBonusState {Hex=b.Hex,Bonus=new ResourceYields {human=b.Bonus.human,energy=b.Bonus.energy,materials=b.Bonus.materials,tech=b.Bonus.tech}}).ToList(),
            events=events.Select(e=>new HexEventEntryState<EventFact> {Hex=e.Hex,Payload=new EventFact {Consumed=e.Payload.Consumed,Triggered=e.Payload.Triggered,Skipped=e.Payload.Skipped,Pending=e.Payload.Pending,
                GuardName=e.Payload.GuardName,GuardOwnerId=e.Payload.GuardOwnerId,Error=e.Payload.Error,DiscoveredIds=e.Payload.DiscoveredIds.ToArray(),KnownIds=e.Payload.KnownIds.ToArray(),RewardMaterials=e.Payload.RewardMaterials}}).ToList()
        };
        foreach(var definition in Definitions) copy.Definitions.Add(definition.Key,definition.Value);
        copy.Players=new[]{new PlayerSetupData {PlayerId=Human.PlayerId,IsHuman=true,Hand=PlayerHandData.FromState(copy.Hand,key=>copy.Definitions[key])},new PlayerSetupData {PlayerId=Ai.PlayerId}};
        return copy;
    }
    private static MapVisualSnapshotReference CopyAppearance(MapVisualSnapshotReference p)=>new MapVisualSnapshotReference {TypeKey=p.TypeKey,SnapshotKey=p.SnapshotKey};
    private static HandState CopyHand(HandState hand)=>new HandState {capacity=hand.capacity,currentTurn=hand.currentTurn,mutationVersion=hand.mutationVersion,
        cards=hand.cards.Select(c=>new CardState {runtimeId=c.runtimeId,definitionKey=c.definitionKey,equipmentKey=c.equipmentKey,mutatorKey=c.mutatorKey,researchProductionCreated=c.researchProductionCreated,acquiredTurn=c.acquiredTurn}).ToList(),remainingDeckKeys=new List<string>(hand.remainingDeckKeys)};
    public void InvalidateActivationReference()=>activation.CoveredUnitIds.Add(Identity.UnitHighWaterMark+100);
    public void Validate(GameConfig config)
    {
        Stages.Add("validate");
        if(activation.CoveredUnitIds.Any(id=>id!=TransportId && id!=PassengerId && id!=DepartedId)) throw new ArgumentException("unresolved activation reference");
        if(Players.Any(p=>string.IsNullOrEmpty(p.PlayerId)) || Players.Select(p=>p.PlayerId).Distinct().Count()!=2) throw new ArgumentException("players");
        if(Identity.UnitHighWaterMark<new[]{TransportId,PassengerId,DepartedId,HiddenId}.Max() || Identity.ArmyHighWaterMark<new[]{ArmyId,AirfieldId,DeckId,EnemyArmyId}.Max()) throw new ArgumentException("identity range");
    }
    public void RestoreWorld(HexMap map)
    {
        Stages.Add("world"); Program.Check(MatchRestoreScope.IsRestoring,"world outside quiet scope");
        foreach(int id in new[]{ArmyId,AirfieldId,DeckId,EnemyArmyId}) Armies.Add(id,ArmyData.WithIdentity(id));
        foreach(int id in new[]{TransportId,PassengerId,DepartedId,HiddenId}) Units.Add(id,UnitData.WithRuntimeId(id));
        var army=Armies[ArmyId]; army.Owner=Human; army.Hex=Hex; army.Members.Add(Units[TransportId]); army.Members.Add(Units[PassengerId]);
        Units[TransportId].Owner=Human; Units[TransportId].AirfieldCapacity=3;
        Units[PassengerId].Owner=Human; Units[DepartedId].Owner=Human; Units[HiddenId].Owner=Ai; Units[HiddenId].IsHidden=true; Units[HiddenId].HasEnteredStealthThisTurn=true;
        Armies[EnemyArmyId].Owner=Ai; Armies[EnemyArmyId].Hex=HiddenHex; Armies[EnemyArmyId].Members.Add(Units[HiddenId]);
        ArmyRegistry.Register(army); ArmyRegistry.Register(Armies[EnemyArmyId]);
        var root=PlayerRoot.Create(Human,"restored"); PlayerRootRegistry.Register(Human,root); OnRoot?.Invoke(root); root.RestoreEconomyState(economy);
        if(FailAt=="world") throw new Exception("injected world");
        BuildingRegistry.Register(Hex,new BuildingData {Owner=Human,Hex=Hex});
        army.RestoreActivationState(activation,ResolveUnit);
        Units[PassengerId].RestoreMovementState(passengerMovement,ResolveUnit,ResolveArmy);
        Units[TransportId].RestoreCarrierLinksState(carrierLinks,ResolveArmy);
        army.RebindRestoredTransportRecipients();
        AntiAirState.RestoreState(antiAir,ResolveUnit);
        HexResourceBonusRegistry.RestoreState(bonuses);
        HexEventRegistry.RestoreEntries(events,(hex,payload)=>new HexEventRegistry.Entry {
            Consumed=payload.Consumed,Triggered=payload.Triggered,Skipped=payload.Skipped,CollectionRewardPending=payload.Pending,
            GuardArmyName=payload.GuardName,GuardOwner=ResolvePlayer(payload.GuardOwnerId),CollectionRewardError=payload.Error,
            DiscoveredBy=new HashSet<PlayerSetupData>(payload.DiscoveredIds.Select(ResolvePlayer)),KnownBy=new HashSet<PlayerSetupData>(payload.KnownIds.Select(ResolvePlayer)),
            SelectedRewards=new List<RewardEntry>{new RewardEntry {resources=new ResourceYields {materials=payload.RewardMaterials}}}
        });
        AiHandRegistry.RegisterRestored(Ai,AiHandData.FromState(AiHand,key=>Definitions[key]));
    }
    public void RestoreKnowledge()
    {
        Stages.Add("knowledge"); Program.Check(MatchRestoreScope.IsRestoring,"knowledge outside quiet scope");
        VisionSystem.RestoreState(fog,ResolvePlayer); StealthSystem.RestoreState(stealth,ResolveUnit,ResolvePlayer);
        if(FailAt=="knowledge") throw new Exception("injected knowledge");
        HumanVisualMemory.RestoreState(visualMemory,ResolvePlayer,key=> {
            var snapshot=ArmyData.CreateVisualSnapshot(); snapshot.Owner=Ai; snapshot.Hex=HiddenHex;
            var unit=UnitData.CreateProjection(); unit.Name=snapshotName; unit.HitPointsCurrent=snapshotHp; snapshot.Members.Add(unit); return snapshot;
        });
    }
    public void RestorePresentation(HexSelectionController selection)
    {
        Stages.Add("presentation"); Program.Check(MatchRestoreScope.IsRestoring,"presentation outside quiet scope");
        if(!OmitPresentation) selection.RestoreRememberedVisuals(appearances,ResolvePlayer,
            reference=>new MapObjectVisual {IsFrozenRememberedAppearance=true,Appearance=reference.SnapshotKey});
        if(FailAt=="presentation") throw new Exception("injected presentation");
        // Fault injection intentionally violates the immutable-source contract after preflight.
        // This exercises the real ResumeGame validation boundary, not a replacement method.
        if(CorruptTurnAfterPresentation) Turn.CurrentPlayerIndex=0;
    }
    public void AssertWorld()
    {
        var root=PlayerRootRegistry.FindFor(Human); var now=root.CaptureEconomyState();
        Program.Check(now.ActionPoints==4 && now.Human==2 && now.Energy==3 && now.Materials==5 && now.Tech==7
            && now.InitiativeContributions.SequenceEqual(economy.InitiativeContributions) && now.LastApFromInitiative==6 && now.LastApBonusSources=="saved source","no AP/income/token replay");
        var army=Armies[ArmyId]; var passenger=Units[PassengerId]; var departed=Units[DepartedId]; var transport=Units[TransportId];
        Program.Check(!army.Members.Contains(departed) && army.Members.Contains(passenger) && army.HasActivatedThisTurn && !army.RequiresActivationCharge(departed),"departed paid unit retained canonically without becoming a member");
        Program.Check(army.CaptureActivationState().CoveredUnitIds.SequenceEqual(activation.CoveredUnitIds),"activation coverage exact");
        Program.Check(passenger.MovementSpent==7 && passenger.PermanentMoveMax==3 && ReferenceEquals(passenger.AssignedTransportCarrier,transport) && ReferenceEquals(passenger.TransportArmy,army),"movement spent/transport canonical links retained");
        Program.Check(ReferenceEquals(transport.CarrierAirfield,Armies[AirfieldId]) && ReferenceEquals(transport.CarrierFlightDeck,Armies[DeckId]),"carrier containers reused canonically");
        Program.Check(!AntiAirState.CanReact(Units[HiddenId],EnemyArmyId) && AntiAirState.CanReact(Units[HiddenId],EnemyArmyId+1),"skipped AA pair retained without refill");
        Program.Check(Units[HiddenId].HasEnteredStealthThisTurn,"stealth turn toggle preserved");
        Program.Check(StealthSystem.CaptureState().Detections.Single().Expiry==stealth.Detections.Single().Expiry,"detection expiry exact");
        Program.Check(VisionSystem.IsVisited(Human,Hex) && VisionSystem.HasEverSeen(Human,HiddenHex) && !VisionSystem.IsVisible(Human,HiddenHex),"visible/visited/everseen distinct");
        Program.Check(HumanVisualMemory.TryGetArmySighting(Human,EnemyArmyId,out var sighting) && sighting.Army.Id==-1 && sighting.Army.Members[0].HitPointsCurrent==3 && sighting.Army.Members[0].Name=="historical","frozen historical snapshot preserved over changed hidden live world");
        var evt=HexEventRegistry.FindAt(Hex);
        Program.Check(evt.Triggered && evt.Skipped && !evt.Consumed && evt.CollectionRewardPending && evt.CollectionRewardError=="prior store failure"
            && evt.SelectedRewards.Single().resources.materials==9 && evt.DiscoveredBy.Contains(Human),"event reward/pending/guard flags retained without reward replay");
        Program.Check(HexResourceBonusRegistry.GetBonus(Hex).human==5,"bonus retained");
    }
}
