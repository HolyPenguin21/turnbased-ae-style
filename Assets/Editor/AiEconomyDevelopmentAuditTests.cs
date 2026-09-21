#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Regression cover for the 2026-09-21 Economy / ActiveDefence / Development / Collector audit.
    // Every test here fails on the pre-fix code and passes after it.
    public class AiEconomyDevelopmentAuditTests
    {
        private static AxisDemand ExtractionDemand(HexCoord target, ResourceType type,
            CardData buildCard = null) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Economy,
            Capability = CapabilityKind.EconomicInfrastructure,
            TargetHex = target,
            EconomyResourceType = type,
            EconomyBuildCard = buildCard,
            EconomyBuildResourceCost = new ResourceCost(materials: 2),
        };

        // ---------------------------------------------------------------------------------------
        //  Block A — independent Economy build obligations
        // ---------------------------------------------------------------------------------------

        [Test]
        public void BlockA_TwoIndependentBuilds_DoNotSupersedeEachOther()
        {
            var player = new PlayerSetupData();
            MissionIntent first = MissionContinuityLayer.BeginEconomyDelivery(player,
                ExtractionDemand(new HexCoord(8, -2), ResourceType.Materials), 15, 5);
            MissionIntent second = MissionContinuityLayer.BeginEconomyDelivery(player,
                ExtractionDemand(new HexCoord(4, 5), ResourceType.Energy), 17, 5);

            var all = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Kind == MissionKind.Economy).ToList();

            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(all.Count, Is.EqualTo(2),
                "a second builder on a second site is not a conflict and must not retire the first");
            Assert.That(all.Select(i => i.PreferredMoverArmyId),
                Is.EquivalentTo(new int?[] { 15, 17 }));
        }

        [Test]
        public void BlockA_SameActorReassignment_SupersedesOnlyItsOwnAssignment()
        {
            var player = new PlayerSetupData();
            MissionContinuityLayer.BeginEconomyDelivery(player,
                ExtractionDemand(new HexCoord(8, -2), ResourceType.Materials), 15, 5);
            MissionContinuityLayer.BeginEconomyDelivery(player,
                ExtractionDemand(new HexCoord(4, 5), ResourceType.Energy), 17, 5);
            // #15 is re-tasked to a third site: its own old obligation goes, #17's stays.
            MissionContinuityLayer.BeginEconomyDelivery(player,
                ExtractionDemand(new HexCoord(1, 1), ResourceType.Human), 15, 6);

            var all = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Kind == MissionKind.Economy).ToList();

            Assert.That(all.Count, Is.EqualTo(2));
            Assert.That(all.Count(i => i.PreferredMoverArmyId == 15), Is.EqualTo(1),
                "one army may never hold two build assignments");
            Assert.That(all.Any(i => i.PreferredMoverArmyId == 17
                && i.Economy.TargetHex.Equals(new HexCoord(4, 5))), Is.True);
        }

        [Test]
        public void BlockA_SamePhysicalBuildCard_IsARealConflict()
        {
            var player = new PlayerSetupData();
            var shared = new CardData(new CardDefinition { cardType = CardType.Base });
            MissionContinuityLayer.BeginEconomyDelivery(player, new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicExpansionBase,
                TargetHex = new HexCoord(8, -2), EconomyBuildCard = shared,
            }, 15, 5);
            MissionContinuityLayer.BeginEconomyDelivery(player, new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicExpansionBase,
                TargetHex = new HexCoord(4, 5), EconomyBuildCard = shared,
            }, 17, 5);

            Assert.That(MissionIntentRegistry.GetOrCreate(player).All
                .Count(i => i != null && i.Kind == MissionKind.Economy), Is.EqualTo(1),
                "one physical card cannot fund two sites at once");
        }

        [Test]
        public void BlockA_RepeatedHandoffOfSameObjective_IsIdempotentAndKeepsHistory()
        {
            var player = new PlayerSetupData();
            AxisDemand demand = ExtractionDemand(new HexCoord(8, -2), ResourceType.Materials);
            MissionIntent created = MissionContinuityLayer.BeginEconomyDelivery(player, demand, 15, 5);
            created.StepsMovedTotal = 3;
            created.CumulativeApSpent = 2f;

            for (int i = 0; i < 10; ++i)
                MissionContinuityLayer.BeginEconomyDelivery(player, demand, 15, 5 + i);

            var all = MissionIntentRegistry.GetOrCreate(player).All
                .Where(i => i != null && i.Kind == MissionKind.Economy).ToList();
            Assert.That(all.Count, Is.EqualTo(1));
            Assert.That(all[0], Is.SameAs(created), "re-entry must reuse the live commitment");
            Assert.That(all[0].CreatedTurn, Is.EqualTo(5));
            Assert.That(all[0].StepsMovedTotal, Is.EqualTo(3));
            Assert.That(all[0].CumulativeApSpent, Is.EqualTo(2f));
        }

        // ---------------------------------------------------------------------------------------
        //  Block B — owner-independent resource reservations
        // ---------------------------------------------------------------------------------------

        private static void ReserveDeferred(PlayerSetupData player, int turn, HexCoord hex,
            ResourceType type, ResourceCost cost) =>
            // The real production entry point for an accepted-but-not-yet-staffed Economy build.
            InfrastructureFulfillment.ReserveDeferredEconomyResourcesForPendingHero(player, turn,
                new AxisDemand
                {
                    RequestingAxis = DesireAxis.Economy,
                    Capability = CapabilityKind.Hero,
                    TargetHex = hex,
                    EconomyResourceType = type,
                    EconomyBuildResourceCost = cost,
                });

        private static string OwnerFor(HexCoord hex, ResourceType type) =>
            InfrastructureFulfillment.EconomyReservationOwner(new AxisDemand
            {
                Capability = CapabilityKind.EconomicInfrastructure,
                TargetHex = hex,
                EconomyResourceType = type,
            });

        [Test]
        public void BlockB_CompletionOfOneOwnerDoesNotSuppressAnotherOwnersDeferredHold()
        {
            var player = new PlayerSetupData();
            const int turn = 7;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            string a = OwnerFor(new HexCoord(8, -2), ResourceType.Materials);
            string b = OwnerFor(new HexCoord(4, 5), ResourceType.Energy);
            var costA = new ResourceCost(materials: 3);
            var costB = new ResourceCost(energy: 2);

            InfrastructureFulfillment.ReserveEconomyCost(player, turn, a, costA, 4f,
                StrategicReservationReason.EconomyBuildCompletion);
            ReserveDeferred(player, turn, new HexCoord(4, 5), ResourceType.Energy, costB);

            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn, a,
                StrategicReservationReason.EconomyBuildCompletion), Is.True);
            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn, b,
                StrategicReservationReason.EconomyDeferredBuild), Is.True,
                "owner A's completion must not decide whether owner B's build is protected");
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Energy), Is.EqualTo(2f));
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Materials), Is.EqualTo(3f));
        }

        [Test]
        public void BlockB_TwoDeferredOwnersCoexist()
        {
            var player = new PlayerSetupData();
            const int turn = 7;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            string a = OwnerFor(new HexCoord(8, -2), ResourceType.Materials);
            string b = OwnerFor(new HexCoord(4, 5), ResourceType.Energy);

            ReserveDeferred(player, turn, new HexCoord(8, -2), ResourceType.Materials,
                new ResourceCost(materials: 3));
            ReserveDeferred(player, turn, new HexCoord(4, 5), ResourceType.Energy,
                new ResourceCost(energy: 2));

            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn, a,
                StrategicReservationReason.EconomyDeferredBuild), Is.True,
                "a second deferred owner must not replace the first");
            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn, b,
                StrategicReservationReason.EconomyDeferredBuild), Is.True);
        }

        [Test]
        public void BlockB_DeferredToCompletionTransitionIsOwnerScoped()
        {
            var player = new PlayerSetupData();
            const int turn = 7;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            string a = OwnerFor(new HexCoord(8, -2), ResourceType.Materials);
            string b = OwnerFor(new HexCoord(4, 5), ResourceType.Energy);
            var costB = new ResourceCost(energy: 2);

            ReserveDeferred(player, turn, new HexCoord(8, -2), ResourceType.Materials,
                new ResourceCost(materials: 3));
            ReserveDeferred(player, turn, new HexCoord(4, 5), ResourceType.Energy, costB);
            // B's builder reaches its site and Provisioning promotes only B.
            InfrastructureFulfillment.ReserveEconomyCost(player, turn, b, costB, 4f,
                StrategicReservationReason.EconomyBuildCompletion);

            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn, a,
                StrategicReservationReason.EconomyDeferredBuild), Is.True,
                "promoting one build must not release every other build's protected resources");
            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn, b,
                StrategicReservationReason.EconomyDeferredBuild), Is.False,
                "B's own deferred stage is superseded by its completion envelope");
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Energy), Is.EqualTo(2f),
                "no double reservation across the same owner's two stages");
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.ActionPoints), Is.EqualTo(4f));
        }

        [Test]
        public void BlockB_OwnValidCompletionSurvivesARepeatedDeferredRequest()
        {
            var player = new PlayerSetupData();
            const int turn = 7;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            string b = OwnerFor(new HexCoord(4, 5), ResourceType.Energy);
            var costB = new ResourceCost(energy: 2);

            InfrastructureFulfillment.ReserveEconomyCost(player, turn, b, costB, 4f,
                StrategicReservationReason.EconomyBuildCompletion);
            // A re-entrant Phase A pass in the SAME turn asks for the deferred hold again.
            ReserveDeferred(player, turn, new HexCoord(4, 5), ResourceType.Energy, costB);

            Assert.That(StrategicResourceReservationLedger.HasOwnerReason(player, turn, b,
                StrategicReservationReason.EconomyBuildCompletion), Is.True,
                "a provisioned completion envelope is the stronger stage of the same build");
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.ActionPoints), Is.EqualTo(4f),
                "the AP a provisioned builder is about to spend must not be dropped");
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Energy), Is.EqualTo(2f));
        }

        [Test]
        public void BlockB_ReleaseByOwnerTouchesOnlyThatOwner()
        {
            var player = new PlayerSetupData();
            const int turn = 7;
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            string a = OwnerFor(new HexCoord(8, -2), ResourceType.Materials);
            string b = OwnerFor(new HexCoord(4, 5), ResourceType.Energy);
            ReserveDeferred(player, turn, new HexCoord(8, -2), ResourceType.Materials,
                new ResourceCost(materials: 3));
            ReserveDeferred(player, turn, new HexCoord(4, 5), ResourceType.Energy,
                new ResourceCost(energy: 2));

            StrategicResourceReservationLedger.ReleaseByOwner(player, turn, a);

            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Materials), Is.Zero);
            Assert.That(StrategicResourceReservationLedger.Active(player, turn,
                StrategicReservedResource.Energy), Is.EqualTo(2f));
        }
    }
}
#endif
