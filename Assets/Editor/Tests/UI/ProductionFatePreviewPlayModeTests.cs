#if UNITY_INCLUDE_TESTS && UNITY_6000_3_OR_NEWER
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.Cards;
using Game.Combat;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    // Enters Play Mode per test (~20 s each): full suite only (Tools/ai-verify/README.md, "Unity runs").
    [Category("Extended")]
    public class ProductionFatePreviewPlayModeTests
    {
        private GameObject _root;
        private BattleCombatantRowUI _row;
        private Sprite _hitSprite;
        private Sprite _baseSprite;

        private static void Field(object obj, string name, object value) =>
            obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
        private List<TokenSlotUI> Slots(string name) => (List<TokenSlotUI>)typeof(BattleCombatantRowUI)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_row);
        private static Image Face(TokenSlotUI slot) => slot.GetComponent<Image>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            yield return new EnterPlayMode();
            _root = new GameObject("production row", typeof(RectTransform));
            _root.SetActive(false); // immediate rolls let these tests inspect landed states
            _row = _root.AddComponent<BattleCombatantRowUI>();
            var container = new GameObject("tokens", typeof(RectTransform));
            container.transform.SetParent(_root.transform, false);
            var template = new GameObject("template", typeof(RectTransform), typeof(Image));
            template.transform.SetParent(_root.transform, false);
            var slot = template.AddComponent<TokenSlotUI>();
            Field(slot, "image", template.GetComponent<Image>());
            _hitSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1), Vector2.zero);
            _baseSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1), Vector2.zero);
            Field(slot, "hitSprite", _hitSprite);
            Field(slot, "baseSprite", _baseSprite);
            template.SetActive(false);
            Field(_row, "tokenContainer", container.transform);
            Field(_row, "tokenSlotPrefab", slot);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Object.Destroy(_root);
            Object.Destroy(_hitSprite);
            Object.Destroy(_baseSprite);
            yield return null;
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator RerollsKeepSettledSlotsAndRemoveOnlyLostPotential()
        {
            _row.SetTokens(new[] { true, true, false });
            _row.SetFatePreview(3);
            TokenSlotUI first = Slots("_tokenSlots")[0];
            TokenSlotUI ghost = Slots("_fatePreviewSlots")[0];
            Assert.That(Slots("_fatePreviewSlots"), Has.Count.EqualTo(2));
            Assert.That(Face(ghost).color.a, Is.EqualTo(0.35f).Within(0.001f));
            Assert.That(Face(ghost).raycastTarget, Is.False);

            _row.SetTokens(new[] { true, true, false }, 2, () => _row.SetFatePreview(2));
            Assert.That(Slots("_fatePreviewSlots"), Has.Count.EqualTo(1));
            _row.SetTokens(new[] { true, true, true }, 2, () => _row.SetFatePreview(1));
            Assert.That(Slots("_fatePreviewSlots"), Has.Count.EqualTo(1));
            Assert.That(Slots("_tokenSlots")[0], Is.SameAs(first));
            Assert.That(Slots("_fatePreviewSlots")[0], Is.SameAs(ghost));
            yield return null;
        }

        [UnityTest]
        public IEnumerator OverflowUsesAGhostAndReservesRemainingFateForItsMiss()
        {
            _row.SetTokens(new[] { true, true, true });
            _row.SetFatePreview(3);
            TokenSlotUI ghost = Slots("_fatePreviewSlots")[0];
            _row.AppendToken(false, () => _row.SetFatePreview(2));
            Assert.That(Slots("_tokenSlots"), Has.Count.EqualTo(4));
            Assert.That(Slots("_tokenSlots")[3], Is.SameAs(ghost));
            Assert.That(Face(ghost).color.a, Is.EqualTo(1f));
            Assert.That(Slots("_fatePreviewSlots"), Has.Count.EqualTo(1));
            Assert.That(ghost.transform.GetSiblingIndex(), Is.EqualTo(3));

            _row.Setup(new UnitData(), null, null);
            Assert.That(Slots("_fatePreviewSlots"), Is.Empty, "Combat popup reuse clears ghosts.");
            _row.SetTokens(new[] { true, false });
            Assert.That(Slots("_tokenSlots"), Has.Count.EqualTo(2));
            yield return null;
        }

        [UnityTest]
        public IEnumerator ActivatingAPreparedRowPreservesGhostAppearanceAndOverflowBecomesOpaque()
        {
            _row.SetTokens(new[] { true, true, true });
            _row.SetFatePreview(3);
            TokenSlotUI ghost = Slots("_fatePreviewSlots")[0];
            _root.SetActive(true); // deferred Awake must not overwrite the prepared presentation
            Assert.That(Face(ghost).color.a, Is.EqualTo(0.35f).Within(0.001f));
            Assert.That(Face(ghost).sprite, Is.SameAs(_hitSprite));
            Assert.That(Face(Slots("_tokenSlots")[0]).sprite, Is.SameAs(_hitSprite));
            bool done = false;
            _row.AppendToken(true, () => { _row.SetFatePreview(2); done = true; });
            Assert.That(Face(ghost).color.a, Is.EqualTo(1f));
            yield return new WaitForSeconds(TokenSlotUI.FateRerollDuration + 0.2f);
            Assert.That(done, Is.True);
            Assert.That(Face(ghost).sprite, Is.SameAs(_hitSprite));
            Assert.That(Slots("_fatePreviewSlots"), Has.Count.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator OneDecisionCannotSpendFateTwiceOrSpendAfterAccepting()
        {
            var popup = _root.AddComponent<BattleAttackPopupUI>();
            var hero = new UnitData { FateMax = 3, Fate = 3 };
            var tokens = new[] { true, true, true };
            var session = new ResearchProductionChallengeSession(hero, new CardDefinition { fate = 9 })
                { Tokens = tokens };
            Field(popup, "_rpHero", hero);
            Field(popup, "_rpTokens", tokens);
            Field(popup, "_rpTactics", session);
            Field(popup, "_awaitingHumanDecision", true);
            var spend = typeof(BattleAttackPopupUI).GetMethod("OnResearchProductionSpend",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var savedRandom = Random.state;
            try
            {
                spend.Invoke(popup, null);
                spend.Invoke(popup, null);
                Assert.That(hero.Fate, Is.EqualTo(2));
                Field(popup, "_humanSpent", false);
                Field(popup, "_humanDeclined", true);
                spend.Invoke(popup, null);
                Assert.That(hero.Fate, Is.EqualTo(2));
            }
            finally { Random.state = savedRandom; }
            yield return null;
        }

        [UnityTest]
        public IEnumerator HeadlessProductionUsesTheSameHeroStatAndPreservesFate()
        {
            var savedRandom = Random.state;
            try
            {
                var hero = new UnitData { FateMax = 3, Fate = 1 };
                Random.InitState(8127);
                int expected = ResearchProductionSystem.CountHits(ChallengeResolver.RollTokens(3));
                Random.InitState(8127);
                var result = ResearchProductionSystem.RollChallenge(hero, new CardDefinition { fate = 9 }, 0);
                Assert.That(result.Successes, Is.EqualTo(expected));
                Assert.That(result.FateSpent, Is.Zero);
                Assert.That(hero.Fate, Is.EqualTo(1));
            }
            finally { Random.state = savedRandom; }
            yield return null;
        }
    }
}
#endif
