#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Game.Saves;
using Game.UI;
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
    // Native EditMode file roundtrips: actual production capture, JSON envelope, atomic slot
    // store, decode, staging and scene-owner transaction. No substitute restore source or codec.
    // Explicit Awake/BuildFromLayout exercise the scene transaction, not PlayMode activation.
    public sealed class MatchSaveIntegrationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly HexCoord Home = new HexCoord(0, 0), Hidden = new HexCoord(1, 0), Bonus = new HexCoord(-1, 0);
        private GameObject objects, scene;
        private Scene isolatedScene, previousActiveScene;
        private GameConfig config;
        private Texture2D texture;
        private Sprite observedSprite, changedSprite;
        private StartingDeckCatalog catalog;
        private FactionCardCatalog faction;
        private ResearchProductionCatalog research;
        private CollectionService collection, previousCollection;
        private string profileDirectory;
        private UnityEngine.Random.State randomBefore;
        private MatchSaveAssetManifest manifest;
        private List<MatchSaveSpriteAsset> previousSprites;
        private List<MatchSaveMaterialAsset> previousMaterials;
        private List<EventCatalog> previousEvents;
        private EventCatalog eventCatalog;
        private MatchSaveStore store;
        private HexMap outgoingMap;
        private GameTurnController outgoingTurns;
        private HexSelectionController outgoingSelection;
        private PlayerSetupData outgoingHuman, outgoingAi;
        private ArmyData outgoingArmy, outgoingHiddenArmy;
        private UnitData outgoingUnit, outgoingHiddenUnit;
        private int gunnerId;
        private string SlotPath(int slot) => Path.Combine(profileDirectory, "matches", "slot-" + slot + ".json");

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
            previousActiveScene = SceneManager.GetActiveScene();
            isolatedScene = IsolatedTestScene.Open();
            SceneManager.SetActiveScene(isolatedScene);
            objects = new GameObject("file-save-fixture"); objects.SetActive(false);
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
            config.collectionDeckCatalog = catalog; config.collectionResearchCatalog = research;
            var marker = Marker("authored marker prefab", Color.white);
            marker.transform.Find("flag").GetComponent<SpriteRenderer>().sprite = changedSprite;
            faction.armyPrefab = marker; faction.basePrefab = marker; faction.citadelPrefab = marker;
            config.facilityMarkerPrefab = marker;
            manifest = Resources.Load<MatchSaveAssetManifest>("MatchSaveAssetManifest");
            Assert.That(manifest, Is.Not.Null, "Editor must generate the production content manifest.");
            previousSprites = manifest.Sprites; previousMaterials = manifest.Materials; previousEvents = manifest.EventCatalogs;
            manifest.Sprites = new List<MatchSaveSpriteAsset>(previousSprites) {
                new MatchSaveSpriteAsset { Key = "native-test:observed", Asset = observedSprite },
                new MatchSaveSpriteAsset { Key = "native-test:changed", Asset = changedSprite } };
            manifest.Materials = new List<MatchSaveMaterialAsset>(previousMaterials);
            var material = marker.GetComponent<SpriteRenderer>().sharedMaterial;
            if (material != null && !manifest.Materials.Any(x => x.Asset == material))
                manifest.Materials.Add(new MatchSaveMaterialAsset { Key = "native-test:sprite-material", Asset = material });
            eventCatalog = ScriptableObject.CreateInstance<EventCatalog>();
            eventCatalog.cardCatalogs.Add(faction);
            eventCatalog.events.Add(new EventDefinition { authoredKey = "save.event", name = "Saved Library", grantsRandomCollectionCard = true });
            manifest.EventCatalogs = new List<EventCatalog>(previousEvents) { eventCatalog };
            profileDirectory = Path.Combine(Path.GetTempPath(), "match-restore-" + Guid.NewGuid().ToString("N"));
            collection = new CollectionService(new DeckRules(catalog, research), new CollectionProfileStore(profileDirectory), new CollectionProfile());
            previousCollection = ProgressionContext.Collection; SetCollection(collection);
            store = new MatchSaveStore(Path.Combine(profileDirectory, "matches"));
        }

        [TearDown]
        public void TearDown()
        {
            // Assertions run BEFORE this hygiene. In particular this is never used to make
            // an injected-failure cleanup assertion pass.
            MatchStateInventory.EndMatch(); SetCollection(previousCollection);
            if (manifest != null && previousSprites != null)
            { manifest.Sprites = previousSprites; manifest.Materials = previousMaterials; manifest.EventCatalogs = previousEvents; }
            Object.DestroyImmediate(eventCatalog);
            Object.DestroyImmediate(objects);
            if (previousActiveScene.IsValid()) SceneManager.SetActiveScene(previousActiveScene);
            IsolatedTestScene.Close(isolatedScene);
            Object.DestroyImmediate(config);
            Object.DestroyImmediate(observedSprite); Object.DestroyImmediate(changedSprite); Object.DestroyImmediate(texture);
            Object.DestroyImmediate(catalog); Object.DestroyImmediate(research); Object.DestroyImmediate(faction);
            if (Directory.Exists(profileDirectory)) Directory.Delete(profileDirectory, true);
            UnityEngine.Random.state = randomBefore;
        }

        private byte[] CaptureFile(bool destroyHiddenBuilding = false)
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
            typeof(GameSession).GetField("<MatchId>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, "native-file-match");
            typeof(GameSession).GetField("<RewardsEligible>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, true);
            var startingDeck = new SavedDeck { deckId = "profile-deck", name = "Before save", faction = human.Faction,
                mainCards = new List<DeckCardEntry> { new DeckCardEntry { cardKey = faction.cards[0].authoredKey, count = 1 } } };
            human.SelectedDeckId = startingDeck.deckId;
            human.MatchLoadout = new MatchLoadout(startingDeck, collection.Rules, _ => 1);
            human.BlueprintQuota = new BlueprintQuota(human.MatchLoadout);
            var oldTurns = Child("outgoing turns").AddComponent<GameTurnController>();
            oldTurns.ResumeGame(SavedTurn());
            Set(oldTurns, "gameConfig", config);
            var root = PlayerRoot.Create(human, "outgoing root"); root.transform.SetParent(objects.transform);
            root.RestoreEconomyState(new PlayerEconomyState { ActionPoints = 3, Human = 2, Energy = 5, Materials = 7, Tech = 11,
                InitiativeContributions = new List<ResourceType> { ResourceType.Tech, ResourceType.Human } });
            PlayerRootRegistry.Register(human, root);
            var aiRoot = PlayerRoot.Create(ai, "outgoing AI root"); aiRoot.transform.SetParent(objects.transform);
            PlayerRootRegistry.Register(ai, aiRoot);
            var army = new ArmyData { Owner = human, Hex = Home };
            var unit = new UnitData { Owner = human, Name = "Spent defender", MoveMax = 3, HitPointsMax = 5, HitPointsCurrent = 4 };
            unit.RestoreMovementSpent(7); army.Members.Add(unit); army.MarkActivated();
            var hiddenArmy = new ArmyData { Owner = ai, Hex = Hidden };
            var hiddenUnit = new UnitData { Owner = ai, Name = "Observed scout", HitPointsMax = 3, HitPointsCurrent = 3, MoveMax = 2 };
            hiddenArmy.Members.Add(hiddenUnit);
            var building = new BuildingData { Owner = human, Hex = Hidden }; building.FacilitySlots[0] = new FacilityData();
            var gunner = new UnitData { Owner = ai, Name = "Facility gunner" }; building.FacilitySlots[0].AntiAirGunner = gunner;
            using (MatchRestoreScope.Begin())
            { ArmyRegistry.Register(army); ArmyRegistry.Register(hiddenArmy); BuildingRegistry.Register(Hidden, building); }
            AntiAirState.RecordPrompted(unit, hiddenArmy.Id, true);
            AntiAirState.RecordPrompted(gunner, army.Id, false);
            HumanVisualMemory.ObserveArmy(human, hiddenArmy, Hidden);
            HumanVisualMemory.ObserveBuilding(human, Hidden, true);
            var oldSelection = Selection("outgoing selection", oldMap);
            Set(oldTurns, "hexSelectionController", oldSelection);
            Set(oldSelection, "turnController", oldTurns);
            var buildingArt = Marker("observed building", Color.red); building.Visual = buildingArt;
            var armyArt = Marker("observed army", Color.yellow);
            var controller = armyArt.gameObject.AddComponent<ArmyController>(); Call(controller, "Awake");
            hiddenArmy.Controller = controller; controller.SetData(hiddenArmy);
            Call(oldSelection, "UpdateRememberedBuildingVisual", human, Hidden, buildingArt);
            Call(oldSelection, "UpdateRememberedArmyVisual", human, hiddenArmy, Hidden);
            // Hidden world changes after the observation. The source captures the changed live
            // facts and the old renderer/sighting facts through their different owner APIs.
            building.Owner = ai; // Captured while hidden: retain the old owner's frozen flag.
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
            var eventEntry = new HexEventRegistry.Entry { Definition = eventCatalog.events[0], Skipped = true }; eventEntry.DiscoveredBy.Add(human); eventEntry.KnownBy.Add(human);
            HexEventRegistry.RestoreEntries(new[] { new HexEventEntryState<HexEventRegistry.Entry> { Hex = Home, Payload = eventEntry } }, (hex, entry) => entry);
            DelayedBattleRegistry.Add(new PendingBattle { Hex = Hidden, Participants = new List<ArmyData> { hiddenArmy, army } });
            if (destroyHiddenBuilding) using (MatchRestoreScope.Begin()) BuildingRegistry.Unregister(Hidden);
            outgoingMap = oldMap; outgoingTurns = oldTurns; outgoingSelection = oldSelection;
            outgoingHuman = human; outgoingAi = ai; outgoingArmy = army; outgoingHiddenArmy = hiddenArmy;
            outgoingUnit = unit; outgoingHiddenUnit = hiddenUnit; gunnerId = gunner.RuntimeId;
            // Include issued but departed IDs; decode must never reserve or issue identities.
            _ = new ArmyData(); _ = new UnitData(); _ = new CardData(faction.cards[0]);
            Assert.That(oldTurns.CanSaveMatch(out string reason), Is.True, reason);
            return MatchSaveCodec.Capture(config, oldMap, oldTurns, oldSelection);
        }

        private static GameTurnState SavedTurn() => new GameTurnState { Round = 7, PlayerOrder = new[] { "ai", "human" }, CurrentPlayerIndex = 1,
            Completed = new[] { new PlayerTurnCount { PlayerId = "ai", CompletedTurns = 7 }, new PlayerTurnCount { PlayerId = "human", CompletedTurns = 6 } },
            GreetingsShown = new[] { "human" }, PendingMessages = Array.Empty<PlayerTurnMessages>() };

        private CitadelSetupController PrepareScene(IMatchRestoreSource source, out HexMap map, out GameTurnController turns, out HexSelectionController selection)
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

        private IMatchRestoreSource ReadSlot(int slot = 0)
        {
            var before = MatchIdentityState.Capture();
            var source = store.Load(slot, bytes => MatchSaveCodec.Decode(bytes, config), out string notice);
            Assert.That(notice, Is.Null);
            AssertCounters(before);
            return source;
        }
        private static void AssertCounters(MatchIdentityState before)
        {
            var after = MatchIdentityState.Capture();
            Assert.That(after.ArmyHighWaterMark, Is.EqualTo(before.ArmyHighWaterMark), "Detached decoding must not issue/reserve army IDs.");
            Assert.That(after.UnitHighWaterMark, Is.EqualTo(before.UnitHighWaterMark), "Detached decoding must not issue/reserve unit IDs.");
            Assert.That(after.CardHighWaterMark, Is.EqualTo(before.CardHighWaterMark), "Detached decoding must not issue/reserve card IDs.");
        }
        private void LoadScene(IMatchRestoreSource source, out GameTurnController turns, out HexSelectionController selection)
        {
            Assert.That(GameSession.TryPrepareRestore(source, config, out string error), Is.True, error);
            var setup = PrepareScene(source, out var map, out turns, out selection);
            int starts = 0, cards = 0, visibility = 0;
            turns.TurnStarted += _ => starts++;
            source.Players.Single(p => p.IsHuman).Hand.HandChanged += () => cards++;
            Action<PlayerSetupData> changed = _ => visibility++;
            var oldRoller = StealthSystem.ChallengeRoller;
            StealthSystem.ChallengeRoller = (spot, hide) => throw new InvalidOperationException("Restore rerolled detection.");
            VisionSystem.VisibilityChanged += changed;
            try
            {
                Assert.That(RestoreThroughSceneOwner(setup), Is.True, GameSession.SetupError);
                Assert.That(GameSession.PendingRestore, Is.Null);
                Assert.That(map.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.GreaterThan(0));
                Assert.That(turns.CanSaveMatch(out error), Is.True, error);
                Assert.That(starts, Is.Zero, "Resume must not begin another player turn.");
                Assert.That(cards, Is.Zero, "Restore must not deal cards or publish hand edits.");
                Assert.That(visibility, Is.Zero, "Restore must not rescan hidden world facts.");
            }
            finally { StealthSystem.ChallengeRoller = oldRoller; VisionSystem.VisibilityChanged -= changed; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FileRoundtripRestoresFreshGraphAndHistoricalVisualsThenContinues(bool destroyedBuilding)
        {
            byte[] bytes = CaptureFile(destroyedBuilding);
            var originalHand = outgoingHuman.Hand;
            int armyId = outgoingArmy.Id, hiddenId = outgoingHiddenArmy.Id, unitId = outgoingUnit.RuntimeId;
            int cardId = originalHand.Hand.Single().RuntimeId;
            var savedHand = originalHand.CaptureState();
            store.Save(0, bytes);
            Assert.That(File.ReadAllBytes(SlotPath(0)), Is.EqualTo(bytes));
            Assert.That(store.Slots(), Is.EqualTo(new[] { 0 }));
            // The saved tactical state precedes this permanent grant. Its durable receipt
            // must remain after every rollback, including a new service reading the profile.
            Assert.That(collection.TryGrantRandomCollectionCard(GameSession.MatchId, "0:0", out var firstReward, out string error), Is.True, error);
            Assert.That(firstReward, Is.SameAs(faction.cards[0]));
            var editedDeck = new SavedDeck { deckId = "profile-deck", name = "Edited after saving", faction = outgoingHuman.Faction };
            Assert.That(collection.SaveDeck(editedDeck, out error), Is.True, error);
            originalHand.RemoveCard(originalHand.Hand.Single());
            outgoingUnit.RestoreMovementSpent(0);
            PlayerRootRegistry.FindFor(outgoingHuman).AddResource(ResourceType.Tech, 99);
            var first = ReadSlot();
            Assert.That(first.Players.Single(p => p.IsHuman), Is.Not.SameAs(outgoingHuman));
            Assert.That(GameSession.FindHumanPlayer(), Is.SameAs(outgoingHuman), "Read/validation cannot replace the active session.");
            LoadScene(first, out var turns, out var selection);
            var firstHuman = GameSession.FindHumanPlayer();
            var firstArmy = ArmyRegistry.AllAt(Home).Single(a => a.Id == armyId);
            var firstHidden = ArmyRegistry.AllAt(Hidden).Single(a => a.Id == hiddenId);
            AssertRestored(turns, selection, destroyedBuilding, armyId, hiddenId, unitId, cardId, savedHand);
            Assert.That(firstArmy, Is.Not.SameAs(outgoingArmy));
            Assert.That(firstArmy.Members.Single(), Is.Not.SameAs(outgoingUnit));
            Assert.That(firstHuman.Hand, Is.Not.SameAs(originalHand));
            Assert.That(firstHidden.Owner, Is.Not.SameAs(outgoingAi));
            // Continue through real owner APIs. Spent activation/reaction/movement survive;
            // drawing consumes the frozen pool, never the edited permanent profile deck.
            PlayerRootRegistry.FindFor(firstHuman).SpendActionPoints(1);
            firstHuman.Hand.RemoveCard(firstHuman.Hand.Hand.Single());
            Assert.That(firstHuman.Hand.DrawOne(), Is.Not.Null);
            Assert.That(firstHuman.Hand.RemainingDeckCount, Is.EqualTo(savedHand.remainingDeckKeys.Count - 1));
            firstHuman.Hand.AddCard(new CardData(firstReward));
            Assert.That(firstHuman.Hand.Hand.Count, Is.EqualTo(2));
            firstArmy.Members.Single().RestoreMovementSpent(0);
            firstHidden.Members.Single().HitPointsCurrent = 0;
            firstHuman.MatchLoadout = null;
            var second = ReadSlot();
            LoadScene(second, out var secondTurns, out var secondSelection);
            AssertRestored(secondTurns, secondSelection, destroyedBuilding, armyId, hiddenId, unitId, cardId, savedHand);
            Assert.That(GameSession.FindHumanPlayer(), Is.Not.SameAs(firstHuman));
            Assert.That(ArmyRegistry.AllAt(Home).Single(a => a.Id == armyId), Is.Not.SameAs(firstArmy));
            Assert.That(File.ReadAllBytes(SlotPath(0)), Is.EqualTo(bytes), "Loads and live mutations cannot rewrite the snapshot.");
            Assert.That(new ArmyData().Id, Is.GreaterThan(second.Identity.ArmyHighWaterMark));
            Assert.That(new UnitData().RuntimeId, Is.GreaterThan(second.Identity.UnitHighWaterMark));
            Assert.That(new CardData(faction.cards[0]).RuntimeId, Is.GreaterThan(second.Identity.CardHighWaterMark));
            var persisted = new CollectionProfileStore(profileDirectory).Load(out _);
            var reloadedCollection = new CollectionService(collection.Rules, new CollectionProfileStore(profileDirectory), persisted);
            Assert.That(reloadedCollection.TryGrantRandomCollectionCard(GameSession.MatchId, "0:0", out var replay, out error), Is.True, error);
            Assert.That(replay, Is.SameAs(firstReward));
            GameSession.FindHumanPlayer().Hand.AddCard(new CardData(replay));
            Assert.That(GameSession.FindHumanPlayer().Hand.Hand.Count, Is.EqualTo(2), "Each tactical timeline can receive its original event card once.");
            Assert.That(reloadedCollection.Owned("restore.card"), Is.EqualTo(1));
            Assert.That(reloadedCollection.Snapshot.eventRewards.Count, Is.EqualTo(1));
            Assert.That(reloadedCollection.GetDeck("profile-deck").name, Is.EqualTo("Edited after saving"));
            Assert.That(reloadedCollection.GetDeck("profile-deck").mainCards, Is.Empty);
        }

        private void AssertRestored(GameTurnController turns, HexSelectionController selection, bool destroyedBuilding,
            int armyId, int hiddenId, int unitId, int cardId, HandState hand)
        {
            var human = GameSession.FindHumanPlayer();
            var ai = GameSession.Players.Single(p => !p.IsHuman);
            var root = PlayerRootRegistry.FindFor(human);
            var army = ArmyRegistry.AllAt(Home).Single(a => a.Id == armyId);
            var hidden = ArmyRegistry.AllAt(Hidden).Single(a => a.Id == hiddenId);
            Assert.That(GameSession.MatchId, Is.EqualTo("native-file-match"));
            Assert.That(turns.CurrentPlayer, Is.SameAs(human));
            Assert.That(turns.CurrentTurnOrder, Is.EqualTo(new[] { ai, human }));
            Assert.That(turns.TurnNumber, Is.EqualTo(7)); Assert.That(turns.CompletedTurnsFor(human), Is.EqualTo(6));
            Assert.That(root.ActionPoints, Is.EqualTo(3)); Assert.That(root.GetResource(ResourceType.Tech), Is.EqualTo(11));
            Assert.That(root.CaptureEconomyState().InitiativeContributions, Is.EqualTo(new[] { ResourceType.Tech, ResourceType.Human }));
            Assert.That(army.Owner, Is.SameAs(human)); Assert.That(army.Members.Single().Owner, Is.SameAs(human));
            Assert.That(army.Members.Single().RuntimeId, Is.EqualTo(unitId));
            Assert.That(army.HasActivatedThisTurn, Is.True); Assert.That(army.RequiresActivationCharge(army.Members.Single()), Is.False);
            Assert.That(army.Members.Single().MovementSpent, Is.EqualTo(7));
            Assert.That(army.Members.Single().PermanentMoveMax, Is.EqualTo(3));
            Assert.That(AntiAirState.CanReact(army.Members.Single(), hiddenId + 100), Is.False);
            Assert.That(hidden.Owner, Is.SameAs(ai)); Assert.That(hidden.Members.Single().HitPointsCurrent, Is.EqualTo(1));
            Assert.That(human.Hand.Hand.Single().RuntimeId, Is.EqualTo(cardId));
            Assert.That(human.Hand.MutationVersion, Is.EqualTo(hand.mutationVersion));
            Assert.That(human.Hand.RemainingDeckCount, Is.EqualTo(hand.remainingDeckKeys.Count));
            Assert.That(human.MatchLoadout.CaptureState().mainCards.Single().count, Is.EqualTo(1));
            Assert.That(AiHandRegistry.Peek(ai), Is.SameAs(ai.Hand)); Assert.That(ai.Hand.Hand.Single().AcquiredTurn, Is.EqualTo(6));
            Assert.That(VisionSystem.IsVisible(human, Hidden), Is.False); Assert.That(VisionSystem.HasEverSeen(human, Hidden), Is.True);
            Assert.That(VisionSystem.IsVisited(human, Bonus), Is.False);
            Assert.That(StealthSystem.IsDetectedBy(hidden.Members.Single(), human), Is.True);
            Assert.That(StealthSystem.TakeDetectionNotices(human), Is.EqualTo(new[] { "Saved detection report" }));
            Assert.That(StealthSystem.TakeDetectionNotices(human), Is.Empty);
            Assert.That(HumanVisualMemory.TryGetArmySighting(human, hiddenId, out var sighting), Is.True);
            Assert.That(sighting.Army.Owner, Is.SameAs(ai)); Assert.That(sighting.Army.Members.Single().Owner, Is.SameAs(ai));
            Assert.That(sighting.Army.Members.Single().HitPointsCurrent, Is.EqualTo(3));
            var building = BuildingRegistry.FindAt(Hidden);
            if (destroyedBuilding) Assert.That(building, Is.Null);
            else
            {
                Assert.That(building.Owner, Is.SameAs(ai));
                Assert.That(building.FacilitySlots[0].AntiAirGunner.RuntimeId, Is.EqualTo(gunnerId));
                Assert.That(building.FacilitySlots[0].AntiAirGunner.Owner, Is.SameAs(ai));
                Assert.That(AntiAirState.CanReact(building.FacilitySlots[0].AntiAirGunner, armyId), Is.False);
            }
            Call(selection, "RefreshRememberedBuildingVisual", Hidden, false);
            Call(selection, "RefreshRememberedArmyVisualsAt", Hidden);
            MapObjectVisual oldBuilding = null, oldArmy = null;
            var appearances = selection.CaptureRememberedVisuals(visual =>
            {
                var tint = visual.GetComponent<SpriteRenderer>().color;
                if (tint == Color.red) oldBuilding = visual; else oldArmy = visual;
                return new MapVisualSnapshotReference { TypeKey = "assertion", SnapshotKey = "assertion" };
            });
            Assert.That(appearances.Buildings.Count, Is.EqualTo(1)); Assert.That(appearances.Armies.Count, Is.EqualTo(1));
            Assert.That(oldBuilding, Is.Not.Null); Assert.That(oldBuilding.IsVisible, Is.True);
            Assert.That(oldBuilding.transform.Find("flag").GetComponent<SpriteRenderer>().sprite, Is.SameAs(observedSprite));
            Assert.That(oldBuilding.GetComponent<MapFlagAnimator>() == null || !oldBuilding.GetComponent<MapFlagAnimator>().enabled, Is.True);
            Assert.That(oldArmy, Is.Not.Null); Assert.That(oldArmy.IsVisible, Is.True);
            Assert.That(DelayedBattleRegistry.TakeNext().Participants, Is.EqualTo(new[] { hidden, army }));
            Assert.That(HexResourceBonusRegistry.CaptureState().Single().Bonus.energy, Is.EqualTo(2));
            Assert.That(HexEventRegistry.FindAt(Home).Skipped, Is.True);
            Assert.That(HexEventRegistry.FindAt(Home).KnownBy.Single(), Is.SameAs(human));
            Assert.That(collection.Owned("restore.card"), Is.EqualTo(1)); Assert.That(collection.Snapshot.eventRewards.Count, Is.EqualTo(1));
        }

        public enum Damage
        {
            Truncated, InvalidUtf8, BadChecksum, FutureVersion, OldVersion, WrongContent, UnknownCard,
            UnknownTerrain, DuplicatePlayer, DuplicateArmy, DuplicateUnit, DuplicateCard, DanglingMember,
            WrongCurrentPlayer, Unsettled, MissingVisualCoverage, InvalidHighWater, DuplicateJsonField,
            ArmyOutsideMap, BuildingOutsideMap, EventOutsideMap, BonusOutsideMap, BattleOutsideMap,
            MissingHumanLoadout, MissingHumanQuota, MissingHumanEconomy, MissingAiEconomy, PendingBlueprint, MissingLivingOrder, OnlyOneLivingPlayer, QuotaMismatch, LoadoutFactionMismatch, MissingCurrentViewer, WrongCurrentViewer, OverflowGeometry,
        }
        private byte[] DamageFile(byte[] bytes, Damage damage)
        {
            if (damage == Damage.Truncated) return bytes.Take(bytes.Length / 2).ToArray();
            if (damage == Damage.InvalidUtf8) return new byte[] { 0xc3, 0x28 };
            var envelope = MatchSaveJson.Deserialize<MatchSaveEnvelope>(Encoding.UTF8.GetString(bytes));
            var data = MatchSaveJson.Deserialize<MatchSaveData>(envelope.payload);
            switch (damage)
            {
                case Damage.MissingCurrentViewer: data.presentation.CurrentViewerId = null; break;
                case Damage.WrongCurrentViewer: data.presentation.CurrentViewerId = "ai"; break;
                case Damage.OverflowGeometry: data.map.OuterRadius = float.MaxValue; break;
                case Damage.BadChecksum: envelope.payloadSha256 = new string('0', 64); break;
                case Damage.FutureVersion: envelope.version++; break;
                case Damage.OldVersion: envelope.version = 0; break;
                case Damage.WrongContent: envelope.contentFingerprint = new string(envelope.contentFingerprint[0] == '0' ? '1' : '0', 64); break;
                case Damage.UnknownCard: data.world.cards[0].definitionKey = "missing-authored-card"; break;
                case Damage.UnknownTerrain: data.map.Cells[0].TerrainKey = "missing-authored-terrain"; break;
                case Damage.DuplicatePlayer: data.world.players.Add(data.world.players[0]); break;
                case Damage.DuplicateArmy: data.world.armies.Add(data.world.armies[0]); break;
                case Damage.DuplicateUnit: data.world.units.Add(data.world.units[0]); break;
                case Damage.DuplicateCard: data.world.cards.Add(data.world.cards[0]); break;
                case Damage.DanglingMember: data.world.armies[0].members.Add(int.MaxValue - 1); break;
                case Damage.WrongCurrentPlayer: data.turn.CurrentPlayerIndex = 0; break;
                case Damage.Unsettled: data.settledHumanTurn = false; break;
                case Damage.MissingVisualCoverage: data.presentation.Visuals.Clear(); break;
                case Damage.InvalidHighWater: data.identity.UnitHighWaterMark = int.MaxValue; break;
                case Damage.ArmyOutsideMap: data.world.armies[0].q = 10000; break;
                case Damage.BuildingOutsideMap: data.world.buildings[0].q = 10000; break;
                case Damage.EventOutsideMap: data.world.events[0].q = 10000; break;
                case Damage.BonusOutsideMap: data.world.bonuses[0].q = 10000; break;
                case Damage.BattleOutsideMap: data.world.battles[0].q = 10000; break;
                case Damage.MissingLivingOrder: data.turn.PlayerOrder = new[] { "human" }; data.turn.CurrentPlayerIndex = 0; break;
                case Damage.OnlyOneLivingPlayer: data.world.players.Single(p => !p.human).eliminated = true; break;
                case Damage.MissingHumanLoadout: data.world.players.Single(p => p.human).hasLoadout = false; break;
                case Damage.MissingHumanQuota: data.world.players.Single(p => p.human).hasQuota = false; break;
                case Damage.MissingHumanEconomy: data.world.players.Single(p => p.human).hasEconomy = false; break;
                case Damage.MissingAiEconomy: data.world.players.Single(p => !p.human).hasEconomy = false; break;
                case Damage.QuotaMismatch: data.world.players.Single(p => p.human).quota.unlockedKeys.Add("restore.card"); break;
                case Damage.LoadoutFactionMismatch: data.world.players.Single(p => p.human).loadout.faction = Faction.Ashen; break;
                case Damage.PendingBlueprint:
                    var quota = data.world.players.Single(p => p.human).quota;
                    quota.unlockedKeys.Add("restore.card"); quota.pendingKeys.Add("restore.card"); break;
            }
            if (damage != Damage.BadChecksum)
            { envelope.payload = MatchSaveJson.Serialize(data); envelope.payloadSha256 = MatchSaveCodec.Sha256(envelope.payload); }
            string json = MatchSaveJson.Serialize(envelope);
            if (damage == Damage.DuplicateJsonField) json = json.Insert(1, "\"version\":1,");
            return Encoding.UTF8.GetBytes(json);
        }

        [TestCaseSource(nameof(AllDamage))]
        public void DamagedFilesFailBeforeReplacingSessionOrIssuingIds(Damage damage)
        {
            byte[] saved = CaptureFile();
            var players = GameSession.Players; var hand = outgoingHuman.Hand;
            var before = MatchIdentityState.Capture();
            store.Save(0, DamageFile(saved, damage));
            Assert.That(() => store.Load(0, bytes => MatchSaveCodec.Decode(bytes, config), out _), Throws.Exception);
            Assert.That(GameSession.Players, Is.SameAs(players)); Assert.That(GameSession.FindHumanPlayer().Hand, Is.SameAs(hand));
            Assert.That(GameSession.MatchId, Is.EqualTo("native-file-match")); Assert.That(GameSession.PendingRestore, Is.Null);
            Assert.That(ArmyRegistry.AllAt(Home).Single(), Is.SameAs(outgoingArmy));
            Assert.That(ArmyRegistry.AllAt(Hidden).Single(), Is.SameAs(outgoingHiddenArmy));
            Assert.That(outgoingUnit.MovementSpent, Is.EqualTo(7));
            Assert.That(MatchRestoreScope.IsRestoring, Is.False); AssertCounters(before);
        }
        private static IEnumerable<Damage> AllDamage => Enum.GetValues(typeof(Damage)).Cast<Damage>();

        [Test]
        public void DetachedSourceExposesCopiesOfMapTurnAndIdentityRecords()
        {
            store.Save(0, CaptureFile());
            var source = ReadSlot();
            string terrain = source.Map.Cells[0].TerrainKey;
            int unitHigh = source.Identity.UnitHighWaterMark;
            source.Map.Cells[0].TerrainKey = "external mutation";
            source.Turn.Round = -1;
            source.Identity.UnitHighWaterMark = -1;
            Assert.That(source.Map.Cells[0].TerrainKey, Is.EqualTo(terrain));
            Assert.That(source.Turn.Round, Is.EqualTo(7));
            Assert.That(source.Identity.UnitHighWaterMark, Is.EqualTo(unitHigh));
            LoadScene(source, out var turns, out _);
            Assert.That(turns.TurnNumber, Is.EqualTo(7));
            Assert.That(ArmyRegistry.AllAt(Home).Single().Members.Single().MovementSpent, Is.EqualTo(7));
        }

        [Test]
        public void SameRoundEliminatedPlayerRetainsItsOrderSlotAcrossActualFileCapture()
        {
            CaptureFile();
            var eliminated = new PlayerSetupData { PlayerId = "eliminated", Nickname = "Eliminated this round",
                Faction = Faction.IronConcord, ColorIndex = 2, IsEliminated = true };
            GameSession.Players.Add(eliminated);
            var root = PlayerRoot.Create(eliminated, "outgoing eliminated root"); root.transform.SetParent(objects.transform);
            PlayerRootRegistry.Register(eliminated, root);
            var turn = SavedTurn(); turn.PlayerOrder = new[] { "ai", "eliminated", "human" }; turn.CurrentPlayerIndex = 2;
            turn.Completed = turn.Completed.Concat(new[] { new PlayerTurnCount { PlayerId = "eliminated", CompletedTurns = 5 } }).ToArray();
            outgoingTurns.ResumeGame(turn);
            store.Save(0, MatchSaveCodec.Capture(config, outgoingMap, outgoingTurns, outgoingSelection));
            LoadScene(ReadSlot(), out var restoredTurns, out _);
            Assert.That(restoredTurns.CurrentTurnOrder.Select(p => p.PlayerId), Is.EqualTo(turn.PlayerOrder));
            Assert.That(restoredTurns.CurrentTurnOrder[1].IsEliminated, Is.True);
            Assert.That(restoredTurns.CurrentPlayer.PlayerId, Is.EqualTo("human"));
            Assert.That(restoredTurns.CompletedTurnsFor(restoredTurns.CurrentTurnOrder[1]), Is.EqualTo(5));
        }

        [Test]
        public void DamagedPrimaryLoadsActualBackupButFutureVersionDoesNotFallBack()
        {
            byte[] bytes = CaptureFile();
            store.Save(0, bytes); store.Save(0, bytes);
            File.WriteAllBytes(SlotPath(0), DamageFile(bytes, Damage.Truncated));
            var before = MatchIdentityState.Capture();
            var recovered = store.Load(0, data => MatchSaveCodec.Decode(data, config), out string notice);
            Assert.That(recovered.MatchId, Is.EqualTo("native-file-match")); Assert.That(notice, Does.Contain("backup"));
            AssertCounters(before);
            Assert.That(GameSession.FindHumanPlayer(), Is.SameAs(outgoingHuman));
            store.Save(0, DamageFile(bytes, Damage.FutureVersion));
            Assert.That(() => store.Load(0, data => MatchSaveCodec.Decode(data, config), out _), Throws.TypeOf<NotSupportedException>());
            Assert.That(File.ReadAllBytes(SlotPath(0)), Is.EqualTo(DamageFile(bytes, Damage.FutureVersion)));
            Assert.That(File.Exists(SlotPath(0) + ".bak"), Is.True); AssertCounters(before);
        }

        [Test]
        public void StaleCampaignDoesNotRecoverAnotherMatchOrQuarantineAnIntactPrimary()
        {
            byte[] backup = CaptureFile();
            var envelope = MatchSaveJson.Deserialize<MatchSaveEnvelope>(Encoding.UTF8.GetString(backup));
            var data = MatchSaveJson.Deserialize<MatchSaveData>(envelope.payload);
            data.hasCampaign = true;
            data.campaign = new MatchCampaignData { campaignId = Guid.NewGuid().ToString("N"),
                operationId = Guid.NewGuid().ToString("N"), matchId = data.matchId, selectedHumanDeckId = "obsolete-deck" };
            envelope.payload = MatchSaveJson.Serialize(data); envelope.payloadSha256 = MatchSaveCodec.Sha256(envelope.payload);
            byte[] primary = Encoding.UTF8.GetBytes(MatchSaveJson.Serialize(envelope));
            store.Save(0, backup); store.Save(0, primary);
            var before = MatchIdentityState.Capture();
            Assert.Throws<NotSupportedException>(() => store.Load(0, bytes => MatchSaveCodec.Decode(bytes, config), out _));
            Assert.That(File.ReadAllBytes(SlotPath(0)), Is.EqualTo(primary));
            Assert.That(File.ReadAllBytes(SlotPath(0) + ".bak"), Is.EqualTo(backup));
            Assert.That(GameSession.FindHumanPlayer(), Is.SameAs(outgoingHuman));
            Assert.That(GameSession.PendingRestore, Is.Null); AssertCounters(before);
        }

        [TestCase("attachment")]
        [TestCase("production")]
        [TestCase("movement")]
        [TestCase("tactic challenge")]
        [TestCase("hand transition")]
        public void CaptureRejectsPendingOwnerOperationsBeforeWritingAnySlot(string operation)
        {
            CaptureFile();
            if (operation == "attachment") outgoingTurns.SetAttachModeActive(true);
            else if (operation == "production") Set(outgoingSelection, "_rpTransactionActive", true);
            else if (operation == "movement")
                typeof(ArmyController).GetField("<IsMoving>k__BackingField", Private)
                    .SetValue(outgoingHiddenArmy.Controller, true);
            else
            {
                var handUi = Child("pending hand operation").AddComponent<CardHandUI>();
                Set(outgoingTurns, "cardHand", handUi);
                Set(handUi, operation == "tactic challenge" ? "_tacticChallengeActive" : "_tacticFlipping", true);
            }
            try
            {
                Assert.That(outgoingTurns.CanSaveMatch(out string reason), Is.False, operation);
                Assert.That(reason, Is.Not.Empty);
                Assert.That(() => store.Save(0, MatchSaveCodec.Capture(config, outgoingMap, outgoingTurns, outgoingSelection)),
                    Throws.TypeOf<InvalidOperationException>());
                Assert.That(store.Exists(0), Is.False);
                Assert.That(GameSession.FindHumanPlayer(), Is.SameAs(outgoingHuman));
            }
            finally
            {
                // Avoid introducing OnDisable cancellation work into unrelated test cleanup.
                outgoingTurns.SetAttachModeActive(false); Set(outgoingSelection, "_rpTransactionActive", false);
                if (outgoingHiddenArmy.Controller != null)
                    typeof(ArmyController).GetField("<IsMoving>k__BackingField", Private).SetValue(outgoingHiddenArmy.Controller, false);
                var handUi = (CardHandUI)typeof(GameTurnController).GetField("cardHand", Private).GetValue(outgoingTurns);
                if (handUi != null) { Set(handUi, "_tacticChallengeActive", false); Set(handUi, "_tacticFlipping", false); }
            }
        }
    }
}
#endif

