#if UNITY_INCLUDE_TESTS
using System;
using System.Reflection;
using Game.Players;
using Game.Turns;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Game.Campaign;
using Game.Core;

namespace Game.EditorTests
{
    public class GameMenuAvailabilityTests
    {
        private GameObject turnObject, menuObject;
        private GameTurnController turns;
        private GameMenuPanelUI menu;
        private Button gear;

        private static void Field(object obj, string name, object value) =>
            obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);

        [SetUp]
        public void SetUp()
        {
            turnObject = new GameObject("menu test turns");
            turnObject.SetActive(false);
            turns = turnObject.AddComponent<GameTurnController>();
            turns.enabled = false;
            turnObject.SetActive(true);
            menuObject = new GameObject("menu test");
            menu = menuObject.AddComponent<GameMenuPanelUI>();
            var gearObject = new GameObject("gear", typeof(RectTransform));
            gearObject.transform.SetParent(menuObject.transform);
            gear = gearObject.AddComponent<Button>();
            Field(menu, "gearButton", gear);
            var blocker = new GameObject("blocker");
            blocker.transform.SetParent(menuObject.transform);
            blocker.SetActive(false);
            Field(menu, "blockingRoot", blocker);
            Field(menu, "menuPanel", blocker);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(menuObject);
            UnityEngine.Object.DestroyImmediate(turnObject);
        }

        private void StartTurn(PlayerSetupData player)
        {
            Field(turns, "<CurrentPlayer>k__BackingField", player);
            var changed = (Action)typeof(GameTurnController)
                .GetField("TurnStateChanged", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(turns);
            changed?.Invoke();
        }

        [Test]
        public void AiTurnKeepsGearVisibleButBlocksOpening()
        {
            StartTurn(new PlayerSetupData { IsHuman = false });
            Assert.That(gear.gameObject.activeSelf, Is.True);
            Assert.That(gear.interactable, Is.False);
            menu.OpenMenu();
            Assert.That(menu.IsShowing, Is.False);
        }

        [Test]
        public void HumanTurnRestoresAvailabilityAfterAi()
        {
            StartTurn(new PlayerSetupData { IsHuman = false });
            StartTurn(new PlayerSetupData { IsHuman = true });
            Assert.That(gear.interactable, Is.True);
            menu.OpenMenu();
            Assert.That(menu.IsShowing, Is.True);
            StartTurn(new PlayerSetupData { IsHuman = false });
            Assert.That(menu.IsShowing, Is.False);
            Assert.That(gear.gameObject.activeSelf, Is.True);
            Assert.That(gear.interactable, Is.False);
        }

        [Test]
        public void NoCurrentPlayerCannotOpenMenu()
        {
            StartTurn(null);
            Assert.That(gear.interactable, Is.False);
            menu.OpenMenu();
            Assert.That(menu.IsShowing, Is.False);
        }

        [TestCase(false, "Return to Main Menu")]
        [TestCase(true, "Return to Campaign")]
        public void ExitLabelFollowsTheCurrentMatchEntry(bool campaign, string expected)
        {
            var priorContext = GameSession.CampaignContext;
            var priorMatch = GameSession.MatchId;
            var priorController = CampaignMatchBridge.Controller;
            try
            {
                const string matchId = "menu-return-test";
                typeof(GameSession).GetProperty("MatchId").SetValue(null, matchId);
                typeof(GameSession).GetProperty("CampaignContext").SetValue(null,
                    campaign ? new CampaignMatchContext { MatchId = matchId } : null);
                typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, null);
                var labelObject = new GameObject("exit label", typeof(RectTransform));
                labelObject.transform.SetParent(menuObject.transform);
                var label = labelObject.AddComponent<TextMeshProUGUI>();
                Field(menu, "exitButtonText", label);
                StartTurn(new PlayerSetupData { IsHuman = true });
                menu.OpenMenu();
                Assert.That(menu.IsShowing, Is.True);
                Assert.That(label.text, Is.EqualTo(expected));
            }
            finally
            {
                typeof(GameSession).GetProperty("MatchId").SetValue(null, priorMatch);
                typeof(GameSession).GetProperty("CampaignContext").SetValue(null, priorContext);
                typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, priorController);
            }
        }
    }
}
#endif
