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

        [Test]
        public void TessekT9_LostSameTurnCompletionDowngradesWithoutLosingMissionResources()
        {
            var player = new PlayerSetupData();
            const int turn = 9;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            var cost = new ResourceCost(energy: 2, materials: 2);
            MissionIntent intent = ActiveFoundBaseIntent(
                builderArmyId: 19, target: new HexCoord(3, 2), cost: cost, buildAp: 4f);
            string owner = EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey);

            // Earlier in the same turn the concrete actor was able to finish, so Provisioning
            // legitimately protected completion AP. Movement/world state then changed and Phase A
            // re-admitted the still-durable delivery as deferred work.
            InfrastructureFulfillment.ReserveEconomyCost(
                player, turn, owner, cost, 4f,
                StrategicReservationReason.EconomyBuildCompletion);
            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.ActionPoints), Is.EqualTo(4f));

            InfrastructureFulfillment.ReserveDeferredEconomyResourcesForActiveIntent(
                player, turn, intent);

            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(
                player, turn, owner, StrategicReservationReason.EconomyBuildCompletion), Is.False,
                "same-owner completion hold must be released immediately when completion is no longer current-turn executable");
            Assert.That(StrategicResourceReservationLedger.Active(
                player, turn, StrategicReservedResource.ActionPoints), Is.Zero);
            Assert.That(StrategicResourceReservationLedger.OwnerReasonMatches(
                player, turn, owner, StrategicReservationReason.EconomyDeferredBuild,
                cost, 0f), Is.True,
                "the durable H/E/M/T obligation survives the AP downgrade");
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
