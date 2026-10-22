#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Campaign;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Terrain;
using Game.Units;
using Game.UI;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class AiHeroDeckIntegrationTests
    {
        private StartingDeckCatalog decks;
        private GameObject host;
        private List<PlayerSetupData> previousPlayers;
        private CampaignMatchContext previousContext;

        [SetUp] public void SetUp()
        {
            previousPlayers = GameSession.Players; previousContext = GameSession.CampaignContext;
            MatchStateInventory.ResetForNewMatch();
            GameSession.TryPrepareMatch(new List<PlayerSetupData>(), null, out _);
            decks = AssetDatabase.LoadAssetAtPath<StartingDeckCatalog>("Assets/Cards/StartingDeckCatalog.asset");
            host = new GameObject("AI deck integration"); host.SetActive(false);
        }
        [TearDown] public void TearDown()
        {
            foreach (var root in host.GetComponentsInChildren<PlayerRoot>(true)) Object.DestroyImmediate(root.gameObject);
            Object.DestroyImmediate(host);
            MatchStateInventory.ResetForNewMatch();
            GameSession.TryPrepareMatch(new List<PlayerSetupData>(), null, out _);
            GameSession.Players = previousPlayers;
            if (previousContext != null) GameSession.SetCampaignContext(previousContext);
        }

        [TestCase(Faction.IronConcord, "Aldric Voss")]
        [TestCase(Faction.Ashen, "Nova Sterling")]
        [TestCase(Faction.Vessels, "Orr the Keeper")]
        public void CampaignGrantsTheActualLeaderOnceWithoutPaymentAndConsumesItsDrawCopy(Faction faction, string name)
        {
            var player = new PlayerSetupData { Faction = faction, AiDeckName = name };
            var root = PlayerRoot.Create(player, "AI"); root.transform.SetParent(host.transform);
            PlayerRootRegistry.Register(player, root);
            var selection = host.AddComponent<HexSelectionController>();
            var setup = host.AddComponent<CitadelSetupController>();
            Set(setup, "startingDeckCatalog", decks); Set(setup, "hexSelectionController", selection);
            var hex = new HexCoord(0, 0);
            var garrison = new ArmyData { Owner = player, Hex = hex, IsGarrison = true };
            ArmyRegistry.Register(garrison);
            GameSession.SetCampaignContext(new CampaignMatchContext { MatchId = "leader-grant" });
            var definition = decks.ResolveAiLeader(faction, name);
            var random = UnityEngine.Random.state;
            Grant(setup, player, hex); Grant(setup, player, hex);
            Assert.That(garrison.Members.Count, Is.EqualTo(1));
            var leader = garrison.Members.Single();
            Assert.That(leader.OriginatingCard, Is.SameAs(definition));
            Assert.That(leader.Owner, Is.SameAs(player));
            Assert.That(leader.CommandRating, Is.EqualTo(definition.commandRating));
            Assert.That(leader.Fate, Is.EqualTo(definition.fate));
            Assert.That(leader.HitPointsCurrent, Is.EqualTo(definition.hitPoints));
            CollectionAssert.AreEquivalent(definition.grantedAbilities, leader.Abilities);
            Assert.That(root.ActionPoints, Is.Zero);
            foreach (ResourceType resource in Enum.GetValues(typeof(ResourceType)))
                Assert.That(root.GetResource(resource), Is.Zero);
            Assert.That(UnityEngine.Random.state, Is.EqualTo(random));
            Assert.That(player.AiGrantedLeaderKey, Is.EqualTo(definition.authoredKey));
            var hand = AiHandRegistry.GetOrCreate(player, decks, 7, 10);
            Assert.That(hand.Hand.Select(c => c.Definition).Concat(hand.RemainingDeck).Contains(definition), Is.False);
            Assert.That(decks.BuildDeckPool(player).Contains(definition), Is.False, "Pre-turn demand uses the same consumed pool.");
            Assert.That(decks.BuildAiDeckPool(faction, name).Count(c => c == definition), Is.EqualTo(1));
        }

        [TestCase(false, false)]
        [TestCase(true, true)]
        public void QuickGameAndHumanReceiveNoFreeLeader(bool campaign, bool human)
        {
            var player = new PlayerSetupData { Faction = Faction.IronConcord, AiDeckName = "Kessa Vrail", IsHuman = human };
            var setup = host.AddComponent<CitadelSetupController>();
            Set(setup, "startingDeckCatalog", decks); Set(setup, "hexSelectionController", host.AddComponent<HexSelectionController>());
            var hex = new HexCoord(0, 0); var garrison = new ArmyData { Owner = player, Hex = hex, IsGarrison = true };
            ArmyRegistry.Register(garrison);
            if (campaign) GameSession.SetCampaignContext(new CampaignMatchContext { MatchId = "human" });
            Grant(setup, player, hex);
            Assert.That(garrison.Members, Is.Empty); Assert.That(player.AiGrantedLeaderKey, Is.Null);
            Assert.That(decks.BuildAiDeckPool(player.Faction, player.AiDeckName).Any(c => c.displayName == "Kessa Vrail"), Is.True);
        }

        [Test]
        public void AiDropdownStartsWithRandomAndSavesOnlyExplicitChoices()
        {
            var row = host.AddComponent<PlayerRowUI>();
            var dropdown = new GameObject("Deck", typeof(RectTransform)).AddComponent<TMP_Dropdown>();
            dropdown.transform.SetParent(host.transform);
            Set(row, "deckDropdown", dropdown); Set(row, "startingDeckCatalog", decks);
            var player = new PlayerSetupData { Faction = Faction.IronConcord };
            row.Bind(player, () => { }, null); row.ConfigureDeckSelection(null);
            Assert.That(dropdown.options[0].text, Is.EqualTo("Random"));
            Assert.That(dropdown.options.Count, Is.EqualTo(9)); Assert.That(player.AiDeckName, Is.Null);
            dropdown.value = 2;
            Assert.That(player.AiDeckName, Is.EqualTo(dropdown.options[2].text));
            row.RefreshDeckOptions(); Assert.That(dropdown.value, Is.EqualTo(2));
            dropdown.value = 0; Assert.That(player.AiDeckName, Is.Null);
            player.Faction = Faction.Ashen; row.RefreshDeckOptions();
            CollectionAssert.AreEqual(new[] { "Random" }.Concat(decks.GetAiDecks(Faction.Ashen).Select(d => d.deckName)),
                dropdown.options.Select(o => o.text));
        }

        [Test]
        public void CampaignChoiceIsRepeatableAndExplicitQuickChoiceIsKept()
        {
            var research = AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>("Assets/Cards/ResearchProductionCatalog.asset");
            var rules = new DeckRules(decks, research);
            foreach (var faction in DeckRules.PlayableFactions)
            {
                var seen = new HashSet<string>();
                for (int seed = 0; seed < 100; seed++)
                {
                    var source = CampaignDeck.AiSource(faction, rules, seed);
                    Assert.That(CampaignDeck.AiSource(faction, rules, seed), Is.SameAs(source));
                    Assert.That(CampaignDeck.AiSource(faction, rules, seed + 1, source.deckName), Is.SameAs(source));
                    Assert.That(CampaignDeck.Ai(faction, rules, seed).Name, Is.EqualTo(source.deckName));
                    seen.Add(source.deckName);
                }
                Assert.That(seen.Count, Is.EqualTo(8));
                var player = new PlayerSetupData { Faction = faction, AiDeckName = seen.First(), AiGrantedLeaderKey = "old" };
                GameSession.AssignAiDecks(new List<PlayerSetupData> { player }, decks);
                Assert.That(player.AiDeckName, Is.EqualTo(seen.First())); Assert.That(player.AiGrantedLeaderKey, Is.Null);
                Assert.That(player.AiBlueprints.Count, Is.EqualTo(68));
            }
        }

        [Test]
        public void RestrictedBlueprintsAlsoConstrainReadinessPreparationAndResourceForecast()
        {
            var player = new PlayerSetupData { Faction = Faction.IronConcord, CitadelHexQ = 0, CitadelHexR = 0 };
            var root = PlayerRoot.Create(player, "Blueprint test"); root.transform.SetParent(host.transform);
            PlayerRootRegistry.Register(player, root); root.ActionPoints = 20;
            foreach (ResourceType resource in Enum.GetValues(typeof(ResourceType))) root.AddResource(resource, 40);
            var research = AssetDatabase.LoadAssetAtPath<ResearchProductionCatalog>("Assets/Cards/ResearchProductionCatalog.asset");
            var body = decks.ResolveCard("concord.card.light-infantry");
            var allowed = research.ResolveFor(ResearchProductionMode.Production, player.Faction)
                .First(c => c.cardType == CardType.Equipment && EquipmentSystem.FitsHost(c, body, out _));
            player.AiBlueprints = new HashSet<string> { allowed.authoredKey };
            var hex = new HexCoord(0, 0);
            var building = new BuildingData { Owner = player, Hex = hex, IsBase = true };
            building.Abilities.Add(UnitAbilities.Barracks);
            building.FacilitySlots[0] = FacilityData.FromDefinition(decks.ResolveCard("neutral.facility.factory"));
            building.FacilitySlots[1] = FacilityData.FromDefinition(decks.ResolveCard("neutral.facility.lab"));
            BuildingRegistry.Register(hex, building);
            var selection = host.AddComponent<HexSelectionController>();
            UnitData Spawn(CardDefinition d) => selection.SpawnUnit(d.displayName, player, d.moveMax,
                d.activationApCost, d.cardType == CardType.Hero, d.commandRating, d.art, d.grantedAbilities,
                sourceDefinition: d);
            var army = new ArmyData { Owner = player, Hex = hex, IsGarrison = true };
            army.Members.Add(Spawn(decks.ResolveCard("concord.card.vector-vance")));
            army.Members.Add(Spawn(decks.ResolveCard("concord.card.bolt-jax")));
            var equipped = Spawn(body); equipped.Equipment = allowed; army.Members.Add(equipped);
            ArmyRegistry.Register(army);
            var hand = new AiHandData(null, player.Faction, 0);
            var context = new AiTurnContext { ResearchProductionCatalog = research, TurnNumber = 1 };
            var readiness = (DevelopmentReadiness)typeof(WorldAnalysis)
                .GetMethod("BuildDevelopment", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { player, root, hand, context, null });
            CollectionAssert.AreEquivalent(new[] { allowed }, readiness.CatalogOutputs);
            CollectionAssert.AreEquivalent(new[] { allowed }, readiness.StaffedOutputs);
            Assert.That(readiness.ForecastSources.All(g => g.BlueprintDef == allowed), Is.True);
            Assert.That(readiness.UpgradeTargetCount, Is.EqualTo(2), "No permitted mutator for the already equipped body.");
            var failure = typeof(DevelopmentOpportunityEvaluator)
                .GetMethod("StructuralPreparationBlock", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { ResearchProductionMode.Research, hex, true, false,
                    Array.Empty<CardDefinition>(), player, hand, context, null, null });
            Assert.That(failure, Is.EqualTo("reason=no_mode_catalog"));
            Assert.That(root.ActionPoints, Is.EqualTo(20));
            foreach (ResourceType resource in Enum.GetValues(typeof(ResourceType)))
                Assert.That(root.GetResource(resource), Is.EqualTo(40));
        }

        [Test]
        public void CampaignLeaderSeesTheCompleteInitialNeutralRosterOnFirstNotification()
        {
            var player = new PlayerSetupData { Faction = Faction.IronConcord, AiDeckName = "Lira Sable" };
            var neutral = new PlayerSetupData { Faction = Faction.Neutral, IsNeutral = true };
            var map = host.AddComponent<HexMap>();
            var terrain = HexGridMath.HexesInRange(new HexCoord(0, 0), 3)
                .ToDictionary(h => h, h => new TerrainTypeEntry { moveCost = 1 });
            map.SetData(3, 1f, terrain); AiMapMemory.EnsureSubscribed(map);
            GameSession.Players = new List<PlayerSetupData> { player };
            var setup = host.AddComponent<CitadelSetupController>();
            Set(setup, "map", map); Set(setup, "startingDeckCatalog", decks);
            Set(setup, "hexSelectionController", host.AddComponent<HexSelectionController>());
            var neutralCatalog = AssetDatabase.LoadAssetAtPath<NeutralArmyCatalog>("Assets/Cards/NeutralArmyCatalog.asset");
            Set(setup, "neutralArmyCatalog", neutralCatalog); Set(setup, "_neutralPlayer", neutral);
            var origin = new HexCoord(0, 0); var target = new HexCoord(2, 0);
            var garrison = new ArmyData { Owner = player, Hex = origin, IsGarrison = true };
            ArmyRegistry.Register(garrison);
            GameSession.SetCampaignContext(new CampaignMatchContext { MatchId = "initial-observation" });
            Grant(setup, player, origin);
            Assert.That(VisionSystem.IsVisible(player, target), Is.True);
            int firstObservedCount = -1;
            void Observe(PlayerSetupData observer, HexCoord changed)
            {
                if (observer == player && changed.Equals(target) && firstObservedCount < 0)
                    firstObservedCount = ArmyRegistry.AllAt(target).Sum(a => a.Members.Count);
            }
            VisionSystem.VisibleContentChanged += Observe;
            try
            {
                var definition = neutralCatalog.MapArmies.First();
                var army = (ArmyData)typeof(CitadelSetupController)
                    .GetMethod("SpawnNeutralArmy", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(setup, new object[] { target, definition });
                Assert.That(firstObservedCount, Is.EqualTo(army.Members.Count));
                Assert.That(firstObservedCount, Is.GreaterThan(0));
                Assert.That(AiMapMemory.KnownEnemySightingAt(player, target).Value.MemberCount,
                    Is.EqualTo(army.Members.Count));
                int before = ArmyRegistry.AllAt(target).Count;
                var empty = typeof(CitadelSetupController)
                    .GetMethod("SpawnNeutralArmy", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(setup, new object[] { target, new ArmyDefinition { name = "Empty" } });
                Assert.That(empty, Is.Null); Assert.That(ArmyRegistry.AllAt(target).Count, Is.EqualTo(before));
            }
            finally { VisionSystem.VisibleContentChanged -= Observe; }
        }

        private static void Set(object instance, string field, object value) => instance.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
        private static void Grant(CitadelSetupController setup, PlayerSetupData player, HexCoord hex) => typeof(CitadelSetupController)
            .GetMethod("GrantCampaignLeader", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(setup, new object[] { player, hex });
    }
}
#endif
