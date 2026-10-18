#if UNITY_INCLUDE_TESTS
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class HexInfoDrawerTests
{
    private static readonly string[] Fields = { "garrisonButton", "baseButton", "researchButton", "productionButton" };

    private GameObject canvas;
    private GameObject root;
    private HexInfoPanelUI panel;
    private Button[] buttons;

    [SetUp]
    public void SetUp()
    {
        canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
        root = new GameObject("Panel", typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);
        panel = root.AddComponent<HexInfoPanelUI>();
        Set("panelRoot", root);
        buttons = new Button[4];
        for (int i = 0; i < 4; i++)
        {
            var go = new GameObject(Fields[i], typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(root.transform, false);
            ((RectTransform)go.transform).sizeDelta = new Vector2(60f, 60f);
            buttons[i] = go.GetComponent<Button>();
            Set(Fields[i], buttons[i]);
        }
    }

    [TearDown] public void TearDown() => Object.DestroyImmediate(canvas);

    [Test]
    public void NothingAvailableHidesPanel()
    {
        panel.ShowHex();
        Assert.IsFalse(root.activeSelf);
    }

    [Test]
    public void AvailableButtonsGetOrderedLayoutSlotsAndUnavailableStayHidden()
    {
        panel.SetBaseButtonVisible(true, () => { });
        panel.SetResearchButtonVisible(true, () => { });
        panel.ShowHex();
        Assert.IsTrue(root.activeSelf);
        for (int i = 0; i < 4; i++)
        {
            Transform slot = buttons[i].transform.parent;
            Assert.AreNotEqual(root.transform, slot);
            Assert.AreEqual(root.transform, slot.parent);
            Assert.AreEqual(new Vector2(60f, 60f), ((RectTransform)slot).sizeDelta);
            Assert.AreEqual(Vector2.zero, ((RectTransform)buttons[i].transform).anchoredPosition);
            // Slots are applied after the exit phase, never before: nothing is visible at once.
            Assert.IsFalse(slot.gameObject.activeSelf);
        }
        Assert.Less(buttons[0].transform.parent.GetSiblingIndex(), buttons[3].transform.parent.GetSiblingIndex());
    }

    [Test]
    public void HideIsImmediateAndForgetsAvailableButtons()
    {
        panel.SetGarrisonButtonVisible(true, () => { });
        panel.ShowHex();
        panel.Hide();
        Assert.IsFalse(root.activeSelf);
        panel.ShowHex();
        Assert.IsFalse(root.activeSelf);
    }

    private void Set(string name, object value) => typeof(HexInfoPanelUI)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(panel, value);
}
#endif
