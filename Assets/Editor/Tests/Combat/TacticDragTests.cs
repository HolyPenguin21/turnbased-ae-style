#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System;
using System.Collections;
using System.Reflection;
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
    public sealed class TacticDragTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _root;
        private BattleAttackPopupUI _popup;
        private BattleHandUI _hand;
        private BattleTacticCardUI _card;
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

        private void Build(bool horizontal)
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
                rollDice: n => new[] { roll++ == 0 });
            _session.Roll();
            _session.TryNextFateTurn(out _);

            var handObject = Child("Hand", _root.transform);
            var handRect = (RectTransform)handObject.transform;
            handRect.sizeDelta = horizontal ? new Vector2(600, 180) : new Vector2(180, 500);
            handRect.anchoredPosition = new Vector2(-300, 0);
            _hand = handObject.AddComponent<BattleHandUI>();
            var handCanvas = handObject.AddComponent<Canvas>();
            handCanvas.overrideSorting = true; handCanvas.sortingOrder = 25000;
            var container = (RectTransform)Child("Cards", handObject.transform).transform;
            container.anchorMin = Vector2.zero; container.anchorMax = Vector2.one; container.sizeDelta = Vector2.zero;
            var template = Child("Card template", _root.transform);
            ((RectTransform)template.transform).sizeDelta = new Vector2(106, 155);
            ((RectTransform)template.transform).anchoredPosition = new Vector2(10000, 10000);
            template.AddComponent<Image>();
            Set(_hand, "cardContainer", container);
            Set(_hand, "cardPrefab", template.AddComponent<BattleTacticCardUI>());
            if (horizontal) _hand.MakeHorizontal();

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
            Set(_popup, "_tacticHand", _hand); Set(_popup, "_tacticSlot", _slot);
            Set(_popup, "_tacticInfoPopup", _info); Set(_popup, "_tacticTurnController", turns);
            _info.VisibilityChanged += (Action)typeof(BattleAttackPopupUI)
                .GetMethod("OnTacticInfoVisibilityChanged", Private).CreateDelegate(typeof(Action), _popup);
            Call(_popup, "OpenTacticDecision", true);
            _card = container.GetComponentInChildren<BattleTacticCardUI>();
            Canvas.ForceUpdateCanvases();
        }

        private PointerEventData BeginDrag()
        {
            var data = new PointerEventData(_events) { button = PointerEventData.InputButton.Left,
                position = RectTransformUtility.WorldToScreenPoint(null, _card.transform.position) };
            Assert.That(_card.GetComponents<IBeginDragHandler>().Length, Is.EqualTo(1),
                "tactics must use CardUI without a second drag handler");
            Assert.That(_card.GetComponent<CardUI>(), Is.Not.Null);
            ExecuteEvents.Execute(_card.gameObject, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(_card.gameObject, data, ExecuteEvents.beginDragHandler);
            Assert.That(_card.IsDragging, Is.True);
            Assert.That(_owner.Tactics.Cards.Count, Is.EqualTo(1), "picking a card up must not consume it");
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
        public IEnumerator BothHandOrientations_DropInSlotAppliesExactlyOnce()
        {
            foreach (bool horizontal in new[] { false, true })
            {
                Build(horizontal);
                var data = BeginDrag();
                Release(data, RectTransformUtility.WorldToScreenPoint(null, _slot.transform.position));
                Assert.That(_owner.Tactics.Cards, Is.Empty);
                Assert.That(_owner.Tactics.Version, Is.EqualTo(2));
                Assert.That(_session.DefenderModifier, Is.EqualTo(1));
                Assert.That((bool)Get(_popup, "_humanSpent"), Is.True);
                ExecuteEvents.Execute(_card.gameObject, data, ExecuteEvents.pointerClickHandler);
                Assert.That(_owner.Tactics.Version, Is.EqualTo(2), "release must not also click and apply a second time");
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

        [TearDown]
        public void Cleanup()
        {
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root);
            _root = null;
        }
    }
}
#endif
