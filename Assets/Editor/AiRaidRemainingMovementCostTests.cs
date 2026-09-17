#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiRaidRemainingMovementCostTests
    {
        [Test]
        public void PinnedSpentMover_ChargesNextTurnActivationWithoutReservingIt()
        {
            WorldSnapshot snap = Snapshot(distance: 3, currentMovement: 0,
                maxMovement: 3, activated: true, activationAp: 4);
            RaidCostEstimate estimate = RaidCostModel.Estimate(snap, Target(3), 9);

            Assert.That(estimate.PlannedMoverArmyId, Is.EqualTo(9));
            Assert.That(estimate.Requirements.MoverKnown, Is.True);
            Assert.That(estimate.Requirements.EstimatedDistance, Is.EqualTo(3));
            Assert.That(estimate.Requirements.EtaTurns, Is.EqualTo(2));
            Assert.That(estimate.Requirements.ApMinimum, Is.Zero);
            Assert.That(estimate.Requirements.ApDesired, Is.Zero);
            Assert.That(estimate.RecurringActivationAp, Is.EqualTo(4f));
            Assert.That(TaskScoreEvaluator.DeliveryFromEta(estimate.RecurringActivationAp,
                estimate.Requirements.EtaTurns, AiConfigV2.taskScoreReactivationApWeight),
                Is.EqualTo(4f));
        }

        [Test]
        public void PartiallySpentMover_ChargesEveryRequiredFutureActivation()
        {
            WorldSnapshot snap = Snapshot(distance: 5, currentMovement: 1,
                maxMovement: 3, activated: true, activationAp: 4);
            RaidCostEstimate estimate = RaidCostModel.Estimate(snap, Target(5), 9);

            Assert.That(estimate.Requirements.EtaTurns, Is.EqualTo(3));
            Assert.That(estimate.Requirements.ApDesired, Is.Zero);
            Assert.That(TaskScoreEvaluator.DeliveryFromEta(estimate.RecurringActivationAp,
                estimate.Requirements.EtaTurns, AiConfigV2.taskScoreReactivationApWeight),
                Is.EqualTo(8f));
        }

        [Test]
        public void EnoughRemainingMovement_KeepsSameTurnRaidFreeOfFutureDelivery()
        {
            WorldSnapshot snap = Snapshot(distance: 3, currentMovement: 3,
                maxMovement: 3, activated: true, activationAp: 4);
            RaidCostEstimate estimate = RaidCostModel.Estimate(snap, Target(3), 9);

            Assert.That(estimate.Requirements.EtaTurns, Is.EqualTo(1));
            Assert.That(estimate.Requirements.ApDesired, Is.Zero);
            Assert.That(TaskScoreEvaluator.DeliveryFromEta(estimate.RecurringActivationAp,
                estimate.Requirements.EtaTurns, AiConfigV2.taskScoreReactivationApWeight),
                Is.Zero);
        }

        [Test]
        public void FreshMover_PreservesCurrentTurnReservationSeparateFromDelivery()
        {
            WorldSnapshot snap = Snapshot(distance: 5, currentMovement: 3,
                maxMovement: 3, activated: false, activationAp: 2);
            RaidCostEstimate estimate = RaidCostModel.Estimate(snap, Target(5), 9);

            Assert.That(estimate.Requirements.EtaTurns, Is.EqualTo(2));
            Assert.That(estimate.Requirements.ApDesired, Is.EqualTo(2f));
            Assert.That(estimate.RecurringActivationAp, Is.EqualTo(2f));
            Assert.That(TaskScoreEvaluator.DeliveryFromEta(estimate.RecurringActivationAp,
                estimate.Requirements.EtaTurns, AiConfigV2.taskScoreReactivationApWeight),
                Is.EqualTo(2f));
        }

        private static WorldSnapshot Snapshot(int distance, int currentMovement,
            int maxMovement, bool activated, int activationAp) => new WorldSnapshot
        {
            Self = new SelfSnapshot
            {
                Citadel = new HexCoord(0, 0),
                Armies = new List<ArmySnapshot>
                {
                    new ArmySnapshot
                    {
                        ArmyId = 9,
                        Hex = new HexCoord(0, 0),
                        MemberCount = 1,
                        MaxMovement = maxMovement,
                        CurrentMovement = currentMovement,
                        HasActivatedThisTurn = activated,
                        ActivationApCost = activationAp,
                    },
                    // Deliberately cheaper, closer alternative: a pinned raid cannot use its cost.
                    new ArmySnapshot
                    {
                        ArmyId = 10,
                        Hex = new HexCoord(distance, 0),
                        MemberCount = 1,
                        MaxMovement = 3,
                        CurrentMovement = 3,
                        ActivationApCost = 1,
                    },
                },
            },
        };

        private static RaidMissionTarget Target(int distance) => new RaidMissionTarget
        {
            Phase = RaidMissionPhase.Assault,
            Target = RaidTargetRef.ForNeutralArmy(42),
            LastKnownHex = new HexCoord(distance, 0),
        };
    }
}
#endif
