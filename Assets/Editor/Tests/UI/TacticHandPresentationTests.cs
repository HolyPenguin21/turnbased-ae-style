#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Collections;
using Game.Cards;
using Game.Ai;
using Game.Combat;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEngine.TestTools;
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
        private static object Call(object target, string method, params object[] args) =>
            target.GetType().GetMethod(method, Private).Invoke(target, args);

        [UnitySetUp] public IEnumerator SetUp()
        {
            yield return new EnterPlayMode();
            root = new GameObject("tactic-presentation-tests", typeof(RectTransform));
            root.SetActive(false);
            definition = new CardDefinition();
            definition.cardType = CardType.Tactic;
            definition.tacticEffect = TacticEffect.AddSuccess;
            definition.tacticAmount = 1;
        }
        [UnityTearDown] public IEnumerator TearDown()
        {
            Object.DestroyImmediate(root);
            if (art != null) Object.DestroyImmediate(art);
            yield return null;
            yield return new ExitPlayMode();
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
            Set(hand, "cardPrefab", Child("ordinary-card-template").AddComponent<CardUI>());
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
            var cards = Get<List<CardUI>>(hand, "_tacticCards");
            Assert.AreEqual(1, cards.Count);
            Assert.AreSame(Get<RectTransform>(hand, "handContainer"), cards[0].transform.parent);
            Assert.IsNull(cards[0].GetComponent<BattleTacticCardUI>());
            Assert.IsTrue(cards[0].gameObject.activeSelf);
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
            hand.ShowTactics(inventory);
            Func<CardData, Vector2, Camera, bool> drop = (card, point, camera) => true;
            hand.BindTactics(inventory, card => true, onDrop: drop);
            button.onClick.Invoke(); button.onClick.Invoke();
            Assert.AreSame(drop, Get<Func<CardData, Vector2, Camera, bool>>(hand, "_onDropTactic"));
        }
        [Test] public void LastTacticDuringChallengeDoesNotTearDownTheDecisionHand()
        {
            var hand = Hand(out var button, out _, out _);
            var inventory = Get<PlayerSetupData>(hand, "_humanPlayer").Tactics;
            inventory.Add(new CardData(definition));
            hand.ShowTactics(inventory);
            inventory.Clear();
            Assert.IsTrue(button.gameObject.activeSelf);
            Assert.IsTrue(Get<bool>(hand, "_showingTactics"));
            hand.RestoreMainHand();
            Assert.IsFalse(Get<bool>(hand, "_showingTactics"));
        }
        [Test] public void EachHandRetainsItsOwnScrollPosition()
        {
            var hand = Hand(out var button, out _, out _);
            for (int i = 0; i < 9; i++)
                hand.AddCard(new CardData(new CardDefinition { cardType = CardType.Unit }));
            var inventory = Get<PlayerSetupData>(hand, "_humanPlayer").Tactics;
            for (int i = 0; i < 8; i++) inventory.Add(new CardData(definition));
            Assert.AreEqual(2, Get<int>(hand, "_scrollOffset"));
            button.onClick.Invoke();
            Call(hand, "OnScrollRightClicked");
            Assert.AreEqual(1, Get<int>(hand, "_tacticScrollOffset"));
            Assert.AreEqual(2, Get<int>(hand, "_scrollOffset"));
            button.onClick.Invoke();
            Assert.AreEqual(2, Get<int>(hand, "_scrollOffset"));
            Assert.AreEqual(1, Get<int>(hand, "_tacticScrollOffset"));
            Assert.AreEqual(9, Get<List<CardUI>>(hand, "_cards").Count);
            Assert.AreEqual(8, inventory.Cards.Count);
        }

        [TestCase(4)]
        [TestCase(7)]
        public static void ReturnedLastCardRemainsVisibleAfterTemporaryRemoval(int visibleCount)
        {
            // One more card than the window: selecting the last card temporarily clamps scroll to zero.
            int originalOffset = 1;
            int offsetWhileSlotted = 0;
            int returnedIndex = visibleCount;
            int restored = UIListUtility.OffsetToReveal(offsetWhileSlotted, returnedIndex, visibleCount);
            Assert.That(returnedIndex, Is.InRange(restored, restored + visibleCount - 1));
            Assert.AreEqual(originalOffset, restored);
        }

        [Test] public void ReturningTheLastMapCardRevealsItWithoutChangingItsInventoryPosition()
        {
            var hand = Hand(out _, out _, out _);
            var inventory = Get<PlayerSetupData>(hand, "_humanPlayer").Tactics;
            for (int i = 0; i < 8; i++) inventory.Add(new CardData(definition));
            hand.ShowTactics(inventory);
            Call(hand, "OnScrollRightClicked");
            CardData last = inventory.Cards[7];
            hand.BindTactics(inventory, excluded: last);
            Assert.AreEqual(0, Get<int>(hand, "_tacticScrollOffset"));
            hand.BindTactics(inventory);
            Assert.AreEqual(1, Get<int>(hand, "_tacticScrollOffset"));
            var view = Get<List<CardUI>>(hand, "_tacticCards").Find(c => c.Data == last);
            Assert.IsTrue(view.gameObject.activeSelf);
            Assert.AreSame(last, inventory.Cards[7]);
            Assert.AreEqual(8, inventory.Version);
        }

        [Test] public void ReturningTheLastBattleCardRevealsItWithoutChangingItsInventoryPosition()
        {
            var view = Child("battle-hand").AddComponent<BattleHandUI>();
            var container = Child("battle-cards").GetComponent<RectTransform>();
            container.SetParent(view.transform, false);
            var prefab = Child("battle-card-template").AddComponent<BattleTacticCardUI>();
            Set(prefab, "rectTransform", (RectTransform)prefab.transform);
            Set(view, "cardContainer", container); Set(view, "cardPrefab", prefab);
            var inventory = new TacticHand();
            for (int i = 0; i < 5; i++) inventory.Add(new CardData(definition));
            view.Bind(inventory);
            Call(view, "Scroll", 1);
            CardData last = inventory.Cards[4];
            view.Bind(inventory, excluded: last);
            Assert.AreEqual(0, Get<int>(view, "_scrollOffset"));
            view.Bind(inventory);
            Assert.AreEqual(1, Get<int>(view, "_scrollOffset"));
            Assert.IsTrue(Get<List<BattleTacticCardUI>>(view, "_visible").Exists(c => c.Data == last));
            Assert.AreSame(last, inventory.Cards[4]);
            Assert.AreEqual(5, inventory.Version);
        }

        [Test] public void HumanTacticDecisionTemporarilyOverridesTheWatchedAiHand()
        {
            var hand = Hand(out _, out _, out _);
            var text = Child("hand-count").AddComponent<TextMeshProUGUI>();
            Set(hand, "handCountText", text);
            hand.AddCard(new CardData(new CardDefinition { cardType = CardType.Unit }));
            var ai = new AiHandData(null, default, 0);
            ai.AddCard(new CardData(new CardDefinition { cardType = CardType.Unit }));
            hand.ShowAiHandDebug(ai);
            var inventory = Get<PlayerSetupData>(hand, "_humanPlayer").Tactics;
            inventory.Add(new CardData(definition));
            hand.ShowTactics(inventory);
            Assert.IsTrue(Get<bool>(hand, "_showingDebugHand"), "The watch subscription survives the human response.");
            Assert.IsTrue(Get<List<CardUI>>(hand, "_debugCards").TrueForAll(c => !c.gameObject.activeSelf));
            Assert.IsTrue(Get<List<CardUI>>(hand, "_tacticCards").TrueForAll(c => c.gameObject.activeSelf));
            Assert.AreEqual("1", text.text);
            ai.AddCard(new CardData(new CardDefinition { cardType = CardType.Unit }));
            Call(hand, "RenderDebugHand", ai);
            Assert.IsTrue(Get<List<CardUI>>(hand, "_debugCards").TrueForAll(c => !c.gameObject.activeSelf));
            Assert.AreEqual("1", text.text, "AI inventory updates cannot overwrite the human counter.");
            hand.RestoreMainHand();
            Assert.IsTrue(Get<List<CardUI>>(hand, "_debugCards").TrueForAll(c => c.gameObject.activeSelf));
            Assert.IsTrue(Get<List<CardUI>>(hand, "_tacticCards").TrueForAll(c => !c.gameObject.activeSelf));
            Assert.IsTrue(Get<List<CardUI>>(hand, "_cards").TrueForAll(c => !c.gameObject.activeSelf));
            Assert.AreEqual("2/10", text.text);
            Assert.AreEqual(1, inventory.Cards.Count);
        }

        [Test] public void TacticalFacesHaveNoTextOrStatBadges()
        {
            var face = Child("ordinary-tactic-face").AddComponent<CardUI>();
            var name = Child("name").AddComponent<TextMeshProUGUI>();
            var description = Child("description").AddComponent<TextMeshProUGUI>();
            var stats = Child("stats");
            var ap = Child("ap");
            Set(face, "nameText", name); Set(face, "typeText", description);
            Set(face, "statsRow", stats); Set(face, "apBadgeRoot", ap);
            definition.displayName = "A tactic"; definition.apCost = 3;
            face.Setup(null, new CardData(definition), 0.9f, 1.2f, 20f, 0.12f, 0.5f);
            Assert.AreEqual(string.Empty, name.text);
            Assert.AreEqual(string.Empty, description.text);
            Assert.IsFalse(stats.activeSelf); Assert.IsFalse(ap.activeSelf);
            var battleFace = Child("battle-tactic-face").AddComponent<BattleTacticCardUI>();
            Set(battleFace, "nameText", name); Set(battleFace, "typeText", description);
            battleFace.Setup(new CardData(definition), null);
            Assert.AreEqual(string.Empty, name.text); Assert.AreEqual(string.Empty, description.text);
        }

        private BattleAttackPopupUI Decision(out PlayerSetupData owner, out BattleChallengeSession session)
        {
            var popup = root.AddComponent<BattleAttackPopupUI>();
            owner = new PlayerSetupData { IsHuman = true };
            var attacker = new UnitData { Owner = new PlayerSetupData() };
            var defender = new UnitData { Owner = owner };
            int rolls = 0;
            session = new BattleChallengeSession(BattleChallengeMode.GroundCombat, attacker, defender,
                1, 1, 0, 1, AbilityMagnitudes.Default, rollTokens: n => new[] { rolls++ != 1 });
            session.Roll(); session.TryNextFateTurn(out _);
            Set(popup, "_attacker", attacker); Set(popup, "_defender", defender);
            Set(popup, "_challengeSession", session);
            Set(popup, "_phase", Enum.Parse(popup.GetType().GetField("_phase", Private).FieldType, "InProgress"));
            Set(popup, "_awaitingHumanDecision", true);
            Set(popup, "tacticalCardInput", Child("input").AddComponent<Image>());
            Set(popup, "currentResultText", Child("result").AddComponent<TextMeshProUGUI>());
            var hand = Child("battle-hand").AddComponent<BattleHandUI>();
            ((RectTransform)hand.transform).sizeDelta = new Vector2(500f, 180f);
            Set(popup, "_tacticHand", hand);
            Call(popup, "InitializeTacticInput");
            Call(popup, "OpenTacticDecision", true);
            return popup;
        }

        private static CardData Tactic(PlayerSetupData owner, TacticEffect effect, int amount)
        {
            var card = new CardData(new CardDefinition { cardType = CardType.Tactic,
                tacticEffect = effect, tacticAmount = amount });
            owner.Tactics.Add(card);
            return card;
        }

        private static (int a, int d) Preview(BattleAttackPopupUI popup)
        {
            object[] args = { 0, 0 };
            Call(popup, "GetTacticPreview", args);
            return ((int)args[0], (int)args[1]);
        }

        private static void ReturnSlotted(BattleAttackPopupUI popup)
        {
            var hand = Get<BattleHandUI>(popup, "_tacticHand");
            var point = RectTransformUtility.WorldToScreenPoint(null, hand.transform.position);
            Assert.IsTrue((bool)Call(popup, "TryReturnTactic", point, null, Get<int>(popup, "_tacticDecisionVersion")));
        }

        [Test] public void ReplaceAndReturnAreOnlyPreviewsAndPreserveInventoryOrder()
        {
            var popup = Decision(out var owner, out var session);
            var first = Tactic(owner, TacticEffect.SuppressSuccess, 2);
            var second = Tactic(owner, TacticEffect.AddSuccess, 2);
            int version = owner.Tactics.Version;
            Assert.IsTrue((bool)Call(popup, "TryStageTactic", first));
            Assert.AreEqual((-1, 0), Preview(popup), "Suppress only the single rolled success.");
            Assert.IsTrue((bool)Call(popup, "TryStageTactic", second));
            Assert.AreEqual((0, 2), Preview(popup), "The previous suppression must disappear completely.");
            Assert.AreEqual(0, session.AttackerModifier); Assert.AreEqual(0, session.DefenderModifier);
            Assert.IsFalse(Get<bool>(popup, "_humanSpent"));
            ReturnSlotted(popup);
            Assert.IsNull(Get<CardData>(popup, "_pendingTactic"));
            Assert.AreEqual((0, 0), Preview(popup));
            Assert.AreEqual(new[] { first, second }, owner.Tactics.Cards);
            Assert.AreEqual(version, owner.Tactics.Version);
        }

        [Test] public void AcceptConsumesOnlyTheFinalCardExactlyOnce()
        {
            var popup = Decision(out var owner, out var session);
            var first = Tactic(owner, TacticEffect.AddSuccess, 1);
            var second = Tactic(owner, TacticEffect.AddSuccess, 2);
            Call(popup, "TryStageTactic", first); Call(popup, "TryStageTactic", second);
            Call(popup, "OnAcceptClicked"); Call(popup, "OnAcceptClicked");
            Assert.AreEqual(new[] { first }, owner.Tactics.Cards);
            Assert.AreEqual(3, owner.Tactics.Version);
            Assert.AreEqual(2, session.DefenderModifier);
            Assert.IsTrue(Get<bool>(popup, "_humanDeclined"));
            Assert.IsTrue(Get<bool>(popup, "_tacticCommittedThisTurn"));
            Assert.IsNull(Get<CardData>(popup, "_pendingTactic"));
        }

        [Test] public void AcceptedTacticCountsAsAnActionForTheOpponentsResponse()
        {
            var popup = Decision(out var owner, out var session);
            var card = Tactic(owner, TacticEffect.AddSuccess, 1);
            var turn = (IEnumerator)Call(popup, "RunHumanTurn", true);
            Assert.IsTrue(turn.MoveNext());
            Call(popup, "TryStageTactic", card); Call(popup, "OnAcceptClicked");
            Assert.IsFalse(turn.MoveNext());
            Assert.IsTrue(Get<bool>(popup, "_turnSpent"));
            session.ReportFateTurn(Get<bool>(popup, "_turnSpent"));
            Assert.IsTrue(session.TryNextFateTurn(out bool defender));
            Assert.IsFalse(defender);
        }

        [Test] public void InvalidReplacementAndStaleDragPreserveThePendingCard()
        {
            var popup = Decision(out var owner, out var session);
            var card = Tactic(owner, TacticEffect.AddSuccess, 1);
            var foreign = new CardData(definition);
            Call(popup, "TryStageTactic", card);
            Assert.IsFalse((bool)Call(popup, "TryStageTactic", foreign));
            int oldVersion = Get<int>(popup, "_tacticDecisionVersion");
            Call(popup, "CloseTacticDecision"); Call(popup, "OpenTacticDecision", true);
            Assert.IsFalse((bool)Call(popup, "TryReturnTactic", Vector2.zero, null, oldVersion));
            Assert.AreSame(card, Get<CardData>(popup, "_pendingTactic"));
            Assert.IsTrue(owner.Tactics.Contains(card));
            Assert.AreEqual(0, session.DefenderModifier);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FateSpendCannotBeRepeatedOrFollowAcceptBeforeTheDecisionLoopAdvances(bool defenderSide)
        {
            var popup = Decision(out var owner, out _);
            var attacker = new UnitData { Owner = owner };
            var defender = new UnitData { Owner = owner };
            var session = new BattleChallengeSession(BattleChallengeMode.GroundCombat, attacker, defender,
                2, 2, defenderSide ? 0 : 2, defenderSide ? 2 : 0, AbilityMagnitudes.Default,
                rollTokens: n => new bool[n]);
            session.Roll(); session.TryNextFateTurn(out _);
            if (!defenderSide) { session.ReportFateTurn(false); session.TryNextFateTurn(out _); }
            Set(popup, "_attacker", attacker); Set(popup, "_attackerHero", attacker);
            Set(popup, "_defender", defender); Set(popup, "_defenderHero", defender);
            Set(popup, "_challengeSession", session); Set(popup, "_defenderTurnActive", defenderSide);
            string spend = defenderSide ? "OnDefenderSpend" : "OnAttackerSpend";
            Call(popup, spend); Call(popup, spend);
            Assert.AreEqual(1, defenderSide ? session.DefenderFateRemaining : session.AttackerFateRemaining);
            Set(popup, "_humanSpent", false); Set(popup, "_humanDeclined", true);
            Call(popup, spend);
            Assert.AreEqual(1, defenderSide ? session.DefenderFateRemaining : session.AttackerFateRemaining);
        }

        [Test] public void PendingCardSurvivesFateAndItsReturnNeverRefundsFate()
        {
            var popup = Decision(out var owner, out var session);
            var card = Tactic(owner, TacticEffect.AddSuccess, 2);
            Call(popup, "TryStageTactic", card);
            Assert.IsTrue(session.TrySpend(true, out _, out _));
            Call(popup, "CloseTacticDecision"); Call(popup, "OpenTacticDecision", true);
            Assert.AreSame(card, Get<CardData>(popup, "_pendingTactic"));
            Assert.AreEqual((0, 2), Preview(popup));
            ReturnSlotted(popup);
            Assert.AreEqual(0, session.DefenderFateRemaining);
            Assert.IsTrue(session.DefenderTokens[0]);
            Assert.AreEqual((0, 0), Preview(popup));
            Assert.IsTrue(owner.Tactics.Contains(card));
        }

        [Test] public void ClosingPresentationReturnsTheUnconfirmedCardWithoutApplyingIt()
        {
            var popup = Decision(out var owner, out var session);
            var card = Tactic(owner, TacticEffect.AddSuccess, 2);
            Call(popup, "TryStageTactic", card);
            Call(popup, "CloseTacticPresentation");
            Assert.IsTrue(owner.Tactics.Contains(card)); Assert.AreEqual(1, owner.Tactics.Version);
            Assert.IsNull(Get<CardData>(popup, "_pendingTactic"));
            Assert.AreEqual(0, session.DefenderModifier);
        }

        [Test] public void ProductionPreviewReplacementAndReturnUseTheOriginalDifficulty()
        {
            var popup = Decision(out var owner, out _);
            var hero = new UnitData { Owner = owner, Fate = 1 };
            var production = new ResearchProductionChallengeSession(hero, new CardDefinition { fate = 4 })
                { Tokens = new[] { true, true, true } };
            Set(popup, "_kind", Enum.Parse(popup.GetType().GetField("_kind", Private).FieldType, "ResearchProduction"));
            Set(popup, "_rpHero", hero); Set(popup, "_rpTactics", production); Set(popup, "_rpTokens", production.Tokens);
            Call(popup, "OpenTacticDecision", false);
            var first = Tactic(owner, TacticEffect.SuppressSuccess, 2);
            var second = Tactic(owner, TacticEffect.AddSuccess, 1);
            Assert.IsTrue((bool)Call(popup, "TryStageTactic", first));
            Assert.AreEqual((0, -2), Preview(popup));
            Assert.IsFalse((bool)Call(popup, "CanSpendResearchProductionFate"));
            Call(popup, "TryStageTactic", second);
            Assert.AreEqual((1, 0), Preview(popup));
            Assert.AreEqual(4, production.Required); Assert.IsFalse(production.TacticUsed);
            ReturnSlotted(popup);
            Assert.IsTrue((bool)Call(popup, "CanSpendResearchProductionFate"));
            Assert.AreEqual((0, 0), Preview(popup));
            Call(popup, "TryStageTactic", second); Call(popup, "OnAcceptClicked");
            Assert.IsTrue(production.Success); Assert.IsTrue(production.TacticUsed);
            Assert.AreEqual(new[] { first }, owner.Tactics.Cards);
        }

        [Test] public void ReturningAHumanPreviewRestoresTheOpponentsCommittedCardAndKeepsItsEffect()
        {
            var popup = Decision(out var owner, out var session);
            var attacker = Get<UnitData>(popup, "_attacker");
            var opposing = Tactic(attacker.Owner, TacticEffect.AddSuccess, 1);
            art = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1), Vector2.zero);
            opposing.Definition.art = art;
            session.ReportFateTurn(false); session.TryNextFateTurn(out _);
            Assert.IsTrue(session.TryPlayTactic(false, opposing));
            session.ReportFateTurn(true); session.TryNextFateTurn(out _);
            Call(popup, "ShowTacticResult", opposing);
            Call(popup, "OpenTacticDecision", true);
            var input = Get<Image>(popup, "tacticalCardInput");
            Assert.AreSame(art, input.sprite);
            var candidate = Tactic(owner, TacticEffect.AddSuccess, 2);
            Call(popup, "TryStageTactic", candidate);
            Assert.IsNull(input.sprite, "The committed opponent image cannot show through the staged card.");
            Assert.AreEqual((1, 2), Preview(popup));
            ReturnSlotted(popup);
            Assert.AreSame(art, input.sprite);
            Assert.AreEqual((1, 0), Preview(popup));
            Assert.AreEqual(1, session.AttackerModifier);
            Assert.IsTrue(owner.Tactics.Contains(candidate));
            Call(popup, "CloseTacticPresentation");
            Assert.IsNull(input.sprite);
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
