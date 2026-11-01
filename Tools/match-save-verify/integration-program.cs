using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Game.Ai;
using Game.Ai.V2;
using Game.Aviation;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Saves;
using Game.Setup;
using Game.Terrain;
using Game.Turns;

// Native construction only: initialize collection fields without Unity Object constructors.
// Production owners subsequently capture/restore these fields through their real boundaries.
internal static class IntegrationNative
{
    internal static int Frame = 100;
    internal static int SceneHandle = 1;
    internal static string ProfileDirectory;
    internal static MatchSaveAssetManifest Manifest;
    internal static T New<T>() where T : class
    {
        var value=(T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        for(Type type=typeof(T);type!=null && type.Assembly==typeof(GameConfig).Assembly;type=type.BaseType)
            foreach(var field in type.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly))
            {
                if(!field.FieldType.IsGenericType || field.FieldType.GetConstructor(Type.EmptyTypes)==null) continue;
                field.SetValue(value,Activator.CreateInstance(field.FieldType));
            }
        if(value is PlayerRoot root) root.RestoreEconomyState(new PlayerEconomyState());
        return value;
    }
}
internal static class IntegrationProgram
{
    private static readonly HexCoord Home = new HexCoord(0,0);
    private static int checks;
    private static void Check(bool condition,string message) { if(!condition)throw new Exception(message); }
    private static void Set(object value,string name,object field) => value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(value,field);
    private static void Invoke(object value,string name) { try{value.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(value,null);}catch(TargetInvocationException ex){throw ex.InnerException;} }
    private static void SameIds(MatchIdentityState state)
    {
        var now=MatchIdentityState.Capture();
        Check(state.ArmyHighWaterMark==now.ArmyHighWaterMark && state.UnitHighWaterMark==now.UnitHighWaterMark && state.CardHighWaterMark==now.CardHighWaterMark,"decode/rejection changed issued IDs");
    }
    private static int Main()
    {
        string directory=Path.Combine(Path.GetTempPath(),"full-file-"+Guid.NewGuid().ToString("N"));
        try { Run(directory);Console.WriteLine("PASS "+checks+" full-file production codec/transaction checks (native creation, UI and rendering adapted; no Unity scene executed)");return 0; }
        catch(Exception ex){Console.WriteLine("FAIL full-file integration: "+ex);return 1;}
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }
    }
    private static HexMap Map(GameConfig config,HexMapLayout layout)
    {
        var map=IntegrationNative.New<HexMap>();
        map.SetData(layout.FieldRadius,layout.OuterRadius,layout.Cells.ToDictionary(c=>new HexCoord(c.Q,c.R),c=>config.mapGeneration.terrainTypes.Single(t=>t.ContentKey==c.TerrainKey)));
        map.SetLayout(layout);return map;
    }
    private static GameTurnController Turns(GameConfig config,HexSelectionController selection)
    {
        var turns=IntegrationNative.New<GameTurnController>();Set(turns,"gameConfig",config);Set(turns,"hexSelectionController",selection);return turns;
    }
    private static void Run(string directory)
    {
        IntegrationNative.ProfileDirectory=Path.Combine(directory,"profile");
        IntegrationNative.Manifest=IntegrationNative.New<MatchSaveAssetManifest>();IntegrationNative.Manifest.ContentFingerprint=new string('a',64);
        var config=IntegrationNative.New<GameConfig>();
        config.extractionFacilityCards=new CardDefinition[4];
        config.collectionDeckCatalog=IntegrationNative.New<StartingDeckCatalog>();
        config.collectionResearchCatalog=IntegrationNative.New<ResearchProductionCatalog>();
        var faction=IntegrationNative.New<FactionCardCatalog>();faction.faction=Faction.IronConcord;
        var card=new CardDefinition {authoredKey="file.unit",displayName="Frozen deck card",faction=faction.faction,cardType=CardType.Unit,deckCopyLimit=4};
        faction.cards.Add(card);config.collectionDeckCatalog.catalogs.Add(faction);
        config.mapGeneration=IntegrationNative.New<MapGenerationSettings>();
        config.mapGeneration.radius=0;config.mapGeneration.complexCount=0;
        config.mapGeneration.terrainTypes.Add(new TerrainTypeEntry {contentKey="file.terrain",terrainName="Plain"});
        var layout=new HexMapLayout {FieldRadius=0,OuterRadius=1,Biome=Biome.Arid,Alpha=1,Cells=new[]{new MapLayoutCell{TerrainKey="file.terrain",TextureKey="texture.none"}}};
        var human=new PlayerSetupData{PlayerId="human",Nickname="Human",IsHuman=true,Faction=faction.faction};
        var ai=new PlayerSetupData{PlayerId="ai",Nickname="AI",Faction=faction.faction,ColorIndex=1};
        human.Hand=new PlayerHandData(new[]{card,card},0,4);human.Hand.SetCurrentTurn(7);human.Hand.AddCard(new CardData(card));
        human.MatchLoadout=new MatchLoadout(new Game.Progression.SavedDeck {deckId="saved-deck",name="Original",faction=faction.faction,mainCards=new List<DeckCardEntry>{new DeckCardEntry{cardKey=card.authoredKey,count=1}}},new DeckRules(config.collectionDeckCatalog,config.collectionResearchCatalog),_=>1);
        human.BlueprintQuota=new BlueprintQuota(human.MatchLoadout);
        var aiHand=new AiHandData(null,ai.Faction,0);aiHand.SetCurrentTurn(6);aiHand.AddCard(new CardData(card));AiHandRegistry.RegisterRestored(ai,aiHand);
        var eliminated=new PlayerSetupData{PlayerId="eliminated",Nickname="Eliminated",Faction=faction.faction,ColorIndex=2,IsEliminated=true};
        GameSession.Players=new List<PlayerSetupData>{human,ai,eliminated};
        PlayerRootRegistry.Register(ai,PlayerRoot.Create(ai,"AI"));PlayerRootRegistry.Register(eliminated,PlayerRoot.Create(eliminated,"Eliminated"));
        typeof(GameSession).GetField("<MatchId>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,"full-file-match");
        var root=PlayerRoot.Create(human,"Human");PlayerRootRegistry.Register(human,root);
        root.RestoreEconomyState(new PlayerEconomyState{ActionPoints=3,Tech=11,InitiativeContributions=new List<ResourceType>{ResourceType.Tech,ResourceType.Human}});
        var army=new ArmyData {Owner=human,Hex=Home,Name="Airfield",IsAirfield=true};
        var unit=new Game.Units.UnitData {Owner=human,Name="Spent aircraft",IsAviation=true,HitPointsMax=5,HitPointsCurrent=4,MoveMax=3};
        unit.RestoreMovementSpent(7);army.Members.Add(unit);army.MarkActivated();
        using(MatchRestoreScope.Begin())ArmyRegistry.Register(army);
        AntiAirState.RecordPrompted(unit,army.Id,true);
        VisionSystem.RestoreState(new VisionState{CurrentViewerId="human",Players=new List<PlayerVisionState>{new PlayerVisionState{PlayerId="human",Visible=new List<HexCoord>{Home},Visited=new List<HexCoord>{Home},EverSeen=new List<HexCoord>{Home}}}},id=>id=="human"?human:ai);
        OperationContinuationWindow.HoldMobilization(ai,6);
        var map=Map(config,layout);var selection=IntegrationNative.New<HexSelectionController>();Set(selection,"map",map);Set(selection,"gameConfig",config);
        var turns=Turns(config,selection);
        turns.ResumeGame(new GameTurnState{Round=7,PlayerOrder=new[]{"ai","eliminated","human"},CurrentPlayerIndex=2,Completed=new[]{new PlayerTurnCount{PlayerId="ai",CompletedTurns=7},new PlayerTurnCount{PlayerId="eliminated",CompletedTurns=6},new PlayerTurnCount{PlayerId="human",CompletedTurns=6}},GreetingsShown=new[]{"human"},PendingMessages=Array.Empty<PlayerTurnMessages>()});
        Check(turns.CanSaveMatch(out string reason),reason);
        int armyId=army.Id,unitId=unit.RuntimeId,cardId=human.Hand.Hand.Single().RuntimeId;
        var identity=MatchIdentityState.Capture();
        var bytes=MatchSaveCodec.Capture(config,map,turns,selection);SameIds(identity);
        var store=new MatchSaveStore(directory);store.Save(3,bytes);
        Check(File.ReadAllBytes(Path.Combine(directory,"slot-3.json")).SequenceEqual(bytes),"actual slot bytes differ");
        Check(store.Slots().SequenceEqual(new[]{3}),"slot roster");checks++;Console.WriteLine("PASS actual Capture / strict transport / atomic slot write");
        var oldHuman=human;var oldHand=human.Hand;var oldArmy=army;
        for(int iteration=0;iteration<2;iteration++)
        {
            identity=MatchIdentityState.Capture();
            var source=store.Load(3,data=>MatchSaveCodec.Decode(data,config),out string notice);SameIds(identity);
            Check(notice==null && ReferenceEquals(GameSession.FindHumanPlayer(),human),"read modified current session");
            Check(!ReferenceEquals(source.Players.Single(p=>p.IsHuman),human),"decoded player aliases current graph");
            Check(GameSession.TryPrepareRestore(source,config,out string error),error);
            var incoming=Map(config,source.Map);var incomingSelection=IntegrationNative.New<HexSelectionController>();Set(incomingSelection,"map",incoming);Set(incomingSelection,"gameConfig",config);
            var incomingTurns=Turns(config,incomingSelection);
            var setup=IntegrationNative.New<CitadelSetupController>();Set(setup,"map",incoming);Set(setup,"gameConfig",config);Set(setup,"turnController",incomingTurns);Set(setup,"hexSelectionController",incomingSelection);
            Invoke(setup,"Awake");Check(!ArmyRegistry.IsRegistered(army),"scene reset retained outgoing graph");
            int starts=0,changes=0;incomingTurns.TurnStarted+=_=>starts++;source.Players.Single(p=>p.IsHuman).Hand.HandChanged+=()=>changes++;
            Check(setup.TryRestorePreparedMatch(),GameSession.SetupError);
            human=GameSession.FindHumanPlayer();army=ArmyRegistry.AllAt(Home).Single();unit=army.Members.Single();
            Check(GameSession.PendingRestore==null && GameSession.MatchId=="full-file-match","restore completion identity");
            Check(incomingTurns.CurrentTurnOrder.Select(p=>p.PlayerId).SequenceEqual(new[]{"ai","eliminated","human"}) && GameSession.Players.Single(p=>p.PlayerId=="eliminated").IsEliminated,"same-round eliminated order slot changed");
            var restoredAi=GameSession.Players.Single(p=>p.PlayerId=="ai");
            Check(OperationContinuationWindow.IsMobilizationHeld(restoredAi,8) && !OperationContinuationWindow.IsMobilizationHeld(restoredAi,9),"AI continuation clock or canonical owner lost");
            Check(incomingTurns.TurnNumber==7 && incomingTurns.CurrentPlayer==human && incomingTurns.CompletedTurnsFor(human)==6,"round/order/clock mismatch");
            Check(starts==0 && changes==0,"restore started/refilled/dealt another turn");
            Check(!ReferenceEquals(human,oldHuman) && !ReferenceEquals(human.Hand,oldHand) && !ReferenceEquals(army,oldArmy),"load retained outgoing graph objects");
            Check(army.Id==armyId && unit.RuntimeId==unitId && human.Hand.Hand.Single().RuntimeId==cardId,"persistent identity mismatch");
            Check(ReferenceEquals(army.Owner,human) && ReferenceEquals(unit.Owner,human),"noncanonical owner");
            Check(unit.MovementSpent==7 && unit.PermanentMoveMax==3 && army.HasActivatedThisTurn && !army.RequiresActivationCharge(unit),"spent movement/activation lost");
            Check(!AntiAirState.CanReact(unit,armyId+100),"spent AA refilled");
            Check(PlayerRootRegistry.FindFor(human).ActionPoints==3 && PlayerRootRegistry.FindFor(human).GetResource(ResourceType.Tech)==11,"income/AP applied again");
            Check(human.Hand.RemainingDeckCount==2 && human.Hand.Hand.Count==1,"deck reordered/refilled");
            Check(incomingTurns.CanSaveMatch(out reason),reason);checks++;Console.WriteLine("PASS full file Decode / stage / scene transaction / ResumeGame / continuation rollback "+iteration);
            PlayerRootRegistry.FindFor(human).SpendActionPoints(1);PlayerRootRegistry.FindFor(human).AddResource(ResourceType.Tech,99);
            human.Hand.RemoveCard(human.Hand.Hand.Single());unit.RestoreMovementSpent(0);
            OperationContinuationWindow.HoldMobilization(restoredAi,99);
            oldHuman=human;oldHand=human.Hand;oldArmy=army;
        }
        Check(File.ReadAllBytes(Path.Combine(directory,"slot-3.json")).SequenceEqual(bytes),"live continuation rewrote immutable saved file");
        // A valid file can become unavailable because the durable campaign moved on.
        // This is not corruption and must not silently restore a different backup match.
        var staleEnvelope=MatchSaveJson.Deserialize<MatchSaveEnvelope>(System.Text.Encoding.UTF8.GetString(bytes));
        var staleData=MatchSaveJson.Deserialize<MatchSaveData>(staleEnvelope.payload);
        staleData.hasCampaign=true;
        staleData.campaign=new MatchCampaignData {campaignId="old-campaign",operationId="old-operation",matchId=staleData.matchId,selectedHumanDeckId="old-deck"};
        staleEnvelope.payload=MatchSaveJson.Serialize(staleData);staleEnvelope.payloadSha256=MatchSaveCodec.Sha256(staleEnvelope.payload);
        var staleBytes=System.Text.Encoding.UTF8.GetBytes(MatchSaveJson.Serialize(staleEnvelope));
        store.Save(12,bytes);store.Save(12,staleBytes);
        bool unavailable=false;var beforePlayers=GameSession.Players;identity=MatchIdentityState.Capture();
        try {store.Load(12,b=>MatchSaveCodec.Decode(b,config),out _);} catch(NotSupportedException) {unavailable=true;}
        Check(unavailable,"stale campaign silently loaded a backup match");
        Check(File.ReadAllBytes(Path.Combine(directory,"slot-12.json")).SequenceEqual(staleBytes),"stale primary quarantined as corrupt");
        Check(File.ReadAllBytes(Path.Combine(directory,"slot-12.json.bak")).SequenceEqual(bytes),"stale rejection modified backup");
        Check(ReferenceEquals(GameSession.Players,beforePlayers)&&GameSession.PendingRestore==null,"stale rejection replaced session");SameIds(identity);
        checks++;Console.WriteLine("PASS stale campaign rejection preserves primary, backup and current match");
        foreach(string damage in new[]{"duplicate unit","dangling member","missing card","future version","wrong content","outside map","checksum","truncated","missing human loadout","missing human quota","missing human economy","missing AI economy","pending blueprint","missing living order","only one living player","quota mismatch","loadout faction mismatch","missing current viewer","wrong current viewer","overflow geometry"})
        {
            var envelope=MatchSaveJson.Deserialize<MatchSaveEnvelope>(System.Text.Encoding.UTF8.GetString(bytes));
            var data=MatchSaveJson.Deserialize<MatchSaveData>(envelope.payload);
            if(damage=="overflow geometry")data.map.OuterRadius=float.MaxValue;
            if(damage=="duplicate unit")data.world.units.Add(data.world.units[0]);
            if(damage=="dangling member")data.world.armies[0].members.Add(int.MaxValue-1);
            if(damage=="missing card")data.world.cards[0].definitionKey="missing";
            if(damage=="future version")envelope.version++;
            if(damage=="missing current viewer")data.presentation.CurrentViewerId=null;
            if(damage=="wrong current viewer") {data.presentation.CurrentViewerId="ai"; data.presentation.Vision.Add(new MatchVisionData {PlayerId="ai"});}
            if(damage=="wrong content")envelope.contentFingerprint=new string('b',64);
            if(damage=="outside map")data.world.armies[0].q=10;
            if(damage=="missing living order") { data.turn.PlayerOrder=new[]{"eliminated","human"};data.turn.CurrentPlayerIndex=1; }
            if(damage=="only one living player") data.world.players.Single(p=>p.id=="ai").eliminated=true;
            if(damage=="missing human loadout")data.world.players.Single(p=>p.human).hasLoadout=false;
            if(damage=="missing human quota")data.world.players.Single(p=>p.human).hasQuota=false;
            if(damage=="missing human economy")data.world.players.Single(p=>p.human).hasEconomy=false;
            if(damage=="missing AI economy")data.world.players.Single(p=>p.id=="ai").hasEconomy=false;
            if(damage=="quota mismatch") data.world.players.Single(p=>p.human).quota.unlockedKeys.Add("file.unit");
            if(damage=="loadout faction mismatch") data.world.players.Single(p=>p.human).loadout.faction=Faction.Ashen;
            if(damage=="pending blueprint") { var quota=data.world.players.Single(p=>p.human).quota;quota.unlockedKeys.Add("file.unit");quota.pendingKeys.Add("file.unit"); }
            envelope.payload=MatchSaveJson.Serialize(data);envelope.payloadSha256=MatchSaveCodec.Sha256(envelope.payload);
            if(damage=="checksum")envelope.payloadSha256=new string('0',64);
            var damaged=System.Text.Encoding.UTF8.GetBytes(MatchSaveJson.Serialize(envelope));
            if(damage=="truncated")damaged=damaged.Take(damaged.Length/2).ToArray();
            store.Save(10,damaged); // no valid backup can mask a bad primary
            string backup=Path.Combine(directory,"slot-10.json.bak");if(File.Exists(backup))File.Delete(backup);
            var currentPlayers=GameSession.Players;identity=MatchIdentityState.Capture();bool rejected=false;
            try{store.Load(10,b=>MatchSaveCodec.Decode(b,config),out _);}catch{rejected=true;}
            Check(rejected,"accepted "+damage);Check(ReferenceEquals(GameSession.Players,currentPlayers) && ArmyRegistry.IsRegistered(army) && GameSession.PendingRestore==null,"rejection replaced current session");SameIds(identity);checks++;Console.WriteLine("PASS reject "+damage+" before session and ID changes");
        }
        var phaseTurns=Turns(config,selection);
        var dead=new PlayerSetupData {PlayerId="skipped",IsEliminated=true};
        var next=new PlayerSetupData {PlayerId="next-human",IsHuman=true};
        typeof(GameTurnController).GetProperty("CurrentTurnOrder").SetValue(phaseTurns,new List<PlayerSetupData>{human,dead,next});
        typeof(GameTurnController).GetProperty("CurrentPlayer").SetValue(phaseTurns,human);
        var clocks=(Dictionary<PlayerSetupData,long>)typeof(GameTurnController).GetField("_completedTurns",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(phaseTurns);
        clocks[human]=9;
        typeof(GameTurnController).GetMethod("BeginPlayerTurn",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(phaseTurns,new object[]{1});
        Check(phaseTurns.CompletedTurnsFor(human)==9,"eliminated slot ended outgoing player twice");
        Check(phaseTurns.CurrentPlayer==next,"eliminated slot did not advance");
        checks++;Console.WriteLine("PASS eliminated slot never repeats outgoing effects");
    }
}
