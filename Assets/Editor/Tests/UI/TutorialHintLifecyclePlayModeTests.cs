#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Cards;
using Game.Core;
using Game.Map;
using Game.Players;
using Game.Turns;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    public sealed class TutorialHintLifecyclePlayModeTests
    {
        private const string EnabledKey = "Rustmarch.Tutorial.Enabled", SeenKey = "Rustmarch.Tutorial.Seen.v1";
        private GameObject host, canvasObject;
        private TutorialHintController controller;
        private GameTurnController turn;
        private CardHandUI hand;
        private PlayerRoot root;
        private PlayerSetupData human;
        private Button target;
        private List<PlayerSetupData> previousPlayers;
        private bool hadEnabled, hadSeen;
        private int oldEnabled, oldSeen;

        private static void Field(object owner, string name, object value) =>
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, value);
        private static object Read(object owner, string name) =>
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
        private static object Invoke(object owner, string name, params object[] args) =>
            owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(owner, args);
        private static void Static(string name, params object[] args) => typeof(TutorialHintController)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
        private TutorialHintView View => (TutorialHintView)Read(controller, "view");
        private void Tick() => Invoke(controller, "LateUpdate");

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return new EnterPlayMode();
            hadEnabled = PlayerPrefs.HasKey(EnabledKey); hadSeen = PlayerPrefs.HasKey(SeenKey);
            oldEnabled = PlayerPrefs.GetInt(EnabledKey); oldSeen = PlayerPrefs.GetInt(SeenKey);
            PlayerPrefs.SetInt(EnabledKey, 1); PlayerPrefs.DeleteKey(SeenKey);
            previousPlayers = GameSession.Players;
            human = new PlayerSetupData { IsHuman = true };
            GameSession.Players = new List<PlayerSetupData> { human };
            root = PlayerRoot.Create(human, "tutorial lifecycle resources");
            PlayerRootRegistry.Register(human, root);
            root.ActionPoints = 0;
            // Avoid starting/dealing a real match. Exercise the installed presentation layer
            // with explicit committed state; the UI targets still live on an active Canvas.
            host = new GameObject("tutorial lifecycle host"); host.SetActive(false);
            turn = host.AddComponent<GameTurnController>();
            typeof(GameTurnController).GetProperty("CurrentPlayer").SetValue(turn, human);
            typeof(GameTurnController).GetProperty("TurnConfirmed").SetValue(turn, true);
            hand = host.AddComponent<CardHandUI>();
            Field(hand, "turnController", turn);
            Field(hand, "_showingTactics", true);
            canvasObject = new GameObject("tutorial test canvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var button = new GameObject("End turn target", typeof(RectTransform), typeof(Image), typeof(Button));
            button.transform.SetParent(canvasObject.transform, false);
            ((RectTransform)button.transform).sizeDelta = new Vector2(120, 36);
            target = button.GetComponent<Button>();
            Field(turn, "endTurnButton", target);
            Canvas.ForceUpdateCanvases();
            Static("Install", turn, hand, null);
            controller = turn.GetComponent<TutorialHintController>();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // The deliberately inactive fixture never ran OnEnable, so Unity need not invoke
            // OnDestroy for it. Release Install's subscriptions explicitly before destroying it.
            if (controller != null) Invoke(controller, "OnDestroy");
            Object.Destroy(host); Object.Destroy(canvasObject);
            PlayerRootRegistry.Clear();
            if (root != null) Object.Destroy(root.gameObject);
            GameSession.Players = previousPlayers;
            if (hadEnabled) PlayerPrefs.SetInt(EnabledKey, oldEnabled); else PlayerPrefs.DeleteKey(EnabledKey);
            if (hadSeen) PlayerPrefs.SetInt(SeenKey, oldSeen); else PlayerPrefs.DeleteKey(SeenKey);
            PlayerPrefs.Save();
            yield return null;
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator TacticalHandDoesNotSuppressAnAvailableEndTurnHint()
        {
            Tick();
            Assert.That(View, Is.Not.Null);
            Assert.That(View.name, Does.StartWith("EndTurnHint"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator HintHasItsOwnRaycasterAboveTheRaisedTacticalHand()
        {
            var raised = target.gameObject.AddComponent<Canvas>();
            raised.overrideSorting = true;
            raised.sortingOrder = 25000;
            target.gameObject.AddComponent<GraphicRaycaster>();
            Tick();
            var hintCanvas = View.GetComponent<Canvas>();
            Assert.That(hintCanvas, Is.Not.Null);
            Assert.That(hintCanvas.overrideSorting, Is.True);
            Assert.That(hintCanvas.sortingLayerID, Is.EqualTo(raised.sortingLayerID));
            Assert.That(hintCanvas.sortingOrder, Is.GreaterThan(25001));
            Assert.That(View.GetComponent<GraphicRaycaster>(), Is.Not.Null);
            Assert.That(View.GetComponent<Graphic>(), Is.Null, "No full-screen raycast surface.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator TacticalDropTargetCanBeTaughtBeforeItsClickButtonIsEnabled()
        {
            target.interactable = false;
            bool decisionActive = true;
            Static("OfferDropTarget", TutorialHintId.Tactic, (RectTransform)target.transform,
                (Func<bool>)(() => decisionActive));
            Tick();
            Assert.That(View.name, Does.StartWith("TacticHint"));
            decisionActive = false; Tick();
            Assert.That(View, Is.Null, "A closed challenge decision must not leave a stale hint.");
            Assert.That(TutorialHintProgress.HasSeen(TutorialHintId.Tactic), Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AnActionLearnsItsTopicEvenBeforeTheHintIsShown()
        {
            Static("Learned", TutorialHintId.EditTactic);
            Assert.That(TutorialHintProgress.HasSeen(TutorialHintId.EditTactic), Is.True);
            Static("OfferDropTarget", TutorialHintId.EditTactic, (RectTransform)target.transform,
                (Func<bool>)(() => true));
            Tick();
            Assert.That(View.name, Does.StartWith("EndTurnHint"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator StagedTacticHintIsInvalidatedWhenTheHumanDecisionEnds()
        {
            var popup = host.AddComponent<BattleAttackPopupUI>();
            var hero = new UnitData { Owner = human, IsHero = true };
            Field(popup, "panelRoot", canvasObject);
            Field(popup, "tacticalCardInput", target.GetComponent<Image>());
            Field(popup, "_rpHero", hero); Field(popup, "_attacker", hero);
            Field(popup, "_awaitingHumanDecision", true);
            Field(popup, "_pendingTactic", new CardData(new CardDefinition()));
            var handObject = new GameObject("challenge hand", typeof(RectTransform));
            handObject.transform.SetParent(canvasObject.transform, false);
            var battleHand = handObject.AddComponent<BattleHandUI>();
            Field(battleHand, "_inventory", new TacticHand());
            Field(popup, "_tacticHand", battleHand);
            var phase = typeof(BattleAttackPopupUI).GetField("_phase", BindingFlags.Instance | BindingFlags.NonPublic);
            phase.SetValue(popup, Enum.Parse(phase.FieldType, "InProgress"));
            var kind = typeof(BattleAttackPopupUI).GetField("_kind", BindingFlags.Instance | BindingFlags.NonPublic);
            kind.SetValue(popup, Enum.Parse(kind.FieldType, "ResearchProduction"));
            target.interactable = false;
            Invoke(popup, "OfferTacticTutorials"); Tick();
            Assert.That(View.name, Does.StartWith("EditTacticHint"));
            var slotObject = new GameObject("slotted tactic", typeof(RectTransform), typeof(Image), typeof(CardUI));
            slotObject.transform.SetParent(canvasObject.transform, false);
            var slotCard = slotObject.GetComponent<CardUI>();
            Field(popup, "_slottedTacticView", slotCard);
            typeof(CardUI).GetProperty("IsDragging").SetValue(slotCard, true);
            Tick(); Assert.That(View, Is.Null, "Hints must not interrupt a drag from the slot.");
            typeof(CardUI).GetProperty("IsDragging").SetValue(slotCard, false);
            Tick(); Assert.That(View.name, Does.StartWith("EditTacticHint"));
            Field(popup, "_mapHand", hand); // Main map hand is inactive while the battle screen is open.
            Tick(); Assert.That(View, Is.Null, "An unavailable hand cannot receive a card returned from the slot.");
            Field(popup, "_mapHand", null);
            Tick(); Assert.That(View.name, Does.StartWith("EditTacticHint"));
            Field(popup, "_pendingTacticDefender", true);
            Tick(); Assert.That(View, Is.Null, "A tactic from the other side's decision cannot be edited.");
            Field(popup, "_pendingTacticDefender", false);
            Tick(); Assert.That(View.name, Does.StartWith("EditTacticHint"));
            Field(popup, "_humanDeclined", true); Tick();
            Assert.That(View, Is.Null);
            Assert.That(TutorialHintProgress.HasSeen(TutorialHintId.EditTactic), Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator CompletingAHintPausesBeforeShowingTheNextTopic()
        {
            Static("Offer", TutorialHintId.Repair, (RectTransform)target.transform, (Func<bool>)(() => true));
            Tick(); Assert.That(View.name, Does.StartWith("RepairHint"));
            Static("Learned", TutorialHintId.Repair); Tick();
            Assert.That(View, Is.Null);
            Assert.That(TutorialHintProgress.HasSeen(TutorialHintId.EndTurn), Is.False);
            Field(controller, "nextHintAt", Time.unscaledTime - 1f); Tick();
            Assert.That(View.name, Does.StartWith("EndTurnHint"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator DisabledControlsAreNotHighlightedAndRecoverWithoutAResourceChange()
        {
            target.interactable = false; Tick();
            Assert.That(View, Is.Null);
            target.interactable = true; Tick();
            Assert.That(View, Is.Not.Null);
            yield return null;
        }

        [UnityTest]
        public IEnumerator DisablingHintsHidesImmediatelyAndReenablingRetainsUnfinishedProgress()
        {
            Tick();
            var oldView = View;
            TutorialHintProgress.Enabled = false;
            Assert.That(oldView.gameObject.activeSelf, Is.False);
            Assert.That(View, Is.Null);
            Assert.That(TutorialHintProgress.HasSeen(TutorialHintId.EndTurn), Is.False);
            TutorialHintProgress.Enabled = true; Tick();
            Assert.That(View, Is.Not.Null);
            Static("Complete", TutorialHintId.EndTurn); Tick();
            Assert.That(View, Is.Null);
            Assert.That(TutorialHintProgress.HasSeen(TutorialHintId.EndTurn), Is.True);
            yield return null;
        }

        [UnityTest]
        public IEnumerator QueueRevalidatesFateAndRestoresTheStillRelevantMapHint()
        {
            bool relevant = false;
            Static("Offer", TutorialHintId.Fate, (RectTransform)target.transform, (Func<bool>)(() => relevant));
            Tick(); Assert.That(View.name, Does.StartWith("EndTurnHint"));
            relevant = true; Tick(); Assert.That(View.name, Does.StartWith("FateHint"));
            relevant = false; Tick(); Assert.That(View.name, Does.StartWith("EndTurnHint"));
            Assert.That(TutorialHintProgress.HasSeen(TutorialHintId.Fate), Is.False);
            yield return null;
        }

        [UnityTest]
        public IEnumerator FateBecomingAvailableInsideAnExistingDecisionOffersTheHint()
        {
            var popup = host.AddComponent<BattleAttackPopupUI>();
            var row = host.AddComponent<BattleCombatantRowUI>();
            Field(row, "spendButton", target);
            var hero = new UnitData { Owner = human, IsHero = true, Fate = 1 };
            var session = new ResearchProductionChallengeSession(hero, new CardDefinition { fate = 2 });
            Field(popup, "panelRoot", canvasObject);
            Field(popup, "_rpHero", hero); Field(popup, "_attacker", hero);
            Field(popup, "_rpTactics", session); Field(popup, "_rpTokens", new[] { true, true });
            Field(popup, "attackerRow", row); Field(popup, "_awaitingHumanDecision", true);
            var kind = typeof(BattleAttackPopupUI).GetField("_kind", BindingFlags.Instance | BindingFlags.NonPublic);
            kind.SetValue(popup, Enum.Parse(kind.FieldType, "ResearchProduction"));
            Invoke(popup, "RefreshTacticSpendButtons"); Tick();
            Assert.That(target.interactable, Is.False);
            Assert.That(View, Is.Null);
            // Staging/replacing/returning a tactic uses this same refresh after changing the
            // effective difficulty. No second roll/decision is opened here.
            typeof(ResearchProductionChallengeSession).GetProperty("DifficultyModifier").SetValue(session, 1);
            Invoke(popup, "RefreshTacticSpendButtons"); Tick();
            Assert.That(target.interactable, Is.True);
            Assert.That(View.name, Does.StartWith("FateHint"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator WrappedBodiesFitTheirAuthoredPanelsAndHintsStayInsideTheCanvas()
        {
            var rect = (RectTransform)target.transform;
            rect.anchorMin = rect.anchorMax = Vector2.one;
            rect.anchoredPosition = new Vector2(-20, -20);
            foreach (TutorialHintId id in Enum.GetValues(typeof(TutorialHintId)))
            {
                var asset = GameAssetCatalog.Load<GameObject>("UI/TutorialHints/" + id + "Hint");
                var hint = Object.Instantiate(asset, canvasObject.transform).GetComponent<TutorialHintView>();
                Canvas.ForceUpdateCanvases(); hint.Follow(rect);
                var body = hint.transform.Find("Bubble/Body").GetComponent<TMP_Text>();
                var preferred = body.GetPreferredValues(body.text, body.rectTransform.rect.width, float.PositiveInfinity);
                Assert.That(preferred.y, Is.LessThanOrEqualTo(body.rectTransform.rect.height + 1f), id.ToString());
                var bubble = (RectTransform)hint.transform.Find("Bubble");
                var bounds = ((RectTransform)hint.transform).rect;
                var half = Vector2.Scale(bubble.rect.size, bubble.localScale) * 0.5f;
                Assert.That(bubble.anchoredPosition.x + half.x, Is.LessThanOrEqualTo(bounds.xMax));
                Assert.That(bubble.anchoredPosition.y + half.y, Is.LessThanOrEqualTo(bounds.yMax));
                hint.gameObject.SetActive(false); Object.Destroy(hint.gameObject);
            }
            yield return null;
        }
    }
}
#endif
