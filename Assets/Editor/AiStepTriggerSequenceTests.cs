#if UNITY_INCLUDE_TESTS
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Level 3: the two baseline trigger sequences (rebase: one take->reenter pair; mission step,
    // recovery, management round: two) over the REAL fan-out and the REAL pending-fact registry.
    // The interesting case is a first re-entry that publishes a compound fact: which consumers get
    // it, when it is processed, and why it is not lost after the first consume.
    public class AiStepTriggerSequenceTests
    {
        private const int Turn = 7;
        private const StrategicInvalidationReason Compound =
            StrategicInvalidationReason.Actor | StrategicInvalidationReason.Capability;

        private PlayerSetupData _player;
        private List<(StrategicInvalidationReason Reasons, HashSet<DesireAxis> Axes)> _reentries;
        private System.Action<int> _onReentry;
        private bool _firstReentryChanged;

        [SetUp]
        public void SetUp()
        {
            _player = new PlayerSetupData { Nickname = "trigger-sequence" };
            _reentries = new List<(StrategicInvalidationReason, HashSet<DesireAxis>)>();
            _onReentry = null;
            _firstReentryChanged = true;
            StrategicInterruptRegistry.ClearAll();
        }

        [TearDown]
        public void TearDown() => StrategicInterruptRegistry.ClearAll();

        // The same take the pipeline performs: fan the pending facts out, consume that snapshot only.
        private TypedTriggerSplit Take()
        {
            TypedTriggerSplit split = TypedTriggerFanOut.Split(
                StrategicInterruptRegistry.Peek(_player, Turn).Reasons, () => false);
            StrategicInterruptRegistry.Consume(_player, Turn, split.Consumed);
            return split;
        }

        private IEnumerator Reenter(StrategicInvalidationReason reasons, HashSet<DesireAxis> axes)
        {
            _reentries.Add((reasons, axes == null ? new HashSet<DesireAxis>() : new HashSet<DesireAxis>(axes)));
            // The re-entry itself may publish facts (a follow-up Phase A action).
            _onReentry?.Invoke(_reentries.Count);
            yield break;
        }

        private StepTriggerOutcome Run(int pairs)
        {
            StepTriggerOutcome outcome = default;
            Drain(StepTriggerSequence.Run(pairs, Take, Reenter,
                () => _firstReentryChanged && _reentries.Count == 1, o => outcome = o));
            return outcome;
        }

        private static void Drain(IEnumerator root)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                if (stack.Peek().Current is IEnumerator nested)
                    stack.Push(nested);
            }
        }

        private StrategicInvalidationReason Pending => StrategicInterruptRegistry.Peek(_player, Turn).Reasons;

        private static HashSet<DesireAxis> ConsumersOf(StrategicInvalidationReason facts) =>
            TypedTriggerFanOut.Split(facts, () => false).DirtyAxes;

        // ---- the counts ------------------------------------------------------------------------

        [Test]
        public void TheBaselinePairCountsAreNamed()
        {
            Assert.AreEqual(2, StepTriggerSequence.StandardPairs);
            Assert.AreEqual(1, StepTriggerSequence.RebasePairs);
            Assert.AreEqual(1, MandatoryAviationOrder.TriggerPairs(MandatoryAviationKind.Rebase));
            Assert.AreEqual(2, MandatoryAviationOrder.TriggerPairs(MandatoryAviationKind.Recovery));
            Assert.AreEqual(2, StepTriggerSequence.StandardPairs, "mission step and management round");
        }

        // ---- a compound fact published by the first re-entry ------------------------------------

        [Test]
        public void TwoPairs_TheSecondPairFansTheCompoundFactOutToEveryConsumerWithinTheSameStep()
        {
            StrategicInterruptRegistry.Record(_player, Turn, StrategicInvalidationReason.Hand);
            _onReentry = n => { if (n == 1) StrategicInterruptRegistry.Record(_player, Turn, Compound); };

            StepTriggerOutcome outcome = Run(StepTriggerSequence.StandardPairs);

            Assert.AreEqual(2, _reentries.Count);
            HashSet<DesireAxis> consumers = ConsumersOf(Compound);
            Assert.IsTrue(consumers.Contains(DesireAxis.Aggression), "Actor is in the Aggression mask");
            Assert.That(_reentries[1].Axes, Is.EquivalentTo(consumers),
                "every strategic consumer of the compound fact is re-entered in the same step");
            Assert.AreEqual(Compound, _reentries[1].Reasons & Compound);
            Assert.AreEqual(StrategicInvalidationReason.None, Pending & Compound,
                "consumed by the second pair, once");
            Assert.AreEqual(Compound, outcome.Strategic & Compound);
        }

        [Test]
        public void OnePair_TheCompoundFactIsNotLostItStaysPendingForTheNextTake()
        {
            StrategicInterruptRegistry.Record(_player, Turn, StrategicInvalidationReason.Hand);
            _onReentry = n => { if (n == 1) StrategicInterruptRegistry.Record(_player, Turn, Compound); };

            StepTriggerOutcome outcome = Run(StepTriggerSequence.RebasePairs);

            Assert.AreEqual(1, _reentries.Count, "the rebase never ran the follow-up pair");
            Assert.AreEqual(StrategicInvalidationReason.None, outcome.Strategic & Compound,
                "this step did not process it");
            Assert.AreEqual(Compound, Pending & Compound,
                "the first consume removed only the snapshot it was given, not the later fact");

            // The next work step's first pair serves the very same consumers, one step later.
            TypedTriggerSplit next = Take();
            Assert.That(next.DirtyAxes, Is.EquivalentTo(ConsumersOf(Compound)));
            Assert.AreEqual(StrategicInvalidationReason.None, Pending & Compound);
        }

        [Test]
        public void BothSequencesServeTheSameConsumers_OnlyTheMomentDiffers()
        {
            StrategicInterruptRegistry.Record(_player, Turn, StrategicInvalidationReason.Hand);
            _onReentry = n => { if (n == 1) StrategicInterruptRegistry.Record(_player, Turn, Compound); };
            Run(StepTriggerSequence.StandardPairs);
            HashSet<DesireAxis> standardServed = _reentries[1].Axes;

            SetUp();
            StrategicInterruptRegistry.Record(_player, Turn, StrategicInvalidationReason.Hand);
            _onReentry = n => { if (n == 1) StrategicInterruptRegistry.Record(_player, Turn, Compound); };
            Run(StepTriggerSequence.RebasePairs);
            HashSet<DesireAxis> rebaseServedLater = Take().DirtyAxes;

            Assert.That(rebaseServedLater, Is.EquivalentTo(standardServed));
        }

        // ---- no new fact: the sequences coincide ------------------------------------------------

        [Test]
        public void WithoutAFactPublishedByTheReentryBothSequencesGiveTheSameOutcome()
        {
            StrategicInterruptRegistry.Record(_player, Turn, StrategicInvalidationReason.Hand);
            StepTriggerOutcome two = Run(StepTriggerSequence.StandardPairs);
            Assert.AreEqual(2, _reentries.Count);
            Assert.AreEqual(StrategicInvalidationReason.None, _reentries[1].Reasons);
            Assert.IsEmpty(_reentries[1].Axes, "the second pair has nothing to admit");

            SetUp();
            StrategicInterruptRegistry.Record(_player, Turn, StrategicInvalidationReason.Hand);
            StepTriggerOutcome one = Run(StepTriggerSequence.RebasePairs);

            Assert.AreEqual(two.Operational, one.Operational);
            Assert.AreEqual(two.Strategic, one.Strategic);
            Assert.AreEqual(two.StrategicChanged, one.StrategicChanged);
        }

        // ---- boundedness: a fact published by the LAST re-entry is not absorbed ------------------

        [Test]
        public void AFactPublishedByTheLastPairIsLeftForTheNextBoundedIteration()
        {
            StrategicInterruptRegistry.Record(_player, Turn, StrategicInvalidationReason.Hand);
            _onReentry = n => { if (n == 2) StrategicInterruptRegistry.Record(_player, Turn, StrategicInvalidationReason.Resources); };

            Run(StepTriggerSequence.StandardPairs);

            Assert.AreEqual(2, _reentries.Count, "never a third pair inside one step");
            Assert.AreEqual(StrategicInvalidationReason.Resources, Pending & StrategicInvalidationReason.Resources);
        }

        [Test]
        public void TheFirstPairConsumesOnlyWhatItWasGiven()
        {
            StrategicInterruptRegistry.Record(_player, Turn, StrategicInvalidationReason.Hand);
            // A fact outside every consumer mask is nobody's share: it must survive the consume.
            StrategicInterruptRegistry.Record(_player, Turn, StrategicInvalidationReason.External);
            TypedTriggerSplit split = Take();
            Assert.AreEqual(StrategicInvalidationReason.External, Pending & StrategicInvalidationReason.External);
            Assert.AreEqual(StrategicInvalidationReason.None, Pending & split.Consumed);
        }
    }
}
#endif
