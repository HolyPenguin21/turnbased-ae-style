#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiCollectorCapabilityDeliveryTests
    {
        private static AxisDemand Demand(ResourceType resource = ResourceType.Materials) =>
            new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.CollectorCapability,
                EconomyResourceType = resource,
                TargetHex = new HexCoord(5, 2),
                DesiredAmount = 1f,
            };

        private static ArmySnapshot Collector(int armyId, float materials = 1f, float energy = 0f) =>
            new ArmySnapshot
            {
                ArmyId = armyId,
                MemberCount = 1,
                CollectionCapacity = new ResourceBundle { Materials = materials, Energy = energy },
            };

        private static WorldSnapshot Snapshot(params ArmySnapshot[] armies) =>
            new WorldSnapshot { Self = new SelfSnapshot { Armies = armies } };

        [Test]
        public void Collector_RequiresMatchingResourceAndSoloMobileFieldArmy()
        {
            var demand = Demand();
            Assert.That(MaterializationDeliveryPolicy.IsArmyOperationalForDemand(
                Collector(3), demand), Is.True);
            Assert.That(MaterializationDeliveryPolicy.IsArmyOperationalForDemand(
                Collector(4, materials: 0f, energy: 1f), demand), Is.False,
                "a different resource is not delivered collection capacity");
            ArmySnapshot garrison = Collector(5);
            garrison.IsGarrison = true;
            Assert.That(MaterializationDeliveryPolicy.IsArmyOperationalForDemand(
                garrison, demand), Is.False);
            ArmySnapshot combinedArmy = Collector(6);
            combinedArmy.MemberCount = 2;
            Assert.That(MaterializationDeliveryPolicy.IsArmyOperationalForDemand(
                combinedArmy, demand), Is.False,
                "the collector demand requires the solo actor emitted by enumeration");
            ArmySnapshot airArmy = Collector(7);
            airArmy.IsAir = true;
            Assert.That(MaterializationDeliveryPolicy.IsArmyOperationalForDemand(
                airArmy, demand), Is.False);
        }

        [Test]
        public void NewCollector_ClosesResidualAndProtectsItsPhysicalArmy()
        {
            var player = new PlayerSetupData();
            var ctx = new AiTurnContext { TurnNumber = 7 };
            var snap = Snapshot(Collector(19));
            IReadOnlyList<int> physical = CapabilityDeliveryEvaluator.OperationalLeaseArmyIds(
                new HashSet<int> { 3 }, snap, null, Demand());
            Assert.That(physical, Is.EquivalentTo(new[] { 19 }));

            bool completed = CapabilityDeliveryEvaluator.FinalizeOperationalDelivery(
                player, ctx, snap, null, Demand(),
                new CapabilityInventory(), new CapabilityInventory(),
                new HashSet<int> { 3 }, out float delivered);

            Assert.That(completed, Is.True,
                "collector supply must not be measured through the combat/scout-only inventory");
            Assert.That(delivered, Is.EqualTo(1f));
            Assert.That(StrategicCapabilityLeaseRegistry.IsLeased(player, ctx.TurnNumber, 19), Is.True,
                "the new solo collector must survive same-turn housekeeping until mission admission");
        }

        [Test]
        public void OldOrWrongResourceCollector_DoesNotFalselyCompleteDemand()
        {
            var player = new PlayerSetupData();
            var ctx = new AiTurnContext { TurnNumber = 8 };
            var alreadyOwned = Snapshot(Collector(19));
            bool oldCompleted = CapabilityDeliveryEvaluator.FinalizeOperationalDelivery(
                player, ctx, alreadyOwned, null, Demand(),
                new CapabilityInventory(), new CapabilityInventory(),
                new HashSet<int> { 19 }, out float oldDelivered);
            Assert.That(oldCompleted, Is.False);
            Assert.That(oldDelivered, Is.Zero,
                "existing supply must not be counted as this materialization's delivery");

            var wrongResource = Snapshot(Collector(20, materials: 0f, energy: 1f));
            bool wrongCompleted = CapabilityDeliveryEvaluator.FinalizeOperationalDelivery(
                player, ctx, wrongResource, null, Demand(),
                new CapabilityInventory(), new CapabilityInventory(),
                new HashSet<int>(), out float wrongDelivered);
            Assert.That(wrongCompleted, Is.False);
            Assert.That(wrongDelivered, Is.Zero);
        }
    }
}
#endif
