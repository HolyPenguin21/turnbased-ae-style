#if UNITY_INCLUDE_TESTS && UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using Game.Cards;
using Game.Setup;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public sealed class RuntimeResponsiveUiTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly List<GameObject> roots = new List<GameObject>();
    private static void Set(object o, string name, object value) => o.GetType().GetField(name, Fields).SetValue(o, value);
    private static T Get<T>(object o, string name) => (T)o.GetType().GetField(name, Fields).GetValue(o);
    private static object Call(object o, string name, params object[] args) => o.GetType()
        .GetMethod(name, Fields | BindingFlags.Public).Invoke(o, args);

    private RectTransform Rect(string name, Transform parent = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        if (parent == null) roots.Add(go); else go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    [TearDown] public void TearDown()
    { foreach (var go in roots) Object.DestroyImmediate(go); roots.Clear(); }

    [Test]
    public void HandResizeKeepsOrdinaryTacticAndDebugCardsInTheSameCoordinateSpace()
    {
        var root = Rect("Hand"); root.gameObject.SetActive(false);
        var hand = root.gameObject.AddComponent<CardHandUI>();
        var area = Rect("Cards", root); area.sizeDelta = new Vector2(842.4f, 176.35f);
        Set(hand, "handContainer", area); Set(hand, "overlapFraction", 0f);
        var data = new CardData(new CardDefinition { cardType = CardType.Unit });
        CardUI Make(string list)
        {
            var card = Rect(list, area).gameObject.AddComponent<CardUI>();
            card.Setup(hand, data, 1f, 1.3f, 20f, .1f, .5f);
            Get<List<CardUI>>(hand, list).Add(card);
            return card;
        }
        var ordinary = Make("_cards"); var tactic = Make("_tacticCards"); var debug = Make("_debugCards");
        Call(hand, "RefreshHandLayout");
        Assert.That(((RectTransform)ordinary.transform).anchoredPosition.x, Is.EqualTo(-327f).Within(.01f));
        area.sizeDelta = new Vector2(421.2f, 88.175f);
        Call(hand, "RefreshHandLayout");
        foreach (var card in new[] { ordinary, tactic, debug })
        {
            Assert.That(card.transform.parent, Is.SameAs(area));
            Assert.That(card.Data, Is.SameAs(data));
            Assert.That(card.transform.localScale.x, Is.EqualTo(.5f).Within(.001f));
        }
        Assert.That(((RectTransform)ordinary.transform).anchoredPosition.x, Is.EqualTo(-163.5f).Within(.01f));
        Set(hand, "_showingTactics", true); Call(hand, "Relayout", false);
        Assert.That(((RectTransform)tactic.transform).anchoredPosition.x, Is.EqualTo(-163.5f).Within(.01f));
        tactic.OnPointerEnter(null); Call(tactic, "TickAnimation", 1f);
        Assert.That(tactic.transform.localScale.x, Is.EqualTo(.65f).Within(.001f));
        Assert.That(((RectTransform)tactic.transform).anchoredPosition.y, Is.EqualTo(10f).Within(.01f));

        // A resize during a flip must settle on the already-selected face.
        Set(hand, "_tacticFlipping", true);
        tactic.transform.localScale = new Vector3(.1f, .5f, .5f);
        area.sizeDelta = new Vector2(842.4f, 176.35f);
        Call(hand, "RefreshHandLayout");
        Assert.That(Get<bool>(hand, "_tacticFlipping"), Is.False);
        Assert.That(ordinary.gameObject.activeSelf, Is.False);
        Assert.That(tactic.gameObject.activeSelf, Is.True);
        Assert.That(tactic.transform.localScale.x, Is.EqualTo(1f).Within(.001f));

        // Resizing changes pointer/slot coordinates: cancel the gesture without attempting a play.
        typeof(CardUI).GetProperty("IsDragging").GetSetMethod(true).Invoke(tactic, new object[] { true });
        var dragged = (RectTransform)tactic.transform;
        dragged.anchoredPosition = new Vector2(12f, 40f);
        area.sizeDelta = new Vector2(421.2f, 88.175f);
        Call(hand, "RefreshHandLayout");
        Assert.That(tactic.IsDragging, Is.False);
        Assert.That(dragged.anchoredPosition.x, Is.EqualTo(-163.5f).Within(.01f));
        Assert.That(dragged.anchoredPosition.y, Is.Zero);
        Assert.DoesNotThrow(() => tactic.OnEndDrag(new UnityEngine.EventSystems.PointerEventData(null)));
        Assert.That(tactic.Data, Is.SameAs(data));
    }

    [Test]
    public void HandSeparatorsKeepTheirOffsetFromCardsWhenOnlyTheAspectChanges()
    {
        var root = Rect("Hand"); root.gameObject.SetActive(false);
        var hand = root.gameObject.AddComponent<CardHandUI>();
        var area = Rect("Cards", root); area.sizeDelta = new Vector2(842.4f, 176.35f);
        area.pivot = new Vector2(.5f, 0f);
        Set(hand, "handContainer", area); Set(hand, "overlapFraction", 0f);
        var separator = Rect("Pin", area);
        separator.anchorMin = separator.anchorMax = new Vector2(.5f, 0f);
        separator.anchoredPosition = new Vector2(0f, 21.1f);
        Set(hand, "cardSeparators", new[] { separator });
        Call(hand, "CreateSeparatorLayer");
        area.sizeDelta = new Vector2(730f, 203f);
        Call(hand, "RefreshHandLayout");
        var reference = new Vector3(0f, 101.5f, 0f);
        float y = area.InverseTransformPoint(separator.position).y;
        Assert.That(y - reference.y, Is.EqualTo(-58.1253f).Within(.01f));
    }

    [Test]
    public void CollectionResizeAndPoolReuseKeepFourProportionalCellsInsideTheirViewport()
    {
        var window = Rect("Collection");
        var ui = window.gameObject.AddComponent<CollectionScreensUI>();
        var content = Rect("Grid", window); content.sizeDelta = new Vector2(604f, 500f);
        var grid = content.gameObject.AddComponent<GridLayoutGroup>();
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = 4;
        grid.padding = new RectOffset(8, 6, 0, 8); grid.spacing = new Vector2(8, 8);
        var template = AssetDatabase.LoadAssetAtPath<CollectionCardCellView>("Assets/Prefabs/UI/Collection/CollectionCard.prefab");
        Assert.That(template, Is.Not.Null);
        Set(ui, "gridContent", content); Set(ui, "cellPrefab", template);
        var cells = new List<CollectionCardCellView>();
        for (int i = 0; i < 4; i++) cells.Add(Object.Instantiate(template, content));
        foreach (float width in new[] { 604f, 432f, 802f })
        {
            content.sizeDelta = new Vector2(width, 500f);
            Call(ui, "FitCardGrid"); LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            foreach (var cell in cells)
            {
                cell.gameObject.SetActive(false); cell.gameObject.SetActive(true);
                var rect = (RectTransform)cell.transform;
                var visual = (RectTransform)cell.transform.Find("Visual");
                Assert.That(rect.anchoredPosition.x + rect.rect.width * (1f - rect.pivot.x),
                    Is.LessThanOrEqualTo(width - 6 + .01f));
                Assert.That(visual.rect.width * visual.localScale.x, Is.EqualTo(rect.rect.width).Within(.01f));
                Assert.That(visual.rect.height * visual.localScale.y, Is.EqualTo(rect.rect.height).Within(.01f));
                Assert.That(visual.localScale.x, Is.EqualTo(visual.localScale.y));
            }
            Assert.That(content.childCount, Is.EqualTo(4), "Resize must reuse the existing cells");
        }
    }

    [Test]
    public void SixInstantiatedPlayerRowsMeasureTheirDeckLineAndCanScrollToTheLastRow()
    {
        var viewport = Rect("Viewport"); viewport.sizeDelta = new Vector2(850, 341);
        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        var content = Rect("Players", viewport);
        content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1);
        content.pivot = new Vector2(.5f, 1); content.sizeDelta = Vector2.zero;
        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false; layout.spacing = 8;
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.content = content; scroll.viewport = viewport; scroll.horizontal = false;
        var template = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/Rows/PlayerRow.prefab");
        Assert.That(template, Is.Not.Null);
        RectTransform last = null;
        for (int i = 0; i < 6; i++) last = (RectTransform)Object.Instantiate(template, content).transform;
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        Assert.That(content.rect.height, Is.GreaterThanOrEqualTo(556f - .01f));
        foreach (RectTransform row in content)
        {
            Assert.That(row.rect.height, Is.GreaterThanOrEqualTo(86f));
            var deck = (RectTransform)row.Find("DeckSelection");
            Assert.That(-deck.anchoredPosition.y + deck.rect.height, Is.LessThanOrEqualTo(row.rect.height + .01f));
        }
        var setupRoot = Rect("Setup controller"); setupRoot.gameObject.SetActive(false);
        var setup = setupRoot.gameObject.AddComponent<GameSetupController>();
        Set(setup, "playerListContainer", content);
        Set(setup, "revealNewestPlayer", true);
        Call(setup, "LateUpdate");
        var corners = new Vector3[4]; last.GetWorldCorners(corners);
        foreach (var corner in corners)
            Assert.That(viewport.InverseTransformPoint(corner).y,
                Is.InRange(viewport.rect.yMin - .01f, viewport.rect.yMax + .01f));
    }
}
#endif
