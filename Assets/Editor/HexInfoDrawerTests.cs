#if UNITY_INCLUDE_TESTS
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class HexInfoDrawerTests
{
    private GameObject root;
    private HexInfoPanelUI panel;
    private Button[] buttons;

    [SetUp]
    public void SetUp()
    {
        root = new GameObject("Drawer", typeof(RectTransform));
        panel = root.AddComponent<HexInfoPanelUI>();
        Set("panelRoot", root);
        Set("drawerLayout", true);
        Set("drawerRect", root.GetComponent<RectTransform>());
        var nav = new GameObject("Navigation", typeof(RectTransform));
        nav.transform.SetParent(root.transform);
        Set("navigationRect", nav.GetComponent<RectTransform>());
        buttons = new Button[4];
        string[] fields = { "baseButton", "garrisonButton", "researchButton", "productionButton" };
        for (int i = 0; i < 4; i++)
        {
            var go = new GameObject(fields[i], typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(nav.transform);
            buttons[i] = go.GetComponent<Button>();
            Set(fields[i], buttons[i]);
        }
    }

    [TearDown] public void TearDown() => Object.DestroyImmediate(root);

    [Test]
    public void HiddenSectionsCompactAndCallbacksRemainIndependent()
    {
        int clicks = 0;
        panel.SetBaseButtonVisible(false, null);
        panel.SetGarrisonButtonVisible(true, () => clicks++);
        panel.SetResearchButtonVisible(false, null);
        panel.SetProductionButtonVisible(true, () => clicks += 10);
        panel.RefreshDrawer(false);
        Assert.AreEqual(((RectTransform)buttons[1].transform).anchoredPosition.y,
            ((RectTransform)buttons[3].transform).anchoredPosition.y);
        Assert.Less(((RectTransform)buttons[1].transform).anchoredPosition.x,
            ((RectTransform)buttons[3].transform).anchoredPosition.x);
        buttons[1].onClick.Invoke();
        Assert.AreEqual(1, clicks);
        panel.SetGarrisonButtonVisible(true, () => clicks += 2);
        buttons[1].onClick.Invoke();
        Assert.AreEqual(3, clicks);
    }

    [Test]
    public void EmptyHexHidesDrawerAndNextSelectionCanReopenIt()
    {
        panel.Hide();
        panel.ShowHex();
        panel.RefreshDrawer(false);
        Assert.IsFalse(root.activeSelf);
        panel.ShowHex();
        panel.SetResearchButtonVisible(true, null);
        panel.RefreshDrawer(false);
        Assert.IsTrue(root.activeSelf);
    }

    [Test]
    public void SecondSectionRowIncreasesHeightAndCollapsesWhenRemoved()
    {
        panel.RefreshDrawer(false);
        float four = ((RectTransform)root.transform).rect.height;
        panel.SetResearchButtonVisible(false, null);
        panel.SetProductionButtonVisible(false, null);
        panel.RefreshDrawer(false);
        Assert.Less(((RectTransform)root.transform).rect.height, four);
    }

    private void Set(string name, object value) => typeof(HexInfoPanelUI)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(panel, value);

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void ExtractorsFitTwoColumnsAndOneUsesFullWidth(int count)
    {
        var go = new GameObject("Extractors", typeof(RectTransform));
        go.transform.SetParent(root.transform);
        var row = go.AddComponent<ResourceActionRowUI>();
        typeof(ResourceActionRowUI).GetField("buttonContainer", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(row, go.transform);
        Set("resourceActions", row);
        var list = (System.Collections.IList)typeof(ResourceActionRowUI)
            .GetField("_buttons", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(row);
        for (int i = 0; i < count; i++)
        {
            var item = new GameObject("Extractor", typeof(RectTransform));
            item.transform.SetParent(go.transform);
            list.Add(item.AddComponent<ResourceActionButtonUI>());
        }
        panel.RefreshDrawer(false);
        Assert.AreEqual(count, row.VisibleButtonCount);
        var container = (RectTransform)go.transform;
        for (int i = 0; i < count; i++)
        {
            var item = (RectTransform)go.transform.GetChild(i);
            Assert.LessOrEqual(item.anchoredPosition.x + item.rect.width, container.rect.width + 0.01f);
            Assert.LessOrEqual(-item.anchoredPosition.y + item.rect.height, container.rect.height + 0.01f);
        }
        if (count == 1)
            Assert.AreEqual(container.rect.width, ((RectTransform)go.transform.GetChild(0)).rect.width);
    }
}
#endif
