#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Economy;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiEconomyDecisionTests
    {
        [Test]
        public void ResourceDeficit_HandNeedOutweighsEquivalentDeckNeed()
        {
            EconomyResourceStanding hand = EconomyStanding.CalculateResource(
                ResourceType.Materials, 1f, 1f, 8f, 0f, 0f, 0f, 0f);
            EconomyResourceStanding deck = EconomyStanding.CalculateResource(
                ResourceType.Materials, 1f, 1f, 0f, 8f, 0f, 0f, 0f);

            Assert.That(hand.DeficitScore, Is.GreaterThan(deck.DeficitScore));
        }

        [Test]
        public void ResourceDeficit_OpponentMedianGapRaisesPressure()
        {
            EconomyResourceStanding even = EconomyStanding.CalculateResource(
                ResourceType.Energy, 2f, 2f, 0f, 0f, 0f, 0f, 0f);
            EconomyResourceStanding behind = EconomyStanding.CalculateResource(
                ResourceType.Energy, 2f, 6f, 0f, 0f, 0f, 0f, 0f);

            Assert.That(behind.RelativeIncomeGap, Is.GreaterThan(even.RelativeIncomeGap));
            Assert.That(behind.DeficitScore, Is.GreaterThan(even.DeficitScore));
        }

        [Test]
        public void ResourceDeficit_OperationalReservationRaisesMatchingPressure()
        {
            EconomyResourceStanding free = EconomyStanding.CalculateResource(
                ResourceType.Tech, 2f, 2f, 0f, 0f, 0f, 2f, 0f);
            EconomyResourceStanding reserved = EconomyStanding.CalculateResource(
                ResourceType.Tech, 2f, 2f, 0f, 0f, 6f, 2f, 0f);

            Assert.That(reserved.OperationalPressure, Is.GreaterThan(free.OperationalPressure));
            Assert.That(reserved.DeficitScore, Is.GreaterThan(free.DeficitScore));
        }

        [Test]
        public void EconomyDesire_IsPositiveForRealDeficit()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.9f, 0.4f, actionable: true);

            RadarAssessment result = StrategyLayer.Evaluate(snapshot, new AiRadarState());

            Assert.That(result.Desires.Raw[DesireAxis.Economy], Is.GreaterThan(0f));
        }

        [Test]
        public void EconomyDesire_IsLowWhenIncomeAndRunwayAreSufficient()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.02f, 0.01f, actionable: true);

            RadarAssessment result = StrategyLayer.Evaluate(snapshot, new AiRadarState());

            Assert.That(result.Desires.Raw[DesireAxis.Economy], Is.LessThan(0.1f));
        }

        [Test]
        public void EconomyDesire_NoActionableSiteAppliesLatentDampInsteadOfZero()
        {
            WorldSnapshot actionable = SnapshotWithDeficits(0.8f, 0.5f, actionable: true);
            WorldSnapshot latent = SnapshotWithDeficits(0.8f, 0.5f, actionable: false);

            float activeValue = StrategyLayer.Evaluate(actionable, new AiRadarState())
                .Desires.Raw[DesireAxis.Economy];
            float latentValue = StrategyLayer.Evaluate(latent, new AiRadarState())
                .Desires.Raw[DesireAxis.Economy];

            Assert.That(latentValue, Is.GreaterThan(0f));
            Assert.That(latentValue, Is.LessThan(activeValue));
        }

        [Test]
        public void EconomySiteScore_ThreatCanMakeSaferPeerWin()
        {
            float safe = DemandLayer.ScoreEconomySite(0.7f, 1f, 0.5f, 0.5f, 2f, 0f, 0.2f);
            float dangerous = DemandLayer.ScoreEconomySite(0.7f, 1f, 0.5f, 0.5f, 2f, 1f, 0.2f);

            Assert.That(safe, Is.GreaterThan(dangerous));
        }

        [Test]
        public void EconomyDemand_SelectsValueBeforeCoordinates()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.9f, 0.2f, actionable: true);
            snapshot.Economy.PerType = new List<EconomyResourceStanding>
            {
                new EconomyResourceStanding { Type = ResourceType.Human, DeficitScore = 0.2f },
                new EconomyResourceStanding { Type = ResourceType.Energy, DeficitScore = 0.1f },
                new EconomyResourceStanding { Type = ResourceType.Materials, DeficitScore = 0.1f },
                new EconomyResourceStanding { Type = ResourceType.Tech, DeficitScore = 0.9f },
            };
            snapshot.Known.ResourceHexes = new List<KeyValuePair<HexCoord, ResourceType>>
            {
                new KeyValuePair<HexCoord, ResourceType>(new HexCoord(-5, -5), ResourceType.Human),
                new KeyValuePair<HexCoord, ResourceType>(new HexCoord(4, 4), ResourceType.Tech),
            };
            snapshot.Known.Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>();

            AxisDemand selected = DemandLayer.EconomyDemands(snapshot, new DesireBreakdown(), null, null, null)
                .First();

            Assert.That(selected.TargetHex, Is.EqualTo(new HexCoord(4, 4)));
            Assert.That(selected.EconomyResourceType, Is.EqualTo(ResourceType.Tech));
        }

        [Test]
        public void EconomyDemand_BuiltSiteIsRejected()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.9f, 0.2f, actionable: true);
            HexCoord site = new HexCoord(2, 1);
            snapshot.Known.ResourceHexes = new List<KeyValuePair<HexCoord, ResourceType>>
            {
                new KeyValuePair<HexCoord, ResourceType>(site, ResourceType.Human),
            };
            snapshot.Known.Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>
            {
                new Game.Ai.AiMapMemory.KnownBuilding(site, null, false,
                    new HashSet<string> { Game.Cards.UnitAbilities.CollectAbilityFor(ResourceType.Human) }),
            };

            Assert.That(DemandLayer.EconomyDemands(snapshot, new DesireBreakdown(), null, null, null), Is.Empty);
        }

        [Test]
        public void EconomyDemand_OrdinaryBaseDoesNotSatisfyExtraction()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.9f, 0.2f, actionable: true);
            HexCoord site = new HexCoord(2, 1);
            snapshot.Known.ResourceHexes = new List<KeyValuePair<HexCoord, ResourceType>>
            {
                new KeyValuePair<HexCoord, ResourceType>(site, ResourceType.Human),
            };
            snapshot.Known.Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>
            {
                new Game.Ai.AiMapMemory.KnownBuilding(site, null, false, null),
            };

            Assert.That(DemandLayer.EconomyDemands(snapshot, new DesireBreakdown(), null, null, null),
                Is.Not.Empty);
        }

        [Test]
        public void EconomyStableKey_DistinguishesTaskAndResource()
        {
            HexCoord hex = new HexCoord(3, -2);
            StableMissionKey human = StableMissionKey.For(EconomyMission(
                EconomyTaskKind.BuildExtraction, hex, ResourceType.Human));
            StableMissionKey tech = StableMissionKey.For(EconomyMission(
                EconomyTaskKind.BuildExtraction, hex, ResourceType.Tech));
            StableMissionKey baseKey = StableMissionKey.For(EconomyMission(
                EconomyTaskKind.FoundBase, hex, null));

            Assert.That(human, Is.Not.EqualTo(tech));
            Assert.That(human, Is.Not.EqualTo(baseKey));
            Assert.That(MissionIntentKey.For(EconomyMission(
                    EconomyTaskKind.BuildExtraction, hex, ResourceType.Human)),
                Is.Not.EqualTo(MissionIntentKey.For(EconomyMission(
                    EconomyTaskKind.BuildExtraction, hex, ResourceType.Tech))));
        }

        [Test]
        public void ReconEconomyDevelopmentScope_AdmitsEconomyAndSuppressesRaid()
        {
            AiStrategyV2Mode previous = AiStrategyV2Scope.Mode;
            try
            {
                AiStrategyV2Scope.Mode = AiStrategyV2Mode.ReconEconomyDevelopment;
                List<MissionProposal> scoped = AiStrategyV2Scope.ApplyMissionScope(new[]
                {
                    EconomyMission(EconomyTaskKind.BuildExtraction, new HexCoord(1, 1), ResourceType.Energy),
                    new MissionProposal { Kind = MissionKind.Raid },
                });

                Assert.That(scoped.Select(x => x.Kind), Is.EqualTo(new[] { MissionKind.Economy }));
                Assert.That(AiStrategyV2Scope.AxisInScope(DesireAxis.Economy), Is.True);
                Assert.That(AiStrategyV2Scope.AxisInScope(DesireAxis.Aggression), Is.False);
            }
            finally
            {
                AiStrategyV2Scope.Mode = previous;
            }
        }

        [Test]
        public void EconomyAdmission_PrefersReadySameTurnMission()
        {
            MissionProposal ready = EconomyMission(EconomyTaskKind.BuildExtraction,
                new HexCoord(1, 0), ResourceType.Materials);
            ready.EffectiveValue = 30f;
            ready.Target = WithBuildValue((EconomyMissionTarget)ready.Target, 20f);
            ready.Requirements = new MissionRequirements { ApDesired = 2f, EtaTurns = 0 };
            MissionProposal delayed = EconomyMission(EconomyTaskKind.BuildExtraction,
                new HexCoord(4, 0), ResourceType.Materials);
            delayed.EffectiveValue = 30f;
            delayed.Target = WithBuildValue((EconomyMissionTarget)delayed.Target, 20f);
            delayed.Requirements = new MissionRequirements { ApDesired = 2f, EtaTurns = 2 };

            Assert.That(MissionAdmissionPolicy.AdmissionRank(ready),
                Is.GreaterThan(MissionAdmissionPolicy.AdmissionRank(delayed)));
        }

        [Test]
        public void EconomyLoan_SoftReconCanBeBorrowedForHighSameTurnValue()
        {
            MissionIntent donor = ScoutDonor(CommitmentTier.Soft, ScoutTargetKind.Explore);

            Assert.That(ProvisioningManager.EconomyLoanAllowed(donor, 80f, 2, 3, out float net), Is.True);
            Assert.That(net, Is.GreaterThanOrEqualTo(AiConfigV2.economyLoanHysteresisThreshold));
        }

        [Test]
        public void EconomyLoan_HardOrCriticalSurveilCannotBeBorrowed()
        {
            Assert.That(ProvisioningManager.EconomyLoanAllowed(
                ScoutDonor(CommitmentTier.Hard, ScoutTargetKind.Explore), 100f, 1, 3, out _), Is.False);
            Assert.That(ProvisioningManager.EconomyLoanAllowed(
                ScoutDonor(CommitmentTier.Soft, ScoutTargetKind.Surveil), 100f, 1, 3, out _), Is.False);
        }

        [Test]
        public void EconomyLoan_StartedRaidCannotBeBorrowed()
        {
            var donor = new MissionIntent
            {
                Kind = MissionKind.Raid, Funding = CommitmentTier.Soft,
                Objective = new RaidIntent { OperationStarted = true },
            };

            Assert.That(ProvisioningManager.EconomyLoanAllowed(donor, 100f, 1, 3, out _), Is.False);
        }

        [Test]
        public void EconomyLoan_MustCompleteMovementThisTurn()
        {
            MissionIntent donor = ScoutDonor(CommitmentTier.Soft, ScoutTargetKind.Explore);

            Assert.That(ProvisioningManager.EconomyLoanAllowed(donor, 100f, 4, 3, out _), Is.False);
        }

        private static MissionIntent ScoutDonor(CommitmentTier funding, ScoutTargetKind kind) =>
            new MissionIntent
            {
                Kind = MissionKind.Scout, Funding = funding,
                Objective = new ScoutIntent { Kind = kind },
            };

        private static MissionProposal EconomyMission(EconomyTaskKind kind, HexCoord hex,
            ResourceType? resource) => new MissionProposal
        {
            Kind = MissionKind.Economy,
            Target = new EconomyMissionTarget { Kind = kind, TargetHex = hex, ResourceType = resource },
            Requirements = new MissionRequirements(),
        };

        private static EconomyMissionTarget WithBuildValue(EconomyMissionTarget target, float value)
        {
            target.BuildValue = value;
            return target;
        }

        private static WorldSnapshot SnapshotWithDeficits(float max, float other, bool actionable)
        {
            var perType = new List<EconomyResourceStanding>
            {
                new EconomyResourceStanding { Type = ResourceType.Human, DeficitScore = max, IncomeGap = max },
                new EconomyResourceStanding { Type = ResourceType.Energy, DeficitScore = other },
                new EconomyResourceStanding { Type = ResourceType.Materials, DeficitScore = other },
                new EconomyResourceStanding { Type = ResourceType.Tech, DeficitScore = other },
            };
            return new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot
                {
                    TotalPower = 1f,
                    Stockpile = new ResourceBundle(),
                    PerTurnIncome = new ResourceBundle(),
                    BaseHexes = new List<HexCoord> { new HexCoord(0, 0) },
                    Armies = new List<ArmySnapshot>(),
                    Hand = new List<Game.Cards.CardData>(),
                    Deck = new List<Game.Cards.CardDefinition>(),
                },
                Known = new KnownSnapshot
                {
                    EnemySightings = new List<Game.Ai.AiMapMemory.KnownEnemySighting>(),
                    NeutralSightings = new List<Game.Ai.AiMapMemory.KnownEnemySighting>(),
                    Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>(),
                    ResourceHexes = new List<KeyValuePair<HexCoord, ResourceType>>(),
                },
                TrueWorld = new TrueWorldSnapshot { Opponents = new List<OpponentSnapshot>() },
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    Frontier = new List<FrontierHexSnapshot>(),
                    AllHexes = new List<HexCoord>(),
                },
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                },
                Development = new DevelopmentReadiness(),
                Economy = new EconomyStanding
                {
                    PerType = perType,
                    MaxDeficitScore = max,
                    MeanDeficitScore = (max + other * 3f) / 4f,
                    HasActionableOpportunity = actionable,
                    EconomicSecurity = 1f - max,
                },
            };
        }
    }
}
#endif
