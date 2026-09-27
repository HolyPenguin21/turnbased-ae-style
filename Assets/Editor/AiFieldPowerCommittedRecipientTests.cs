#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Log 2026-09-27, Orlan T4: Phase A joined "AA Crawler" to army #17 — the hero it had just
    // delivered to an Economy build — for a Raid FieldCombatPower demand. The plan-level gate
    // accepted it (shape Any skipped the committed-actor check), the post-play inventory counted
    // #17 as committed and measured 0: 1 AP and the card spent, the Raid demand unchanged.
    public class AiFieldPowerCommittedRecipientTests
    {
        private static AxisDemand FreePowerDemand() => new AxisDemand
        {
            RequestingAxis = DesireAxis.Aggression,
            Capability = CapabilityKind.FieldCombatPower,
            DeliveryShape = CapabilityDeliveryShape.Any,
            DesiredAmount = 5f,
            RequiredCapabilityPower = 5f,
        };

        private static MaterializationPlan JoinPlan(ArmyData recipient) => new MaterializationPlan
        {
            FinalCapability = CapabilityKind.FieldCombatPower,
            Deploy = new PlacementOption(new HexCoord(0, 0), DeploymentKind.ExistingArmy, recipient),
        };

        [Test]
        public void FreeFieldPower_JoiningAnArmyOwnedByAnotherIntentIsNotADelivery()
        {
            var player = new PlayerSetupData { Nickname = "CommittedRecipient" };
            MissionIntentRegistry.Clear();
            try
            {
                var recipient = new ArmyData();
                var free = new ArmyData();
                HexCoord site = new HexCoord(2, -3);
                MissionIntentRegistry.GetOrCreate(player).Put(new MissionIntent
                {
                    IntentKey = MissionIntentKey.ForEconomy(EconomyTaskKind.BuildExtraction, 0, site),
                    Kind = MissionKind.Economy,
                    Status = IntentStatus.Active,
                    Objective = new EconomyIntent { Kind = EconomyTaskKind.BuildExtraction, TargetHex = site },
                    PreferredMoverArmyId = recipient.Id,
                });

                var committed = MaterializationDeliveryPolicy.AssessDemandOperationally(
                    JoinPlan(recipient), FreePowerDemand(), null, player, null);
                Assert.That(committed.CanDeliver, Is.False);
                Assert.That(committed.FailureReason,
                    Is.EqualTo(MaterializationDeliveryPolicy.DeliveryFailureReason.WrongPlacement));

                Assert.That(MaterializationDeliveryPolicy.AssessDemandOperationally(
                        JoinPlan(free), FreePowerDemand(), null, player, null).CanDeliver, Is.True,
                    "an uncommitted recipient still delivers");
            }
            finally
            {
                MissionIntentRegistry.Clear();
            }
        }
    }
}
#endif
