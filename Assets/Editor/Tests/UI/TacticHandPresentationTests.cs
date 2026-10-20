#if UNITY_INCLUDE_TESTS
using System;
using System.Reflection;
using Game.Cards;
using Game.Combat;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class TacticHandPresentationTests
    {
        private GameObject root;
        private CardDefinition definition;
        private Sprite art;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, Private).SetValue(target, value);
        private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Private).GetValue(target);
        private static void Call(object target, string method) => target.GetType().GetMethod(method, Private).Invoke(target, null);

        [SetUp] public void SetUp()
        {
            root = new GameObject("tactic-presentation-tests", typeof(RectTransform));
            root.SetActive(false);
            definition = new CardDefinition();
            definition.cardType = CardType.Tactic;
            definition.tacticEffect = TacticEffect.AddSuccess;
            definition.tacticAmount = 1;
        }
        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(root);
            if (art != null) Object.DestroyImmediate(art);
        }
        private GameObject Child(string name)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(root.transform, false);
            return child;
        }
        private CardHandUI Hand(out Button button, out GameObject on, out GameObject off)
        {
            var hand = root.AddComponent<CardHandUI>();
            var owner = new PlayerSetupData { IsHuman = true };
            Set(hand, "_humanPlayer", owner);
            button = Child("switch").AddComponent<Button>();
            on = Child("on"); off = Child("off");
            var lamps = button.gameObject.AddComponent<ArmyButtonUI>();
            Set(lamps, "lampOn", on); Set(lamps, "lampOff", off);
            Set(hand, "cardsTypeSwitchButton", button);
            Set(hand, "handContainer", Child("ordinary").GetComponent<RectTransform>());
            Set(hand, "tacticHandTemplate", Child("template").AddComponent<BattleHandUI>());
            Call(hand, "InitializeTacticSwitch");
            Call(hand, "BindTacticInventory");
            return hand;
        }
        [Test] public void HiddenHandBindsTheRealTacticPrefabBeforeAwake()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<BattleTacticCardUI>(
                "Assets/Prefabs/UI/BattleScreen/BattleTacticCard.prefab");
            Assert.IsNotNull(prefab);
            var viewObject = Child("hidden-hand");
            var view = viewObject.AddComponent<BattleHandUI>();
            var container = Child("cards").GetComponent<RectTransform>();
            container.SetParent(viewObject.transform, false);
            Set(view, "cardContainer", container);
            Set(view, "cardPrefab", prefab);
            var card = new CardData(definition);
            var inventory = new TacticHand();
            inventory.Add(card);
            Assert.DoesNotThrow(() => view.Bind(inventory, data => true, onDrop: (data, point, camera) => true));
            var rendered = container.GetComponentInChildren<BattleTacticCardUI>(true);
            Assert.IsNotNull(rendered);
            Assert.AreSame(card, rendered.GetComponent<CardUI>().Data);
            Assert.IsFalse(rendered.gameObject.activeInHierarchy);
            Assert.DoesNotThrow(() => rendered.SetSlot(new Vector2(20f, 10f)));
            Assert.AreEqual(new Vector2(20f, 10f), ((RectTransform)rendered.transform).anchoredPosition);
        }

        [Test] public void SwitchTracksInventoryAndRestoresOrdinaryHandWhenLastCardIsGone()
        {
            var hand = Hand(out var button, out var on, out var off);
            Assert.IsFalse(button.gameObject.activeSelf);
            var inventory = Get<PlayerSetupData>(hand, "_humanPlayer").Tactics;
            inventory.Add(new CardData(definition));
            Assert.IsTrue(button.gameObject.activeSelf);
            Assert.IsFalse(on.activeSelf); Assert.IsTrue(off.activeSelf);
            button.onClick.Invoke();
            Assert.IsTrue(on.activeSelf); Assert.IsFalse(off.activeSelf);
            Assert.IsTrue(Get<BattleHandUI>(hand, "_tacticView").gameObject.activeSelf);
            inventory.Clear();
            Assert.IsFalse(button.gameObject.activeSelf);
            Assert.IsFalse(on.activeSelf); Assert.IsTrue(off.activeSelf);
            Assert.IsTrue(Get<RectTransform>(hand, "handContainer").gameObject.activeSelf);
        }
        [Test] public void SwitchingAwayAndBackPreservesChallengeDropCallback()
        {
            var hand = Hand(out var button, out _, out _);
            var inventory = Get<PlayerSetupData>(hand, "_humanPlayer").Tactics;
            inventory.Add(new CardData(definition));
            var view = hand.ShowTactics(Get<BattleHandUI>(hand, "tacticHandTemplate"), inventory);
            Func<CardData, Vector2, Camera, bool> drop = (card, point, camera) => true;
            view.Bind(inventory, card => true, onDrop: drop);
            button.onClick.Invoke(); button.onClick.Invoke();
            Assert.AreSame(drop, Get<Func<CardData, Vector2, Camera, bool>>(view, "_onDrop"));
        }
        [Test] public void LastTacticDuringChallengeDoesNotTearDownTheDecisionHand()
        {
            var hand = Hand(out var button, out _, out _);
            var inventory = Get<PlayerSetupData>(hand, "_humanPlayer").Tactics;
            inventory.Add(new CardData(definition));
            var view = hand.ShowTactics(Get<BattleHandUI>(hand, "tacticHandTemplate"), inventory);
            inventory.Clear();
            Assert.IsFalse(button.gameObject.activeSelf);
            Assert.IsTrue(view.gameObject.activeSelf);
            hand.RestoreMainHand();
            Assert.IsFalse(view.gameObject.activeSelf);
        }
        [Test] public void AppliedCardRemainsVisibleAfterDecisionAndResetsForNextChallenge()
        {
            var popup = root.AddComponent<BattleAttackPopupUI>();
            var slot = Child("input").AddComponent<Image>();
            slot.color = new Color(1, 1, 1, 0.4f);
            Set(popup, "tacticalCardInput", slot);
            Call(popup, "InitializeTacticInput");
            art = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1), Vector2.zero);
            definition.art = art;
            typeof(BattleAttackPopupUI).GetMethod("ShowTacticResult", Private).Invoke(popup, new object[] { new CardData(definition) });
            Call(popup, "CloseTacticDecision");
            Assert.AreSame(art, slot.sprite); Assert.AreEqual(1f, slot.color.a);
            Call(popup, "CloseTacticPresentation");
            Assert.IsNull(slot.sprite); Assert.AreEqual(0.4f, slot.color.a);
        }
        [Test] public void CurrentResultUpdatesAfterTacticAndClearsOnReset()
        {
            var popup = root.AddComponent<BattleAttackPopupUI>();
            var text = Child("result").AddComponent<TextMeshProUGUI>();
            Set(popup, "currentResultText", text);
            var owner = new PlayerSetupData { IsHuman = true };
            var attacker = new UnitData { Owner = owner };
            var session = new BattleChallengeSession(BattleChallengeMode.GroundCombat, attacker, new UnitData(),
                1, 1, 0, 0, AbilityMagnitudes.Default, rollTokens: count => new[] { false });
            Set(popup, "_challengeSession", session);
            Call(popup, "RefreshTacticTotals");
            Assert.AreEqual(string.Empty, text.text);
            session.Roll(); Call(popup, "RefreshTacticTotals");
            Assert.AreEqual("Result : Miss", text.text);
            var card = new CardData(definition); owner.Tactics.Add(card);
            session.TryNextFateTurn(out var defenderSide); session.ReportFateTurn(false);
            session.TryNextFateTurn(out defenderSide);
            Assert.IsTrue(session.TryPlayTactic(false, card));
            Call(popup, "RefreshTacticTotals");
            Assert.AreEqual("Result : Hit", text.text);
            Call(popup, "CloseTacticPresentation");
            Assert.AreEqual(string.Empty, text.text);
        }
    }
}
#endif
