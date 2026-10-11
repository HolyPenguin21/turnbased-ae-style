#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.UI;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class ArmyRadioDispatchTests
    {
        private ArmyRadioDispatch _dispatch;
        private PlayerSetupData _viewer;
        private ArmyData _army;
        [SetUp] public void Setup()
        { _dispatch = new ArmyRadioDispatch(); _viewer = new PlayerSetupData(); _army = new ArmyData { Owner = _viewer }; }
        private ArmyRadioCandidate Candidate(ArmyRadioCategory category, float now = 0f, float ttl = 10f,
            object subject = null, ArmyData speaker = null) =>
            new ArmyRadioCandidate(category, speaker ?? _army, _viewer, subject ?? new object(), new HexCoord(0, 0), now, ttl);
        private bool Take(float now, out ArmyRadioCandidate candidate, Func<ArmyRadioCandidate, bool> valid = null,
            Func<ArmyRadioCandidate, bool> ready = null) =>
            _dispatch.TryTake(now, -1, 25f, valid ?? (_ => true), ready ?? (_ => true), out candidate);

        [Test] public void PriorityAndFreshness_ChooseImportantThenNewestWithoutSorting()
        {
            var ordinary = Candidate(ArmyRadioCategory.BaseFounded, 3f);
            var old = Candidate(ArmyRadioCategory.BattleWon, 1f);
            var fresh = Candidate(ArmyRadioCategory.Retreat, 2f);
            foreach (var item in new[] { ordinary, old, fresh }) _dispatch.Add(item, 3f, 24);
            Assert.That(Take(3f, out var selected), Is.True); Assert.That(selected, Is.SameAs(fresh));
            Assert.That(Take(3f, out selected), Is.True); Assert.That(selected, Is.SameAs(old));
        }
        [Test] public void Ttl_ExpiresAtBoundaryAndBehindModalWithoutBurst()
        {
            for (int i = 0; i < 20; i++) _dispatch.Add(Candidate(ArmyRadioCategory.BaseFounded, 0f, 6f), 0f, 24);
            Assert.That(Take(6f, out _), Is.False); Assert.That(_dispatch.PendingCount, Is.Zero);
        }
        [Test] public void Deduplication_CoversPendingShownAndProbabilityRefusal()
        {
            var first = Candidate(ArmyRadioCategory.BattleWon);
            Assert.That(_dispatch.Add(first, 0f, 24), Is.True);
            Assert.That(_dispatch.Add(first, 0f, 24), Is.False);
            Assert.That(Take(1f, out var selected), Is.True);
            Assert.That(ArmyRadioDispatch.Accept(.99, .6f, 0), Is.False);
            Assert.That(_dispatch.Add(first, 2f, 24), Is.False);
            Assert.That(Take(2f, out _), Is.False, "A refusal cannot be rolled again next frame.");
            Assert.That(selected, Is.SameAs(first));
        }
        [Test] public void OneEncounterOutcome_DoesNotRetryAsAnotherCategory()
        {
            object encounter = new object();
            var loss = Candidate(ArmyRadioCategory.HeavyLosses, subject: encounter);
            _dispatch.Add(loss, 0f, 24);
            Assert.That(_dispatch.Add(Candidate(ArmyRadioCategory.BattleWon, subject: encounter), 0f, 24), Is.False);
            Take(1f, out _);
            Assert.That(_dispatch.Add(Candidate(ArmyRadioCategory.Retreat, subject: encounter), 2f, 24), Is.False);
        }
        [TestCase(.59, .6f, 0, true)]
        [TestCase(.6001, .6f, 0, false)]
        [TestCase(.39, .6f, 2, true)]
        [TestCase(.41, .6f, 2, false)]
        [TestCase(0d, 0f, 0, false)]
        [TestCase(.99d, 1f, 0, true)]
        public void Probability_UsesOneControlledRollAndRecentDensity(double roll, float chance, int density, bool expected)
            => Assert.That(ArmyRadioDispatch.Accept(roll, chance, density), Is.EqualTo(expected));
        [Test] public void Probability_ExactRepresentableThresholdIsExcluded()
            => Assert.That(ArmyRadioDispatch.Accept((double).6f, .6f, 0), Is.False);
        [Test] public void EqualPriorityAndFreshness_RotateAwayFromPreviousSpeaker()
        {
            var other = new ArmyData { Owner = _viewer };
            var previous = Candidate(ArmyRadioCategory.BattleWon);
            var alternative = Candidate(ArmyRadioCategory.BattleWon, speaker: other);
            _dispatch.Add(previous, 0f, 24); _dispatch.Add(alternative, 0f, 24);
            Assert.That(_dispatch.TryTake(1f, _army.Id, 25f, _ => true, _ => true, out var selected), Is.True);
            Assert.That(selected, Is.SameAs(alternative));
        }
        [Test] public void LoweredCapacity_ImmediatelyTrimsEvenWhenIncomingPriorityIsWorse()
        {
            for (int i = 0; i < 8; i++) _dispatch.Add(Candidate(ArmyRadioCategory.BattleWon), 0f, 8);
            Assert.That(_dispatch.Add(Candidate(ArmyRadioCategory.EquipmentReceived), 0f, 1), Is.False);
            Assert.That(_dispatch.PendingCount, Is.EqualTo(1));
        }
        [Test] public void Probability_SeededReplayIsDeterministic()
        {
            var a = new Random(719); var b = new Random(719); int shown = 0;
            for (int i = 0; i < 1000; i++)
            {
                bool decision = ArmyRadioDispatch.Accept(a.NextDouble(), .6f, 0);
                Assert.That(decision, Is.EqualTo(ArmyRadioDispatch.Accept(b.NextDouble(), .6f, 0)));
                if (decision) shown++;
            }
            Assert.That(shown, Is.InRange(500, 700));
        }
        [Test] public void GlobalCadence_ReservesSilenceAndCapsBothDialogueLines()
        {
            var clock = new ArmyRadioCadence(0f);
            clock.Quiet(2f, 3f);
            Assert.That(clock.ChannelReady(4.99f, 3), Is.False);
            Assert.That(clock.ChannelReady(5f, 3), Is.True);
            clock.Spoke(1, 5f, 3f); clock.Spoke(2, 7f, 3f);
            Assert.That(clock.DensityReady(10f, 3, 2), Is.False);
            clock.Spoke(3, 10f, 3f);
            Assert.That(clock.ChannelReady(20f, 3), Is.False);
            Assert.That(clock.ChannelReady(65f, 3), Is.True);
        }
        [Test] public void PerArmyCooldown_DoesNotBlockAnotherEligibleSpeaker()
        {
            var other = new ArmyData { Owner = _viewer };
            var clock = new ArmyRadioCadence(0f); clock.Spoke(_army.Id, 2f, 3f);
            _dispatch.Add(Candidate(ArmyRadioCategory.Retreat, 3f), 3f, 24);
            var alternative = Candidate(ArmyRadioCategory.BaseFounded, 3f, speaker: other);
            _dispatch.Add(alternative, 3f, 24);
            Assert.That(Take(5f, out var best, ready: item => clock.ArmyReady(item.Speaker.Id, 5f, 45f)), Is.True);
            Assert.That(best, Is.SameAs(alternative));
        }
        [Test] public void ViewerSpeakerAndEventInvalidation_RemoveBeforeRanking()
        {
            var invalid = Candidate(ArmyRadioCategory.BattleWon);
            var valid = Candidate(ArmyRadioCategory.BaseFounded);
            _dispatch.Add(invalid, 0f, 24); _dispatch.Add(valid, 0f, 24);
            Assert.That(Take(1f, out var best, item => item == valid), Is.True);
            Assert.That(best, Is.SameAs(valid)); Assert.That(_dispatch.PendingCount, Is.Zero);
        }
        [Test] public void AtmosphericFallback_IsNeverQueuedAndHasLowestPriority()
        {
            Assert.That(_dispatch.Add(Candidate(ArmyRadioCategory.Atmosphere), 0f, 24), Is.False);
            Assert.That(Take(1f, out _), Is.False);
            var state = Candidate(ArmyRadioCategory.Contact);
            var flavor = Candidate(ArmyRadioCategory.Atmosphere);
            Assert.That(ArmyRadioDispatch.Better(state, flavor, -1), Is.True);
            Assert.That(ArmyRadioDispatch.Chance(flavor.Category, new ArmyRadioChatterSettings()), Is.EqualTo(.1f));
        }
        [Test] public void Overflow_DropsLeastImportantOldestAndKeepsRefusalConsumed()
        {
            var older = Candidate(ArmyRadioCategory.BaseFounded);
            var newer = Candidate(ArmyRadioCategory.ReinforcementsArrived, 1f);
            _dispatch.Add(older, 1f, 2); _dispatch.Add(newer, 1f, 2);
            var critical = Candidate(ArmyRadioCategory.Retreat, 1f);
            Assert.That(_dispatch.Add(critical, 1f, 2), Is.True);
            Assert.That(_dispatch.PendingCount, Is.EqualTo(2));
            Assert.That(_dispatch.Add(older, 1f, 2), Is.False);
            Assert.That(Take(2f, out var best), Is.True); Assert.That(best, Is.SameAs(critical));
        }
        [Test] public void HistoryAndCategoryCooldown_AreSharedBoundedAndResetForNewViewer()
        {
            _dispatch.Shown(ArmyRadioCategory.BattleWon, "first", 0f, 2);
            _dispatch.Shown(ArmyRadioCategory.Atmosphere, "second", 1f, 2);
            _dispatch.Shown(ArmyRadioCategory.Contact, "third", 2f, 2);
            Assert.That(_dispatch.Recent("first"), Is.False); Assert.That(_dispatch.Recent("second"), Is.True);
            Assert.That(_dispatch.CategoryReady(ArmyRadioCategory.BattleWon, 24.99f, 25f), Is.False);
            Assert.That(_dispatch.CategoryReady(ArmyRadioCategory.BattleWon, 25f, 25f), Is.True);
            _dispatch.Clear(); Assert.That(_dispatch.PhraseHistoryCount, Is.Zero);
            Assert.That(_dispatch.CategoryReady(ArmyRadioCategory.BattleWon, 1f, 25f), Is.True);
        }
        [Test] public void LongRunningAndHighDensity_AllBuffersRemainBoundedWithoutBacklog()
        {
            var clock = new ArmyRadioCadence(0f);
            for (int i = 0; i < 10000; i++)
            {
                float now = i * .05f;
                _dispatch.Add(Candidate(ArmyRadioCategory.BaseFounded, now, 6f), now, 24);
                _dispatch.Shown(ArmyRadioCategory.BaseFounded, "phrase " + i, now, 16);
                clock.Spoke(i, now, 3f); clock.Forget(i);
                Assert.That(_dispatch.PendingCount, Is.LessThanOrEqualTo(24));
                Assert.That(_dispatch.DecisionCount, Is.LessThanOrEqualTo(64));
                Assert.That(_dispatch.PhraseHistoryCount, Is.LessThanOrEqualTo(16));
                Assert.That(clock.RecentCount(now), Is.LessThanOrEqualTo(6));
            }
            _dispatch.Prune(1000f, _ => true); Assert.That(_dispatch.PendingCount, Is.Zero);
        }
        [Test] public void Content_ContainsAllOriginalLinesAndTwoHundredFourNewDistinctLines()
        {
            var phrases = new HashSet<string>(); int operational = 0;
            for (int i = 0; i < 15; i++)
            {
                var category = (ArmyRadioCategory)i;
                Assert.That(ArmyRadioChatter.OperationalPhraseCount(category), Is.EqualTo(10));
                for (int j = 0; j < 10; j++) { Assert.That(phrases.Add(ArmyRadioChatter.OperationalPhrase(category, j)), Is.True); operational++; }
            }
            Assert.That(operational, Is.EqualTo(150)); Assert.That(ArmyRadioChatter.AmbientCount, Is.EqualTo(25));
            Assert.That(ArmyRadioChatter.PilotAmbientCount, Is.EqualTo(5)); Assert.That(ArmyRadioChatter.DialogueCount, Is.EqualTo(24));
            for (int i = 0; i < ArmyRadioChatter.AmbientCount; i++) Assert.That(phrases.Add(ArmyRadioChatter.Ambient(i)), Is.True);
            for (int i = 0; i < ArmyRadioChatter.PilotAmbientCount; i++) Assert.That(phrases.Add(ArmyRadioChatter.PilotAmbient(i)), Is.True);
            for (int i = 0; i < ArmyRadioChatter.DialogueCount; i++)
            { Assert.That(phrases.Add(ArmyRadioChatter.Dialogue(i, false)), Is.True); Assert.That(phrases.Add(ArmyRadioChatter.Dialogue(i, true)), Is.True); }
            Assert.That(phrases.Count, Is.EqualTo(228)); // includes the 24 original dialogue lines
            foreach (ArmyRadioSituation situation in Enum.GetValues(typeof(ArmyRadioSituation)))
                for (int i = 0; i < ArmyRadioChatter.PhraseCount(situation); i++) Assert.That(phrases.Add(ArmyRadioChatter.Phrase(situation, i)), Is.True);
            for (int i = 0; i < ArmyRadioChatter.JokeCount; i++) Assert.That(phrases.Add(ArmyRadioChatter.Joke(i)), Is.True);
            Assert.That(phrases.Count, Is.EqualTo(353));
        }
    }
}
#endif
