#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Aviation;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Terrain;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.EditorTests
{
    [Category("UnityScene")]
    public sealed class CommandPostEventTests
    {
        private readonly HexCoord hex = new HexCoord(2, 1);
        private GameObject scene;
        private HexMap map;
        private HexSelectionController selection;
        private StartingDeckCatalog starting;
        private FactionCardCatalog catalog;
        private PlayerSetupData owner;
        private ArmyData explorer;
        private CardDefinition baseCard;
        private HexEventRegistry.Entry entry;

        [SetUp] public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); HexEventRegistry.Clear();
            DelayedBattleRegistry.Clear();
            VisionSystem.Clear(); StealthSystem.Clear();
            scene = new GameObject("Command post event test"); scene.SetActive(false);
            map = scene.AddComponent<HexMap>();
            map.SetData(3, 1, new Dictionary<HexCoord, TerrainTypeEntry> { [hex] = new TerrainTypeEntry() });
            selection = scene.AddComponent<HexSelectionController>();
            var hand = scene.AddComponent<CardHandUI>();
            starting = ScriptableObject.CreateInstance<StartingDeckCatalog>();
            catalog = ScriptableObject.CreateInstance<FactionCardCatalog>();
            starting.catalogs.Add(catalog);
            SetField(hand, "startingDeckCatalog", starting);
            SetField(selection, "cardHandUI", hand);
            SetField(selection, "map", map);
            owner = new PlayerSetupData { Faction = Faction.IronConcord, Hand = new PlayerHandData(null, 0, 0) };
            // No hero, no card, no free hand slot and no PlayerRoot/budget are needed.
            explorer = new ArmyData { Owner = owner, Hex = hex };
            explorer.Members.Add(new UnitData { Owner = owner, HitPointsCurrent = 4, HitPointsMax = 4 });
            ArmyRegistry.Register(explorer);
            catalog.faction = owner.Faction;
            catalog.cards.Add(new CardDefinition { cardType = CardType.Base, deckBuilderExcluded = true });
            baseCard = new CardDefinition
            {
                authoredKey = "test.base", displayName = "Restored base", faction = owner.Faction,
                cardType = CardType.Base, hitPoints = 8, defenseRating = 1, airfieldCapacity = 4,
                grantedAbilities = new List<string> { UnitAbilities.Barracks, UnitAbilities.CollectMaterials },
            };
            catalog.cards.Add(baseCard);
            HexEventRegistry.Set(hex, new EventDefinition { grantsBase = true }, "Guard", null,
                new PlayerSetupData { IsNeutral = true }, null);
            entry = HexEventRegistry.FindAt(hex);
            entry.Triggered = true; // Exploration has completed; the guard is no longer on the hex.
        }

        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(scene); Object.DestroyImmediate(starting); Object.DestroyImmediate(catalog);
            Game.Ai.AiMapMemory.Clear(); ArmyRegistry.Clear(); BuildingRegistry.Clear(); HexEventRegistry.Clear();
            DelayedBattleRegistry.Clear();
            VisionSystem.Clear(); StealthSystem.Clear();
        }

        [TestCase(Faction.IronConcord, true), TestCase(Faction.IronConcord, false)]
        [TestCase(Faction.Ashen, true), TestCase(Faction.Ashen, false)]
        [TestCase(Faction.Vessels, true), TestCase(Faction.Vessels, false)]
        public void SurvivorReceivesFactionBaseOnceEvenWithFullHand(Faction faction, bool human)
        {
            owner.Faction = faction; owner.IsHuman = human; catalog.faction = faction; baseCard.faction = faction;
            int founded = 0;
            void OnFounded(BuildingData _) => founded++;
            InfrastructureActions.BaseFounded += OnFounded;
            try
            {
                selection.TriggerHexEventIfClear(hex, explorer);
                BuildingData building = BuildingRegistry.FindAt(hex);
                Assert.That(building, Is.Not.Null);
                Assert.That(building.Owner, Is.SameAs(owner));
                Assert.That(building.CatalogSource, Is.SameAs(baseCard), "Never select the starting citadel.");
                Assert.That(building.IsStartingCitadel, Is.False);
                Assert.That(building.Level, Is.EqualTo(1));
                Assert.That(building.UnlockedFacilitySlots, Is.EqualTo(2));
                Assert.That(building.StructurePointsCurrent, Is.EqualTo(8));
                Assert.That(ArmyRegistry.FindGarrisonAt(hex, owner), Is.Not.Null);
                Assert.That(AviationRules.FindAirfieldAt(hex, owner), Is.Not.Null);
                Assert.That(entry.ConsumedBy, Is.SameAs(owner));
                Assert.That(owner.Hand.Hand, Is.Empty);
                selection.GrantEventReward(hex, explorer);
                Assert.That(BuildingRegistry.FindAt(hex), Is.SameAs(building));
                Assert.That(founded, Is.EqualTo(1));
                Assert.That(ArmyRegistry.AllAt(hex).Count(a => a.IsGarrison), Is.EqualTo(1));
                Assert.That(ArmyRegistry.AllAt(hex).Count(a => a.IsAirfield), Is.EqualTo(1));
            }
            finally { InfrastructureActions.BaseFounded -= OnFounded; }
        }

        [Test] public void GuardVictoryCannotConsumeEventOrCreateNeutralBase()
        {
            explorer.Owner = entry.GuardOwner;
            selection.GrantEventReward(hex, explorer);
            Assert.That(entry.Consumed, Is.False);
            Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
            Assert.That(entry.Definition.CanVisit(new PlayerSetupData()), Is.True, "AI may claim it.");
        }

        [Test] public void LostGuardBattleKeepsEventUnclaimedForNextPlayer()
        {
            var guard = new ArmyData { Owner = entry.GuardOwner, Hex = hex, Name = entry.GuardArmyName };
            guard.Members.Add(new UnitData { Owner = guard.Owner, HitPointsCurrent = 4, HitPointsMax = 4 });
            ArmyRegistry.Register(guard);
            explorer.Members.Clear();
            var result = BattleEngine.FinalizeEncounter(new BattleState(hex, explorer, guard, new BattleGrid(), 123), selection);
            Assert.That(result.Survivor, Is.SameAs(guard));
            Assert.That(BattleEncounterCoordinator.ResolveContinuation(hex, guard).CanTriggerEvent, Is.False);
            selection.TriggerHexEventIfClear(hex, guard);
            Assert.That(entry.Consumed, Is.False);
            Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
        }

        [Test] public void LivingGuardBlocksReward()
        {
            var guard = new ArmyData { Owner = entry.GuardOwner, Hex = hex, Name = entry.GuardArmyName };
            guard.Members.Add(new UnitData { Owner = guard.Owner, HitPointsCurrent = 4, HitPointsMax = 4 });
            ArmyRegistry.Register(guard);
            entry.ResolvedGuardMembers.Add((new CardDefinition(), 1));
            selection.GrantEventReward(hex, explorer);
            Assert.That(InfrastructureActions.TryClaimEventBase(map, baseCard, hex, owner).Ok, Is.False);
            Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
            Assert.That(entry.Consumed, Is.False);
            Assert.That(entry.HasResolvedGuard, Is.True, "A living guard must not be marked defeated.");
        }

        [Test] public void SkipAndRetreatDoNotCreateBase()
        {
            entry.Triggered = false;
            HexEventRegistry.MarkSkipped(hex, owner);
            selection.GrantEventReward(hex, explorer);
            Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
            entry.Triggered = true;
            entry.ResolvedGuardMembers.Add((new CardDefinition(), 2));
            var guard = new ArmyData { Owner = entry.GuardOwner, Hex = hex, Name = entry.GuardArmyName };
            guard.Members.Add(new UnitData { Owner = guard.Owner, HitPointsCurrent = 4, HitPointsMax = 4 });
            ArmyRegistry.Register(guard);
            BattleEncounterCoordinator.HandleRetreatFromHexEvent(hex, owner);
            selection.GrantEventReward(hex, explorer);
            Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
            Assert.That(entry.Consumed, Is.False);
            Assert.That(ArmyRegistry.IsRegistered(guard), Is.False);
            Assert.That(entry.HasResolvedGuard, Is.True, "Retreat must retain the guard for the next exploration.");
        }

        [Test] public void ExistingBaseKeepsDamageUpgradesAndFacilities()
        {
            var existing = BuildingData.FromDefinition(baseCard, hex, owner);
            existing.Level = 3; existing.StructurePointsCurrent = 2;
            existing.FacilitySlots[0] = new FacilityData();
            BuildingRegistry.Register(hex, existing);
            selection.GrantEventReward(hex, explorer);
            Assert.That(BuildingRegistry.FindAt(hex), Is.SameAs(existing));
            Assert.That(existing.Level, Is.EqualTo(3));
            Assert.That(existing.StructurePointsCurrent, Is.EqualTo(2));
            Assert.That(existing.FacilitySlots[0], Is.Not.Null);
            Assert.That(entry.Consumed, Is.True);
        }

        [Test] public void ResourceSiteFacilitiesSurviveRestoration()
        {
            var site = new BuildingData { Owner = owner, Hex = hex, HasTieredUnlock = false };
            var facility = new FacilityData(); site.FacilitySlots[3] = facility;
            BuildingRegistry.Register(hex, site);
            selection.GrantEventReward(hex, explorer);
            Assert.That(BuildingRegistry.FindAt(hex).FacilitySlots[3], Is.SameAs(facility));
            Assert.That(BuildingRegistry.FindAt(hex).FacilitySlots[0], Is.Null);
            Assert.That(BuildingRegistry.FindAt(hex).IsBase, Is.True);
        }

        [Test] public void RewardBelongsToFinishingPlayerRatherThanFirstDiscoverer()
        {
            var firstVisitor = new PlayerSetupData { IsHuman = true };
            HexEventRegistry.MarkSkipped(hex, firstVisitor);
            selection.GrantEventReward(hex, explorer);
            Assert.That(BuildingRegistry.FindAt(hex).Owner, Is.SameAs(owner));
            Assert.That(entry.ConsumedBy, Is.SameAs(owner));
            Assert.That(entry.ConsumedBy, Is.Not.SameAs(firstVisitor));
        }

        [Test] public void AircraftCannotClaimBase()
        {
            explorer.Members[0].IsAviation = true; // Classification uses the roster, not the cached flag.
            selection.GrantEventReward(hex, explorer);
            Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
            Assert.That(entry.Consumed, Is.False);
        }

        [Test] public void AirfieldContainerCannotClaimBase()
        {
            explorer.IsAirfield = true;
            selection.GrantEventReward(hex, explorer);
            Assert.That(BuildingRegistry.FindAt(hex), Is.Null);
            Assert.That(entry.Consumed, Is.False);
        }

        [Test] public void FailedRestorationCanRetryAfterRegistryRoundTripWithoutRespawningGuards()
        {
            entry.ResolvedGuardMembers.Add((new CardDefinition(), 2));
            catalog.cards.Remove(baseCard); // A missing definition must leave an honest, retryable event.
            LogAssert.Expect(LogType.Error, "Event base reward could not be granted: The command post cannot be restored here.");
            selection.GrantEventReward(hex, explorer);
            Assert.That(entry.Consumed, Is.False);
            Assert.That(entry.Triggered, Is.False);
            Assert.That(entry.Skipped, Is.True);
            Assert.That(entry.HasResolvedGuard, Is.False);
            Assert.That(BuildingRegistry.FindAt(hex), Is.Null);

            var snapshot = HexEventRegistry.CaptureEntries((h, e) => e);
            HexEventRegistry.Clear(); HexEventRegistry.RestoreEntries(snapshot, (h, e) => e);
            entry = HexEventRegistry.FindAt(hex);
            Assert.That(entry.HasResolvedGuard, Is.False);
            catalog.cards.Add(baseCard);
            bool openedBattle = selection.TriggerAiEventExplore(explorer, hex);
            Assert.That(openedBattle, Is.False);
            Assert.That(entry.Consumed, Is.True);
            Assert.That(BuildingRegistry.FindAt(hex)?.Owner, Is.SameAs(owner));
        }

        [Test] public void AuthoredEventHasArtAndThreeResolvableGuardTiers()
        {
            var events = AssetDatabase.LoadAssetAtPath<EventCatalog>("Assets/Cards/EventCatalog.asset");
            var armies = AssetDatabase.LoadAssetAtPath<NeutralArmyCatalog>("Assets/Cards/NeutralArmyCatalog.asset");
            var definition = events.ResolveEvent("event-abandoned-command-post");
            Assert.That(definition?.grantsBase, Is.True);
            Assert.That(definition.image, Is.Not.Null);
            Assert.That(definition.description, Does.Contain("level 1 base"));
            Assert.That(definition.variants.Count, Is.EqualTo(3));
            foreach (var variant in definition.variants)
            {
                var guard = armies.GetArmy(variant.guardArmyName);
                Assert.That(guard, Is.Not.Null);
                Assert.That(guard.members.Any(m => m.count > 0 && armies.ResolveCard(m.cardKey) != null), Is.True);
            }
            foreach (var faction in new[] { Faction.IronConcord, Faction.Ashen, Faction.Vessels })
                Assert.That(events.cardCatalogs.Single(c => c.faction == faction).ForType(CardType.Base)
                    .Count(c => !c.deckBuilderExcluded), Is.EqualTo(1));
        }

        [Test] public void PlacementRejectsEveryCitadelAndItsSixNeighboursOnlyForBaseEvent()
        {
            var setup = scene.AddComponent<CitadelSetupController>();
            SetField(setup, "map", map);
            var players = (List<PlayerSetupData>)typeof(CitadelSetupController)
                .GetField("_allPlayers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(setup);
            var centres = new[] { new HexCoord(-3, 0), new HexCoord(3, 0) };
            players.AddRange(centres.Select(c => new PlayerSetupData { CitadelHexQ = c.Q, CitadelHexR = c.R }));
            map.SetData(6, 1, HexGridMath.HexesInRange(new HexCoord(0, 0), 6)
                .ToDictionary(h => h, h => new TerrainTypeEntry()));
            var place = typeof(CitadelSetupController).GetMethod("PlaceEvent", BindingFlags.Instance | BindingFlags.NonPublic);
            var commandPost = new EventDefinition { grantsBase = true };
            var ordinary = new EventDefinition();
            foreach (var centre in centres)
            {
                foreach (var near in HexGridMath.Neighbors(centre).Prepend(centre))
                {
                    HexEventRegistry.Clear();
                    place.Invoke(setup, new object[] { near, commandPost });
                    Assert.That(HexEventRegistry.FindAt(near), Is.Null);
                    place.Invoke(setup, new object[] { near, ordinary });
                    Assert.That(HexEventRegistry.HasActiveEvent(near), Is.EqualTo(!near.Equals(centre)));
                }
            }
            var safe = new HexCoord(0, 0);
            place.Invoke(setup, new object[] { safe, commandPost });
            Assert.That(HexEventRegistry.FindAt(safe)?.Definition, Is.SameAs(commandPost));
        }

        [TestCase(false), TestCase(true)]
        public void GenerationRespectsBaseExclusionForPlainAndGuaranteedRuins(bool distantRuin)
        {
            var setup = scene.AddComponent<CitadelSetupController>();
            var config = ScriptableObject.CreateInstance<Game.Core.GameConfig>();
            var events = ScriptableObject.CreateInstance<EventCatalog>();
            var randomState = Random.state;
            try
            {
                SetField(setup, "map", map); SetField(setup, "gameConfig", config); SetField(setup, "eventCatalog", events);
                var players = (List<PlayerSetupData>)typeof(CitadelSetupController)
                    .GetField("_allPlayers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(setup);
                players.Add(new PlayerSetupData { CitadelHexQ = -3, CitadelHexR = 0 });
                players.Add(new PlayerSetupData { CitadelHexQ = 3, CitadelHexR = 0 });
                var nearRuin = new HexCoord(-2, 0);
                var farRuin = new HexCoord(0, 0);
                map.SetData(6, 1, HexGridMath.HexesInRange(new HexCoord(0, 0), 6).ToDictionary(h => h,
                    h => new TerrainTypeEntry { terrainName = h.Equals(nearRuin) || distantRuin && h.Equals(farRuin)
                        ? HexMap.CityRuinsTerrainName : "Plain" }));
                var commandPost = new EventDefinition { grantsBase = true };
                var ordinary = new EventDefinition();
                events.events.Add(commandPost); events.events.Add(ordinary);
                var generate = typeof(CitadelSetupController).GetMethod("GenerateRandomEvents", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int seed = 0; seed < 64; seed++)
                {
                    HexEventRegistry.Clear(); HexResourceBonusRegistry.Clear(); Random.InitState(seed);
                    generate.Invoke(setup, null);
                    Assert.That(HexEventRegistry.FindAt(nearRuin)?.Definition, Is.SameAs(ordinary), "seed " + seed);
                    var postHexes = map.AllCoords.Where(h => HexEventRegistry.FindAt(h)?.Definition == commandPost).ToList();
                    Assert.That(postHexes.Count, Is.EqualTo(1), "seed " + seed);
                    foreach (var h in postHexes)
                    foreach (var player in players)
                        Assert.That(HexGridMath.Distance(h, new HexCoord(player.CitadelHexQ.Value, player.CitadelHexR.Value)),
                            Is.GreaterThanOrEqualTo(2), "seed " + seed);
                    if (distantRuin) Assert.That(postHexes[0], Is.EqualTo(farRuin));
                }
            }
            finally
            {
                Random.state = randomState; HexResourceBonusRegistry.Clear();
                Object.DestroyImmediate(events); Object.DestroyImmediate(config);
            }
        }

        private static void SetField(object target, string field, object value) => target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }
}
#endif
