#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Reflection;
using Game.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class ResponsiveUiInteractionTests
{
    private readonly List<GameObject> roots = new List<GameObject>();

    private RectTransform Rect(string name, Transform parent = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        if (parent == null) roots.Add(go);
        else go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static void Set(object target, string name, object value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    private static object Call(object target, string name, params object[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);

    [TearDown]
    public void TearDown()
    {
        foreach (GameObject root in roots) Object.DestroyImmediate(root);
        roots.Clear();
    }

    [TestCase(1f, 1f)]
    [TestCase(.5f, .5f)]
    [TestCase(2f, 3f)]
    public void DragUsesParentUnitsUnderNestedScale(float scaleX, float scaleY)
    {
        RectTransform canvasRect = Rect("Canvas");
        Canvas canvas = canvasRect.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        RectTransform parent = Rect("Scaled parent", canvasRect);
        parent.localScale = new Vector3(scaleX, scaleY, 1f);
        RectTransform card = Rect("Card", parent);
        card.anchoredPosition = new Vector2(7f, 9f);
        var data = new PointerEventData(null)
        {
            position = new Vector2(400f, 300f),
            delta = new Vector2(20f * scaleX, 10f * scaleY)
        };
        UIDragUtility.ApplyScreenDelta(card, data, canvas);
        Assert.That(card.anchoredPosition.x, Is.EqualTo(27f).Within(.001f));
        Assert.That(card.anchoredPosition.y, Is.EqualTo(19f).Within(.001f));
    }

    [TestCase(96f, 300f)]
    [TestCase(80f, 600f)]
    [TestCase(128f, 600f)]
    public void FourTacticsFitBothContainerAxes(float width, float height)
    {
        RectTransform area = Rect("Battle hand");
        area.sizeDelta = new Vector2(width, height);
        BattleHandUI hand = area.gameObject.AddComponent<BattleHandUI>();
        RectTransform prefab = Rect("Tactic prefab");
        prefab.sizeDelta = new Vector2(96f, 140f);
        BattleTacticCardUI template = prefab.gameObject.AddComponent<BattleTacticCardUI>();
        Set(hand, "cardContainer", area);
        Set(hand, "cardPrefab", template);
        var visible = (List<BattleTacticCardUI>)typeof(BattleHandUI)
            .GetField("_visible", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(hand);
        for (int i = 0; i < 4; i++)
        {
            RectTransform card = Rect("Tactic", area);
            card.sizeDelta = prefab.sizeDelta;
            card.pivot = new Vector2(.5f, 1f);
            BattleTacticCardUI view = card.gameObject.AddComponent<BattleTacticCardUI>();
            Set(view, "rectTransform", card);
            visible.Add(view);
        }
        Call(hand, "FitVisibleCards");
        foreach (BattleTacticCardUI view in visible)
        {
            var card = (RectTransform)view.transform;
            Assert.That(card.localScale.x, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f));
            Assert.That(card.rect.width * card.localScale.x, Is.LessThanOrEqualTo(width + .01f));
            Assert.That(card.anchoredPosition.y - card.rect.height * card.localScale.y,
                Is.GreaterThanOrEqualTo(-height - .01f));
        }
    }

    [Test]
    public void PlayerStatisticsGrowAndClampScrollWhenTextShrinks()
    {
        RectTransform root = Rect("Statistics panel");
        PlayerDataPanelUI panel = root.gameObject.AddComponent<PlayerDataPanelUI>();
        RectTransform viewport = Rect("Viewport", root);
        viewport.sizeDelta = new Vector2(620f, 290f);
        RectTransform content = Rect("Statistics", viewport);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(.5f, 1f);
        content.sizeDelta = new Vector2(0f, 290f);
        TMP_Text text = content.gameObject.AddComponent<TextMeshProUGUI>();
        text.font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        Assert.That(text.font, Is.Not.Null);
        text.fontSize = 16f;
        text.enableAutoSizing = false;
        ScrollRect scroll = root.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        Set(panel, "dataTmpText", text);
        Set(panel, "contentPanel", root.gameObject);
        var lines = new string[100];
        for (int i = 0; i < lines.Length; i++) lines[i] = "Player statistics and army readiness";
        text.text = string.Join("\n", lines);
        Call(panel, "FitTextContent");
        Assert.That(content.rect.height, Is.GreaterThan(viewport.rect.height));
        content.anchoredPosition = new Vector2(0f, 100f);
        Call(panel, "FitTextContent");
        Assert.That(content.anchoredPosition.y, Is.EqualTo(100f));
        text.text = "One player";
        Call(panel, "FitTextContent");
        Assert.That(content.rect.height, Is.EqualTo(viewport.rect.height).Within(.01f));
        Assert.That(content.anchoredPosition.y, Is.EqualTo(0f));
    }

    [Test]
    public void LargeArmyScrollExtentKeepsClippedRowsOutOfDropTargets()
    {
        RectTransform canvasRect = Rect("Canvas");
        Canvas canvas = canvasRect.gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        ArmyViewerModalUI modal = canvasRect.gameObject.AddComponent<ArmyViewerModalUI>();
        RectTransform viewport = Rect("Viewport", canvasRect);
        viewport.sizeDelta = new Vector2(380f, 288f);
        RectTransform content = Rect("Grid", viewport);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(.5f, 1f);
        content.sizeDelta = new Vector2(0f, 288f);
        GridLayoutGroup grid = content.gameObject.AddComponent<GridLayoutGroup>();
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 4;
        grid.cellSize = new Vector2(92f, 134f);
        grid.spacing = new Vector2(3f, 17f);
        ScrollRect scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        Set(modal, "gridContainer", content);
        Set(modal, "grid", grid);
        Call(modal, "FitGridContent", 12);
        Assert.That(content.rect.height, Is.EqualTo(436f).Within(.001f));
        Assert.That(Call(modal, "ResolveGridSlotIndex", new Vector2(0f, -200f)), Is.Null);
        Assert.That(Call(modal, "ResolveGridSlotIndex", Vector2.zero), Is.Not.Null);
        content.anchoredPosition = new Vector2(0f, 100f);
        Assert.That(Call(modal, "ResolveGridSlotIndex", Vector2.zero), Is.Not.Null);
    }
}
#endif
