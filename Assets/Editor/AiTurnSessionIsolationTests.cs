#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai.V2;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiTurnSessionIsolationTests
    {
        [TearDown]
        public void Reset() { AiTurnSession.ClearAll(); MissionIntentRegistry.Clear(); }

        [Test]
        public void NewTurnHasFreshReconAndResourcesButKeepsIntent()
        {
            var player = new PlayerSetupData();
            MissionIntent intent = new MissionIntent { IntentKey = new MissionIntentKey(MissionKind.Scout, 0, 0, 2, 3) };
            using (var first = AiTurnSession.Begin(player, null, null, null, 4))
            {
                first.PersistentState.Put(intent);
                first.Recon.MarkReconGroundActorUsed(4, 0);
                first.Recon.MarkReconActorTrimmed(4, 7);
                first.Recon.TryConsumeReconLaneTrim(4);
                Reserve(player, 4);
                Assert.That(StrategicResourceReservationLedger.HasAny(player, 4), Is.True);
            }
            using (var next = AiTurnSession.Begin(player, null, null, null, 5))
            {
                Assert.That(next.Recon.ReconGroundActorsUsedThisTurn(5), Is.Empty);
                Assert.That(next.Recon.ReconActorsTrimmedThisTurn(5), Is.Empty);
                Assert.That(next.Recon.TryConsumeReconLaneTrim(5), Is.True);
                Assert.That(StrategicResourceReservationLedger.Rows(player, 5), Is.Empty);
                Assert.That(next.PersistentState.TryGet(intent.IntentKey, out var retained), Is.True);
                Assert.That(retained, Is.SameAs(intent));
            }
        }

        [Test]
        public void AnotherPlayerCannotSeeOrClearTheFirstPlayersTurnState()
        {
            var a = new PlayerSetupData(); var b = new PlayerSetupData();
            using var first = AiTurnSession.Begin(a, null, null, null, 4);
            first.Recon.MarkReconGroundActorUsed(4, 0); Reserve(a, 4);
            using (var other = AiTurnSession.Begin(b, null, null, null, 4))
            {
                Assert.That(other.Recon.ReconGroundActorsUsedThisTurn(4), Is.Empty);
                Assert.That(StrategicResourceReservationLedger.Rows(b, 4), Is.Empty);
            }
            Assert.That(first.Recon.ReconGroundActorsUsedThisTurn(4), Is.EqualTo(new[] { 0 }));
            Assert.That(StrategicResourceReservationLedger.HasAny(a, 4), Is.True);
        }

        [Test]
        public void EndIsIdempotentAndClosedSessionCannotPublish()
        {
            var p = new PlayerSetupData(); var session = AiTurnSession.Begin(p, null, null, null, 8);
            Reserve(p, 8); session.Recon.MarkReconGroundActorUsed(8, 9);
            session.Apply(new WorldDelta(false, StrategicInvalidationReason.Actor, new[] { 9 }));
            session.Dispose(); session.Dispose();
            Assert.That(StrategicResourceReservationLedger.HasAny(p, 8), Is.False);
            Assert.That(StrategicInterruptRegistry.Peek(p, 8).Any, Is.False);
            Assert.That(AiTurnSession.Peek(p, 8), Is.Null);
            Assert.Throws<ObjectDisposedException>(() => session.Apply(default));
            Assert.Throws<ObjectDisposedException>(() => { var unused = session.Recon; });
        }

        [Test]
        public void NextTurnClosesAbandonedCoroutineScopeAndSameTurnCannotNest()
        {
            var p = new PlayerSetupData(); var old = AiTurnSession.Begin(p, null, null, null, 8);
            Reserve(p, 8);
            Assert.Throws<InvalidOperationException>(() => AiTurnSession.Begin(p, null, null, null, 8));
            using var next = AiTurnSession.Begin(p, null, null, null, 9);
            Assert.That(StrategicResourceReservationLedger.Rows(p, 9), Is.Empty);
            Assert.Throws<ObjectDisposedException>(() => old.Apply(default));
            old.Dispose();
            Assert.That(AiTurnSession.Peek(p, 9), Is.SameAs(next));
        }

        [Test]
        public void ProvisioningPassesOwnIndependentTentativeClaims()
        {
            var p = new PlayerSetupData();
            using var turn = AiTurnSession.Begin(p, null, null, null, 8);
            var operation = new MissionIntentKey(MissionKind.Scout, 0, 0, 2, 3);
            turn.Leases.For(operation).Claim(42);
            var snapshot = new WorldSnapshot { Observer = p, TurnNumber = 8 };
            using var first = new ProvisioningSession(snapshot, turn);
            using var second = new ProvisioningSession(snapshot, turn);
            Assert.That(first.ClaimedArmyIds.Add(7), Is.True);
            Assert.That(first.ClaimedArmyIds.Add(7), Is.False);
            Assert.That(second.ClaimedArmyIds.Contains(7), Is.False);
            first.Dispose();
            Assert.Throws<ObjectDisposedException>(() => first.ClaimedArmyIds.Contains(7));
            Assert.Throws<ObjectDisposedException>(() => first.ClaimedArmyIds.Add(8));
            Assert.Throws<ObjectDisposedException>(() => first.RegisterSuccess(default,
                new ProvisionedMission { MoverArmyId = 8, ClaimedAp = 2 }));
            Assert.That(first.Successful, Is.Empty);
            Assert.That(first.ApClaimed, Is.Zero);
            Assert.That(second.ClaimedArmyIds.Add(7), Is.True);
            Assert.That(turn.Leases.For(operation).ActorClaims, Is.EqualTo(new[] { 42 }));
        }

        [Test]
        public void NextTurnClosesAbandonedProvisioningPass()
        {
            var p = new PlayerSetupData();
            var oldTurn = AiTurnSession.Begin(p, null, null, null, 8);
            using var oldPass = new ProvisioningSession(new WorldSnapshot { Observer = p, TurnNumber = 8 }, oldTurn);
            oldPass.ClaimedArmyIds.Add(7);
            using var nextTurn = AiTurnSession.Begin(p, null, null, null, 9);
            using var nextPass = new ProvisioningSession(new WorldSnapshot { Observer = p, TurnNumber = 9 }, nextTurn);
            Assert.That(nextPass.ClaimedArmyIds, Is.Empty);
            Assert.Throws<ObjectDisposedException>(() => oldPass.ClaimedArmyIds.Contains(7));
            Assert.Throws<ObjectDisposedException>(() => oldPass.ClaimedArmyIds.Add(8));
            oldTurn.Dispose();
            Assert.That(nextPass.ClaimedArmyIds.Add(7), Is.True);
        }

        [Test]
        public void ProvisioningCannotBindAnotherPlayerOrTurn()
        {
            var p = new PlayerSetupData();
            using var turn = AiTurnSession.Begin(p, null, null, null, 8);
            Assert.Throws<InvalidOperationException>(() => new ProvisioningSession(
                new WorldSnapshot { Observer = new PlayerSetupData(), TurnNumber = 8 }, turn));
            Assert.Throws<InvalidOperationException>(() => new ProvisioningSession(
                new WorldSnapshot { Observer = p, TurnNumber = 9 }, turn));
            using var own = new ProvisioningSession(new WorldSnapshot { Observer = p, TurnNumber = 8 }, turn);
            Assert.That(own.ClaimedArmyIds.Add(7), Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SettlementRejectsForeignFrameBeforeChangingOwnership(bool anotherTurn)
        {
            var p = new PlayerSetupData();
            using var turn = AiTurnSession.Begin(p, null, null, null, 8);
            var key = new MissionIntentKey(MissionKind.Development, 0, 0, 2, 3);
            var intent = new MissionIntent { IntentKey = key, Kind = MissionKind.Development };
            turn.PersistentState.Put(intent); turn.Leases.For(key).Claim(7);
            var foreign = new WorldSnapshot { Observer = anotherTurn ? p : new PlayerSetupData(),
                TurnNumber = anotherTurn ? 9 : 8 };
            Assert.Throws<InvalidOperationException>(() => turn.Settle(
                new MissionStepResult<DevelopmentStepPayload>(key,
                    MissionStepDisposition.PermanentFailure, null), foreign));
            Assert.That(turn.PersistentState.TryGet(key, out var retained), Is.True);
            Assert.That(retained, Is.SameAs(intent));
            Assert.That(turn.Leases.For(key).ActorClaims, Is.EqualTo(new[] { 7 }));
        }

        private static void Reserve(PlayerSetupData p, int turn) => StrategicResourceReservationLedger.Upsert(p, turn,
            new StrategicResourceReservation { Owner = "test", Resource = StrategicReservedResource.ActionPoints,
                Reason = StrategicReservationReason.StrategicReactionPass, Amount = 1,
                ExpirationStage = StrategicReservationExpiry.EndOfTurn });
    }
}
#endif
