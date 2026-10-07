#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiMissionLeaseLifecycleTests
    {
        [TearDown] public void Reset() { MissionIntentRegistry.Clear(); AiAllocatorStateRegistry.Clear(); }

        [TestCase(MissionStepDisposition.Progress, true)]
        [TestCase(MissionStepDisposition.Waiting, true)]
        [TestCase(MissionStepDisposition.Replan, true)]
        [TestCase(MissionStepDisposition.Completed, false)]
        [TestCase(MissionStepDisposition.Invalidated, false)]
        [TestCase(MissionStepDisposition.PermanentFailure, false)]
        public void DomainTransitionRetainsOrReleasesOnlyItsOwnLease(MissionStepDisposition disposition, bool retained)
        {
            var player = new PlayerSetupData();
            using var session = AiTurnSession.Begin(player, null, null, null, 4);
            var key = new MissionIntentKey(MissionKind.Development, 0, 0, 2, 3);
            var otherKey = new MissionIntentKey(MissionKind.Development, 0, 0, 7, 3);
            var intent = new MissionIntent { Kind = MissionKind.Development, IntentKey = key, CreatedTurn = 4 };
            session.PersistentState.Put(intent);
            MissionLease lease = session.Leases.For(key), other = session.Leases.For(otherKey);
            lease.Claim(0); other.Claim(1);
            lease.Reserve(StrategicReservationReason.EconomyBuildCompletion, StrategicReservedResource.ActionPoints, 2);
            other.Reserve(StrategicReservationReason.EconomyBuildCompletion, StrategicReservedResource.ActionPoints, 3);
            MissionContinuityLayer.ReconcileStep(player, 4, new MissionTurnOutcome
            {
                IntentKey = key, MissionKind = MissionKind.Development, Disposition = disposition,
                MadeProgress = disposition == MissionStepDisposition.Progress,
                ObjectiveSatisfied = disposition == MissionStepDisposition.Completed,
            });
            Assert.That(session.PersistentState.TryGet(key, out _), Is.EqualTo(retained));
            Assert.That(lease.ActorClaims.Count > 0, Is.EqualTo(retained));
            Assert.That(lease.ResourceClaims.Count > 0, Is.EqualTo(retained));
            Assert.That(other.ActorClaims, Is.EqualTo(new[] { 1 }));
            Assert.That(other.ResourceClaims.Count, Is.EqualTo(1));
            Assert.That(other.ResourceClaims[0].Amount, Is.EqualTo(3));
        }

        [Test]
        public void TerminalFreshAttemptReleasesItsPreIntentReservationInTheSameTurn()
        {
            var p = new PlayerSetupData(); using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = MissionIntentKey.ForEconomy(EconomyTaskKind.FoundBase, 0, new HexCoord(2, 3));
            var otherKey = MissionIntentKey.ForEconomy(EconomyTaskKind.FoundBase, 0, new HexCoord(6, 3));
            var failed = session.Leases.For(key); var other = session.Leases.For(otherKey);
            failed.Reserve(StrategicReservationReason.EconomyDeferredBuild, StrategicReservedResource.Materials, 3);
            other.Reserve(StrategicReservationReason.EconomyDeferredBuild, StrategicReservedResource.Materials, 4);
            Assert.That(session.PersistentState.Count, Is.Zero);
            MissionContinuityLayer.ReconcileStep(p, 4, new MissionTurnOutcome
            { IntentKey = key, MissionKind = MissionKind.Economy, Disposition = MissionStepDisposition.PermanentFailure });
            Assert.That(failed.ResourceClaims, Is.Empty);
            Assert.That(other.ResourceClaims[0].Amount, Is.EqualTo(4));
        }

        [TestCase(MissionKind.Attack)]
        [TestCase(MissionKind.Raid)]
        public void InvalidSupportReleasesOnlyThatOperationsActorAtSettlement(MissionKind kind)
        {
            var p = new PlayerSetupData();
            using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = new MissionIntentKey(kind, 0, 0, 2, 3);
            var otherKey = new MissionIntentKey(MissionKind.Economy, 0, 0, 5, 3);
            var intent = new MissionIntent { IntentKey = key, Kind = kind, Status = IntentStatus.Active };
            if (kind == MissionKind.Attack)
            {
                var attack = new AttackIntent { Phase = AttackMissionPhase.Gather };
                attack.GatherSupportArmyIds.Add(7); attack.GatherSupportArmyIds.Add(8);
                intent.Objective = attack;
            }
            else intent.Objective = new RaidIntent { Phase = RaidMissionPhase.Reinforcement, SupportArmyId = 7 };
            session.PersistentState.Put(intent);
            var lease = session.Leases.For(key); lease.Claim(7);
            if (kind == MissionKind.Attack) lease.Claim(8);
            lease.Reserve(StrategicReservationReason.EconomyDeferredBuild, StrategicReservedResource.Materials, 3);
            session.Leases.For(otherKey).Claim(7);
            session.Leases.ClaimForPass(99, ArmyMutationContract.FullyProtected);
            var snapshot = new WorldSnapshot { Observer = p, TurnNumber = 4,
                Self = new SelfSnapshot { Armies = new[] {
                    new ArmySnapshot { ArmyId = 7, MemberCount = 1 },
                    new ArmySnapshot { ArmyId = 8, MemberCount = 1 } } } };
            var result = new MissionTurnOutcome { IntentKey = key, MissionKind = kind,
                Disposition = MissionStepDisposition.PermanentFailure,
                ProvisionFailureKindValue = ProvisionFailureKind.AssemblyInfeasible };
            if (kind == MissionKind.Attack)
            {
                result.HasAttackPayload = true;
                result.AttackTarget = new AttackMissionTarget { Phase = AttackMissionPhase.Gather, SupportArmyId = 7 };
            }
            else
            {
                result.HasRaidPayload = true; result.RaidPhase = RaidMissionPhase.Reinforcement;
                result.RaidSupportArmyId = 7;
            }
            session.Settle(result, snapshot);
            Assert.That(session.PersistentState.TryGet(key, out _), Is.True);
            Assert.That(lease.ActorClaims, Is.EqualTo(kind == MissionKind.Attack ? new[] { 8 } : Array.Empty<int>()));
            Assert.That(lease.ResourceClaims.Count, Is.EqualTo(1));
            Assert.That(session.Leases.For(otherKey).ActorClaims, Is.EqualTo(new[] { 7 }));
            Assert.That(session.Leases.IsClaimed(99), Is.True);
        }

        [Test]
        public void StaleResourceWriteCannotResetANewTurnsReservationStorage()
        {
            var p = new PlayerSetupData(); using var session = AiTurnSession.Begin(p, null, null, null, 5);
            var key = MissionIntentKey.ForEconomy(EconomyTaskKind.FoundBase, 0, new HexCoord(2, 3));
            var lease = session.Leases.For(key);
            lease.Reserve(StrategicReservationReason.EconomyBuildCompletion, StrategicReservedResource.ActionPoints, 3);
            Assert.Throws<InvalidOperationException>(() => MissionLeaseBook.Upsert(p, 4,
                new StrategicResourceReservation { Owner = "stale", Amount = 1 }));
            Assert.That(lease.ResourceClaims[0].Amount, Is.EqualTo(3));
        }

        [Test]
        public void RemovingOneOwnerRestoresTheOtherOwnersMutationContract()
        {
            var book = new MissionLeaseBook();
            var a = new MissionIntentKey(MissionKind.Attack, 0, 0, 1, 1);
            var b = new MissionIntentKey(MissionKind.Raid, 0, 2, 0, 0);
            book.For(a).Claim(0, ArmyMutationContract.PreparationHost());
            book.For(b).Claim(0, ArmyMutationContract.FullyProtected);
            var view = new ActorCommitments(book);
            Assert.That(view.MutationContractOf(0).MayReceive, Is.False);
            book.Retire(b);
            Assert.That(view.OwnersOf(0), Is.EqualTo(new[] { a }));
            Assert.That(view.MutationContractOf(0).MayReceive, Is.True);
            Assert.That(view.MutationContractOf(0).MayReleaseExcessHeroes, Is.True);
            book.Retire(a);
            Assert.That(view.IsArmyClaimed(0), Is.False);
            Assert.That(view.MutationContractOf(0), Is.Null);
        }

        [Test]
        public void ExistingReconRekeyTransfersOwnershipWithoutRetiringTheRole()
        {
            var player = new PlayerSetupData();
            using var session = AiTurnSession.Begin(player, null, null, null, 4);
            var oldKey = new MissionIntentKey(MissionKind.Scout, 0, 0, 1, 2);
            var newKey = new MissionIntentKey(MissionKind.Scout, 0, 0, 5, 2);
            var intent = new MissionIntent { Kind = MissionKind.Scout, IntentKey = oldKey };
            session.PersistentState.Put(intent); session.Leases.For(oldKey).Claim(7);
            intent.IntentKey = newKey;
            session.PersistentState.Remove(oldKey); session.PersistentState.Put(intent);
            Assert.That(session.Leases.For(newKey).ActorClaims, Is.EqualTo(new[] { 7 }));
            Assert.That(session.Leases.For(oldKey).ActorClaims, Is.Empty);
            Assert.That(session.PersistentState.Count, Is.EqualTo(1));
        }

        [TestCase(MissionKind.Raid)]
        [TestCase(MissionKind.Scout)]
        [TestCase(MissionKind.Attack)]
        public void CompletingASublegDoesNotRetireTheDurableOperation(MissionKind kind)
        {
            var player = new PlayerSetupData();
            using var session = AiTurnSession.Begin(player, null, null, null, 4);
            var key = new MissionIntentKey(kind, 0, 0, 2, 3);
            var intent = new MissionIntent { Kind = kind, IntentKey = key, CreatedTurn = 4 };
            if (kind == MissionKind.Raid) intent.Objective = new RaidIntent { Phase = RaidMissionPhase.Assault };
            if (kind == MissionKind.Scout) intent.Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore };
            if (kind == MissionKind.Attack) intent.Objective = new AttackIntent { Phase = AttackMissionPhase.Gather };
            session.PersistentState.Put(intent); var lease = session.Leases.For(key); lease.Claim(7);
            MissionContinuityLayer.ReconcileStep(player, 4, new MissionTurnOutcome
            {
                IntentKey = key, MissionKind = kind, Disposition = MissionStepDisposition.Completed,
                ObjectiveSatisfied = true, ObjectiveSatisfiedExternally = kind == MissionKind.Scout,
                HasRaidPayload = kind == MissionKind.Raid, RaidPhase = RaidMissionPhase.Assault,
                HasAttackPayload = kind == MissionKind.Attack,
                AttackTarget = new AttackMissionTarget { Phase = AttackMissionPhase.Gather },
            });
            Assert.That(session.PersistentState.TryGet(key, out _), Is.True);
            Assert.That(lease.ActorClaims, Is.EqualTo(new[] { 7 }));
        }

        [Test]
        public void TurnEndClosesOldLeaseAndNextTurnHasNoClaims()
        {
            var player = new PlayerSetupData();
            var old = AiTurnSession.Begin(player, null, null, null, 4);
            var key = MissionIntentKey.ForEconomy(EconomyTaskKind.FoundBase, 0, new HexCoord(1, 2));
            var lease = old.Leases.For(key); lease.Claim(0);
            lease.Reserve(StrategicReservationReason.EconomyBuildCompletion, StrategicReservedResource.ActionPoints, 2);
            old.Dispose();
            Assert.Throws<ObjectDisposedException>(() => lease.Claim(9));
            using var next = AiTurnSession.Begin(player, null, null, null, 5);
            Assert.That(next.Leases.For(key).ActorClaims, Is.Empty);
            Assert.That(next.Leases.For(key).ResourceClaims, Is.Empty);
        }

        [Test]
        public void EconomyUpgradeDowngradeAndAbortRemainOwnerScopedAndApNeverBecomesDeferred()
        {
            var p = new PlayerSetupData(); using var session = AiTurnSession.Begin(p, null, null, null, 4);
            var key = MissionIntentKey.ForEconomy(EconomyTaskKind.FoundBase, 0, new HexCoord(2, 3));
            var otherKey = MissionIntentKey.ForEconomy(EconomyTaskKind.FoundBase, 0, new HexCoord(6, 3));
            var lease = session.Leases.For(key); var other = session.Leases.For(otherKey);
            var cost = new ResourceCost { materials = 3 };
            InfrastructureFulfillment.ReserveEconomyCost(p, 4, ReservationOwner.ForOperation(key), cost, 9,
                StrategicReservationReason.EconomyDeferredBuild);
            other.Reserve(StrategicReservationReason.EconomyBuildCompletion, StrategicReservedResource.ActionPoints, 2);
            Assert.That(lease.ResourceClaims.Count, Is.EqualTo(1)); // no deferred AP
            InfrastructureFulfillment.ReserveEconomyCost(p, 4, ReservationOwner.ForOperation(key), cost, 4);
            InfrastructureFulfillment.ReserveEconomyCost(p, 4, ReservationOwner.ForOperation(key), cost, 4);
            Assert.That(lease.ResourceClaims.Count, Is.EqualTo(2)); // idempotent, no double cost
            var intent = new MissionIntent { Kind = MissionKind.Economy, IntentKey = key,
                LastAttemptKey = StableMissionKey.ForEconomy(EconomyTaskKind.FoundBase, 0, new HexCoord(2, 3)),
                Objective = new EconomyIntent { Kind = EconomyTaskKind.FoundBase, BuildResourceCost = cost } };
            InfrastructureFulfillment.ReconcileEconomyCompletionOwner(p, 4, ReservationOwner.ForOperation(key),
                intent, durableValid: true, completionThisTurn: false);
            Assert.That(lease.ResourceClaims.Count, Is.EqualTo(1));
            Assert.That(lease.ResourceClaims[0].Reason, Is.EqualTo(StrategicReservationReason.EconomyDeferredBuild));
            Assert.That(lease.ResourceClaims[0].Identity.Operation, Is.EqualTo(key));
            Assert.That(other.ResourceClaims[0].Amount, Is.EqualTo(2));
            session.Leases.Retire(key);
            Assert.That(lease.ResourceClaims, Is.Empty);
            Assert.That(other.ResourceClaims[0].Amount, Is.EqualTo(2));
        }

        [Test]
        public void TypedEconomyOwnerPreservesTheExistingBankTokenAndSurvivesDetachedCopies()
        {
            var player = new PlayerSetupData();
            using var session = AiTurnSession.Begin(player, null, null, null, 4);
            var key = MissionIntentKey.ForEconomy(EconomyTaskKind.BuildExtraction, 2, new HexCoord(-3, 6));
            var lease = session.Leases.For(key);
            lease.Reserve(StrategicReservationReason.EconomyDeferredBuild, StrategicReservedResource.Materials, 3);
            var row = lease.ResourceClaims[0];
            Assert.That(row.Identity.Operation, Is.EqualTo(key));
            Assert.That(row.Owner, Is.EqualTo(EconomyMissionPlanner.OwnerKey(
                StableMissionKey.ForEconomy(EconomyTaskKind.BuildExtraction, 2, new HexCoord(-3, 6)))));
            row.Amount = 99;
            Assert.That(lease.ResourceClaims[0].Amount, Is.EqualTo(3));
        }
    }
}
#endif
