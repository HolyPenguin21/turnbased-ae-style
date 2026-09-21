#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiEconomyReservationLifecycleTests
    {
        private static MissionIntent ActiveFoundBaseIntent(int builderArmyId, HexCoord target,
            ResourceCost cost, float buildAp)
        {
            var key = new StableMissionKey(MissionKind.Economy,
                (int)EconomyTaskKind.FoundBase, 0, target.Q, target.R);
            return new MissionIntent
            {
                Kind = MissionKind.Economy,
                Status = IntentStatus.Active,
                LastAttemptKey = key,
                PreferredMoverArmyId = builderArmyId,
                Objective = new EconomyIntent
                {
                    Kind = EconomyTaskKind.FoundBase,
                    TargetHex = target,
                    BuilderArmyId = builderArmyId,
                    BuildResourceCost = cost,
                    BuildApCost = buildAp,
                    MinimumFollowupAp = buildAp,
                },
            };
        }

        [Test]
        public void TessekT9_ActiveDeliveryProtectsPersistentResourcesButNotFutureBuildAp()
        {
            var player = new PlayerSetupData();
            const int turn = 9;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            var cost = new ResourceCost(human: 1, materials: 3);
            MissionIntent intent = ActiveFoundBaseIntent(
                builderArmyId: 19, target: new HexCoord(3, 2), cost: cost, buildAp: 4f);

            InfrastructureFulfillment.ReserveDeferredEconomyResourcesForActiveIntent(
                player, turn, intent);

            string owner = EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey);
            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.ActionPoints), Is.Zero,
                "delivery that cannot complete this turn must not remove AP from Phase B");
            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.Human), Is.EqualTo(1f));
            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.Materials), Is.EqualTo(3f));
            Assert.That(StrategicResourceReservationLedger.OwnerReasonMatches(
                player, turn, owner, StrategicReservationReason.EconomyDeferredBuild,
                cost, 4f), Is.True,
                "deferred matching treats future completion AP as non-reservable by definition");
        }

        // 2026-09-21 Block B — contract change. This case used to assert that ANY later deferred
        // request downgrades the owner's completion envelope. That made "is this build protected"
        // depend on which pass asked last, and (through the global HasReason test it relied on)
        // let one owner's completion decide another owner's protection. A provisioned completion
        // envelope is now the stronger stage of the SAME build and survives a repeated deferred
        // request; it is released by its own owner through the existing Execution/retirement
        // release path (TaskExecutor.ReleaseEconomyReservation / ReleaseByOwner), not as a side
        // effect of another pass re-stating the deferred obligation.
        [Test]
        public void TessekT9_ProvisionedCompletionSurvivesARepeatedDeferredRequest()
        {
            var player = new PlayerSetupData();
            const int turn = 9;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            var cost = new ResourceCost(energy: 2, materials: 2);
            MissionIntent intent = ActiveFoundBaseIntent(
                builderArmyId: 19, target: new HexCoord(3, 2), cost: cost, buildAp: 4f);
            string owner = EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey);

            // Earlier in the same turn the concrete actor was proved able to finish, so
            // Provisioning legitimately protected completion AP. A re-entrant Phase A pass in the
            // same turn then re-states the durable delivery's deferred obligation.
            InfrastructureFulfillment.ReserveEconomyCost(
                player, turn, owner, cost, 4f,
                StrategicReservationReason.EconomyBuildCompletion);
            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.ActionPoints), Is.EqualTo(4f));

            InfrastructureFulfillment.ReserveDeferredEconomyResourcesForActiveIntent(
                player, turn, intent);

            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(
                player, turn, owner, StrategicReservationReason.EconomyBuildCompletion), Is.True,
                "a provisioned completion envelope is the stronger stage of the same build");
            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.ActionPoints), Is.EqualTo(4f),
                "the AP the provisioned builder is about to spend must not be dropped");
            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(
                player, turn, owner, StrategicReservationReason.EconomyDeferredBuild), Is.False,
                "and the two stages never double-reserve the same build");
            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.Energy), Is.EqualTo(2f));
            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.Materials), Is.EqualTo(2f));
        }

        [Test]
        public void TessekT10_ReachingSitePromotesDeferredDeliveryToCompletionAp()
        {
            var player = new PlayerSetupData();
            const int turn = 10;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            var cost = new ResourceCost(human: 1, energy: 1, materials: 2);
            MissionIntent intent = ActiveFoundBaseIntent(
                builderArmyId: 19, target: new HexCoord(3, 2), cost: cost, buildAp: 4f);
            string owner = EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey);

            InfrastructureFulfillment.ReserveDeferredEconomyResourcesForActiveIntent(
                player, turn, intent);
            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.ActionPoints), Is.Zero);

            // This is the canonical Provisioning transition once CompletionThisTurn is true.
            InfrastructureFulfillment.ReserveEconomyCost(
                player, turn, owner, cost, 4f,
                StrategicReservationReason.EconomyBuildCompletion);

            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(
                player, turn, owner, StrategicReservationReason.EconomyDeferredBuild), Is.False);
            Assert.That(StrategicResourceReservationLedger.OwnerReasonMatches(
                player, turn, owner, StrategicReservationReason.EconomyBuildCompletion,
                cost, 4f), Is.True);
            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.ActionPoints), Is.EqualTo(4f),
                "completion AP becomes protected exactly when the actor can finish this turn");
        }
    }
}
#endif
