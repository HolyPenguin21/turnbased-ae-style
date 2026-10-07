#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Map;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class ArmyButtonRowPresentationPlayModeTests
    {
        private GameObject _canvas;
        private GameConfig _config;
        private ArmyButtonRowUI _row;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return new EnterPlayMode();
            _canvas = new GameObject("canvas", typeof(RectTransform), typeof(Canvas));
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var panel = new GameObject("row", typeof(RectTransform), typeof(VerticalLayoutGroup));
            panel.transform.SetParent(_canvas.transform, false);
            _row = panel.AddComponent<ArmyButtonRowUI>();
            var prefab = new GameObject("button", typeof(RectTransform), typeof(Image), typeof(Button));
            prefab.transform.SetParent(_canvas.transform, false);
            prefab.GetComponent<RectTransform>().sizeDelta = new Vector2(150, 40);
            var ui = prefab.AddComponent<ArmyButtonUI>();
            Field(ui, "button", prefab.GetComponent<Button>());
            foreach (string name in new[] { "Image_LampOn", "Image_LampOff" })
            {
                var lamp = new GameObject(name, typeof(RectTransform), typeof(Image));
                lamp.transform.SetParent(prefab.transform, false);
            }
            prefab.SetActive(false);
            _config = ScriptableObject.CreateInstance<GameConfig>();
            _config.armyButtonPrefab = ui;
            Field(_row, "panelRoot", panel);
            Field(_row, "buttonContainer", panel.transform);
            Field(_row, "gameConfig", _config);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Object.Destroy(_canvas);
            Object.Destroy(_config);
            yield return null;
            yield return new ExitPlayMode();
        }

        private static void Field(object obj, string name, object value) =>
            obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);


        [UnityTest]
        public IEnumerator MarkerTargetPrefersSelectionThenGarrisonThenFirstMobileArmy()
        {
            var resolve = typeof(HexSelectionController).GetMethod("ResolveArmyMarkerTarget",
                BindingFlags.Static | BindingFlags.NonPublic);
            var first = new ArmyData();
            var second = new ArmyData();
            var garrison = new ArmyData { IsGarrison = true };
            var armies = new List<ArmyData> { first, second, garrison };
            Assert.That(resolve.Invoke(null, new object[] { armies, second }), Is.SameAs(second));
            Assert.That(resolve.Invoke(null, new object[] { armies, null }), Is.SameAs(garrison));
            armies.Remove(garrison);
            Assert.That(resolve.Invoke(null, new object[] { armies, second }), Is.SameAs(second));
            Assert.That(resolve.Invoke(null, new object[] { armies, null }), Is.SameAs(first));
            Assert.That(resolve.Invoke(null, new object[] { armies, new ArmyData() }), Is.SameAs(first));
            yield return null;
        }

        [UnityTest]
        public IEnumerator ClosingStorageTabsDoesNotReturnAnArmyForMapSelection()
        {
            var root = new GameObject("modal");
            root.transform.SetParent(_canvas.transform, false);
            var modal = root.AddComponent<ArmyViewerModalUI>();
            foreach (ArmyData army in new[]
            {
                new ArmyData { IsGarrison = true }, new ArmyData { IsPrison = true },
                new ArmyData { IsAirfield = true }, new ArmyData()
            })
            {
                army.Members.Add(new Game.Units.UnitData());
                Field(modal, "_currentArmy", army);
                modal.Hide();
                if (army.IsGarrison || army.IsPrison || army.IsAirfield)
                    Assert.That(modal.LastClosedSelectableArmy, Is.Null);
                else
                    Assert.That(modal.LastClosedSelectableArmy, Is.SameAs(army));
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator MapBadgeDisplaysRosterCountSeparatelyFromMovement()
        {
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<ArmyButtonUI>(
                "Assets/Prefabs/UI/ArmyButton_Map.prefab");
            Assert.That(prefab, Is.Not.Null);
            var button = Object.Instantiate(prefab, _canvas.transform);
            var army = new ArmyData { Name = "Roster" };
            army.Members.Add(new Game.Units.UnitData());
            army.Members.Add(new Game.Units.UnitData());
            button.Setup(army, null, false, true);
            Assert.That(button.transform.Find("Text_Cap").GetComponent<TMPro.TMP_Text>().text,
                Is.EqualTo($"2/{army.EffectiveCapacity}"));
            Assert.That(button.transform.Find("Text_Move").GetComponent<TMPro.TMP_Text>().text,
                Is.EqualTo($"{army.CurrentMovement}/{army.MaxMovement}"));
            Assert.That(button.transform.Find("Icon_Move").GetComponent<TMPro.TMP_Text>().text,
                Is.EqualTo(">>"));
            army.Members.RemoveAt(0);
            button.Refresh();
            Assert.That(button.transform.Find("Text_Cap").GetComponent<TMPro.TMP_Text>().text,
                Is.EqualTo($"1/{army.EffectiveCapacity}"));
            button.Setup(army, null, false, false);
            Assert.That(button.transform.Find("Text_Cap").gameObject.activeSelf, Is.False);
            Assert.That(button.transform.Find("Text_Move").gameObject.activeSelf, Is.False);
            Assert.That(button.transform.Find("Icon_Move").gameObject.activeSelf, Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator SelectionUpdatesLampsWithoutReplacingButtons()
        {
            var a = new ArmyData { Name = "A" };
            var b = new ArmyData { Name = "B" };
            var armies = new List<ArmyData> { a, b };
            _row.Show(armies, null, a, true);
            yield return new WaitForSecondsRealtime(0.5f);
            ArmyButtonUI first = _row.Buttons[0];
            Assert.That(first.transform.Find("Image_LampOn").gameObject.activeSelf, Is.True);
            Assert.That(first.transform.Find("Image_LampOff").gameObject.activeSelf, Is.False);
            _row.Show(armies, null, b, true);
            Assert.That(_row.Buttons[0], Is.SameAs(first));
            Assert.That(first.transform.Find("Image_LampOn").gameObject.activeSelf, Is.False);
            Assert.That(_row.Buttons[1].transform.Find("Image_LampOn").gameObject.activeSelf, Is.True);
        }

        [UnityTest]
        public IEnumerator ModalUsesItsOwnPrefabAndUpdatesLampsOnClick()
        {
            var modalPrefab = Object.Instantiate(_config.armyButtonPrefab, _canvas.transform);
            modalPrefab.name = "modal-button";
            modalPrefab.RectTransform.sizeDelta = new Vector2(123, 28.35f);
            var prefabField = typeof(GameConfig).GetField("armyModalButtonPrefab");
            Assert.That(prefabField, Is.Not.Null, "Modal needs a separate configured prefab");
            prefabField.SetValue(_config, modalPrefab);
            var a = new ArmyData { Name = "A" };
            var b = new ArmyData { Name = "B" };
            var armies = new[] { a, b };
            System.Action<ArmyData> select = null;
            select = army => _row.Show(armies, select, army);
            _row.Show(armies, select, a);
            ArmyButtonUI first = _row.Buttons[0];
            ArmyButtonUI second = _row.Buttons[1];
            Assert.That(first.name, Does.StartWith("modal-button"));
            Assert.That(first.transform.Find("Image_LampOn").gameObject.activeSelf, Is.True);
            Assert.That(first.transform.Find("Image_LampOff").gameObject.activeSelf, Is.False);
            Assert.That(second.transform.Find("Image_LampOff").gameObject.activeSelf, Is.True);
            Assert.That(first.GetComponent<Button>().interactable, Is.True);
            second.GetComponent<Button>().onClick.Invoke();
            Assert.That(_row.Buttons[0], Is.SameAs(first));
            Assert.That(first.transform.Find("Image_LampOff").gameObject.activeSelf, Is.True);
            Assert.That(second.transform.Find("Image_LampOn").gameObject.activeSelf, Is.True);
            Assert.That(second.transform.Find("Image_LampOff").gameObject.activeSelf, Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator OneMobileArmyShowsPanelButStorageContainersDoNot()
        {
            var selectionObject = new GameObject("selection");
            selectionObject.transform.SetParent(_canvas.transform, false);
            var selection = selectionObject.AddComponent<HexSelectionController>();
            selection.enabled = false;
            Field(selection, "armyButtonRow", _row);
            var refresh = typeof(HexSelectionController).GetMethod("RefreshArmyButtonRow",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var storage = new List<ArmyData>
            {
                new ArmyData { IsGarrison = true }, new ArmyData { IsAirfield = true },
                new ArmyData { IsPrison = true }
            };
            refresh.Invoke(selection, new object[] { storage });
            Assert.That(_row.gameObject.activeSelf, Is.False);
            storage.Add(new ArmyData { Name = "mobile", IsAirArmy = true });
            refresh.Invoke(selection, new object[] { storage });
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(_row.gameObject.activeSelf, Is.True);
            Assert.That(_row.Buttons.Count, Is.EqualTo(1));
            Assert.That(_row.Buttons[0].Army.Name, Is.EqualTo("mobile"));
            refresh.Invoke(selection, new object[] { new List<ArmyData>() });
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(_row.gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator FirstShowAndShowAfterHideBothSlideFromLeft()
        {
            var army = new ArmyData { Name = "mobile" };
            _row.Hide();
            for (int cycle = 0; cycle < 2; cycle++)
            {
                _row.Show(new[] { army }, null, army, true);
                for (int frame = 0; frame < 60 && _row.Buttons.Count == 0; frame++) yield return null;
                Assert.That(_row.Buttons.Count, Is.EqualTo(1));
                Assert.That(_row.Buttons[0].RectTransform.anchoredPosition.x, Is.LessThan(0f));
                yield return new WaitForSecondsRealtime(0.5f);
                Assert.That(_row.Buttons[0].RectTransform.anchoredPosition, Is.EqualTo(Vector2.zero));
                _row.Hide();
                yield return new WaitForSecondsRealtime(0.4f);
                Assert.That(_row.gameObject.activeSelf, Is.False);
            }
        }

        [UnityTest]
        public IEnumerator HideKeepsOldRowsUntilSlideFinishesAndLatestShowWins()
        {
            _row.Show(new[] { new ArmyData { Name = "A" } }, null, null, true);
            yield return new WaitForSecondsRealtime(0.5f);
            _row.Hide();
            Assert.That(_row.Buttons.Count, Is.EqualTo(1), "retain rows to animate their exit");
            _row.Show(new[] { new ArmyData { Name = "B" } }, null, null, true);
            _row.Show(new[] { new ArmyData { Name = "C" } }, null, null, true);
            yield return new WaitForSecondsRealtime(0.8f);
            Assert.That(_row.Buttons.Count, Is.EqualTo(1));
            Assert.That(_row.Buttons[0].Army.Name, Is.EqualTo("C"));
            _row.Hide();
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(_row.Buttons.Count, Is.Zero);
            Assert.That(_row.gameObject.activeSelf, Is.False);
        }
    }
}
#endif

