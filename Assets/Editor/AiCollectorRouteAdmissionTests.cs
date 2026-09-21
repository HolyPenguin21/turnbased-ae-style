#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiCollectorRouteAdmissionTests
    {
        private static AxisDemand Demand() => new AxisDemand
        {
            RequestingAxis = DesireAxis.Economy,
            Capability = CapabilityKind.CollectorCapability,
            EconomyResourceType = ResourceType.Materials,
            TargetHex = new HexCoord(5, 2),
            DesiredAmount = 1f,
        };

        private static MaterializationPlan Plan(DeploymentKind kind = DeploymentKind.NewArmy) =>
            new MaterializationPlan
            {
                FinalCapability = CapabilityKind.CollectorCapability,
                Deploy = new PlacementOption(new HexCoord(0, 0), kind, null),
            };

        [Test]
        public void CollectorPreflight_MustNotClaimDeliveryWithoutWorldAndRoute()
        {
            var demand = Demand();
            var player = new PlayerSetupData();
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot { BaseHexes = new[] { new HexCoord(0, 0) } },
            };
            var result = MaterializationDeliveryPolicy.AssessDemandOperationally(
                Plan(), demand, snapshot, player, new AiTurnContext());
            Assert.That(result.CanDeliver, Is.False);
            Assert.That(result.FailureReason,
                Is.EqualTo(MaterializationDeliveryPolicy.DeliveryFailureReason.MissingWorldContext));
        }

        [Test]
        public void CollectorPreflight_StillRejectsNonSoloDeploymentFirst()
        {
            var result = MaterializationDeliveryPolicy.AssessDemandOperationally(
                Plan(DeploymentKind.Garrison), Demand());
            Assert.That(result.CanDeliver, Is.False);
            Assert.That(result.FailureReason,
                Is.EqualTo(MaterializationDeliveryPolicy.DeliveryFailureReason.WrongPlacement));
        }

        [Test]
        public void CollectorPreflight_StillRequiresTargetResourceAndHex()
        {
            var demand = Demand();
            demand.EconomyResourceType = null;
            var result = MaterializationDeliveryPolicy.AssessDemandOperationally(
                Plan(), demand);
            Assert.That(result.CanDeliver, Is.False);
            Assert.That(result.FailureReason,
                Is.EqualTo(MaterializationDeliveryPolicy.DeliveryFailureReason.MissingTarget));
        }
    }
}
#endif
