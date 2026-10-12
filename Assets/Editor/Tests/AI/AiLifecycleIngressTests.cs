#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai.V2;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiLifecycleIngressTests
    {
        // Deliberately absent from production enums/domain bindings and legacy ledger fields.
        private sealed class ExtensionFacts : IMissionStepPayload { public int Sequence; }

        [TestCase(MissionKind.Scout)]
        [TestCase(MissionKind.Raid)]
        [TestCase(MissionKind.Attack)]
        [TestCase(MissionKind.ActiveDefence)]
        [TestCase(MissionKind.Development)]
        public void ExistingNonEconomyReservationTokensRemainExactlyTheLegacyTokens(MissionKind kind)
        {
            var key = new MissionIntentKey(kind, 3, 17, 2, 3);
            var owner = ReservationOwner.ForOperation(key);
            Assert.That(owner.Token, Is.EqualTo(key.ToString()));
            Assert.That(owner.Operation.Value, Is.EqualTo(key));
        }

        [Test]
        public void ExtensionReservationTokensEncodeEveryCanonicalKeyField()
        {
            var key = new MissionIntentKey((MissionKind)900, 3, 17, 2, 3);
            var keys = new[] { key, new MissionIntentKey((MissionKind)901, 3, 17, 2, 3),
                new MissionIntentKey((MissionKind)900, 4, 17, 2, 3),
                new MissionIntentKey((MissionKind)900, 3, 18, 2, 3),
                new MissionIntentKey((MissionKind)900, 3, 17, 3, 3),
                new MissionIntentKey((MissionKind)900, 3, 17, 2, 4),
                new MissionIntentKey((MissionKind)900, 3, 17, 2, 3, RaidTargetKind.EventGuard) };
            var tokens = new System.Collections.Generic.HashSet<string>();
            foreach (var operation in keys) Assert.That(tokens.Add(ReservationOwner.ForOperation(operation).Token), Is.True);
        }

        [TestCase(MissionStepDisposition.Progress, true)]
        [TestCase(MissionStepDisposition.Waiting, true)]
        [TestCase(MissionStepDisposition.Replan, true)]
        [TestCase(MissionStepDisposition.Completed, false)]
        [TestCase(MissionStepDisposition.Invalidated, false)]
        [TestCase(MissionStepDisposition.PermanentFailure, false)]
        public void NewDomainPayloadUsesGenericLeaseAndLifecycleWithoutCleanupBindings(
            MissionStepDisposition disposition, bool retained)
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = new MissionIntentKey((MissionKind)900, 3, 17, 2, 3);
            var otherKey = new MissionIntentKey((MissionKind)900, 3, 18, 7, 3);
            session.PersistentState.Put(new MissionIntent { Kind = key.Kind, IntentKey = key,
                CreatedTurn = 4 });
            var lease = session.Leases.For(key); var other = session.Leases.For(otherKey);
            lease.Claim(0); other.Claim(1);
            lease.Reserve(StrategicReservationReason.EconomyBuildCompletion,
                StrategicReservedResource.ActionPoints, 2);
            other.Reserve(StrategicReservationReason.EconomyBuildCompletion,
                StrategicReservedResource.ActionPoints, 3);
            int before = WorldDeltaLifecycle.Current;
            session.Apply(new WorldDelta(true, StrategicInvalidationReason.Actor, actorIds: new[] { 0 }));
            var payload = new ExtensionFacts { Sequence = 7 };
            var step = new MissionStepResult<ExtensionFacts>(key, disposition, payload) {
                MadeProgress = disposition == MissionStepDisposition.Progress,
                ObjectiveSatisfied = disposition == MissionStepDisposition.Completed,
                StateVersionAfter = WorldDeltaLifecycle.Current };
            session.Settle(step);
            Assert.That(step.Payload, Is.SameAs(payload));
            Assert.That(payload.Sequence, Is.EqualTo(7));
            Assert.That(session.PersistentState.TryGet(key, out _), Is.EqualTo(retained));
            Assert.That(lease.ActorClaims.Count > 0, Is.EqualTo(retained));
            Assert.That(lease.ResourceClaims.Count > 0, Is.EqualTo(retained));
            Assert.That(other.ActorClaims, Is.EqualTo(new[] { 1 }));
            Assert.That(other.ResourceClaims[0].Amount, Is.EqualTo(3));
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(before + 1));
            Assert.That(session.PendingInvalidations.ActorIds, Is.EqualTo(new[] { 0 }));
            session.Dispose();
            Assert.That(StrategicResourceReservationLedger.Rows(p, 4), Is.Empty);
            Assert.Throws<ObjectDisposedException>(() => lease.Claim(2));
        }

        [TestCase(MissionStepDisposition.Completed)]
        [TestCase(MissionStepDisposition.Invalidated)]
        [TestCase(MissionStepDisposition.PermanentFailure)]
        public void NewDomainTerminalAttemptWithoutDurableIntentReleasesOnlyItsOwnLease(
            MissionStepDisposition disposition)
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = new MissionIntentKey((MissionKind)900, 3, 17, 2, 3);
            var otherKey = new MissionIntentKey((MissionKind)900, 3, 18, 7, 3);
            var lease = session.Leases.For(key); var other = session.Leases.For(otherKey);
            lease.Claim(0); other.Claim(1);
            lease.Reserve(StrategicReservationReason.EconomyBuildCompletion,
                StrategicReservedResource.ActionPoints, 2);
            other.Reserve(StrategicReservationReason.EconomyBuildCompletion,
                StrategicReservedResource.ActionPoints, 3);
            session.Settle(new MissionStepResult<ExtensionFacts>(key, disposition, new ExtensionFacts()) {
                ObjectiveSatisfied = disposition == MissionStepDisposition.Completed });
            Assert.That(lease.ActorClaims, Is.Empty);
            Assert.That(lease.ResourceClaims, Is.Empty);
            Assert.That(other.ActorClaims, Is.EqualTo(new[] { 1 }));
            Assert.That(other.ResourceClaims[0].Amount, Is.EqualTo(3));
        }

        [TearDown] public void Reset()
        {
            MissionIntentRegistry.Clear();
            AiAllocatorStateRegistry.Clear();
        }

        [Test]
        public void CompatibilityViewSharesDispositionCommonFactsAndTypedPayloadStorage()
        {
            var key = new MissionIntentKey(MissionKind.Development, 0, 0, 2, 3);
            var payload = new DevelopmentStepPayload { DeliveryReady = true };
            var result = new MissionStepResult<DevelopmentStepPayload>(key,
                MissionStepDisposition.Progress, payload) { ApSpent = 2, MoverArmyId = 0 };
            var view = result;
            Assert.That(view.GetPayload<DevelopmentStepPayload>(), Is.SameAs(payload));
            Assert.That(view.MissionKind, Is.EqualTo(MissionKind.Development));
            Assert.That(view.MoverArmyId, Is.EqualTo(0));
            view.Disposition = MissionStepDisposition.PermanentFailure;
            Assert.That(result.Disposition, Is.EqualTo(MissionStepDisposition.PermanentFailure));
            result.ApSpent = 4;
            Assert.That(view.ApSpent, Is.EqualTo(4));
            view.MadeProgress = true;
            Assert.That(result.MadeProgress, Is.True);
            view.SetPayload<DevelopmentStepPayload>(null);
            Assert.That(result.Payload, Is.Null);
            result.SetPayload(payload);
            Assert.That(view.GetPayload<DevelopmentStepPayload>(), Is.SameAs(payload));
            Assert.That(view, Is.SameAs(view));
        }

        [TestCase(MissionStepDisposition.Progress, true)]
        [TestCase(MissionStepDisposition.Waiting, true)]
        [TestCase(MissionStepDisposition.Replan, true)]
        [TestCase(MissionStepDisposition.Completed, false)]
        [TestCase(MissionStepDisposition.Invalidated, false)]
        [TestCase(MissionStepDisposition.PermanentFailure, false)]
        public void TypedOperationalIngressUsesExistingTransitionAndIndependentLeaseCleanup(
            MissionStepDisposition disposition, bool retained)
        {
            var player = new PlayerSetupData();
            using var session = AiTurnSession.Begin(player, null, null, null, 4);
            var key = new MissionIntentKey(MissionKind.Development, 0, 0, 2, 3);
            var otherKey = new MissionIntentKey(MissionKind.Development, 0, 0, 7, 3);
            session.PersistentState.Put(new MissionIntent { Kind = MissionKind.Development,
                IntentKey = key, CreatedTurn = 4 });
            var lease = session.Leases.For(key); var other = session.Leases.For(otherKey);
            lease.Claim(0); other.Claim(1);
            lease.Reserve(StrategicReservationReason.EconomyBuildCompletion,
                StrategicReservedResource.ActionPoints, 2);
            other.Reserve(StrategicReservationReason.EconomyBuildCompletion,
                StrategicReservedResource.ActionPoints, 3);
            var result = new MissionStepResult<DevelopmentStepPayload>(key, disposition, null)
            {
                MadeProgress = disposition == MissionStepDisposition.Progress,
                ObjectiveSatisfied = disposition == MissionStepDisposition.Completed,
            };
            session.Settle(result);
            Assert.That(session.PersistentState.TryGet(key, out _), Is.EqualTo(retained));
            Assert.That(lease.ActorClaims.Count > 0, Is.EqualTo(retained));
            Assert.That(lease.ResourceClaims.Count > 0, Is.EqualTo(retained));
            Assert.That(other.ActorClaims, Is.EqualTo(new[] { 1 }));
            Assert.That(other.ResourceClaims[0].Amount, Is.EqualTo(3));
        }

        [TestCase(MissionKind.Scout)]
        [TestCase(MissionKind.Economy)]
        [TestCase(MissionKind.Raid)]
        [TestCase(MissionKind.Attack)]
        [TestCase(MissionKind.ActiveDefence)]
        [TestCase(MissionKind.Development)]
        public void CommonAndLegacyIngressKeepIdenticalExistingDomainProgress(MissionKind kind)
        {
            var legacyPlayer = new PlayerSetupData(); var commonPlayer = new PlayerSetupData();
            using var legacySession = AiTurnSession.Begin(legacyPlayer, null, null, null, 4);
            using var commonSession = AiTurnSession.Begin(commonPlayer, null, null, null, 4);
            var legacy = Outcome(kind); var common = Outcome(kind);
            var beforeLegacy = Intent(kind, legacy.IntentKey); var beforeCommon = Intent(kind, common.IntentKey);
            legacySession.PersistentState.Put(beforeLegacy); commonSession.PersistentState.Put(beforeCommon);
            MissionContinuityLayer.ReconcileStep(legacyPlayer, 4, legacy);
            commonSession.Settle(common);
            Assert.That(commonSession.PersistentState.Count, Is.EqualTo(legacySession.PersistentState.Count));
            Assert.That(beforeCommon.Status, Is.EqualTo(beforeLegacy.Status));
            Assert.That(beforeCommon.Suspended, Is.EqualTo(beforeLegacy.Suspended));
            Assert.That(beforeCommon.StepsMovedTotal, Is.EqualTo(beforeLegacy.StepsMovedTotal));
            Assert.That(beforeCommon.CumulativeApSpent, Is.EqualTo(beforeLegacy.CumulativeApSpent));
            Assert.That(beforeCommon.LastProgressTurn, Is.EqualTo(beforeLegacy.LastProgressTurn));
            Assert.That(beforeCommon.StallTurns, Is.EqualTo(beforeLegacy.StallTurns));
            Assert.That(beforeCommon.PreferredMoverArmyId, Is.EqualTo(beforeLegacy.PreferredMoverArmyId));
            Assert.That(beforeCommon.TurnsActive, Is.EqualTo(beforeLegacy.TurnsActive));
        }

        [Test]
        public void EndedSessionCannotSettleIntoNextTurn()
        {
            var p = new PlayerSetupData(); var old = AiTurnSession.Begin(p, null, null, null, 4);
            using var next = AiTurnSession.Begin(p, null, null, null, 5);
            Assert.Throws<ObjectDisposedException>(() => old.Settle(Outcome(MissionKind.Development)));
            Assert.That(next.PersistentState.Count, Is.Zero);
        }

        [Test]
        public void OperationalLedgerProjectsNormalizedCommonFactsWithoutAnotherClassification()
        {
            var facts = AiLifecycleParityTests.Facts(MissionKind.Development);
            var ledger = new MissionOutcomeLedger(); ledger.RegisterProposals(new[] { facts.Proposal });
            ledger.RecordProvisionSuccess(facts.Proposal, facts.Provisioned);
            ledger.RecordExecution(new ExecutionResult { Key = StableMissionKey.For(facts.Proposal),
                StopReason = ExecutionStopReason.StepCompleted, StepsMoved = 1, ApSpent = 2 });
            MissionStepResult result = ledger.FinalizeSteps()[0];
            Assert.That(result.Disposition, Is.EqualTo(MissionStepDisposition.Progress));
            Assert.That(result.StepsMoved, Is.EqualTo(1));
            Assert.That(result.ApSpent, Is.EqualTo(2));
            Assert.That(result.GetPayload<DevelopmentStepPayload>(), Is.Not.Null);
        }

        private static MissionStepResult Outcome(MissionKind kind)
        {
            var facts = AiLifecycleParityTests.Facts(kind);
            facts.Execution = new ExecutionResult { StopReason = ExecutionStopReason.StepCompleted,
                StepsMoved = 1, ApSpent = 2, ActualActorArmyId = 0 };
            return MissionStepResultPolicy.Normalize(StableMissionKey.For(facts.Proposal), facts);
        }
        private static MissionIntent Intent(MissionKind kind, MissionIntentKey key) =>
            new MissionIntent { Kind = kind, IntentKey = key, CreatedTurn = 4,
                LastReconciledTurn = 3, LastProgressTurn = 4 };
    }
}
#endif
