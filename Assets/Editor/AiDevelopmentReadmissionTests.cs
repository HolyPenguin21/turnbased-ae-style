#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiDevelopmentReadmissionTests
    {
        [Test]
        public void ApReservationReleaseInvalidatesAdmissionWithoutChangingPhysicalAp()
        {
            var snapshot = Snapshot(Army());
            var player = new Game.Players.PlayerSetupData();
            const int turn = 17, physicalAp = 5;
            try
            {
                StrategicResourceReservationLedger.Upsert(player, turn, new StrategicResourceReservation
                {
                    Owner = "reaction", Reason = StrategicReservationReason.StrategicReactionPass,
                    Resource = StrategicReservedResource.ActionPoints, Amount = 3,
                    ExpirationStage = StrategicReservationExpiry.EndOfReaction,
                });
                string Key() => Pipeline.DevelopmentApAffordability(snapshot, null, physicalAp,
                    TurnResourceBook.Free(physicalAp, TurnResourceBook.LedgerClaims(player, turn),
                        StrategicReservedResource.ActionPoints, default));
                string held = Key();
                StrategicResourceReservationLedger.ReleaseByOwner(player, turn, "reaction");
                Assert.That(Key(), Is.Not.EqualTo(held),
                    "A released AP hold must not be suppressed as an unchanged settled state");
            }
            finally { StrategicResourceReservationLedger.ClearAll(); }
        }

        [Test]
        public void DisplacedMissionValueInvalidatesPreparationWithoutChangingActorClaim()
        {
            var intent = new MissionIntent
                { Kind = MissionKind.Scout, Status = IntentStatus.Active, PreferredMoverArmyId = 9,
                  LastIntrinsicValue = 1f };
            var intents = new[] { intent };
            var snapshot = Snapshot(Army());
            string before = Pipeline.DevelopmentAdmissionFacts(snapshot, intents);
            intent.LastIntrinsicValue = 8f;
            Assert.That(Pipeline.DevelopmentAdmissionFacts(snapshot, intents), Is.Not.EqualTo(before));
        }

        [Test]
        public void AttachmentOccupancyInvalidatesEvenWithoutHandMutation()
        {
            var definition = AttachmentSlotTests.Host(hero: true);
            var card = new CardData(definition);
            var hand = new AiHandData(null, default, 0); hand.AddCard(card);
            string before = Pipeline.DevelopmentRecipientFacts(null, hand);
            card.Equipment = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment, EquipmentStat.Attack, 0);
            string equipment = Pipeline.DevelopmentRecipientFacts(null, hand);
            Assert.That(equipment, Is.Not.EqualTo(before));
            card.Mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator, EquipmentStat.Attack, 0);
            Assert.That(Pipeline.DevelopmentRecipientFacts(null, hand), Is.Not.EqualTo(equipment));
        }

        [Test]
        public void CommanderFateInvalidatesAnOtherwiseIdenticalThreatRoster()
        {
            var snapshot = Snapshot(Army());
            var enemy = new ArmySnapshot
            {
                Members = new[] { new Game.Combat.WorthIt.DefenderProfile(2, false, attack: 3, hitPoints: 8) },
                Commander = new Game.Combat.WorthIt.SideCommander(2, 1),
            };
            snapshot.TrueWorld = new TrueWorldSnapshot { EnemyArmies = new[] { enemy } };
            string before = Pipeline.DevelopmentAdmissionFacts(snapshot, null);
            enemy.Commander = new Game.Combat.WorthIt.SideCommander(2, 4);
            Assert.That(Pipeline.DevelopmentAdmissionFacts(snapshot, null), Is.Not.EqualTo(before));
        }

        [Test]
        public void PreparationApChangeInvalidatesEvenWhenOfferingThresholdIsUnchanged()
        {
            var snapshot = Snapshot(Army());
            snapshot.Development.Offerings = new[] { new DevelopmentOffering
            {
                Card = new CardDefinition { cardType = CardType.Equipment, apCost = 1, activationApCost = 1 },
            } };
            Assert.That(Pipeline.DevelopmentApAffordability(snapshot, null, 3),
                Is.Not.EqualTo(Pipeline.DevelopmentApAffordability(snapshot, null, 4)));
        }

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
        public void MutatorOccupancyChangesFingerprintEvenWhenCombatStatsAndHandVersionMatch()
        {
            var snapshot = Snapshot(Army());
            snapshot.Self.Armies[0].NonHeroMutatorOccupied = new[] { false, false };
            string before = Pipeline.DevelopmentAdmissionFacts(snapshot, null);
            snapshot.Self.Armies[0].NonHeroMutatorOccupied = new[] { true, false };
            Assert.That(Pipeline.DevelopmentAdmissionFacts(snapshot, null), Is.Not.EqualTo(before));
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
