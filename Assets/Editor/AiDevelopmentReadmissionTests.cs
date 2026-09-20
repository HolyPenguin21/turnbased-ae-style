#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiDevelopmentReadmissionTests
    {
        private static WorldSnapshot Snapshot(ArmySnapshot army,
            bool staffed = false, int upgradeTargets = 1)
        {
            return new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    BaseHexes = new[] { new HexCoord(0, 0) },
                    Armies = new[] { army },
                },
                Development = new DevelopmentReadiness
                {
                    Facilities = new[]
                    {
                        new DevelopmentFacility
                        {
                            Hex = new HexCoord(0, 0),
                            Mode = ResearchProductionMode.Research,
                            HasHero = staffed,
                        },
                    },
                    AnyFacilityWithHero = staffed,
                    AnyOperatorlessFacility = !staffed,
                    DevPathViable = true,
                    UpgradeTargetCount = upgradeTargets,
                },
            };
        }

        private static ArmySnapshot Army(bool developmentOperator = false) => new ArmySnapshot
        {
            ArmyId = 9,
            Hex = new HexCoord(4, 4),
            MemberCount = 3,
            HasHero = true,
            Capacity = 4,
            OccupiedBattleSlots = 3,
            EffectiveArmyPower = 12f,
            HasResearchOperator = developmentOperator,
            CurrentMovement = 3,
        };

        private static string Fingerprint(WorldSnapshot snapshot, int ap = 4,
            string resources = "3,3,3,3", int handVersion = 7) =>
            Pipeline.DevelopmentAdmissionFingerprint(snapshot, null, ap, resources, handVersion);

        [Test]
        public void RaidReturnMovement_DoesNotReadmitDevelopment_ButActorStillInvalidatesOperations()
        {
            WorldSnapshot snapshot = Snapshot(Army());
            string before = Fingerprint(snapshot);
            snapshot.Self.Armies[0].Hex = new HexCoord(5, 4);
            snapshot.Self.Armies[0].CurrentMovement = 2;

            Assert.That(Fingerprint(snapshot), Is.EqualTo(before));
            Assert.That(DesireAxes.InvalidationMaskFor(DesireAxis.Recon)
                .HasFlag(StrategicInvalidationReason.Actor), Is.True);
            Assert.That(DesireAxes.InvalidationMaskFor(DesireAxis.Aggression)
                .HasFlag(StrategicInvalidationReason.Actor), Is.True);
        }

        [Test]
        public void DevelopmentOperatorArrival_ChangesFingerprint()
        {
            WorldSnapshot snapshot = Snapshot(Army(developmentOperator: true));
            string before = Fingerprint(snapshot);
            snapshot.Self.Armies[0].Hex = new HexCoord(0, 0);
            snapshot.Development.Facilities = new[]
            {
                new DevelopmentFacility
                {
                    Hex = new HexCoord(0, 0), Mode = ResearchProductionMode.Research,
                    HasHero = true, HeroFate = 2,
                },
            };
            snapshot.Development.AnyFacilityWithHero = true;
            snapshot.Development.AnyOperatorlessFacility = false;

            Assert.That(Fingerprint(snapshot), Is.Not.EqualTo(before));
        }

        [Test]
        public void ResourcesOrHandChange_ChangesFingerprint()
        {
            WorldSnapshot snapshot = Snapshot(Army());
            string before = Fingerprint(snapshot);

            Assert.That(Fingerprint(snapshot, resources: "3,3,4,3"), Is.Not.EqualTo(before));
            Assert.That(Fingerprint(snapshot, handVersion: 8), Is.Not.EqualTo(before));
        }

        [Test]
        public void EconomyOnlyDirty_DoesNotRefreshDevelopmentOpportunities()
        {
            Assert.That(Pipeline.RefreshDevelopmentOpportunities(
                new HashSet<DesireAxis> { DesireAxis.Economy }), Is.False);
            Assert.That(Pipeline.RefreshDevelopmentOpportunities(
                new HashSet<DesireAxis> { DesireAxis.Economy, DesireAxis.Development }), Is.True);
        }

        [Test]
        public void ActorAndResourcesTogether_DoNotSuppressDevelopmentReadmission()
        {
            WorldSnapshot snapshot = Snapshot(Army());
            string before = Fingerprint(snapshot);
            snapshot.Self.Armies[0].Hex = new HexCoord(5, 4);

            Assert.That(Fingerprint(snapshot, resources: "4,3,3,3"), Is.Not.EqualTo(before));
        }

        [Test]
        public void OperatorAvailabilityChangeWithoutMovement_ChangesFingerprint()
        {
            WorldSnapshot snapshot = Snapshot(Army(developmentOperator: true));
            string before = Fingerprint(snapshot);
            snapshot.Self.Armies[0].HasResearchOperator = false;

            Assert.That(Fingerprint(snapshot), Is.Not.EqualTo(before));
        }
    }
}
#endif
