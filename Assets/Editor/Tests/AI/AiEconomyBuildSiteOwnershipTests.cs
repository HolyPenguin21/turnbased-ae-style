#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiEconomyBuildSiteOwnershipTests
    {
        private static MissionIntent Build(EconomyTaskKind kind, HexCoord hex, int actor)
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy,
                Status = IntentStatus.Active,
                PreferredMoverArmyId = actor,
                Objective = new EconomyIntent
                {
                    Kind = kind, TargetHex = hex, BuilderArmyId = actor,
                },
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            intent.LastAttemptKey = new StableMissionKey(MissionKind.Economy,
                (int)kind, 0, hex.Q, hex.R);
            return intent;
        }

        private static AxisDemand Demand(HexCoord hex) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Economy,
            Capability = CapabilityKind.EconomicInfrastructure,
            TargetHex = hex,
        };

        [Test]
        public void DistinctActorAndCard_CannotOverwriteFoundBaseWithExtractionOnSameHex()
        {
            var player = new PlayerSetupData();
            var site = new HexCoord(4, 3);
            var incumbent = Build(EconomyTaskKind.FoundBase, site, 12);
            MissionIntentRegistry.GetOrCreate(player).Put(incumbent);
            Assert.That(MissionContinuityLayer.BeginEconomyDelivery(player, Demand(site), 20, 3),
                Is.Null);
            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(
                incumbent.IntentKey, out MissionIntent actual), Is.True);
            Assert.That(actual, Is.SameAs(incumbent));
        }

        [Test]
        public void IndependentSites_DoNotConflict()
        {
            var player = new PlayerSetupData();
            var incumbent = Build(EconomyTaskKind.FoundBase, new HexCoord(4, 3), 12);
            MissionIntentRegistry.GetOrCreate(player).Put(incumbent);
            Assert.That(MissionContinuityLayer.BeginEconomyDelivery(player,
                Demand(new HexCoord(7, 3)), 20, 3), Is.Not.Null);
            Assert.That(MissionIntentRegistry.GetOrCreate(player).TryGet(
                incumbent.IntentKey, out MissionIntent actual), Is.True);
            Assert.That(actual, Is.SameAs(incumbent));
        }

        [Test]
        public void SameDeliveryReentry_IsIdempotent()
        {
            var player = new PlayerSetupData();
            var site = new HexCoord(4, 3);
            MissionIntent first = MissionContinuityLayer.BeginEconomyDelivery(
                player, Demand(site), 20, 3);
            MissionIntent second = MissionContinuityLayer.BeginEconomyDelivery(
                player, Demand(site), 20, 4);
            Assert.That(second, Is.SameAs(first));
            Assert.That(second.CreatedTurn, Is.EqualTo(3));
        }

        [Test]
        public void RecoveryAndMobileCollection_DoNotLeaseBuildSite()
        {
            var hex = new HexCoord(4, 3);
            Assert.That(MissionContinuityLayer.HoldsEconomyBuildSite(
                Build(EconomyTaskKind.ReturnBuilder, hex, 1), hex), Is.False);
            Assert.That(MissionContinuityLayer.HoldsEconomyBuildSite(
                Build(EconomyTaskKind.MobileCollection, hex, 2), hex), Is.False);
            Assert.That(MissionContinuityLayer.HoldsEconomyBuildSite(
                Build(EconomyTaskKind.BuildExtraction, hex, 3), hex), Is.True);
        }
    }
}
#endif
