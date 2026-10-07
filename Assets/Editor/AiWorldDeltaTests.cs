#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System;
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
        [TestCase(true)]
        [TestCase(false)]
        public void AviationLaunchRevisionUsesTheCommittedReceiptWithoutRequiringASurvivingActor(bool committed)
        {
            int before = WorldDeltaLifecycle.Current;
            // No registry actor: a destroyed wing is no longer available to the caller.
            AviationRebasePlanner.RecordLaunchReceipt(new Game.Ai.AiMoveExecutionTrace
                { FormationCommitted = committed });
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before + (committed ? 1 : 0)));
        }

        [Test]
        public void MissingAviationLaunchReceiptDoesNotAdvanceRevision()
        {
            int before = WorldDeltaLifecycle.Current;
            AviationRebasePlanner.RecordLaunchReceipt(null);
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ObservationReceiptsReachTheCommonResultWithoutRepublishingOrLosingReasonEvidence(bool changed)
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var before = new WorldSnapshot { Observer = p, TurnNumber = 4,
                Self = new SelfSnapshot { Armies = new[] {
                    new ArmySnapshot { ArmyId = 7, MemberCount = 1, Hex = new HexCoord(1, 1) } } } };
            var after = changed ? new WorldSnapshot { Observer = p, TurnNumber = 4,
                Self = new SelfSnapshot { Armies = new[] {
                    new ArmySnapshot { ArmyId = 7, MemberCount = 1, Hex = new HexCoord(2, 1) } } },
                Known = new KnownSnapshot { Buildings = new[] {
                    new Game.Ai.AiMapMemory.KnownBuilding(new HexCoord(5, 5), p, false, null, isBase: true) } } } : before;
            // Only identity/version are read here; constructing a deck invokes unrelated native
            // Unity Object equality in the managed harness. No gameplay hand methods are mocked.
            var hand = changed ? (Game.Ai.AiHandData)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(Game.Ai.AiHandData)) : null;
            var proposal = new MissionProposal { Kind = MissionKind.Development };
            var execution = new ExecutionResult { Key = StableMissionKey.For(proposal),
                StopReason = ExecutionStopReason.StepCompleted, StepsMoved = changed ? 1 : 0 };
            int initial = WorldDeltaLifecycle.Current;
            WorldDeltaLifecycle.RecordExecutionMutation(execution, changed);
            WorldAnalysis.PublishStepObservationDelta(p, 4,
                new WorldAnalysis.StepObservationStamp(before, new V2ResourceStamp(3, 2, 2, 2, 2), null),
                new WorldAnalysis.StepObservationStamp(after,
                    new V2ResourceStamp(3, changed ? 1 : 2, 2, 2, 2), hand), execution);
            var pending = session.PendingInvalidations;
            Assert.That(pending.RegistryVersion, Is.EqualTo(changed ? 4 : 0));
            Assert.That(execution.WorldDeltas.Count, Is.EqualTo(changed ? 4 : 0));
            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(new[] { proposal }); ledger.RecordExecution(execution);
            var step = ledger.FinalizeSteps()[0];
            Assert.That(step.WorldDeltas, Is.SameAs(execution.WorldDeltas));
            Assert.That(step.WorldDeltas, Is.SameAs(step.WorldDeltas));
            Assert.That(step.StateVersionAfter, Is.EqualTo(execution.StateVersionAfter));
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(initial + (changed ? 1 : 0)));
            Assert.That(session.PendingInvalidations.RegistryVersion, Is.EqualTo(pending.RegistryVersion));
            if (changed)
            {
                Assert.That(step.WorldDeltas[0].DirtyFacts, Is.EqualTo(StrategicInvalidationReason.Actor));
                Assert.That(step.WorldDeltas[0].ActorIds, Is.EqualTo(new[] { 7 }));
                Assert.Throws<NotSupportedException>(() => ((IList<WorldDelta>)step.WorldDeltas).Clear());
                Assert.Throws<NotSupportedException>(() => ((ICollection<int>)step.WorldDeltas[0].ActorIds).Clear());
                session.ConsumeInvalidations(StrategicInvalidationReason.Actor);
                Assert.That(session.PendingInvalidations.ActorIds, Is.Empty);
                Assert.That(session.PendingInvalidations.Reasons, Is.EqualTo(
                    StrategicInvalidationReason.Infrastructure | StrategicInvalidationReason.Capability
                    | StrategicInvalidationReason.Resources | StrategicInvalidationReason.Hand));
            }
        }

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
                int before = WorldDeltaLifecycle.Current;
                var result = HousekeepingExecutor.Execute(plan, analysis, p,
                    new Game.Ai.AiTurnContext { TurnNumber = 4 }, new ActorCommitments());
                Assert.That(result.Applied, Is.EqualTo(2));
                Assert.That(result.Failed, Is.EqualTo(1));
                Assert.That(army.Members, Is.EqualTo(new[] { first, second }));
                Assert.That(WorldDeltaLifecycle.Current - before, Is.EqualTo(2));
            }
            finally { indexed.Remove(army.Hex); }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        public void ConsecutiveFlightActionsKeepSeparateReceiptsWithoutAnAggregateBump(int returnSteps)
        {
            int before = WorldDeltaLifecycle.Current;
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
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before + 1 + returnSteps));
            Assert.That(result.StateVersionAfter, Is.EqualTo(WorldDeltaLifecycle.Current));
        }

        [Test]
        public void RejectedFlightDoesNotManufactureAReceiptOrRevision()
        {
            int before = WorldDeltaLifecycle.Current;
            var result = new ExecutionResult();
            WorldDeltaLifecycle.RecordExecutionMutation(result, false);
            Assert.That(result.StateVersionAfter, Is.EqualTo(-1));
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before));
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
            int before = WorldDeltaLifecycle.Current;
            session.Apply(new WorldDelta(true, facts, new[] { 0 }, new[] { 3 }));
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before + 1));
            Assert.That(StrategicInterruptRegistry.Peek(p, 7).Reasons, Is.EqualTo(facts));
            Assert.That(StrategicInterruptRegistry.Peek(p, 7).ActorIds, Is.EqualTo(new[] { 0 }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NoOpAndRollbackDoNotAdvanceRevision(bool rollback)
        {
            int before = WorldDeltaLifecycle.Current;
            WorldDeltaLifecycle.CommitMutation(committed: false);
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before), rollback ? "rollback" : "no-op");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CanonicalCommitOwnsNestedCardPlayStampsAndRollbackDropsFacts(bool committed)
        {
            var p = new PlayerSetupData(); using var session = AiTurnSession.Begin(p, null, null, null, 7);
            int before = WorldDeltaLifecycle.Current;
            using (var transaction = WorldDeltaLifecycle.BeginTransaction())
            {
                // FoundBase's synchronous garrison card is a child of the shared build transaction.
                WorldDeltaLifecycle.CommitMutation();
                WorldDeltaLifecycle.CommitMutation();
                WorldDeltaLifecycle.Publish(p, 7, StrategicInvalidationReason.Actor, new[] { 0 });
                Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before));
                Assert.That(StrategicInterruptRegistry.Peek(p, 7).Any, Is.False);
                transaction.Commit(p, 7, new WorldDelta(committed,
                    StrategicInvalidationReason.Infrastructure | StrategicInvalidationReason.Hand));
            }
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before + (committed ? 1 : 0)));
            Assert.That(StrategicInterruptRegistry.Peek(p, 7).Reasons, Is.EqualTo(committed
                ? StrategicInvalidationReason.Actor | StrategicInvalidationReason.Infrastructure | StrategicInvalidationReason.Hand
                : StrategicInvalidationReason.None));
        }

        [Test]
        public void ExceptionDisposalDropsChildStampsAndDoesNotCaptureTheNextAction()
        {
            int before = WorldDeltaLifecycle.Current;
            Assert.Throws<System.InvalidOperationException>(() =>
            {
                using var transaction = WorldDeltaLifecycle.BeginTransaction();
                WorldDeltaLifecycle.CommitMutation();
                throw new System.InvalidOperationException("authoritative transaction aborted");
            });
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before));
            WorldDeltaLifecycle.CommitMutation();
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before + 1));
        }

        [Test]
        public void NestedCommitStillBelongsToTheOutermostCanonicalTransaction()
        {
            int before = WorldDeltaLifecycle.Current;
            using (var outer = WorldDeltaLifecycle.BeginTransaction())
            {
                using (var child = WorldDeltaLifecycle.BeginTransaction())
                {
                    WorldDeltaLifecycle.CommitMutation();
                    child.Commit(null, -1, new WorldDelta(true, StrategicInvalidationReason.None));
                }
                Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before));
                outer.Commit(null, -1, new WorldDelta(true, StrategicInvalidationReason.None));
            }
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before + 1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AggregateExecutionConsumesAnExistingChildReceiptExactlyOnce(bool childStamped)
        {
            int before = WorldDeltaLifecycle.Current;
            var result = new ExecutionResult { StepsMoved = 1, CombatChanged = true };
            if (childStamped) result.StateVersionAfter = WorldDeltaLifecycle.CommitMutation();
            typeof(TaskExecutor).GetMethod("StampVersion", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { result });
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before + 1));
            Assert.That(result.StateVersionAfter, Is.EqualTo(WorldDeltaLifecycle.Current));
        }

        [Test]
        public void StaleNoOpCompletionDoesNotAdvanceEvenWhenItsGoalIsSatisfied()
        {
            int before = WorldDeltaLifecycle.Current;
            var result = new ExecutionResult { ReachedGoal = true, StaleNoOp = true, NeedsReplan = true };
            typeof(TaskExecutor).GetMethod("StampVersion", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { result });
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before));
        }

        [Test]
        public void ObservationNeverDoubleBumpsAndPayloadIsFrozen()
        {
            var p = new PlayerSetupData(); using var session = AiTurnSession.Begin(p, null, null, null, 7);
            var ids = new List<int> { 0 };
            var delta = new WorldDelta(false, StrategicInvalidationReason.Actor, ids);
            ids.Clear();
            int before = WorldDeltaLifecycle.Current;
            session.Apply(delta);
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before));
            Assert.That(StrategicInterruptRegistry.Peek(p, 7).ActorIds, Is.EqualTo(new[] { 0 }));
        }
    }
}
#endif
