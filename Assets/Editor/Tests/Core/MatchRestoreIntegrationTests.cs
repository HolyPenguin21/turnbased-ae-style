#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Ai;
using Game.Aviation;
using Game.Campaign;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Progression;
using Game.Setup;
using Game.Terrain;
using Game.Turns;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    // Native EditMode fixture: real GameObjects, meshes, renderer snapshots and owners.
    // It deliberately invokes the scene's early reset and restore transaction explicitly:
    // EditMode does not execute PlayMode scene activation/OnEnable ordering. No tactical JSON,
    // disk codec, slot, new-game setup or replacement implementations are involved.
    public sealed class MatchRestoreIntegrationTests
    {
        public enum Failure { None, Prevalidation, World, Knowledge, Presentation, Resume }
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly HexCoord Home = new HexCoord(0, 0), Hidden = new HexCoord(1, 0), Bonus = new HexCoord(-1, 0);
        private GameObject objects, scene;
        private Scene isolatedScene;
        private GameConfig config;
        private Texture2D texture;
        private Sprite observedSprite, changedSprite;
        private StartingDeckCatalog catalog;
        private FactionCardCatalog faction;
        private ResearchProductionCatalog research;
        private CollectionService collection, previousCollection;
        private string profileDirectory;
        private UnityEngine.Random.State randomBefore;

        private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        private static object Call(object target, string method, params object[] arguments)
        {
            var found = target.GetType().GetMethod(method, Private);
            Assert.That(found, Is.Not.Null, "The fixture must exercise the runtime scene owner: " + method);
            try { return found.Invoke(target, arguments); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }
        private GameObject Child(string name, Transform parent = null)
        {
            var go = new GameObject(name); go.transform.SetParent(parent ?? objects.transform); return go;
        }
        private HexSelectionController Selection(string name, HexMap map)
        {
            var selection = Child(name).AddComponent<HexSelectionController>();
            Set(selection, "map", map); Set(selection, "gameConfig", config); return selection;
        }
        private MapObjectVisual Marker(string name, Color color)
        {
            var go = Child(name);
            var visual = go.AddComponent<MapObjectVisual>();
            var circle = go.AddComponent<SpriteRenderer>(); circle.sprite = observedSprite; Set(visual, "innerCircle", circle);
            var flag = Child("flag", go.transform).AddComponent<SpriteRenderer>(); flag.sprite = observedSprite;
            Set(visual, "factionAccent", flag);
            go.AddComponent<MapFlagAnimator>(); visual.SetColor(color); return visual;
        }
        private void SetCollection(CollectionService value) => typeof(ProgressionContext)
            .GetField("<Collection>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, value);

        [SetUp]
        public void SetUp()
        {
            randomBefore = UnityEngine.Random.state;
            MatchStateInventory.EndMatch();
            isolatedScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            objects = new GameObject("in-memory-restore-fixture"); objects.SetActive(false);
            SceneManager.MoveGameObjectToScene(objects, isolatedScene);
            config = ScriptableObject.CreateInstance<GameConfig>();
            texture = new Texture2D(2, 1);
            observedSprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(.5f, .5f));
            changedSprite = Sprite.Create(texture, new Rect(1, 0, 1, 1), new Vector2(.5f, .5f));
            config.mapGeneration = new MapGenerationSettings { radius = 1, complexCount = 0, borderDepthFraction = .5f,
                terrainTypes = new List<TerrainTypeEntry> { new TerrainTypeEntry {
                    contentKey = "restore.terrain", textureKey = "restore.texture", terrainName = "Restore", texture = texture } } };
            config.resourceIconPrefab = Child("resource prefab").AddComponent<ResourceIconVisual>();
            config.eventMarkerPrefab = Child("event prefab").AddComponent<EventMarkerVisual>();
            catalog = ScriptableObject.CreateInstance<StartingDeckCatalog>();
            research = ScriptableObject.CreateInstance<ResearchProductionCatalog>();
            faction = ScriptableObject.CreateInstance<FactionCardCatalog>(); faction.faction = Faction.IronConcord;
            faction.cards.Add(new CardDefinition { authoredKey = "restore.card", displayName = "Restored card",
                faction = Faction.IronConcord, cardType = CardType.Unit, deckCopyLimit = 4 });
            catalog.catalogs.Add(faction);
            // Only the permanent profile uses a real temporary store. The tactical source is
            // wholly in memory. This checks its receipt survives the actual match cleanup.
            profileDirectory = Path.Combine(Path.GetTempPath(), "match-restore-" + Guid.NewGuid().ToString("N"));
            collection = new CollectionService(new DeckRules(catalog, research), new CollectionProfileStore(profileDirectory), new CollectionProfile());
            previousCollection = ProgressionContext.Collection; SetCollection(collection);
        }

        [TearDown]
        public void TearDown()
        {
            // Assertions run BEFORE this hygiene. In particular this is never used to make
            // an injected-failure cleanup assertion pass.
            MatchStateInventory.EndMatch(); SetCollection(previousCollection);
            Object.DestroyImmediate(objects);
            if (isolatedScene.IsValid()) EditorSceneManager.CloseScene(isolatedScene, true);
            Object.DestroyImmediate(config);
            Object.DestroyImmediate(observedSprite); Object.DestroyImmediate(changedSprite); Object.DestroyImmediate(texture);
            Object.DestroyImmediate(catalog); Object.DestroyImmediate(research); Object.DestroyImmediate(faction);
            if (Directory.Exists(profileDirectory)) Directory.Delete(profileDirectory, true);
            UnityEngine.Random.state = randomBefore;
        }

        private DetachedSource Capture(Failure failure = Failure.None)
        {
            var oldMapObject = Child("outgoing map");
            var oldMap = oldMapObject.AddComponent<HexMap>();
            var generator = oldMapObject.AddComponent<HexMapGenerator>(); Set(generator, "gameConfig", config);
            generator.Generate();
            var human = new PlayerSetupData { PlayerId = "human", IsHuman = true, Faction = Faction.IronConcord };
            var ai = new PlayerSetupData { PlayerId = "ai", ColorIndex = 1, Faction = Faction.IronConcord };
            human.Hand = new PlayerHandData(new[] { faction.cards[0], faction.cards[0], faction.cards[0] }, 1, 4);
            human.Hand.SetCurrentTurn(7);
            var aiHand = new AiHandData(null, ai.Faction, 0); aiHand.SetCurrentTurn(6);
            aiHand.AddCard(new CardData(faction.cards[0])); AiHandRegistry.RegisterRestored(ai, aiHand);
            GameSession.Players = new List<PlayerSetupData> { human, ai };
            GameSession.SetCampaignContext(new CampaignMatchContext { MatchId = "native-in-memory-match" });
            var oldTurns = Child("outgoing turns").AddComponent<GameTurnController>();
            oldTurns.ResumeGame(SavedTurn());
            var root = PlayerRoot.Create(human, "outgoing root"); root.transform.SetParent(objects.transform);
            root.RestoreEconomyState(new PlayerEconomyState { ActionPoints = 3, Human = 2, Energy = 5, Materials = 7, Tech = 11,
                InitiativeContributions = new List<ResourceType> { ResourceType.Tech, ResourceType.Human } });
            PlayerRootRegistry.Register(human, root);
            var army = new ArmyData { Owner = human, Hex = Home };
            var unit = new UnitData { Owner = human, Name = "Spent defender", MoveMax = 3, HitPointsCurrent = 4 };
            unit.RestoreMovementSpent(7); army.Members.Add(unit); army.MarkActivated();
            var hiddenArmy = new ArmyData { Owner = ai, Hex = Hidden };
            var hiddenUnit = new UnitData { Owner = ai, Name = "Observed scout", HitPointsCurrent = 3, MoveMax = 2 };
            hiddenArmy.Members.Add(hiddenUnit);
            var building = new BuildingData { Owner = ai, Hex = Hidden }; building.FacilitySlots[0] = new FacilityData();
            var gunner = new UnitData { Owner = ai, Name = "Facility gunner" }; building.FacilitySlots[0].AntiAirGunner = gunner;
            using (MatchRestoreScope.Begin())
            { ArmyRegistry.Register(army); ArmyRegistry.Register(hiddenArmy); BuildingRegistry.Register(Hidden, building); }
            AntiAirState.RecordPrompted(unit, hiddenArmy.Id, true);
            AntiAirState.RecordPrompted(gunner, army.Id, false);
            HumanVisualMemory.ObserveArmy(human, hiddenArmy, Hidden);
            HumanVisualMemory.ObserveBuilding(human, Hidden, true);
            var oldSelection = Selection("outgoing selection", oldMap);
            var buildingArt = Marker("observed building", Color.red); building.Visual = buildingArt;
            var armyArt = Marker("observed army", Color.yellow);
            var controller = armyArt.gameObject.AddComponent<ArmyController>(); Call(controller, "Awake");
            hiddenArmy.Controller = controller; controller.SetData(hiddenArmy);
            Call(oldSelection, "UpdateRememberedBuildingVisual", human, Hidden, buildingArt);
            Call(oldSelection, "UpdateRememberedArmyVisual", human, hiddenArmy, Hidden);
            // Hidden world changes after the observation. The source captures the changed live
            // facts and the old renderer/sighting facts through their different owner APIs.
            buildingArt.SetColor(Color.blue); buildingArt.transform.Find("flag").GetComponent<SpriteRenderer>().sprite = changedSprite;
            hiddenUnit.HitPointsCurrent = 1; hiddenUnit.IsHidden = true;
            StealthSystem.MarkDetected(hiddenUnit, human);
            var detection = StealthSystem.CaptureState();
            detection.Notices.Add(new DetectionNoticesState { PlayerId = human.PlayerId, Notices = new List<string> { "Saved detection report" } });
            StealthSystem.RestoreState(detection, id => id == hiddenUnit.RuntimeId ? hiddenUnit : null, id => id == human.PlayerId ? human : ai);
            VisionSystem.RestoreState(new VisionState { CurrentViewerId = "human", Players = new List<PlayerVisionState> {
                new PlayerVisionState { PlayerId = "human", Visible = new List<HexCoord> { Home, Bonus },
                    Visited = new List<HexCoord> { Home }, EverSeen = new List<HexCoord> { Home, Hidden, Bonus } },
                new PlayerVisionState { PlayerId = "ai", Visible = new List<HexCoord> { Hidden }, EverSeen = new List<HexCoord> { Hidden } } } },
                id => id == "human" ? human : ai);
            HexResourceBonusRegistry.RestoreState(new[] { new HexResourceBonusState { Hex = Bonus, Bonus = new ResourceYields { energy = 2 } } });
            var eventEntry = new HexEventRegistry.Entry { Skipped = true }; eventEntry.DiscoveredBy.Add(human); eventEntry.KnownBy.Add(human);
            HexEventRegistry.RestoreEntries(new[] { new HexEventEntryState<HexEventRegistry.Entry> { Hex = Home, Payload = eventEntry } }, (hex, entry) => entry);
            DelayedBattleRegistry.Add(new PendingBattle { Hex = Hidden, Participants = new List<ArmyData> { hiddenArmy, army } });
            var source = new DetachedSource(this, failure, oldMap.Layout, oldTurns.CaptureTurnState(), human, ai, root,
                army, hiddenArmy, unit, hiddenUnit, gunner, oldSelection);
            Assert.That(HumanVisualMemory.TryGetArmySighting(human, hiddenArmy.Id, out var outgoingSighting), Is.True);
            Assert.That(outgoingSighting.Army.Owner, Is.SameAs(ai), "Detaching/rebinding the source must leave outgoing knowledge untouched.");
            Assert.That(collection.TryGrantRandomCollectionCard(source.MatchId, "0:0", out var granted, out string error), Is.True, error);
            Assert.That(granted, Is.SameAs(faction.cards[0]));
            return source;
        }

        private static GameTurnState SavedTurn() => new GameTurnState { Round = 7, PlayerOrder = new[] { "ai", "human" }, CurrentPlayerIndex = 1,
            Completed = new[] { new PlayerTurnCount { PlayerId = "ai", CompletedTurns = 7 }, new PlayerTurnCount { PlayerId = "human", CompletedTurns = 6 } },
            GreetingsShown = new[] { "human" }, PendingMessages = Array.Empty<PlayerTurnMessages>() };

        private CitadelSetupController PrepareScene(DetachedSource source, out HexMap map, out GameTurnController turns, out HexSelectionController selection)
        {
            scene = Child("incoming Game scene"); scene.SetActive(false);
            var mapObject = Child("restored map", scene.transform); map = mapObject.AddComponent<HexMap>();
            var generator = mapObject.AddComponent<HexMapGenerator>(); Set(generator, "gameConfig", config);
            var resources = mapObject.AddComponent<MapResourceDisplay>(); Set(resources, "gameConfig", config);
            var events = mapObject.AddComponent<MapEventDisplay>(); Set(events, "gameConfig", config);
            turns = Child("restored turns", scene.transform).AddComponent<GameTurnController>();
            selection = Selection("restored selection", map); selection.transform.SetParent(scene.transform);
            Set(selection, "turnController", turns); Set(turns, "hexSelectionController", selection); Set(turns, "gameConfig", config);
            var setup = Child("real scene setup", scene.transform).AddComponent<CitadelSetupController>();
            Set(setup, "map", map); Set(setup, "gameConfig", config); Set(setup, "turnController", turns); Set(setup, "hexSelectionController", selection);
            // This is the production early scene reset, not test code clearing each registry.
            Call(setup, "Awake");
            Assert.That(GameSession.PendingRestore, Is.SameAs(source));
            Assert.That(ArmyRegistry.AllOccupiedHexes(), Is.Empty);
            Assert.That(HumanVisualMemory.CaptureState(_ => "unexpected").Players, Is.Empty);
            generator.BuildFromLayout(GameSession.PendingRestore.Map);
            return setup;
        }

        private bool RestoreThroughSceneOwner(CitadelSetupController setup) => (bool)Call(setup, "TryRestorePreparedMatch");

        [Test]
        public void FullInMemoryRestoreRebindsOwnersAndResumesWithoutNewTurnSideEffects()
        {
            var source = Capture();
            var oldHumanHand = GameSession.FindHumanPlayer().Hand;
            Assert.That(GameSession.TryPrepareRestore(source, config, out string error), Is.True, error);
            Assert.That(GameSession.FindHumanPlayer(), Is.SameAs(source.Human));
            Assert.That(source.Human.Hand, Is.Not.SameAs(oldHumanHand));
            var setup = PrepareScene(source, out var map, out var turns, out var selection);
            int starts = 0, notices = 0, cardChanges = 0;
            turns.TurnStarted += _ => starts++;
            source.Human.Hand.HandChanged += () => cardChanges++;
            Action<PlayerSetupData> vision = _ => notices++;
            VisionSystem.VisibilityChanged += vision;
            var challenge = StealthSystem.ChallengeRoller;
            StealthSystem.ChallengeRoller = (spot, hide) => throw new InvalidOperationException("Unexpected detection roll during restore");
            UnityEngine.Random.InitState(617); float next = UnityEngine.Random.value; UnityEngine.Random.InitState(617);
            try
            {
                Assert.That(RestoreThroughSceneOwner(setup), Is.True);
                Assert.That(UnityEngine.Random.value, Is.EqualTo(next), "Restoring must not deal, generate or roll again.");
                Assert.That(source.Stages, Is.EqualTo(new[] { "world", "knowledge", "presentation" }));
                Assert.That(GameSession.PendingRestore, Is.Null);
                Assert.That(GameSession.MatchId, Is.EqualTo(source.MatchId));
                Assert.That(map.Layout.Cells.Select(c => c.TerrainKey), Is.EqualTo(source.Map.Cells.Select(c => c.TerrainKey)));
                Assert.That(map.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.GreaterThan(0));
                Assert.That(turns.CurrentPlayer, Is.SameAs(source.Human)); Assert.That(turns.CurrentTurnOrder, Is.EqualTo(new[] { source.Ai, source.Human }));
                Assert.That(turns.TurnNumber, Is.EqualTo(7)); Assert.That(turns.CompletedTurnsFor(source.Human), Is.EqualTo(6));
                Assert.That(turns.TurnConfirmed, Is.True); Assert.That(turns.CanSaveMatch(out error), Is.True, error);
                Assert.That(starts, Is.Zero); Assert.That(notices, Is.Zero); Assert.That(cardChanges, Is.Zero);
                source.AssertWorldAndKnowledge(turns, selection);
                Assert.That(((IDictionary)typeof(MapResourceDisplay).GetField("_iconsByHex", Private).GetValue(map.GetComponent<MapResourceDisplay>())).Count, Is.EqualTo(1));
                Assert.That(((IDictionary)typeof(MapEventDisplay).GetField("_markersByHex", Private).GetValue(map.GetComponent<MapEventDisplay>())).Count, Is.EqualTo(1));
                AssertProfileReceipt(source);
                // Roll back the same captured payload after its first live graph diverges.
                // Fresh source players/hands must come from saved DTOs, never that live graph.
                source.Human.Hand.RemoveCard(source.Human.Hand.Hand.Single());
                source.CreatedRoot.AddResource(ResourceType.Tech, 99);
                source.RestoredArmy.Members[0].RestoreMovementSpent(0);
                source.RestoredHiddenArmy.Members[0].HitPointsCurrent = 0;
                var rollback = source.FreshSource();
                Assert.That(rollback.Human, Is.Not.SameAs(source.Human));
                Assert.That(rollback.Human.Hand, Is.Not.SameAs(source.Human.Hand));
                Assert.That(GameSession.TryPrepareRestore(rollback, config, out error), Is.True, error);
                var secondSetup = PrepareScene(rollback, out _, out var secondTurns, out var secondSelection);
                Assert.That(RestoreThroughSceneOwner(secondSetup), Is.True);
                rollback.AssertWorldAndKnowledge(secondTurns, secondSelection);
                AssertProfileReceipt(rollback);
                MatchStateInventory.EndMatch(secondTurns);
                AssertClean(rollback); Assert.That(GameSession.SetupError, Is.Null);
                AssertProfileReceipt(rollback);
            }
            finally { VisionSystem.VisibilityChanged -= vision; StealthSystem.ChallengeRoller = challenge; }
        }

        [Test]
        public void PrevalidationRejectsDetachedSourceWithoutReplacingTheOutgoingMatch()
        {
            var source = Capture(Failure.Prevalidation);
            var players = GameSession.Players; var army = ArmyRegistry.AllAt(Home).Single();
            string identity = GameSession.MatchId;
            Assert.That(GameSession.TryPrepareRestore(source, config, out string error), Is.False);
            Assert.That(error, Does.Contain("injected Prevalidation"));
            Assert.That(GameSession.Players, Is.SameAs(players)); Assert.That(GameSession.MatchId, Is.EqualTo(identity));
            Assert.That(ArmyRegistry.AllAt(Home).Single(), Is.SameAs(army)); Assert.That(GameSession.PendingRestore, Is.Null);
            Assert.That(source.Stages, Is.Empty); AssertProfileReceipt(source);
        }

        [TestCase(Failure.World)]
        [TestCase(Failure.Knowledge)]
        [TestCase(Failure.Presentation)]
        [TestCase(Failure.Resume)]
        public void SceneFailureUsesActualCleanupRouteAndRetainsPermanentRewardReceipt(Failure failure)
        {
            var source = Capture(failure);
            Assert.That(GameSession.TryPrepareRestore(source, config, out string error), Is.True, error);
            var setup = PrepareScene(source, out _, out var turns, out _);
            LogAssert.Expect(LogType.Exception, new Regex(failure == Failure.Resume ? "ArgumentException" : "injected " + failure));
            Assert.That(RestoreThroughSceneOwner(setup), Is.False);
            Assert.That(source.CreatedRoot, Is.Not.Null, "The failure must follow actual world construction.");
            Assert.That(source.RestoredArmy, Is.Not.Null);
            int reached = failure == Failure.World ? 1 : failure == Failure.Knowledge ? 2 : 3;
            Assert.That(source.Stages, Is.EqualTo(new[] { "world", "knowledge", "presentation" }.Take(reached)));
            AssertClean(source);
            Assert.That(GameSession.SetupError, Does.StartWith("Could not restore the saved match:"));
            Assert.That(turns.CanSaveMatch(out _), Is.False);
            AssertProfileReceipt(source);
        }

        private void AssertProfileReceipt(DetachedSource source)
        {
            Assert.That(ProgressionContext.Collection, Is.SameAs(collection));
            Assert.That(collection.Owned("restore.card"), Is.EqualTo(1));
            Assert.That(collection.TryGrantRandomCollectionCard(source.MatchId, "0:0", out var replay, out string error), Is.True, error);
            Assert.That(replay, Is.SameAs(faction.cards[0])); Assert.That(collection.Owned("restore.card"), Is.EqualTo(1));
            Assert.That(collection.Snapshot.eventRewards.Count, Is.EqualTo(1));
        }
        private static void AssertClean(DetachedSource source)
        {
            Assert.That(GameSession.PendingRestore, Is.Null); Assert.That(GameSession.MatchId, Is.Null); Assert.That(GameSession.Players, Is.Empty);
            Assert.That(ArmyRegistry.AllOccupiedHexes(), Is.Empty); Assert.That(BuildingRegistry.AllBuildings(), Is.Empty);
            Assert.That(PlayerRootRegistry.FindFor(source.Human), Is.Null); Assert.That(AiHandRegistry.Peek(source.Ai), Is.Null);
            Assert.That(VisionSystem.CurrentViewer, Is.Null); Assert.That(VisionSystem.HasEverSeen(source.Human, Hidden), Is.False);
            Assert.That(HumanVisualMemory.CaptureState(_ => "unexpected").Players, Is.Empty);
            Assert.That(StealthSystem.CaptureState().Detections, Is.Empty); Assert.That(AntiAirState.CaptureState().Prompted, Is.Empty);
            Assert.That(DelayedBattleRegistry.HasAny, Is.False); Assert.That(HexResourceBonusRegistry.CaptureState(), Is.Empty);
            Assert.That(HexEventRegistry.FindAt(Home), Is.Null);
            Assert.That(MatchRestoreScope.IsRestoring, Is.False);
        }

        // Test-only detached source. DTOs are captured through real owner boundaries. Frozen
        // Unity appearances and display rosters are kept in a private in-memory snapshot store;
        // neither resolver looks up live hidden entities. This is intentionally not a codec.
        private sealed class DetachedSource : IMatchRestoreSource
        {
            private readonly MatchRestoreIntegrationTests fixture;
            private readonly Failure failure;
            private readonly GameTurnState turn;
            private readonly HandState savedHumanHand, savedAiHand;
            private readonly PlayerEconomyState economy;
            private readonly ArmyActivationState activation;
            private readonly UnitMovementState movement;
            private readonly AntiAirTurnState antiAir;
            private readonly StealthState stealth;
            private readonly VisionState vision;
            private readonly HumanVisualMemoryState memory;
            private readonly RememberedMapVisualState appearances;
            private readonly Dictionary<string, MapObjectVisual> frozen = new Dictionary<string, MapObjectVisual>();
            private readonly Dictionary<string, ArmyData> rosters = new Dictionary<string, ArmyData>();
            private readonly int armyId, hiddenArmyId, unitId, hiddenUnitId, gunnerId;
            private readonly int humanCardId, aiCardId, humanMutation, humanDeckCount;
            private readonly HexResourceBonusState[] bonuses;
            private readonly List<DelayedBattleState> battles;
            private bool presentationFinished;
            public readonly PlayerSetupData Human, Ai;
            public readonly List<string> Stages = new List<string>();
            public PlayerRoot CreatedRoot;
            public ArmyData RestoredArmy, RestoredHiddenArmy;
            private UnitData restoredUnit, restoredHiddenUnit, restoredGunner;
            public string MatchId => "native-in-memory-match";
            public bool RewardsEligible => true;
            public IReadOnlyList<PlayerSetupData> Players { get; }
            public CampaignMatchContext CampaignContext => null;
            public HexMapLayout Map { get; }
            // The Resume case deliberately violates the immutable-source contract only AFTER
            // both validations, injecting a real ResumeGame validation failure.
            public GameTurnState Turn => failure == Failure.Resume && presentationFinished ? new GameTurnState() : turn;
            public MatchIdentityState Identity { get; }

            public DetachedSource(MatchRestoreIntegrationTests fixture, Failure failure, HexMapLayout map, GameTurnState turn,
                PlayerSetupData human, PlayerSetupData ai, PlayerRoot root, ArmyData army, ArmyData hiddenArmy,
                UnitData unit, UnitData hiddenUnit, UnitData gunner, HexSelectionController selection)
            {
                this.fixture = fixture; this.failure = failure; Map = map.Copy(); this.turn = turn;
                var humanHand = human.Hand.CaptureState(); var aiHand = AiHandRegistry.Peek(ai).CaptureState();
                savedHumanHand = humanHand; savedAiHand = aiHand;
                Human = new PlayerSetupData { PlayerId = human.PlayerId, IsHuman = true, Faction = human.Faction,
                    Hand = PlayerHandData.FromState(humanHand, ResolveCard) };
                Ai = new PlayerSetupData { PlayerId = ai.PlayerId, Faction = ai.Faction, ColorIndex = ai.ColorIndex,
                    Hand = AiHandData.FromState(aiHand, ResolveCard) };
                Players = new[] { Human, Ai };
                humanCardId = humanHand.cards[0].runtimeId; aiCardId = aiHand.cards[0].runtimeId;
                humanMutation = humanHand.mutationVersion; humanDeckCount = humanHand.remainingDeckKeys.Count;
                economy = root.CaptureEconomyState(); activation = army.CaptureActivationState(); movement = unit.CaptureMovementState();
                armyId = army.Id; hiddenArmyId = hiddenArmy.Id; unitId = unit.RuntimeId; hiddenUnitId = hiddenUnit.RuntimeId; gunnerId = gunner.RuntimeId;
                antiAir = AntiAirState.CaptureState(); stealth = StealthSystem.CaptureState(); vision = VisionSystem.CaptureState();
                memory = HumanVisualMemory.CaptureState(snapshot => {
                    string key = "roster:" + rosters.Count;
                    StoreRoster(key, snapshot); return key;
                });
                appearances = selection.CaptureRememberedVisuals(visual => {
                    string key = "appearance:" + frozen.Count; frozen.Add(key, visual.CreateFrozenSnapshot(fixture.objects.transform));
                    return new MapVisualSnapshotReference { TypeKey = "native.frozen-marker", SnapshotKey = key };
                });
                bonuses = HexResourceBonusRegistry.CaptureState().ToArray(); battles = DelayedBattleRegistry.CaptureState();
                // Issued but no longer live entities are part of the identity range too.
                _ = new ArmyData(); _ = new UnitData(); _ = new CardData(fixture.faction.cards[0]);
                Identity = MatchIdentityState.Capture();
            }
            // Saved DTOs/frozen templates are read-only after capture. Each load gets new
            // canonical players/hands and new display shells; no live owner graph is reused.
            public DetachedSource FreshSource() => new DetachedSource(this);
            private DetachedSource(DetachedSource saved)
            {
                fixture = saved.fixture; failure = Failure.None; Map = saved.Map.Copy(); turn = saved.turn;
                savedHumanHand = saved.savedHumanHand; savedAiHand = saved.savedAiHand;
                Human = new PlayerSetupData { PlayerId = saved.Human.PlayerId, IsHuman = true, Faction = saved.Human.Faction,
                    Hand = PlayerHandData.FromState(savedHumanHand, ResolveCard) };
                Ai = new PlayerSetupData { PlayerId = saved.Ai.PlayerId, Faction = saved.Ai.Faction, ColorIndex = saved.Ai.ColorIndex,
                    Hand = AiHandData.FromState(savedAiHand, ResolveCard) };
                Players = new[] { Human, Ai };
                humanCardId = saved.humanCardId; aiCardId = saved.aiCardId; humanMutation = saved.humanMutation; humanDeckCount = saved.humanDeckCount;
                economy = saved.economy; activation = saved.activation; movement = saved.movement;
                armyId = saved.armyId; hiddenArmyId = saved.hiddenArmyId; unitId = saved.unitId; hiddenUnitId = saved.hiddenUnitId; gunnerId = saved.gunnerId;
                antiAir = saved.antiAir; stealth = saved.stealth; vision = saved.vision; memory = saved.memory;
                appearances = saved.appearances; bonuses = saved.bonuses; battles = saved.battles; Identity = saved.Identity;
                foreach (var pair in saved.frozen) frozen.Add(pair.Key, pair.Value);
                foreach (var pair in saved.rosters) StoreRoster(pair.Key, pair.Value);
            }
            private void StoreRoster(string key, ArmyData snapshot)
            {
                // Copy this fixture's observed display facts into new projection shells.
                // Never rebind the outgoing owner's snapshot in place during validation.
                var copy = ArmyData.CreateVisualSnapshot();
                copy.Name = snapshot.Name; copy.Hex = snapshot.Hex; copy.Owner = Player(snapshot.Owner.PlayerId);
                copy.VisualSnapshotDefenseBonus = snapshot.VisualSnapshotDefenseBonus;
                copy.VisualSnapshotConstructionDefense = snapshot.VisualSnapshotConstructionDefense;
                copy.VisualSnapshotCurrentMovement = snapshot.VisualSnapshotCurrentMovement;
                copy.VisualSnapshotMaxMovement = snapshot.VisualSnapshotMaxMovement;
                foreach (var member in snapshot.Members)
                {
                    var body = member.CloneForCombat(); body.Owner = Player(member.Owner.PlayerId); copy.Members.Add(body);
                }
                rosters.Add(key, copy);
            }
            private CardDefinition ResolveCard(string key) => key == fixture.faction.cards[0].authoredKey ? fixture.faction.cards[0] : null;
            private PlayerSetupData Player(string id) => id == Human.PlayerId ? Human : id == Ai.PlayerId ? Ai : null;
            private UnitData Unit(int id) => id == unitId ? restoredUnit : id == hiddenUnitId ? restoredHiddenUnit : id == gunnerId ? restoredGunner : null;
            private ArmyData Army(int id) => id == armyId ? RestoredArmy : id == hiddenArmyId ? RestoredHiddenArmy : null;
            private void ThrowAt(Failure stage) { if (failure == stage) throw new InvalidOperationException("injected " + stage); }
            public void Validate(GameConfig config)
            {
                ThrowAt(Failure.Prevalidation);
                if (Map.Cells.Length != 7 || Identity.ArmyHighWaterMark < hiddenArmyId || Identity.UnitHighWaterMark < gunnerId
                    || ResolveCard(Human.Hand.Hand[0].Definition.authoredKey) == null || Players.Select(p => p.PlayerId).Distinct().Count() != 2)
                    throw new ArgumentException("Detached test source has unresolved identities/content.");
            }
            public void RestoreWorld(HexMap map)
            {
                Assert.That(MatchRestoreScope.IsRestoring, Is.True); Stages.Add("world");
                Assert.That(map.AllCoords, Does.Contain(Hidden));
                CreatedRoot = PlayerRoot.Create(Human, "restored human root"); CreatedRoot.transform.SetParent(fixture.scene.transform);
                CreatedRoot.RestoreEconomyState(economy); PlayerRootRegistry.Register(Human, CreatedRoot);
                var aiRoot = PlayerRoot.Create(Ai, "restored AI root"); aiRoot.transform.SetParent(fixture.scene.transform); PlayerRootRegistry.Register(Ai, aiRoot);
                AiHandRegistry.RegisterRestored(Ai, (AiHandData)Ai.Hand);
                RestoredArmy = ArmyData.WithIdentity(armyId); RestoredArmy.Owner = Human; RestoredArmy.Hex = Home;
                RestoredHiddenArmy = ArmyData.WithIdentity(hiddenArmyId); RestoredHiddenArmy.Owner = Ai; RestoredHiddenArmy.Hex = Hidden;
                restoredUnit = UnitData.WithRuntimeId(unitId); restoredUnit.Owner = Human; restoredUnit.Name = "Spent defender"; restoredUnit.HitPointsCurrent = 4;
                restoredHiddenUnit = UnitData.WithRuntimeId(hiddenUnitId); restoredHiddenUnit.Owner = Ai; restoredHiddenUnit.Name = "Observed scout";
                restoredHiddenUnit.HitPointsCurrent = 1; restoredHiddenUnit.MoveMax = 2; restoredHiddenUnit.IsHidden = true;
                restoredGunner = UnitData.WithRuntimeId(gunnerId); restoredGunner.Owner = Ai;
                RestoredArmy.Members.Add(restoredUnit); RestoredHiddenArmy.Members.Add(restoredHiddenUnit);
                restoredUnit.RestoreMovementState(movement, Unit, Army); RestoredArmy.RestoreActivationState(activation, Unit);
                ArmyRegistry.Register(RestoredArmy); ArmyRegistry.Register(RestoredHiddenArmy);
                var building = new BuildingData { Owner = Ai, Hex = Hidden }; building.FacilitySlots[0] = new FacilityData(); BuildingRegistry.Register(Hidden, building);
                // Today's hidden appearance deliberately differs from the saved red appearance.
                var live = fixture.Marker("changed hidden live building", Color.blue); live.transform.SetParent(fixture.scene.transform);
                live.transform.Find("flag").GetComponent<SpriteRenderer>().sprite = fixture.changedSprite; building.Visual = live;
                foreach (var army in new[] { RestoredArmy, RestoredHiddenArmy })
                {
                    var marker = fixture.Marker("restored army " + army.Id, Color.blue); marker.transform.SetParent(fixture.scene.transform);
                    var controller = marker.gameObject.AddComponent<ArmyController>(); Call(controller, "Awake");
                    army.Controller = controller; controller.SetData(army);
                }
                AntiAirState.RestoreState(antiAir, Unit); DelayedBattleRegistry.RestoreState(battles, Army);
                HexResourceBonusRegistry.RestoreState(bonuses);
                var entry = new HexEventRegistry.Entry { Skipped = true }; entry.DiscoveredBy.Add(Human); entry.KnownBy.Add(Human);
                HexEventRegistry.RestoreEntries(new[] { new HexEventEntryState<HexEventRegistry.Entry> { Hex = Home, Payload = entry } }, (hex, value) => value);
                ThrowAt(Failure.World);
            }
            public void RestoreKnowledge()
            {
                Assert.That(MatchRestoreScope.IsRestoring, Is.True); Stages.Add("knowledge");
                VisionSystem.RestoreState(vision, Player); StealthSystem.RestoreState(stealth, Unit, Player);
                HumanVisualMemory.RestoreState(memory, Player, key => rosters[key]); ThrowAt(Failure.Knowledge);
            }
            public void RestorePresentation(HexSelectionController selection)
            {
                Assert.That(MatchRestoreScope.IsRestoring, Is.True); Stages.Add("presentation");
                selection.RestoreRememberedVisuals(appearances, Player, reference => frozen[reference.SnapshotKey]);
                presentationFinished = true; ThrowAt(Failure.Presentation);
            }
            public void AssertWorldAndKnowledge(GameTurnController turns, HexSelectionController selection)
            {
                Assert.That(turns.CurrentPlayer, Is.SameAs(Human)); Assert.That(turns.TurnNumber, Is.EqualTo(7));
                Assert.That(turns.CompletedTurnsFor(Human), Is.EqualTo(6)); Assert.That(turns.TurnConfirmed, Is.True);
                Assert.That(CreatedRoot.Setup, Is.SameAs(Human)); Assert.That(CreatedRoot.ActionPoints, Is.EqualTo(3));
                Assert.That(CreatedRoot.GetResource(ResourceType.Tech), Is.EqualTo(11));
                Assert.That(CreatedRoot.CaptureEconomyState().InitiativeContributions, Is.EqualTo(economy.InitiativeContributions));
                Assert.That(RestoredArmy.Owner, Is.SameAs(Human)); Assert.That(restoredUnit.Owner, Is.SameAs(Human));
                Assert.That(RestoredArmy.HasActivatedThisTurn, Is.True); Assert.That(RestoredArmy.RequiresActivationCharge(restoredUnit), Is.False);
                Assert.That(restoredUnit.MovementSpent, Is.EqualTo(7)); Assert.That(restoredUnit.PermanentMoveMax, Is.EqualTo(3));
                Assert.That(AntiAirState.CanReact(restoredUnit, hiddenArmyId + 100), Is.False);
                Assert.That(BuildingRegistry.FindAt(Hidden).FacilitySlots[0].AntiAirGunner, Is.SameAs(restoredGunner));
                Assert.That(AntiAirState.CanReact(restoredGunner, armyId), Is.False); Assert.That(AntiAirState.CanReact(restoredGunner, armyId + 100), Is.True);
                Assert.That(StealthSystem.CaptureState().Detections.Single().Expiry, Is.EqualTo(stealth.Detections.Single().Expiry));
                Assert.That(StealthSystem.IsDetectedBy(restoredHiddenUnit, Human), Is.True);
                Assert.That(StealthSystem.TakeDetectionNotices(Human), Is.EqualTo(new[] { "Saved detection report" }));
                Assert.That(StealthSystem.TakeDetectionNotices(Human), Is.Empty);
                Assert.That(VisionSystem.IsVisible(Human, Home), Is.True); Assert.That(VisionSystem.IsVisible(Human, Hidden), Is.False);
                Assert.That(VisionSystem.IsVisited(Human, Home), Is.True); Assert.That(VisionSystem.IsVisited(Human, Bonus), Is.False);
                Assert.That(VisionSystem.HasEverSeen(Human, Hidden), Is.True);
                Assert.That(HumanVisualMemory.TryGetArmySighting(Human, hiddenArmyId, out var sighting), Is.True);
                Assert.That(sighting.Army.Owner, Is.SameAs(Ai)); Assert.That(sighting.Army.Members[0].Owner, Is.SameAs(Ai));
                Assert.That(sighting.Army.Members[0].HitPointsCurrent, Is.EqualTo(3)); Assert.That(restoredHiddenUnit.HitPointsCurrent, Is.EqualTo(1));
                Assert.That(Human.Hand.Hand.Single().RuntimeId, Is.EqualTo(humanCardId)); Assert.That(Human.Hand.MutationVersion, Is.EqualTo(humanMutation));
                Assert.That(Human.Hand.RemainingDeckCount, Is.EqualTo(humanDeckCount));
                Assert.That(AiHandRegistry.Peek(Ai), Is.SameAs(Ai.Hand)); Assert.That(Ai.Hand.Hand.Single().RuntimeId, Is.EqualTo(aiCardId));
                Assert.That(Ai.Hand.Hand.Single().AcquiredTurn, Is.EqualTo(6));
                Call(selection, "RefreshRememberedBuildingVisual", Hidden, false); Call(selection, "RefreshRememberedArmyVisualsAt", Hidden);
                MapObjectVisual buildingGhost = null, armyGhost = null;
                var roundTrip = selection.CaptureRememberedVisuals(visual => {
                    if (visual.GetComponent<SpriteRenderer>().color == Color.red) buildingGhost = visual; else armyGhost = visual;
                    return new MapVisualSnapshotReference { TypeKey = "verified", SnapshotKey = "frozen" };
                });
                Assert.That(roundTrip.Buildings.Count, Is.EqualTo(1)); Assert.That(roundTrip.Armies.Count, Is.EqualTo(1));
                Assert.That(buildingGhost, Is.Not.Null); Assert.That(buildingGhost.IsVisible, Is.True);
                Assert.That(BuildingRegistry.FindAt(Hidden).Visual.GetComponent<SpriteRenderer>().color, Is.EqualTo(Color.blue));
                Assert.That(buildingGhost.GetComponent<SpriteRenderer>().color, Is.EqualTo(Color.red));
                Assert.That(buildingGhost.transform.Find("flag").GetComponent<SpriteRenderer>().sprite, Is.SameAs(fixture.observedSprite));
                Assert.That(buildingGhost.GetComponent<MapFlagAnimator>().enabled, Is.False);
                Assert.That(armyGhost, Is.Not.Null); Assert.That(armyGhost.IsVisible, Is.True, "The per-hex remembered army index must bind.");
                Assert.That(DelayedBattleRegistry.TakeNext().Participants, Is.EqualTo(new[] { RestoredHiddenArmy, RestoredArmy }));
                Assert.That(new ArmyData().Id, Is.GreaterThan(Identity.ArmyHighWaterMark));
                Assert.That(new UnitData().RuntimeId, Is.GreaterThan(Identity.UnitHighWaterMark));
                Assert.That(new CardData(fixture.faction.cards[0]).RuntimeId, Is.GreaterThan(Identity.CardHighWaterMark));
            }
        }
    }
}
#endif
