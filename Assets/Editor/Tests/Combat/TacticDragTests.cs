#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System;
using System.Collections;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using Game.Cards;
using Game.Combat;
using Game.Players;
using Game.Turns;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Game.EditorTests
{
    // Enters Play Mode per test (~20 s each): full suite only (Tools/ai-verify/README.md, "Unity runs").
    [Category("Extended")]
    public sealed class TacticDragTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _root;
        private BattleAttackPopupUI _popup;
        private BattleHandUI _hand;
        private CardUI _card;
        private CardHandUI _mapHand;
        private RectTransform _container;
        private PopupPanelUI _info;
        private Button _slot;
        private EventSystem _events;
        private PlayerSetupData _owner;
        private BattleChallengeSession _session;

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, Private).SetValue(target, value);
        private static object Get(object target, string name) => target.GetType().GetField(name, Private).GetValue(target);
        private static object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Private).Invoke(target, args);

        private GameObject Child(string name, Transform parent)
        {
            var obj = new GameObject(name, typeof(RectTransform));
            obj.transform.SetParent(parent, false);
            return obj;
        }

        private void Build(bool onMap)
        {
            _root = new GameObject("Tactic drag test", typeof(RectTransform), typeof(Canvas));
            _root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            _events = Child("Events", _root.transform).AddComponent<EventSystem>();
            _owner = new PlayerSetupData { IsHuman = true };
            _owner.Tactics.Add(new CardData(new CardDefinition { cardType = CardType.Tactic,
                tacticEffect = TacticEffect.AddSuccess, tacticAmount = 1 }));
            var attacker = new UnitData { Owner = new PlayerSetupData() };
            var defender = new UnitData { Owner = _owner };
            int roll = 0;
            _session = new BattleChallengeSession(BattleChallengeMode.GroundCombat,
                attacker, defender, 1, 1, 0, 0, AbilityMagnitudes.Default,
                rollTokens: n => new[] { roll++ == 0 });
            _session.Roll();
            _session.TryNextFateTurn(out _);

            var handObject = Child("Hand", _root.transform);
            var handRect = (RectTransform)handObject.transform;
            handRect.sizeDelta = onMap ? new Vector2(600, 180) : new Vector2(180, 500);
            handRect.anchoredPosition = new Vector2(-300, 0);
            handObject.SetActive(false);
            var handCanvas = handObject.AddComponent<Canvas>();
            handCanvas.overrideSorting = true; handCanvas.sortingOrder = 25000;
            var container = (RectTransform)Child("Cards", handObject.transform).transform;
            container.anchorMin = Vector2.zero; container.anchorMax = Vector2.one; container.sizeDelta = Vector2.zero;
            var template = Child("Card template", _root.transform);
            ((RectTransform)template.transform).sizeDelta = new Vector2(106, 155);
            ((RectTransform)template.transform).anchoredPosition = new Vector2(10000, 10000);
            template.AddComponent<Image>();
            if (onMap)
            {
                _mapHand = handObject.AddComponent<CardHandUI>();
                Set(_mapHand, "_humanPlayer", _owner);
                Set(_mapHand, "handContainer", container);
                Set(_mapHand, "cardPrefab", template.AddComponent<CardUI>());
                handObject.SetActive(true);
                _mapHand.ShowTactics(_owner.Tactics);
                Call(_mapHand, "StopTacticFlip"); Call(_mapHand, "Relayout", false);
            }
            else
            {
                _hand = handObject.AddComponent<BattleHandUI>();
                Set(_hand, "cardContainer", container);
                Set(_hand, "cardPrefab", template.AddComponent<BattleTacticCardUI>());
                handObject.SetActive(true);
            }
            _container = container;

            var infoObject = Child("Info window", _root.transform);
            infoObject.SetActive(false);
            _info = infoObject.AddComponent<PopupPanelUI>();
            Set(_info, "panelRoot", infoObject);
            var confirm = Child("Confirm", infoObject.transform).AddComponent<Button>();
            Set(_info, "confirmButton", confirm);
            var turnObject = Child("Turn controller", _root.transform); turnObject.SetActive(false);
            var turns = turnObject.AddComponent<GameTurnController>(); Set(turns, "popupPanel", _info);

            _slot = Child("Tactic slot", _root.transform).AddComponent<Button>();
            ((RectTransform)_slot.transform).sizeDelta = new Vector2(145, 200);
            ((RectTransform)_slot.transform).anchoredPosition = new Vector2(250, 0);
            var popupObject = Child("Challenge", _root.transform);
            _popup = popupObject.AddComponent<BattleAttackPopupUI>();
            Set(_popup, "panelRoot", popupObject);
            Set(_popup, "_attacker", attacker); Set(_popup, "_defender", defender);
            Set(_popup, "_challengeSession", _session); Set(_popup, "_awaitingHumanDecision", true);
            Set(_popup, "_phase", Enum.Parse(typeof(BattleAttackPopupUI).GetField("_phase", Private).FieldType, "InProgress"));
            Set(_popup, "_tacticHand", _hand); Set(_popup, "_mapHand", _mapHand);
            Set(_popup, "tacticalCardInput", _slot.gameObject.AddComponent<Image>());
            Call(_popup, "InitializeTacticInput");
            Set(_popup, "_tacticInfoPopup", _info); Set(_popup, "_tacticTurnController", turns);
            _info.VisibilityChanged += (Action)typeof(BattleAttackPopupUI)
                .GetMethod("OnTacticInfoVisibilityChanged", Private).CreateDelegate(typeof(Action), _popup);
            Call(_popup, "OpenTacticDecision", true);
            _card = container.GetComponentInChildren<CardUI>();
            Canvas.ForceUpdateCanvases();
        }

        private PointerEventData BeginDrag()
        {
            int count = _owner.Tactics.Cards.Count;
            var data = new PointerEventData(_events) { button = PointerEventData.InputButton.Left,
                position = RectTransformUtility.WorldToScreenPoint(null, _card.transform.position) };
            Assert.That(_card.GetComponents<IBeginDragHandler>().Length, Is.EqualTo(1),
                "tactics must use CardUI without a second drag handler");
            Assert.That(_card.GetComponent<CardUI>(), Is.Not.Null);
            ExecuteEvents.Execute(_card.gameObject, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(_card.gameObject, data, ExecuteEvents.beginDragHandler);
            Assert.That(_card.IsDragging, Is.True);
            Assert.That(_owner.Tactics.Cards.Count, Is.EqualTo(count), "picking a card up must not consume it");
            return data;
        }

        private void Release(PointerEventData data, Vector2 point)
        {
            data.delta = point - data.position; data.position = point;
            var rect = (RectTransform)_card.transform;
            Vector2 start = rect.anchoredPosition;
            ExecuteEvents.Execute(_card.gameObject, data, ExecuteEvents.dragHandler);
            if (data.delta.sqrMagnitude > 0f)
                Assert.That(rect.anchoredPosition, Is.Not.EqualTo(start), "the source card follows the drag");
            ExecuteEvents.Execute(_card.gameObject, data, ExecuteEvents.endDragHandler);
        }

        [UnityTest]
        public IEnumerator MapAndBattleHands_DropPreviewsAndAcceptConsumesExactlyOnce()
        {
            foreach (bool onMap in new[] { false, true })
            {
                Build(onMap);
                var data = BeginDrag();
                Release(data, RectTransformUtility.WorldToScreenPoint(null, _slot.transform.position));
                Assert.That(_owner.Tactics.Cards.Count, Is.EqualTo(1));
                Assert.That(_owner.Tactics.Version, Is.EqualTo(1));
                Assert.That(_session.DefenderModifier, Is.Zero);
                Assert.That((bool)Get(_popup, "_humanSpent"), Is.False);
                Assert.That(Get(_popup, "_pendingTactic"), Is.SameAs(_owner.Tactics.Cards[0]));
                ExecuteEvents.Execute(_card.gameObject, data, ExecuteEvents.pointerClickHandler);
                Assert.That(_owner.Tactics.Version, Is.EqualTo(1), "release must not also click and commit");
                Call(_popup, "OnAcceptClicked");
                Assert.That(_owner.Tactics.Cards, Is.Empty);
                Assert.That(_owner.Tactics.Version, Is.EqualTo(2));
                Assert.That(_session.DefenderModifier, Is.EqualTo(1));
                yield return null;
                Cleanup();
            }
        }

        [UnityTest]
        public IEnumerator InvalidDrop_ShowsStandardInfoWindow_AndKeepsCardAndDecision()
        {
            Build(false);
            Vector2 home = ((RectTransform)_card.transform).anchoredPosition;
            var data = BeginDrag();
            Release(data, new Vector2(-10000, -10000));
            Assert.That(((RectTransform)_card.transform).anchoredPosition, Is.EqualTo(home));
            Assert.That(_card.GetComponent<Canvas>().overrideSorting, Is.False);
            Assert.That(_info.IsShowing, Is.True);
            Assert.That(_hand.GetComponent<Canvas>().overrideSorting, Is.False,
                "the hand must not render above the standard info window");
            Assert.That(_owner.Tactics.Cards.Count, Is.EqualTo(1));
            Assert.That(_session.DefenderModifier, Is.Zero);
            Call(_popup, "OnAcceptClicked");
            Assert.That((bool)Get(_popup, "_humanDeclined"), Is.False);
            _info.Hide();
            Call(_popup, "OnAcceptClicked");
            Assert.That((bool)Get(_popup, "_humanDeclined"), Is.False, "dismissing the hint cannot accept the challenge in the same frame");
            yield return null;
            Call(_popup, "OnAcceptClicked");
            Assert.That((bool)Get(_popup, "_humanDeclined"), Is.True);
        }

        [UnityTest]
        public IEnumerator ReturningToHand_AndEndingDecisionDuringDrag_DoNotConsume()
        {
            Build(true);
            Vector2 home = ((RectTransform)_card.transform).anchoredPosition;
            var data = BeginDrag(); Release(data, data.position);
            Assert.That(_info.IsShowing, Is.False);
            Assert.That(_owner.Tactics.Cards.Count, Is.EqualTo(1));
            data = BeginDrag();
            data.delta = new Vector2(0f, 100f); data.position += data.delta;
            ExecuteEvents.Execute(_card.gameObject, data, ExecuteEvents.dragHandler);
            Call(_popup, "CloseTacticDecision");
            Assert.That(_card.IsDragging, Is.False);
            Assert.That(((RectTransform)_card.transform).anchoredPosition, Is.EqualTo(home));
            ExecuteEvents.Execute(_card.gameObject, data, ExecuteEvents.endDragHandler);
            Assert.That(_owner.Tactics.Cards.Count, Is.EqualTo(1));
            Assert.That(_session.DefenderModifier, Is.Zero);
            yield return null;
            Assert.That(_card.GetComponent<CardUI>().IsDragging, Is.False);
        }

        [UnityTest]
        public IEnumerator MapAndBattleHands_CanReplaceThenDragTheSlottedCardBack()
        {
            foreach (bool onMap in new[] { false, true })
            {
                Build(onMap);
                CardData first = _owner.Tactics.Cards[0];
                var second = new CardData(new CardDefinition { cardType = CardType.Tactic,
                    tacticEffect = TacticEffect.AddSuccess, tacticAmount = 2 });
                _owner.Tactics.Add(second);
                yield return null;
                _card = _container.GetComponentsInChildren<CardUI>().Single(c => c.Data == first);
                var data = BeginDrag();
                Release(data, RectTransformUtility.WorldToScreenPoint(null, _slot.transform.position));
                yield return null;
                _card = _container.GetComponentsInChildren<CardUI>().Single(c => c.Data == second);
                data = BeginDrag();
                Release(data, RectTransformUtility.WorldToScreenPoint(null, _slot.transform.position));
                Assert.That(Get(_popup, "_pendingTactic"), Is.SameAs(second));
                Assert.That(_owner.Tactics.Cards, Is.EqualTo(new[] { first, second }));
                Assert.That(_session.DefenderModifier, Is.Zero);
                yield return null;
                _card = (CardUI)Get(_popup, "_slottedTacticView");
                var slotPosition = ((RectTransform)_slot.transform).anchoredPosition;
                data = BeginDrag();
                Release(data, RectTransformUtility.WorldToScreenPoint(null, _container.position));
                Assert.That(Get(_popup, "_pendingTactic"), Is.Null);
                Assert.That(((RectTransform)_slot.transform).anchoredPosition, Is.EqualTo(slotPosition),
                    "Only the card moves; the input slot stays fixed.");
                Assert.That(_owner.Tactics.Version, Is.EqualTo(2));
                Assert.That(_session.DefenderModifier, Is.Zero);
                yield return null;
                Assert.That(_container.GetComponentsInChildren<CardUI>().Length, Is.EqualTo(2));
                Cleanup();
            }
        }

        [UnityTest]
        public IEnumerator MapFlipStaggersCardsAndLeavesTheHandContainerStationary()
        {
            Build(true);
            for (int i = 0; i < 2; i++)
                _owner.Tactics.Add(new CardData(new CardDefinition { cardType = CardType.Tactic,
                    tacticEffect = TacticEffect.AddSuccess, tacticAmount = 1 }));
            for (int i = 0; i < 5; i++)
                _mapHand.AddCard(new CardData(new CardDefinition { cardType = CardType.Unit }));
            Set(_mapHand, "tacticFlipDuration", 0.6f);
            Set(_mapHand, "tacticFlipInterval", 0.12f);
            Call(_mapHand, "StartTacticFlip", false);
            Call(_mapHand, "StopTacticFlip"); Call(_mapHand, "Relayout", false);
            var ordinary = (List<CardUI>)Get(_mapHand, "_cards");
            var tactics = (List<CardUI>)Get(_mapHand, "_tacticCards");
            Vector3 scale = _container.localScale;
            Vector2 position = _container.anchoredPosition;
            bool sawDelay = false, sawOverlap = false;
            Call(_mapHand, "StartTacticFlip", true);
            float deadline = Time.realtimeSinceStartup + 4f;
            while (_mapHand.IsHandTransitioning && Time.realtimeSinceStartup < deadline)
            {
                Assert.That(_container.localScale, Is.EqualTo(scale));
                Assert.That(_container.anchoredPosition, Is.EqualTo(position));
                float first = ordinary[0].transform.localScale.x;
                float next = ordinary[1].transform.localScale.x;
                sawDelay |= first < 0.89f && next > 0.899f;
                sawOverlap |= first > 0f && first < 0.89f && next > 0f && next < 0.89f;
                yield return null;
            }
            Assert.That(_mapHand.IsHandTransitioning, Is.False);
            Assert.That(sawDelay, Is.True, "The second card starts later.");
            Assert.That(sawOverlap, Is.True, "The second card starts before the first finishes.");
            Assert.That(ordinary.All(c => !c.gameObject.activeSelf), Is.True);
            Assert.That(tactics.All(c => c.gameObject.activeSelf), Is.True);
            Assert.That(_container.GetComponentsInChildren<CardUI>().Length, Is.EqualTo(3),
                "Slots without tactical cards stay empty.");
            // Reversing a running wave must leave no half-sized or duplicated cards.
            Call(_mapHand, "StartTacticFlip", false);
            yield return null;
            Call(_mapHand, "StartTacticFlip", true);
            deadline = Time.realtimeSinceStartup + 4f;
            while (_mapHand.IsHandTransitioning && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(_mapHand.IsHandTransitioning, Is.False);
            Assert.That(ordinary.All(c => !c.gameObject.activeSelf), Is.True);
            Assert.That(tactics.All(c => c.gameObject.activeSelf && c.transform.localScale == Vector3.one * 0.9f), Is.True);
            Call(_mapHand, "StartTacticFlip", false);
            deadline = Time.realtimeSinceStartup + 4f;
            while (_mapHand.IsHandTransitioning && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(_mapHand.IsHandTransitioning, Is.False);
            Assert.That(ordinary.All(c => c.gameObject.activeSelf), Is.True);
            Assert.That(tactics.All(c => !c.gameObject.activeSelf), Is.True);
            Assert.That(_container.localScale, Is.EqualTo(scale));
        }

        [UnityTest]
        public IEnumerator OrdinaryInventoryChangesSettleAnInProgressFlipBeforeMutatingItsLists()
        {
            Build(true);
            for (int i = 0; i < 5; i++)
                _mapHand.AddCard(new CardData(new CardDefinition { cardType = CardType.Unit }));
            Set(_mapHand, "tacticFlipDuration", 0.6f);
            Call(_mapHand, "StartTacticFlip", false);
            yield return null;
            Assert.IsTrue(_mapHand.IsHandTransitioning);
            _mapHand.AddCard(new CardData(new CardDefinition { cardType = CardType.Unit }));
            Assert.IsFalse(_mapHand.IsHandTransitioning);
            var ordinary = (List<CardUI>)Get(_mapHand, "_cards");
            var tactics = (List<CardUI>)Get(_mapHand, "_tacticCards");
            Assert.AreEqual(6, ordinary.Count);
            Assert.IsTrue(ordinary.All(c => c.gameObject.activeSelf));
            Assert.IsTrue(tactics.All(c => !c.gameObject.activeSelf));
            Call(_mapHand, "StartTacticFlip", true);
            yield return null;
            Assert.IsTrue(_mapHand.IsHandTransitioning);
            var removed = ordinary[0];
            Call(_mapHand, "RemoveCard", removed);
            Assert.IsFalse(_mapHand.IsHandTransitioning);
            Assert.IsFalse(removed.gameObject.activeSelf);
            Assert.AreEqual(5, ordinary.Count);
            Assert.IsTrue(ordinary.All(c => !c.gameObject.activeSelf));
            Assert.IsTrue(tactics.All(c => c.gameObject.activeSelf));
            yield return null;
            Assert.IsTrue(tactics.All(c => c.transform.localScale == Vector3.one * 0.9f));
        }

        [UnitySetUp]
        public IEnumerator EnterPlayModeForTest()
        {
            yield return new EnterPlayMode();
        }

        [UnityTearDown]
        public IEnumerator ExitPlayModeAfterTest()
        {
            Cleanup();
            yield return null;
            yield return new ExitPlayMode();
        }

        public void Cleanup()
        {
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            _root = null;
            _hand = null; _mapHand = null; _card = null;
        }
    }
}
#endif
