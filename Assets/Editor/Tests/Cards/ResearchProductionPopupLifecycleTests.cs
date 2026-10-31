#if UNITY_INCLUDE_TESTS
using System;
using System.Reflection;
using Game.Cards;
using Game.Combat;
using Game.Map;
using Game.Players;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    [Category("UnityNative")]
    public class ResearchProductionPopupLifecycleTests
    {
        private GameObject _root, _panel;
        private BattleAttackPopupUI _popup;
        [SetUp] public void SetUp()
        {
            _root = new GameObject("R/P popup lifecycle");
            _popup = _root.AddComponent<BattleAttackPopupUI>();
            _panel = new GameObject("panel"); _panel.transform.SetParent(_root.transform);
            Set("panelRoot", _panel);
        }
        [TearDown] public void TearDown() { UnityEngine.Object.DestroyImmediate(_root); }
        private void Set(string field, object value) => typeof(BattleAttackPopupUI)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_popup, value);
        private object Get(string field) => typeof(BattleAttackPopupUI)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_popup);
        private void Invoke(string method) => typeof(BattleAttackPopupUI)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_popup, null);
        private ResearchProductionChallengeSession Install(Action<bool> callback, bool accepted = false)
        {
            var hero = new UnitData { Fate = 2, FateMax = 1 };
            var session = new ResearchProductionChallengeSession(hero, new CardDefinition { fate = 1 });
            session.Tokens = new[] { true };
            if (accepted) session.Accept();
            Set("_rpTactics", session); Set("_rpHero", hero); Set("_rpActive", true);
            Set("_onResearchProductionResolved", callback);
            Set("_rpResultSuccess", accepted);
            var kind = typeof(BattleAttackPopupUI).GetField("_kind", BindingFlags.NonPublic | BindingFlags.Instance);
            kind.SetValue(_popup, Enum.Parse(kind.FieldType, "ResearchProduction"));
            var phase = typeof(BattleAttackPopupUI).GetField("_phase", BindingFlags.NonPublic | BindingFlags.Instance);
            phase.SetValue(_popup, Enum.Parse(phase.FieldType, "Resolved"));
            Set("_okAlreadyHandled", false); _panel.SetActive(true);
            return session;
        }
        [Test] public void OkClosesOldPresentationBeforeCallbackCanOpenReplacement()
        {
            ResearchProductionChallengeSession replacement = null;
            int completions = 0;
            Install(success => { completions++; Assert.That(success, Is.True);
                Assert.That(_panel.activeSelf, Is.False);
                replacement = Install(_ => completions++); }, accepted: true);
            Invoke("OnOkClicked");
            Assert.That(Get("_rpTactics"), Is.SameAs(replacement));
            Assert.That(_panel.activeSelf, Is.True);
            Assert.That(completions, Is.EqualTo(1));
        }
        [Test] public void ThrowingCompletionStillDetachesAndClosesExactlyOnce()
        {
            int completions = 0;
            var session = Install(_ => { completions++; throw new ApplicationException("Caller failed"); }, accepted: true);
            Assert.Throws<TargetInvocationException>(() => Invoke("OnOkClicked"));
            Assert.That(Get("_rpActive"), Is.False); Assert.That(Get("_rpTactics"), Is.Null);
            Assert.That(_panel.activeSelf, Is.False);
            _popup.Hide();
            Assert.That(completions, Is.EqualTo(1));
        }
        [Test] public void HideCancelsEvenIfPanelAlreadyInactiveAndDoesNotCloseReplacement()
        {
            int completions = 0;
            var original = Install(success => { Assert.That(success, Is.False); completions++; Install(_ => { }); });
            _panel.SetActive(false);
            _popup.Hide();
            Assert.That(original.IsCancelled, Is.True);
            Assert.That(completions, Is.EqualTo(1)); Assert.That(_panel.activeSelf, Is.True);
        }
        [Test] public void ReentrantBeginKeepsNestedAttemptAndRejectsOuterAttemptExactlyOnce()
        {
            int abandoned = 0, rejected = 0, nestedCompletions = 0;
            var hero = new UnitData { FateMax = 1, Fate = 0 };
            var card = new CardDefinition { fate = 1 };
            var nested = new ResearchProductionChallengeSession(hero, card);
            nested.Tokens = new[] { false };
            var outer = new ResearchProductionChallengeSession(hero, card);
            Install(success =>
            {
                Assert.That(success, Is.False); abandoned++;
                _popup.BeginResearchProduction(hero, card, ResearchProductionMode.Research,
                    null, null, _ => nestedCompletions++, nested);
            });
            _popup.BeginResearchProduction(hero, card, ResearchProductionMode.Research,
                null, null, success => { Assert.That(success, Is.False); rejected++; }, outer);
            Assert.That(abandoned, Is.EqualTo(1)); Assert.That(rejected, Is.EqualTo(1));
            Assert.That(nestedCompletions, Is.Zero);
            Assert.That(Get("_rpTactics"), Is.SameAs(nested));
            Assert.That(nested.IsTerminal, Is.False); Assert.That(outer.IsCancelled, Is.True);
            Assert.That(_panel.activeSelf, Is.True);
        }
        [TestCase("ordinary")] [TestCase("capture")] [TestCase("announcement")]
        public void ReentrantCleanupSupersedesOuterNonProductionPresentation(string entry)
        {
            var hero = new UnitData { FateMax = 1, Fate = 0 };
            var card = new CardDefinition { fate = 1 };
            var nested = new ResearchProductionChallengeSession(hero, card);
            nested.Tokens = new[] { false };
            Install(_ => _popup.BeginResearchProduction(hero, card, ResearchProductionMode.Research,
                null, null, _ => { }, nested));
            if (entry == "ordinary") _popup.Begin(hero, null, hero, null, null, null, null);
            else if (entry == "capture") _popup.BeginCaptureKill(null, hero, null, null, null);
            else _popup.ShowSecondaryAttackResult(hero, hero, "old", false, null);
            Assert.That(Get("_rpTactics"), Is.SameAs(nested));
            Assert.That(Get("_kind").ToString(), Is.EqualTo("ResearchProduction"));
            Assert.That(_panel.activeSelf, Is.True);
        }
        [Test] public void CaptureKindAndCallbackExistBeforeAutomaticRollStarts()
        {
            var hunter = new UnitData { IsHero = true, Fate = 0 };
            var target = new UnitData { IsHero = true, Fate = 0 };
            var army = new ArmyData(); army.Members.Add(hunter);
            Action<Game.Combat.BattleChallengeRollResult> callback = _ => { };
            _popup.BeginCaptureKill(army, target, null, null, callback, automateHumanSides: true);
            Assert.That(Get("_kind").ToString(), Is.EqualTo("CaptureKill"));
            Assert.That(Get("_onCaptureKillResolved"), Is.SameAs(callback));
            Assert.That(((Game.Combat.BattleChallengeSession)Get("_challengeSession")).Mode,
                Is.EqualTo(Game.Combat.BattleChallengeMode.CaptureKill));
        }
        [Test] public void IteratorExceptionCancelsAndReleasesAttemptOnce()
        {
            int completions = 0;
            Install(success => { Assert.That(success, Is.False); completions++; });
            var session = new ResearchProductionChallengeSession(new UnitData { Fate = 3, FateMax = 1 },
                new CardDefinition { fate = 2 }, n => throw new ApplicationException("Injected initial roll failure"));
            Set("_rpTactics", session);
            var routine = (System.Collections.IEnumerator)typeof(BattleAttackPopupUI)
                .GetMethod("RunResearchProductionSafely", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(_popup, new object[] { session });
            Assert.Throws<ApplicationException>(() => routine.MoveNext());
            Assert.That(session.IsCancelled, Is.True);
            Assert.That(completions, Is.EqualTo(1)); Assert.That(_panel.activeSelf, Is.False);
            _popup.Hide(); Assert.That(completions, Is.EqualTo(1));
        }
        [Test] public void OldIteratorCannotAdvanceReplacementPresentation()
        {
            var session = Install(_ => { });
            var routine = (System.Collections.IEnumerator)typeof(BattleAttackPopupUI)
                .GetMethod("RunResearchProductionSafely", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(_popup, new object[] { session });
            Assert.That(routine.MoveNext(), Is.True);
            var replacement = Install(_ => { });
            Assert.That(routine.MoveNext(), Is.False);
            Assert.That(Get("_rpTactics"), Is.SameAs(replacement));
            Assert.That(replacement.IsTerminal, Is.False);
        }
        [Test] public void DisableCancelsAndReleasesCallbackOnlyOnce()
        {
            int completions = 0;
            var session = Install(success => { Assert.That(success, Is.False); completions++; });
            _root.SetActive(false);
            Assert.That(session.IsCancelled, Is.True); Assert.That(completions, Is.EqualTo(1));
            _popup.Hide(); Assert.That(completions, Is.EqualTo(1));
        }
        [TestCase(false)] [TestCase(true)]
        public void CaptureCallbackCannotMutateAReplacedEncounter(bool replaceState)
        {
            var screen = _root.AddComponent<BattleScreenUI>();
            var owner = new PlayerSetupData { IsHuman = true };
            var hunter = new ArmyData { Owner = owner };
            hunter.Members.Add(new UnitData { Owner = owner, HitPointsCurrent = 5 });
            var target = new ArmyData { Owner = new PlayerSetupData { IsHuman = true } };
            var hero = new UnitData { Owner = target.Owner, IsHero = true, HitPointsCurrent = 5 };
            target.Members.Add(hero);
            var state = new BattleState(default, hunter, target, new BattleGrid(), 1);
            var engine = new BattleEngine(state, AbilityMagnitudes.Default);
            var sequence = BattleEngine.CreateTargetOnlyCaptureKillSequence(state, hunter, target);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(BattleScreenUI).GetField("attackPopup", flags).SetValue(screen, _popup);
            typeof(BattleScreenUI).GetField("_battleState", flags).SetValue(screen, state);
            typeof(BattleScreenUI).GetField("_battleEngine", flags).SetValue(screen, engine);
            int completed = 0;
            typeof(BattleScreenUI).GetMethod("RunCaptureKillSequence", flags).Invoke(screen,
                new object[] { sequence, (Action)(() => completed++), true });
            var callback = (Action<BattleChallengeRollResult>)Get("_onCaptureKillResolved");
            if (replaceState) typeof(BattleScreenUI).GetField("_battleState", flags).SetValue(screen,
                new BattleState(default, hunter, target, new BattleGrid(), 2));
            else typeof(BattleScreenUI).GetField("_attackPresentationGeneration", flags).SetValue(screen, 1);
            callback(default);
            Assert.That(sequence.Count, Is.EqualTo(1)); Assert.That(target.Members, Does.Contain(hero));
            Assert.That(hero.HitPointsCurrent, Is.EqualTo(5)); Assert.That(completed, Is.Zero);
        }
    }
}
#endif
