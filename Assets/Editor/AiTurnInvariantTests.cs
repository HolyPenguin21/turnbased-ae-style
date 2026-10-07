#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // End-of-turn invariants and the lifecycle line. Both are diagnostics: they report, they
    // never change lifecycle state.
    public sealed class AiTurnInvariantTests
    {
        [TearDown] public void Reset() { MissionIntentRegistry.Clear(); AiAllocatorStateRegistry.Clear(); }

        private static MissionIntent EconomyIntent(MissionIntentKey key, int actor) => new MissionIntent
        {
            Kind = MissionKind.Economy, IntentKey = key, CreatedTurn = 3, LastProgressTurn = 3,
            PreferredMoverArmyId = actor,
            Objective = new EconomyIntent { Kind = EconomyTaskKind.MobileCollection, TargetHex = new HexCoord(2, 3) },
        };

        private static WorldSnapshot Snapshot(params int[] armyIds) => new WorldSnapshot
        {
            TurnNumber = 4,
            Self = new SelfSnapshot
            {
                Armies = armyIds.Select(id => new ArmySnapshot { ArmyId = id, MemberCount = 1 }).ToList(),
            },
        };

        [Test]
        public void AnEmptyTurnAuditsClean()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            WorldDeltaLifecycle.StampAction(true);
            WorldDeltaLifecycle.StampAction(true);
            Assert.That(session.AuditTurnEnd(Snapshot(), null), Is.Empty);
        }

        [Test]
        public void AClaimOwnerWithoutADurableIntentIsReported()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = MissionIntentKey.ForEconomy(EconomyTaskKind.MobileCollection, 1, new HexCoord(2, 3));
            session.Leases.For(key).Claim(3);
            IReadOnlyList<string> violations = session.AuditTurnEnd(Snapshot(3), null);
            Assert.That(violations.Single(), Does.Contain("no durable intent"));
        }

        [Test]
        public void ClaimTableDriftFromTheDerivedViewIsReported()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = MissionIntentKey.ForEconomy(EconomyTaskKind.MobileCollection, 1, new HexCoord(2, 3));
            session.PersistentState.Put(EconomyIntent(key, 5));
            session.RefreshActors(session.PersistentState.All.ToList(), Snapshot(5), null);
            Assert.That(session.Leases.ActorsFor(key), Is.EqualTo(new[] { 5 }));

            // The actor vanished from the world after the table was last refreshed.
            IReadOnlyList<string> violations = session.AuditTurnEnd(Snapshot(), null);
            Assert.That(violations.Single(), Does.Contain("claim table != derived view"));
        }

        [Test]
        public void ConsistentClaimsAuditClean()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = MissionIntentKey.ForEconomy(EconomyTaskKind.MobileCollection, 1, new HexCoord(2, 3));
            session.PersistentState.Put(EconomyIntent(key, 5));
            session.RefreshActors(session.PersistentState.All.ToList(), Snapshot(5), null);
            Assert.That(session.AuditTurnEnd(Snapshot(5), null), Is.Empty);
        }

        [Test]
        public void AProgressingStepWithoutARevisionAdvanceIsReported()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = new MissionIntentKey(MissionKind.Development, 0, 0, 2, 3);
            session.PersistentState.Put(new MissionIntent { Kind = MissionKind.Development, IntentKey = key,
                CreatedTurn = 3, LastProgressTurn = 3 });
            session.Settle(new MissionStepResult { IntentKey = key, MissionKind = MissionKind.Development,
                Disposition = MissionStepDisposition.Progress, MadeProgress = true, StepsMoved = 1,
                StateVersionAfter = WorldDeltaLifecycle.Current });
            Assert.That(session.AuditTurnEnd(Snapshot(), null).Single(), Does.Contain("missed stamp"));
        }

        [Test]
        public void AProgressingStepWithATurnLocalReceiptAuditsClean()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = new MissionIntentKey(MissionKind.Development, 0, 0, 2, 3);
            session.PersistentState.Put(new MissionIntent { Kind = MissionKind.Development, IntentKey = key,
                CreatedTurn = 3, LastProgressTurn = 3 });
            int receipt = WorldDeltaLifecycle.StampAction(true);
            session.Settle(new MissionStepResult { IntentKey = key, MissionKind = MissionKind.Development,
                Disposition = MissionStepDisposition.Progress, MadeProgress = true, StepsMoved = 1,
                StateVersionAfter = receipt });
            Assert.That(session.AuditTurnEnd(Snapshot(), null), Is.Empty);
        }

        [Test]
        public void AnOpenWorldTransactionIsReported()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            using (WorldDeltaLifecycle.BeginTransaction())
                Assert.That(session.AuditTurnEnd(Snapshot(), null).Single(), Does.Contain("transaction is still open"));
        }

        [Test]
        public void StagedChildStampsNeverCountAsCommittedActions()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            using (var tx = WorldDeltaLifecycle.BeginTransaction())
            {
                WorldDeltaLifecycle.CommitMutation();
                WorldDeltaLifecycle.CommitMutation();
                tx.Commit(p, 4, new WorldDelta(true, StrategicInvalidationReason.Infrastructure));
            }
            Assert.That(WorldDeltaLifecycle.StagedChildMutations, Is.GreaterThanOrEqualTo(2));
            Assert.That(session.AuditTurnEnd(Snapshot(), null), Is.Empty);
        }

        [Test]
        public void LifecycleLineShowsExecutionNormalizationDomainDecisionAndClaims()
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = new MissionIntentKey(MissionKind.Development, 0, 0, 2, 3);
            session.PersistentState.Put(new MissionIntent { Kind = MissionKind.Development, IntentKey = key,
                CreatedTurn = 3, LastProgressTurn = 3 });
            session.Leases.For(key).Claim(7);
            session.Settle(new MissionStepResult { IntentKey = key, MissionKind = MissionKind.Development,
                Disposition = MissionStepDisposition.Waiting, StopReason = ExecutionStopReason.NoSafeStep,
                MoverArmyId = 7 });
            string line = session.LastLifecycleLine;
            Assert.That(line, Does.StartWith("[AI][V2][Lifecycle] operation="));
            Assert.That(line, Does.Contain("| exec stop=NoSafeStep"));
            Assert.That(line, Does.Contain("mover=7"));
            Assert.That(line, Does.Contain("| norm src=execution result="));
            Assert.That(line, Does.Contain("| domain intent="));
            Assert.That(line, Does.Contain("| claims actors=1>"));
        }
    }
}
#endif
