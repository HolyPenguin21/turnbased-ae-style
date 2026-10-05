#if UNITY_INCLUDE_TESTS
using System;
using System.Reflection;
using Game.Players;
using Game.Turns;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

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
    }
}
#endif
