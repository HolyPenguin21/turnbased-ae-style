#if UNITY_INCLUDE_TESTS
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public class HexInfoDrawerTests
{
    private GameObject canvas;
    private GameObject root;
    private HexInfoPanelUI panel;
    private RectTransform rect;
    private Button[] buttons;

    [SetUp]
    public void SetUp()
    {
        canvas = new GameObject("Canvas", typeof(RectTransform), typeof(Canvas));
        ((RectTransform)canvas.transform).sizeDelta = new Vector2(1024f, 768f);
        root = new GameObject("Drawer", typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);
        rect = (RectTransform)root.transform;
        rect.anchorMin = new Vector2(.85f, .23739585f);
        rect.anchorMax = new Vector2(1f, .5453959f);
        rect.pivot = new Vector2(.5f, 0f);
        rect.sizeDelta = Vector2.zero;
        rect.anchoredPosition = new Vector2(13f, -107f);
        panel = root.AddComponent<HexInfoPanelUI>();
        Set("panelRoot", root);
        Set("drawerLayout", true);
        Set("drawerRect", rect);
        buttons = new Button[4];
        string[] fields = { "baseButton", "garrisonButton", "researchButton", "productionButton" };
        for (int i = 0; i < 4; i++)
        {
            var go = new GameObject(fields[i], typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(root.transform, false);
            ((RectTransform)go.transform).anchoredPosition = new Vector2(i * 10f, 20f);
            buttons[i] = go.GetComponent<Button>();
            Set(fields[i], buttons[i]);
        }
    }

    [TearDown] public void TearDown() => Object.DestroyImmediate(canvas);

    [Test]
    public void DisabledSectionsStayInPlaceAndClearOldCallback()
    {
        int clicks = 0;
        panel.SetGarrisonButtonVisible(true, () => clicks++);
        buttons[1].onClick.Invoke();
        panel.SetGarrisonButtonVisible(false, null);
        Assert.IsTrue(buttons[1].gameObject.activeSelf);
        Assert.IsFalse(buttons[1].interactable);
        Assert.AreEqual(new Vector2(10f, 20f), ((RectTransform)buttons[1].transform).anchoredPosition);
        buttons[1].onClick.Invoke();
        Assert.AreEqual(1, clicks);
        panel.SetGarrisonButtonVisible(true, () => clicks += 2);
        buttons[1].onClick.Invoke();
        Assert.AreEqual(3, clicks);
    }

    [TestCase(0, -107f)]
    [TestCase(1, -81f)]
    [TestCase(2, -56f)]
    [TestCase(3, -31f)]
    [TestCase(4, 0f)]
    public void ResourceCountChangesOnlyVerticalPosition(int count, float expected)
    {
        Vector2 size = rect.sizeDelta;
        Vector2 min = rect.anchorMin;
        Vector2 max = rect.anchorMax;
        panel.ShowHex(count);
        panel.RefreshDrawer(false);
        Assert.AreEqual(expected, rect.anchoredPosition.y, .01f);
        Assert.AreEqual(13f, rect.anchoredPosition.x);
        Assert.AreEqual(size, rect.sizeDelta);
        Assert.AreEqual(min, rect.anchorMin);
        Assert.AreEqual(max, rect.anchorMax);
    }

    [Test]
    public void OffsetsFollowCanvasHeight()
    {
        panel.ShowHex(1);
        panel.RefreshDrawer(false);
        ((RectTransform)canvas.transform).sizeDelta = new Vector2(1024f, 1536f);
        panel.RefreshDrawer(false);
        Assert.AreEqual(-162f, rect.anchoredPosition.y, .01f);
    }

    [Test]
    public void RapidSelectionRetargetsWithoutResettingPosition()
    {
        panel.ShowHex(1);
        panel.RefreshDrawer(false);
        panel.ShowHex(4);
        Assert.AreEqual(-81f, rect.anchoredPosition.y, .01f);
        panel.ShowHex(2);
        panel.RefreshDrawer(false);
        Assert.AreEqual(-56f, rect.anchoredPosition.y, .01f);
    }

    [Test]
    public void HideAndReopenStartsBelowScreenWithoutChangingSize()
    {
        panel.Hide();
        Assert.IsFalse(root.activeSelf);
        panel.ShowHex(0);
        Assert.IsTrue(root.activeSelf);
        Vector3[] corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        Assert.Less(corners[1].y, ((RectTransform)canvas.transform).rect.yMin);
        panel.RefreshDrawer(false);
        Assert.AreEqual(-107f, rect.anchoredPosition.y, .01f);
        Assert.AreEqual(Vector2.zero, rect.sizeDelta);
    }

    private void Set(string name, object value) => typeof(HexInfoPanelUI)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(panel, value);

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void ExtractorsStackVerticallyWithoutMovingContainer(int count)
    {
        var go = new GameObject("Extractors", typeof(RectTransform));
        go.transform.SetParent(root.transform, false);
        var container = (RectTransform)go.transform;
        container.sizeDelta = new Vector2(120f, 100f);
        container.anchoredPosition = new Vector2(0f, 7.7f);
        var row = go.AddComponent<ResourceActionRowUI>();
        typeof(ResourceActionRowUI).GetField("buttonContainer", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(row, container);
        var list = (System.Collections.IList)typeof(ResourceActionRowUI)
            .GetField("_buttons", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(row);
        for (int i = 0; i < count; i++)
        {
            var item = new GameObject("Extractor", typeof(RectTransform));
            item.transform.SetParent(go.transform, false);
            list.Add(item.AddComponent<ResourceActionButtonUI>());
        }
        typeof(ResourceActionRowUI).GetMethod("LayoutButtons", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(row, null);
        Assert.AreEqual(new Vector2(120f, 100f), container.sizeDelta);
        Assert.AreEqual(new Vector2(0f, 7.7f), container.anchoredPosition);
        for (int i = 0; i < count; i++)
        {
            var item = (RectTransform)go.transform.GetChild(i);
            Assert.AreEqual(new Vector2(0f, -i * 25f), item.anchoredPosition);
            Assert.AreEqual(120f, item.rect.width, .01f);
            Assert.AreEqual(25f, item.rect.height, .01f);
        }
    }
}
#endif
