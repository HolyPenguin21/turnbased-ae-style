#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Turns;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.EditorTests
{
    // Requires the Unity editor (real GameObjects/TMP and authored card assets).
    public sealed class TacticalIntelligencePanelTests
    {
        private GameObject _root, _content;
        private PlayerDataPanelUI _panel;
        private TMP_Text _text;
        private Button _button;
        private PlayerSetupData _human, _enemy;
        private List<PlayerSetupData> _previousPlayers;
        private GameTurnController _turn;

        private T Child<T>(string name) where T : Component
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_root.transform, false);
            return go.GetComponent<T>() ?? go.AddComponent<T>();
        }

        private static void Set(object target, string name, object value) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private object Call(string name) => typeof(PlayerDataPanelUI)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_panel, null);

        [SetUp]
        public void SetUp()
        {
            _previousPlayers = GameSession.Players;
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear(); VisionSystem.Configure(null);
            MissionIntentRegistry.Clear(); AiHandRegistry.Clear();
            _human = new PlayerSetupData { IsHuman = true, Nickname = "Local player" };
            _enemy = new PlayerSetupData { Nickname = "Opponent", AiDeckName = "Secret deck",
                CitadelHexQ = 0, CitadelHexR = 0 };
            GameSession.Players = new List<PlayerSetupData> { _human, _enemy };
            _root = new GameObject("Intelligence fixture");
            _root.SetActive(false); // No hand dealing or turn-controller lifecycle in this UI test.
            _panel = _root.AddComponent<PlayerDataPanelUI>();
            _button = Child<Button>("Intel");
            _content = Child<RectTransform>("Report").gameObject;
            _text = Child<TextMeshProUGUI>("Report text");
            _text.transform.SetParent(_content.transform, false);
            _turn = Child<GameTurnController>("Inactive turns");
            Set(_panel, "dataButton", _button);
            Set(_panel, "contentPanel", _content);
            Set(_panel, "dataTmpText", _text);
            Set(_panel, "cardHand", Child<CardHandUI>("Inactive hand"));
            Set(_panel, "turnController", _turn);
            Set(_panel, "map", Child<HexMap>("Map"));
            Call("Awake");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); VisionSystem.Clear();
            MissionIntentRegistry.Clear(); AiHandRegistry.Clear();
            GameSession.Players = _previousPlayers;
        }

        private BuildingData PlaceCenter()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<FactionCardCatalog>(
                "Assets/Cards/Neutral/CardCatalog_Neutral.asset");
            CardDefinition card = catalog.cards.Single(c => c.authoredKey == "neutral.facility.tactical");
            Assert.That(card.grantedAbilities, Does.Contain(UnitAbilities.TacticalIntelligence));
            var building = new BuildingData { Owner = _human, IsBase = true, Hex = new HexCoord(9, 9) };
            building.FacilitySlots[0] = FacilityData.FromDefinition(card);
            BuildingRegistry.Register(building.Hex, building);
            return building;
        }

        [Test]
        public void PlacedAbilityUnlocksButtonAndLossClosesAndClearsReport()
        {
            Assert.That(_button.gameObject.activeSelf, Is.False);
            _panel.Toggle();
            Assert.That(_content.activeSelf, Is.False);
            BuildingData building = PlaceCenter();
            Call("Update");
            Assert.That(_button.gameObject.activeSelf, Is.True);
            _panel.Toggle();
            Assert.That(_content.activeSelf, Is.True);
            Assert.That(_text.text.Split(new[] { "???" }, System.StringSplitOptions.None).Length - 1, Is.EqualTo(16));
            Assert.That(_text.text, Does.Not.Contain("Secret deck").And.Not.Contain("Local player"));
            BuildingRegistry.Unregister(building.Hex);
            Call("Update");
            Assert.That(_button.gameObject.activeSelf, Is.False);
            Assert.That(_content.activeSelf, Is.False);
            Assert.That(_text.text, Is.Empty);
        }

        [Test]
        public void FullReportCountsAircraftSeparatelyAndNeverPrintsArmyIdentityOrPosition()
        {
            PlaceCenter();
            var scout = new ArmyData { Owner = _human, Hex = new HexCoord(0, 0) };
            scout.Members.Add(new UnitData { HitPointsCurrent = 3, HitPointsMax = 3 });
            ArmyRegistry.Register(scout);
            var enemyArmy = new ArmyData { Owner = _enemy, Name = "Secret army", Hex = new HexCoord(7, -4) };
            enemyArmy.Members.Add(new UnitData { Attack = 3, HitPointsCurrent = 3, HitPointsMax = 3 });
            ArmyRegistry.Register(enemyArmy);
            var airfield = new ArmyData { Owner = _enemy, IsAirfield = true, Hex = new HexCoord(6, -4) };
            airfield.Members.Add(new UnitData { IsAviation = true, HitPointsCurrent = 3, HitPointsMax = 3 });
            ArmyRegistry.Register(airfield);
            _panel.Toggle();
            Assert.That(_text.text, Does.Contain("100% (16/16)").And.Contain("Ground troops: 1")
                .And.Contain("Aircraft: 1").And.Not.Contain("???")
                .And.Not.Contain("Secret army").And.Not.Contain("Secret deck").And.Not.Contain("7, -4"));
            Assert.That(MissionIntentRegistry.Peek(_enemy), Is.Null);
            Assert.That(AiHandRegistry.Peek(_enemy), Is.Null);
        }

        [Test]
        public void FourPlayerReportSortsByCoverageAndKeepsEqualCoverageStable()
        {
            PlaceCenter();
            _enemy.Nickname = "Low intel";
            var high = new PlayerSetupData { Nickname = "High intel", CitadelHexQ = 20, CitadelHexR = 0 };
            var middle = new PlayerSetupData { Nickname = "Middle intel", CitadelHexQ = 10, CitadelHexR = 0 };
            GameSession.Players.Add(high);
            GameSession.Players.Add(middle);
            foreach (int q in new[] { 3, 19, 12 })
            {
                var scout = new ArmyData { Owner = _human, Hex = new HexCoord(q, 0) };
                scout.Members.Add(new UnitData { HitPointsCurrent = 3, HitPointsMax = 3 });
                ArmyRegistry.Register(scout);
            }
            _panel.Toggle();
            Assert.That(_text.text.IndexOf("High intel"), Is.LessThan(_text.text.IndexOf("Middle intel")));
            Assert.That(_text.text.IndexOf("Middle intel"), Is.LessThan(_text.text.IndexOf("Low intel")));
            // All three now have 25%; the original match order wins the tie.
            high.CitadelHexQ = 22;
            middle.CitadelHexQ = 15;
            Call("Refresh");
            Assert.That(_text.text.IndexOf("Low intel"), Is.LessThan(_text.text.IndexOf("High intel")));
            Assert.That(_text.text.IndexOf("High intel"), Is.LessThan(_text.text.IndexOf("Middle intel")));
        }

        [Test]
        public void PersonalTurnChangesReportOnlyWhenObserverStartsNextTurn()
        {
            PlaceCenter();
            var scout = new ArmyData { Owner = _human, Hex = new HexCoord(2, 0) };
            scout.Members.Add(new UnitData { HitPointsCurrent = 3, HitPointsMax = 3 });
            ArmyRegistry.Register(scout);
            Set(_turn, "<CurrentPlayer>k__BackingField", _human);
            _panel.Toggle();
            string before = _text.text;
            _panel.Toggle(); _panel.Toggle();
            Assert.That(_text.text, Is.EqualTo(before));
            var completed = (Dictionary<PlayerSetupData, long>)typeof(GameTurnController)
                .GetField("_completedTurns", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_turn);
            completed[_human] = 1;
            Set(_turn, "<CurrentPlayer>k__BackingField", _enemy);
            Call("Refresh");
            Assert.That(_text.text, Is.EqualTo(before), "Ending our turn must not grant another report");
            Set(_turn, "<CurrentPlayer>k__BackingField", _human);
            Call("Refresh");
            Assert.That(_text.text, Is.Not.EqualTo(before));
        }

        [Test]
        public void AiObserverMatchAlwaysShowsButtonAndFullReportForEveryPlayer()
        {
            _human.IsHuman = false;
            _human.Nickname = "First AI";
            var far = new PlayerSetupData { Nickname = "Second AI", CitadelHexQ = 20, CitadelHexR = 0 };
            GameSession.Players.Add(far);
            Set(_turn, "_aiObserverMatch", true);
            Call("Update");
            Assert.That(_button.gameObject.activeSelf, Is.True, "No Tactical Intelligence source is placed");
            _panel.Toggle();
            Assert.That(_content.activeSelf, Is.True);
            Assert.That(_text.text, Does.Contain("First AI").And.Contain("Opponent").And.Contain("Second AI")
                .And.Not.Contain("???").And.Not.Contain("Intel coverage"));
            Assert.That(_text.text.IndexOf("First AI"), Is.LessThan(_text.text.IndexOf("Opponent")));
            Assert.That(_text.text.IndexOf("Opponent"), Is.LessThan(_text.text.IndexOf("Second AI")));
        }
    }
}
#endif
