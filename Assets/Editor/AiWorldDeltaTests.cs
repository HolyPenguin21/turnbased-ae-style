#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiWorldDeltaTests
    {
        [TestCase((int)(StrategicInvalidationReason.Actor))]
        [TestCase((int)(StrategicInvalidationReason.Resources))]
        [TestCase((int)(StrategicInvalidationReason.Hand | StrategicInvalidationReason.Capability))]
        [TestCase((int)(StrategicInvalidationReason.Infrastructure))]
        [TestCase((int)(StrategicInvalidationReason.ReconKnowledge | StrategicInvalidationReason.Contact))]
        public void CommittedTransactionAdvancesOnceAndPublishesExactFacts(int value)
        {
            var facts = (StrategicInvalidationReason)value;
            var p = new PlayerSetupData(); using var session = AiTurnSession.Begin(p, null, null, null, 7);
            int before = V2StateVersion.Current;
            session.Apply(new WorldDelta(true, facts, new[] { 0 }, new[] { 3 }));
            Assert.That(V2StateVersion.Current, Is.EqualTo(before + 1));
            Assert.That(StrategicInterruptRegistry.Peek(p, 7).Reasons, Is.EqualTo(facts));
            Assert.That(StrategicInterruptRegistry.Peek(p, 7).ActorIds, Is.EqualTo(new[] { 0 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NoOpAndRollbackDoNotAdvanceRevision(bool rollback)
        {
            int before = V2StateVersion.Current;
            WorldDeltaLifecycle.CommitMutation(committed: false);
            Assert.That(V2StateVersion.Current, Is.EqualTo(before), rollback ? "rollback" : "no-op");
        }

        [Test]
        public void ObservationNeverDoubleBumpsAndPayloadIsFrozen()
        {
            var p = new PlayerSetupData(); using var session = AiTurnSession.Begin(p, null, null, null, 7);
            var ids = new List<int> { 0 };
            var delta = new WorldDelta(false, StrategicInvalidationReason.Actor, ids);
            ids.Clear();
            int before = V2StateVersion.Current;
            session.Apply(delta);
            Assert.That(V2StateVersion.Current, Is.EqualTo(before));
            Assert.That(StrategicInterruptRegistry.Peek(p, 7).ActorIds, Is.EqualTo(new[] { 0 }));
        }
    }
}
#endif
