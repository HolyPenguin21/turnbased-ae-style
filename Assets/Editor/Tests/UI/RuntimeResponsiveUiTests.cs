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
        var reference = new Vector3(0f, 76.40996f, 0f);
        float y = area.InverseTransformPoint(separator.position).y;
        Assert.That(y - reference.y, Is.EqualTo(-58.1253f).Within(.01f));
    }

    [Test]
    public void SetupAndArmyWindowsKeepTheirLayoutAcrossResizeAndReopen()
    {
        var parent = Rect("Canvas coordinates");
        foreach (var spec in new[] {
            (size: new Vector2(901.68f, 532.64f), min: new Vector2(.14778125f, .09977779f), max: new Vector2(.8522188f, .8395557f)),
            (size: new Vector2(828.32f, 478.32f), min: new Vector2(.17665625f, .2854445f), max: new Vector2(.82378125f, .94977784f)) })
        {
            var panel = Rect("Window", parent); panel.anchorMin = spec.min; panel.anchorMax = spec.max;
            panel.pivot = new Vector2(.5f, .5f); panel.sizeDelta = Vector2.zero;
            // Resize the SAME instance, including returning to the original size.
            foreach (var size in new[] { new Vector2(1280, 720), new Vector2(802, 1149),
                new Vector2(1558, 591), new Vector2(1108.5125f, 831.3844f), new Vector2(1280, 720) })
            {
                parent.sizeDelta = size;
                UIUtility.FitPanelToAnchors(panel, spec.size);
                Assert.That(panel.rect.width, Is.EqualTo(spec.size.x).Within(.01f));
                Assert.That(panel.rect.height, Is.EqualTo(spec.size.y).Within(.01f));
                Assert.That(panel.localScale.x, Is.EqualTo(panel.localScale.y));
                var min = parent.rect.min + Vector2.Scale(size, spec.min);
                var max = parent.rect.min + Vector2.Scale(size, spec.max);
                var centre = parent.InverseTransformPoint(panel.position);
                Assert.That(centre.x, Is.EqualTo((min.x + max.x) / 2f).Within(.01f));
                Assert.That(centre.y, Is.EqualTo((min.y + max.y) / 2f).Within(.01f));
                var corners = new Vector3[4]; panel.GetWorldCorners(corners);
                foreach (var corner in corners)
                {
                    var local = parent.InverseTransformPoint(corner);
                    Assert.That(local.x, Is.InRange(min.x - .01f, max.x + .01f));
                    Assert.That(local.y, Is.InRange(min.y - .01f, max.y + .01f));
                }
                panel.gameObject.SetActive(false); panel.gameObject.SetActive(true);
                Assert.That(UIUtility.FitPanelToAnchors(panel, spec.size), Is.False, "Reopen must not multiply scale");
            }
        }
    }

    [Test]
    public void TallHandKeepsCardsSlotsPinsAndDragBandOnTheBottomReference()
    {
        var root = Rect("Hand"); root.gameObject.SetActive(false);
        var hand = root.gameObject.AddComponent<CardHandUI>();
        var area = Rect("Cards", root); area.sizeDelta = new Vector2(842.4f, 176.35f);
        Set(hand, "handContainer", area); Set(hand, "overlapFraction", 0f);
        var template = AssetDatabase.LoadAssetAtPath<CardUI>("Assets/Prefabs/UI/Cards/Card_Hand.prefab");
        var card = Object.Instantiate(template, area);
        card.Setup(hand, new CardData(new CardDefinition { cardType = CardType.Unit }), 1f, 1.3f, 20f, .1f, .5f);
        Get<List<CardUI>>(hand, "_cards").Add(card);
        Call(hand, "CreateSlotBackgrounds");
        area.sizeDelta = new Vector2(730f, 203f);
        Call(hand, "RefreshHandLayout");
        var rect = (RectTransform)card.transform;
        Assert.That(rect.anchoredPosition.y, Is.EqualTo(-25.09004f).Within(.01f));
        Assert.That(203f / 2f + rect.anchoredPosition.y - rect.rect.height * card.transform.localScale.y / 2f,
            Is.EqualTo(9.25065f).Within(.01f));
        Assert.That(Get<RectTransform[]>(hand, "slotRects")[0].anchoredPosition.y, Is.EqualTo(rect.anchoredPosition.y));
        Assert.That(Call(hand, "IsWithinDragBand", -135f), Is.EqualTo(true), "Band must follow the moved card centre");
    }

    [TestCase(1280f, 224.52f)]
    [TestCase(1800f, 160f)]
    [TestCase(802f, 358f)]
    public void WideHandKeepsCardsSlotsPinsAndArrowsOnOneHorizontalScale(float firstWidth, float firstHeight)
    {
        var root = Rect("Hand"); root.sizeDelta = new Vector2(firstWidth, firstHeight);
        root.gameObject.SetActive(false);
        var hand = root.gameObject.AddComponent<CardHandUI>();
        var area = Rect("Cards", root);
        area.anchorMin = new Vector2(.171f, 0f); area.anchorMax = new Vector2(.829125f, .785454f);
        area.pivot = new Vector2(.5f, 0f); area.sizeDelta = Vector2.zero;
        Set(hand, "handContainer", area); Set(hand, "overlapFraction", 0f);
        var left = Rect("Left", root); var right = Rect("Right", root);
        left.anchorMin = left.anchorMax = new Vector2(.1775f, .31668058f);
        right.anchorMin = right.anchorMax = new Vector2(.8225f, .31668058f);
        left.sizeDelta = right.sizeDelta = new Vector2(32f, 97f);
        Set(hand, "scrollLeftButton", left.gameObject.AddComponent<Button>());
        Set(hand, "scrollRightButton", right.gameObject.AddComponent<Button>());
        var separator = Rect("Pin", area);
        separator.anchorMin = separator.anchorMax = new Vector2(.5f, 0f);
        separator.anchoredPosition = new Vector2(0f, 21.1f);
        Set(hand, "cardSeparators", new[] { separator });
        Call(hand, "CreateSlotBackgrounds"); Call(hand, "CreateSeparatorLayer");
        var cards = Get<List<CardUI>>(hand, "_cards");
        for (int i = 0; i < 2; i++)
        {
            var rect = Rect("Card", area); rect.sizeDelta = new Vector2(106f, 155f);
            var card = rect.gameObject.AddComponent<CardUI>();
            card.Setup(hand, new CardData(new CardDefinition { cardType = CardType.Unit }), 1f, 1.3f, 20f, .1f, .5f);
            cards.Add(card);
        }
        foreach (var size in new[] { new Vector2(firstWidth, firstHeight), new Vector2(1800f, 160f),
            new Vector2(802f, 358f), new Vector2(1280f, 224.52f) })
        {
            root.sizeDelta = size; Call(hand, "RefreshHandLayout");
            var first = (RectTransform)cards[0].transform; var second = (RectTransform)cards[1].transform;
            float scale = first.localScale.x;
            Assert.That((second.anchoredPosition.x - first.anchoredPosition.x - 106f * scale) / scale,
                Is.EqualTo(3f).Within(.01f), "Visible gap must scale with the card");
            var slots = Get<RectTransform[]>(hand, "slotRects");
            Assert.That(area.InverseTransformPoint(slots[0].position).x,
                Is.EqualTo(area.InverseTransformPoint(first.position).x).Within(.01f));
            Assert.That(area.InverseTransformPoint(separator.position).x,
                Is.EqualTo((first.anchoredPosition.x + second.anchoredPosition.x) / 2f).Within(.01f));
            Assert.That(area.InverseTransformPoint(left.position).x / scale, Is.EqualTo(-412.88f).Within(.02f));
            Assert.That(area.InverseTransformPoint(right.position).x / scale, Is.EqualTo(412.72f).Within(.02f));
            Assert.That(Call(hand, "SlotX", 1), Is.EqualTo(second.anchoredPosition.x));
        }
    }

    [Test]
    public void SameAspectCanvasScaleChangeCancelsAnExistingHandDrag()
    {
        var root = Rect("Hand"); root.gameObject.SetActive(false);
        var hand = root.gameObject.AddComponent<CardHandUI>();
        var area = Rect("Cards", root); area.sizeDelta = new Vector2(842.4f, 176.35f);
        Set(hand, "handContainer", area);
        var card = Rect("Card", area).gameObject.AddComponent<CardUI>();
        card.Setup(hand, new CardData(new CardDefinition { cardType = CardType.Unit }), 1f, 1.3f, 20f, .1f, .5f);
        Get<List<CardUI>>(hand, "_cards").Add(card);
        Call(hand, "RefreshHandLayout");
        typeof(CardUI).GetProperty("IsDragging").GetSetMethod(true).Invoke(card, new object[] { true });
        root.localScale = Vector3.one * 2f;
        Call(hand, "RefreshHandLayout");
        Assert.That(card.IsDragging, Is.False, "Same logical size can still have changed screen coordinates");
    }

    [Test]
    public void ArmyDetailUsesABoundedScrollableViewportWithoutDuplicatingItOnReopen()
    {
        var root = Rect("Army"); root.gameObject.SetActive(false);
        var modal = root.gameObject.AddComponent<ArmyViewerModalUI>();
        var detail = Rect("DetailText", root); detail.sizeDelta = new Vector2(140, 80);
        var text = detail.gameObject.AddComponent<TMPro.TextMeshProUGUI>();
        text.font = TMPro.TMP_Settings.defaultFontAsset;
        text.fontSize = 12; text.text = string.Join("\n", new string[30]);
        Set(modal, "detailText", text);
        Call(modal, "EnsureDetailScroll");
        var scroll = detail.GetComponentInParent<ScrollRect>(true);
        Assert.That(scroll, Is.Not.Null);
        Assert.That(scroll.content, Is.SameAs(detail));
        Assert.That(scroll.viewport.GetComponent<RectMask2D>(), Is.Not.Null);
        Assert.That(scroll.viewport.GetComponent<Image>().raycastTarget, Is.True);
        Assert.That(scroll.horizontal, Is.False);
        Assert.That(scroll.vertical, Is.True);
        root.gameObject.SetActive(true);
        text.text = string.Join("\n", System.Linq.Enumerable.Repeat("Unit ability description", 30));
        LayoutRebuilder.ForceRebuildLayoutImmediate(detail);
        Assert.That(detail.rect.height, Is.GreaterThan(scroll.viewport.rect.height));
        Call(modal, "EnsureDetailScroll");
        Assert.That(root.GetComponentsInChildren<ScrollRect>(true).Length, Is.EqualTo(1));
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
