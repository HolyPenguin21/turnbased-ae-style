#if UNITY_INCLUDE_TESTS
using System.Reflection;
using Game.UI;
using Game.Units;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class BattleAiThoughtsTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject root;
        private TMP_Text text;
        private BattleAiThoughtsUI ui;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Thought test canvas", typeof(RectTransform), typeof(Canvas));
            var child = new GameObject("Thought text", typeof(RectTransform), typeof(TextMeshProUGUI));
            child.transform.SetParent(root.transform, false);
            ((RectTransform)child.transform).sizeDelta = new Vector2(600f, 200f);
            text = child.GetComponent<TextMeshProUGUI>();
            text.font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            Assert.That(text.font, Is.Not.Null);
            ui = child.AddComponent<BattleAiThoughtsUI>();
            Set("thoughtText", text);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        [Test]
        public void BurstFinishesVisiblePhraseAndKeepsOnlyNewestPendingThought()
        {
            ui.Show(null, "First complete sentence.");
            Tick(.2f);
            Assert.That(text.maxVisibleCharacters, Is.GreaterThan(0).And.LessThan(text.textInfo.characterCount));
            ui.Show(null, "Second sentence.");
            ui.Show(null, "Latest sentence.");
            Tick(.6f);
            Assert.That(text.text, Is.EqualTo("First complete sentence."));
            AssertComplete();
            Tick(2.8f);
            Assert.That(text.text, Is.EqualTo("Latest sentence."));
        }

        [Test]
        public void LongFrameStillShowsTheCompletedPhraseBeforeReplacement()
        {
            ui.Show(null, "First sentence.");
            ui.Show(null, "Next sentence.");
            Tick(10f);
            Assert.That(text.text, Is.EqualTo("First sentence."));
            AssertComplete();
            Tick(0f);
            Assert.That(text.text, Is.EqualTo("Next sentence."));
        }

        [Test]
        public void HoldIsMeasuredFromTypingStart()
        {
            ui.Show(null, "First sentence.");
            ui.Show(null, "Next sentence.");
            Tick(.8f);
            Tick(2.6f);
            Assert.That(text.text, Is.EqualTo("First sentence."));
            Tick(.2f);
            Assert.That(text.text, Is.EqualTo("Next sentence."));
        }

        [Test]
        public void LatestEventMatchingVisibleTextCancelsAnOlderPendingEvent()
        {
            ui.Show(null, "Same event.");
            Tick(.2f);
            ui.Show(null, "Older pending event.");
            ui.Show(null, "Same event.");
            Tick(.6f);
            Tick(4f);
            Assert.That(text.text, Is.EqualTo("Same event."));
            AssertComplete();
        }

        [Test]
        public void RepeatedPendingThoughtDoesNotResetTheVisibleTimer()
        {
            ui.Show(null, "First sentence.");
            Tick(.8f);
            ui.Show(null, "Pending sentence.");
            Tick(2f);
            ui.Show(null, "Pending sentence.");
            Tick(.8f);
            Assert.That(text.text, Is.EqualTo("Pending sentence."));
        }

        [Test]
        public void TerminalThoughtIsImmediatelyCompleteAndDropsPendingReactions()
        {
            ui.Show(null, "Still attacking.");
            ui.Show(null, "A hit landed.");
            ui.Show(null, "Battle over.", immediate: true);
            Assert.That(text.text, Is.EqualTo("Battle over."));
            AssertComplete();
            Tick(10f);
            Assert.That(text.text, Is.EqualTo("Battle over."));
        }

        [Test]
        public void IdleNudgeCannotReplaceOrQueueBehindCombatThoughts()
        {
            ui.Show(null, "Combat reaction.");
            ui.Show(null, "Latest combat reaction.");
            ui.ShowIdle("Still waiting?");
            Tick(.8f);
            Tick(3f);
            Assert.That(text.text, Is.EqualTo("Latest combat reaction."));
            Tick(.8f);
            Tick(4f);
            Assert.That(text.text, Is.EqualTo("Latest combat reaction."));
        }

        [Test]
        public void IdleNudgeCanAppearAfterTheCombatHoldEnds()
        {
            ui.Show(null, "Combat reaction.");
            Tick(4f);
            ui.ShowIdle("Still waiting?");
            Assert.That(text.text, Is.EqualTo("Still waiting?"));
        }

        [Test]
        public void DisableClearsTypingAndPendingStateAndIgnoresHiddenEvents()
        {
            ui.Show(null, "Old battle.");
            ui.Show(null, "Old pending thought.");
            root.SetActive(false);
            Assert.That(text.text, Is.Empty);
            ui.Show(null, "Hidden event.");
            root.SetActive(true);
            ui.Show(null, "New battle.");
            Tick(.8f);
            Tick(5f);
            Assert.That(text.text, Is.EqualTo("New battle."));
            AssertComplete();
        }

        [Test]
        public void ZeroDurationCanDisplaySuccessiveThoughtsWithoutGettingStuck()
        {
            Set("typeInDuration", 0f);
            Set("minDisplaySeconds", 0f);
            ui.Show(null, "First.");
            ui.Show(null, "Second.");
            ui.Show(null, "Third.");
            Assert.That(text.text, Is.EqualTo("Third."));
            AssertComplete();
        }

        [Test]
        public void MissingTextReferenceDoesNotBlockLaterValidThoughts()
        {
            Set("thoughtText", null);
            ui.Show(null, "Discard this event.");
            Set("thoughtText", text);
            ui.Show(null, "Valid event.");
            Tick(.8f);
            Assert.That(text.text, Is.EqualTo("Valid event."));
            AssertComplete();
        }

        [Test]
        public void CommanderNameIsLiteralAndHeroLessThoughtHasNoPrefix()
        {
            ui.Show(new UnitData { Name = "<b>Commander</b>" }, "Ready.", immediate: true);
            Assert.That(text.text, Is.EqualTo("<b>Commander</b>: Ready."));
            Assert.That(text.richText, Is.False);
            AssertComplete();
            ui.Show(null, "No commander.", immediate: true);
            Assert.That(text.text, Is.EqualTo("No commander."));
        }

        [Test]
        public void ClearRestartsDisplayAndWhitespaceDoesNotCreateThoughts()
        {
            ui.Show(null, "Old.");
            ui.Clear();
            ui.Show(null, "  ");
            ui.ShowIdle("\n");
            Assert.That(text.text, Is.Empty);
            ui.Show(null, "New.");
            Tick(.8f);
            Assert.That(text.text, Is.EqualTo("New."));
            AssertComplete();
        }

        private void Set(string name, object value) => typeof(BattleAiThoughtsUI).GetField(name, Private).SetValue(ui, value);
        private void Tick(float seconds) => typeof(BattleAiThoughtsUI).GetMethod("AdvanceDisplay", Private).Invoke(ui, new object[] { seconds });
        private void AssertComplete() => Assert.That(text.maxVisibleCharacters, Is.GreaterThanOrEqualTo(text.textInfo.characterCount));
    }
}
#endif
