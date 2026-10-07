#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Reflection;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiWorldDeltaTests
    {
        [Test]
        public void HousekeepingCommitsEachCanonicalReorderButNotARejectedNoOp()
        {
            var p = new PlayerSetupData();
            var first = new UnitData { IsHero = true, Owner = p };
            var second = new UnitData { IsHero = true, Owner = p };
            var army = new ArmyData { Owner = p, Hex = new HexCoord(177777, 299999) };
            army.Members.Add(first); army.Members.Add(second);
            // Seed the registered-world fixture without unrelated native visibility callbacks.
            // Execution still invokes the real canonical ArmyData.TryReorderCommander.
            var indexed = (Dictionary<HexCoord, List<ArmyData>>)typeof(ArmyRegistry)
                .GetField("ByHex", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            indexed.Add(army.Hex, new List<ArmyData> { army });
            try
            {
                var analysis = new ArmyReorgAnalysis {
                    ArmyById = new Dictionary<int, ArmyData> { { army.Id, army } },
                    UnitByKey = new Dictionary<int, UnitData> { { 1, first }, { 2, second } } };
                var plan = new ReorganizationPlan();
                plan.Transfers.Add(PlannedTransfer.Reorder(2, army.Id, "promote second"));
                plan.Transfers.Add(PlannedTransfer.Reorder(1, army.Id, "restore first"));
                plan.Transfers.Add(PlannedTransfer.Reorder(1, army.Id, "already first"));
                int before = V2StateVersion.Current;
                var result = HousekeepingExecutor.Execute(plan, analysis, p,
                    new Game.Ai.AiTurnContext { TurnNumber = 4 }, new ActorCommitments());
                Assert.That(result.Applied, Is.EqualTo(2));
                Assert.That(result.Failed, Is.EqualTo(1));
                Assert.That(army.Members, Is.EqualTo(new[] { first, second }));
                Assert.That(V2StateVersion.Current - before, Is.EqualTo(2));
            }
            finally { indexed.Remove(army.Hex); }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        public void ConsecutiveFlightActionsKeepSeparateReceiptsWithoutAnAggregateBump(int returnSteps)
        {
            int before = V2StateVersion.Current;
            var result = new ExecutionResult { CombatChanged = true };
            // One committed stationary strike, then zero or more actual return moves. A rejected
            // return is not a mutation even though the aggregate still contains CombatChanged.
            WorldDeltaLifecycle.RecordExecutionMutation(result, true);
            for (int step = 0; step < returnSteps; step++)
            {
                result.StepsMoved++;
                WorldDeltaLifecycle.RecordExecutionMutation(result, true);
            }
            WorldDeltaLifecycle.RecordExecutionMutation(result, false);
            typeof(TaskExecutor).GetMethod("StampVersion", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { result });
            Assert.That(V2StateVersion.Current, Is.EqualTo(before + 1 + returnSteps));
            Assert.That(result.StateVersionAfter, Is.EqualTo(V2StateVersion.Current));
        }

        [Test]
        public void RejectedFlightDoesNotManufactureAReceiptOrRevision()
        {
            int before = V2StateVersion.Current;
            var result = new ExecutionResult();
            WorldDeltaLifecycle.RecordExecutionMutation(result, false);
            Assert.That(result.StateVersionAfter, Is.EqualTo(-1));
            Assert.That(V2StateVersion.Current, Is.EqualTo(before));
        }

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

        [TestCase(true)]
        [TestCase(false)]
        public void CanonicalCommitOwnsNestedCardPlayStampsAndRollbackDropsFacts(bool committed)
        {
            var p = new PlayerSetupData(); using var session = AiTurnSession.Begin(p, null, null, null, 7);
            int before = V2StateVersion.Current;
            using (var transaction = WorldDeltaLifecycle.BeginTransaction())
            {
                // FoundBase's synchronous garrison card is a child of the shared build transaction.
                WorldDeltaLifecycle.CommitMutation();
                WorldDeltaLifecycle.CommitMutation();
                WorldDeltaLifecycle.Publish(p, 7, StrategicInvalidationReason.Actor, new[] { 0 });
                Assert.That(V2StateVersion.Current, Is.EqualTo(before));
                Assert.That(StrategicInterruptRegistry.Peek(p, 7).Any, Is.False);
                transaction.Commit(p, 7, new WorldDelta(committed,
                    StrategicInvalidationReason.Infrastructure | StrategicInvalidationReason.Hand));
            }
            Assert.That(V2StateVersion.Current, Is.EqualTo(before + (committed ? 1 : 0)));
            Assert.That(StrategicInterruptRegistry.Peek(p, 7).Reasons, Is.EqualTo(committed
                ? StrategicInvalidationReason.Actor | StrategicInvalidationReason.Infrastructure | StrategicInvalidationReason.Hand
                : StrategicInvalidationReason.None));
        }

        [Test]
        public void ExceptionDisposalDropsChildStampsAndDoesNotCaptureTheNextAction()
        {
            int before = V2StateVersion.Current;
            Assert.Throws<System.InvalidOperationException>(() =>
            {
                using var transaction = WorldDeltaLifecycle.BeginTransaction();
                WorldDeltaLifecycle.CommitMutation();
                throw new System.InvalidOperationException("authoritative transaction aborted");
            });
            Assert.That(V2StateVersion.Current, Is.EqualTo(before));
            WorldDeltaLifecycle.CommitMutation();
            Assert.That(V2StateVersion.Current, Is.EqualTo(before + 1));
        }

        [Test]
        public void NestedCommitStillBelongsToTheOutermostCanonicalTransaction()
        {
            int before = V2StateVersion.Current;
            using (var outer = WorldDeltaLifecycle.BeginTransaction())
            {
                using (var child = WorldDeltaLifecycle.BeginTransaction())
                {
                    WorldDeltaLifecycle.CommitMutation();
                    child.Commit(null, -1, new WorldDelta(true, StrategicInvalidationReason.None));
                }
                Assert.That(V2StateVersion.Current, Is.EqualTo(before));
                outer.Commit(null, -1, new WorldDelta(true, StrategicInvalidationReason.None));
            }
            Assert.That(V2StateVersion.Current, Is.EqualTo(before + 1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AggregateExecutionConsumesAnExistingChildReceiptExactlyOnce(bool childStamped)
        {
            int before = V2StateVersion.Current;
            var result = new ExecutionResult { StepsMoved = 1, CombatChanged = true };
            if (childStamped) result.StateVersionAfter = WorldDeltaLifecycle.CommitMutation();
            typeof(TaskExecutor).GetMethod("StampVersion", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { result });
            Assert.That(V2StateVersion.Current, Is.EqualTo(before + 1));
            Assert.That(result.StateVersionAfter, Is.EqualTo(V2StateVersion.Current));
        }

        [Test]
        public void StaleNoOpCompletionDoesNotAdvanceEvenWhenItsGoalIsSatisfied()
        {
            int before = V2StateVersion.Current;
            var result = new ExecutionResult { ReachedGoal = true, StaleNoOp = true, NeedsReplan = true };
            typeof(TaskExecutor).GetMethod("StampVersion", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { result });
            Assert.That(V2StateVersion.Current, Is.EqualTo(before));
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
