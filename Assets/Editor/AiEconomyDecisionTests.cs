#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
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
            snapshot.Economy.ExtractionOpportunities = new List<EconomyExtractionOpportunity>
            {
                ExtractionOpportunity(new HexCoord(-5, -5), ResourceType.Human, 1),
                ExtractionOpportunity(new HexCoord(4, 4), ResourceType.Tech, 1),
            };

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
            snapshot.Economy.ExtractionOpportunities = new List<EconomyExtractionOpportunity>
            {
                ExtractionOpportunity(site, ResourceType.Human, 1),
            };

            Assert.That(DemandLayer.EconomyDemands(snapshot, new DesireBreakdown(), null, null, null),
                Is.Not.Empty);
        }

        [Test]
        public void MarginalCollection_SaturatedCitadelAddsNothing()
        {
            Assert.That(IncomeProjection.MarginalBuildingCollection(
                effectiveHexYield: 1, currentCollectionCapacity: 1), Is.EqualTo(0));
        }

        [Test]
        public void MarginalCollection_OwnArmyAlreadyCollectingIsNotGrowth()
        {
            Assert.That(IncomeProjection.MarginalOwnerCollectionAtHex(
                effectiveHexYield: 1, currentBuildingCollectionCapacity: 0,
                additionalBuildingCollectionCapacity: 1, ownerArmyCollectorCount: 1,
                ownerArmiesCanCollect: true), Is.EqualTo(0));
        }

        [Test]
        public void MarginalCollection_PartialYieldIsCappedByRemainingPool()
        {
            Assert.That(IncomeProjection.MarginalBuildingCollection(
                effectiveHexYield: 3, currentCollectionCapacity: 2,
                additionalCollectionCapacity: 4), Is.EqualTo(1));
        }

        [Test]
        public void EconomyStanding_RejectsZeroMarginalOpportunity()
        {
            HexCoord site = new HexCoord(7, 5);
            var standing = new EconomyStanding
            {
                ExtractionOpportunities = new List<EconomyExtractionOpportunity>
                {
                    ExtractionOpportunity(site, ResourceType.Human, 0),
                },
            };

            Assert.That(standing.IsExtractionActionable(site, ResourceType.Human), Is.False);
        }

        [Test]
        public void EconomyDemand_NoMobileBuilderRequestsExistingHeroCapability()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.9f, 0.2f, actionable: true);
            HexCoord site = new HexCoord(3, 1);
            snapshot.Economy.ExtractionOpportunities = new List<EconomyExtractionOpportunity>
            {
                ExtractionOpportunity(site, ResourceType.Human, 1),
            };

            AxisDemand demand = DemandLayer.EconomyDemands(
                snapshot, new DesireBreakdown(), null, null, null).Single();

            Assert.That(demand.Capability, Is.EqualTo(CapabilityKind.Hero));
            Assert.That(demand.RequestingAxis, Is.EqualTo(DesireAxis.Economy));
            Assert.That(demand.TargetHex, Is.EqualTo(site));
        }

        [Test]
        public void EconomyDemand_FieldHeroKeepsInfrastructureDemand()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.9f, 0.2f, actionable: true);
            HexCoord site = new HexCoord(3, 1);
            snapshot.Self.Armies = new List<ArmySnapshot>
            {
                new ArmySnapshot
                {
                    ArmyId = 11, Hex = new HexCoord(1, 1), HasHero = true,
                    IsGarrison = false, IsPrison = false, IsAir = false,
                    IsAirfield = false, MemberCount = 2,
                },
            };
            snapshot.Economy.ExtractionOpportunities = new List<EconomyExtractionOpportunity>
            {
                ExtractionOpportunity(site, ResourceType.Human, 1),
            };

            AxisDemand demand = DemandLayer.EconomyDemands(
                snapshot, new DesireBreakdown(), null, null, null).Single();

            Assert.That(demand.Capability,
                Is.EqualTo(CapabilityKind.EconomicInfrastructure));
        }

        [Test]
        public void EconomyDemand_GarrisonHeroBuildsOnlyAtItsOwnHex()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.9f, 0.2f, actionable: true);
            HexCoord local = new HexCoord(3, 1);
            snapshot.Self.Armies = new List<ArmySnapshot>
            {
                new ArmySnapshot
                {
                    ArmyId = 10, Hex = local, HasHero = true,
                    IsGarrison = true, MemberCount = 1,
                },
            };
            snapshot.Economy.ExtractionOpportunities = new List<EconomyExtractionOpportunity>
            {
                ExtractionOpportunity(local, ResourceType.Human, 1),
            };

            AxisDemand localDemand = DemandLayer.EconomyDemands(
                snapshot, new DesireBreakdown(), null, null, null).Single();
            Assert.That(localDemand.Capability,
                Is.EqualTo(CapabilityKind.EconomicInfrastructure));

            HexCoord remote = new HexCoord(5, 1);
            snapshot.Economy.ExtractionOpportunities = new List<EconomyExtractionOpportunity>
            {
                ExtractionOpportunity(remote, ResourceType.Human, 1),
            };
            AxisDemand remoteDemand = DemandLayer.EconomyDemands(
                snapshot, new DesireBreakdown(), null, null, null).Single();
            Assert.That(remoteDemand.Capability, Is.EqualTo(CapabilityKind.Hero));
        }

        [Test]
        public void KnownBuilding_PreservesObservedCollectionAndSlotCapacity()
        {
            var known = new Game.Ai.AiMapMemory.KnownBuilding(
                new HexCoord(1, 2), null, true, null,
                new[] { 1, 2, 3, 4 }, freeFacilitySlots: 0);

            Assert.That(known.CollectedAmount(ResourceType.Materials), Is.EqualTo(3));
            Assert.That(known.FreeFacilitySlots, Is.EqualTo(0));
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

            Assert.That(DemandLayer.EconomyLoanAllowed(donor, 80f, 2, 3, out float net), Is.True);
            Assert.That(net, Is.GreaterThanOrEqualTo(AiConfigV2.economyLoanHysteresisThreshold));
        }

        [Test]
        public void EconomyLoan_HardOrCriticalSurveilCannotBeBorrowed()
        {
            Assert.That(DemandLayer.EconomyLoanAllowed(
                ScoutDonor(CommitmentTier.Hard, ScoutTargetKind.Explore), 100f, 1, 3, out _), Is.False);
            Assert.That(DemandLayer.EconomyLoanAllowed(
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

            Assert.That(DemandLayer.EconomyLoanAllowed(donor, 100f, 1, 3, out _), Is.False);
        }

        [Test]
        public void EconomyLoan_MustCompleteMovementThisTurn()
        {
            MissionIntent donor = ScoutDonor(CommitmentTier.Soft, ScoutTargetKind.Explore);

            Assert.That(DemandLayer.EconomyLoanAllowed(donor, 100f, 4, 3, out _), Is.False);
        }

        [Test]
        public void EconomyHeroMaterialization_NewArmyIsOperationalDeliveryOnlyForEconomy()
        {
            var plan = new MaterializationPlan
            {
                Deploy = new PlacementOption(
                    new HexCoord(0, 0), DeploymentKind.NewArmy, null),
            };
            var economy = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.Hero,
            };
            var aggression = new AxisDemand
            {
                RequestingAxis = DesireAxis.Aggression,
                Capability = CapabilityKind.Hero,
            };

            Assert.That(MaterializationDeliveryPolicy.CanDeliverDemandOperationally(plan, economy),
                Is.True);
            Assert.That(MaterializationDeliveryPolicy.CanDeliverDemandOperationally(plan, aggression),
                Is.False);
        }

        [Test]
        public void EconomyHeroMaterialization_MobileBuilderIsOperationalLeaseCandidate()
        {
            var builder = new ArmySnapshot
            {
                ArmyId = 17,
                HasHero = true,
                IsMobileEconomyBuilder = true,
                IsStructuralRaidActor = false,
            };
            var after = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Armies = new List<ArmySnapshot> { builder },
                },
            };
            var plan = new MaterializationPlan
            {
                Deploy = new PlacementOption(
                    new HexCoord(0, 0), DeploymentKind.NewArmy, null),
            };
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.Hero,
            };

            Assert.That(CapabilityDeliveryEvaluator.IsOperationalForDemand(builder, demand), Is.True);
            Assert.That(CapabilityDeliveryEvaluator.OperationalLeaseArmyIds(
                new HashSet<int>(), after, plan, demand), Is.EqualTo(new[] { 17 }));
        }

        [Test]
        public void EconomyHeroPrerequisite_PreservesExactBuildCardWithoutEarlyResourceReserve()
        {
            var committedCard = new Game.Cards.CardData(null);
            var source = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicExpansionBase,
                EconomyBuildCard = committedCard,
                EconomyBuildResourceCost = new ResourceCost(),
            };

            AxisDemand prerequisite = DemandLayer.EconomyHeroPrerequisite(source);

            Assert.That(prerequisite.Capability, Is.EqualTo(CapabilityKind.Hero));
            Assert.That(prerequisite.EconomyBuildCard, Is.SameAs(committedCard));
            Assert.That(prerequisite.EconomyBuildResourceCost, Is.Null,
                "Missing-builder stage must claim the card instance without reserving H/E/M/T.");
        }

        [Test]
        public void EconomyBuildCardClaim_BlocksOnlyTheExactHandInstance()
        {
            var claimed = new Game.Cards.CardData(null);
            var duplicate = new Game.Cards.CardData(null);
            var reservation = new MaterializationReservation();
            reservation.UnresolvedDemands.Add(new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.Hero,
                EconomyBuildCard = claimed,
            });

            Assert.That(reservation.ClaimsEconomyBuildCard(claimed), Is.True);
            Assert.That(reservation.ClaimsEconomyBuildCard(duplicate), Is.False);
        }

        [Test]
        public void EconomyResourceReserve_OpensOnlyInsideOneTurnBuilderHorizon()
        {
            var builder = new ArmySnapshot { ArmyId = 7, MaxMovement = 3 };
            var snap = new WorldSnapshot
            {
                Self = new SelfSnapshot { Armies = new List<ArmySnapshot> { builder } },
            };
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicInfrastructure,
                EconomyBuilderRoutes = new[]
                {
                    new EconomyBuilderRouteSnapshot
                    {
                        ArmyId = 7, TravelCost = 4, IsOnTarget = false,
                    },
                },
            };

            Assert.That(InfrastructureFulfillment.ShouldReserveDeferredEconomyResources(
                snap, demand), Is.False);

            demand.EconomyBuilderRoutes = new[]
            {
                new EconomyBuilderRouteSnapshot
                {
                    ArmyId = 7, TravelCost = 3, IsOnTarget = false,
                },
            };
            Assert.That(InfrastructureFulfillment.ShouldReserveDeferredEconomyResources(
                snap, demand), Is.True);
        }

        [Test]
        public void EconomyRecoveryTarget_ExcludesFacilityOnlyHex()
        {
            var player = new Game.Players.PlayerSetupData();
            var actor = new ArmySnapshot { ArmyId = 4, Hex = new HexCoord(0, 0) };
            var snap = new WorldSnapshot
            {
                Self = new SelfSnapshot { Armies = new List<ArmySnapshot> { actor } },
                Known = new KnownSnapshot
                {
                    Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>
                    {
                        new Game.Ai.AiMapMemory.KnownBuilding(
                            new HexCoord(1, 0), player, false, null),
                        new Game.Ai.AiMapMemory.KnownBuilding(
                            new HexCoord(2, 0), player, false, null, isBase: true),
                    },
                },
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                },
            };

            HexCoord? target = MissionContinuityLayer.SelectEconomyRecoveryTarget(
                snap, player, actor);

            Assert.That(target, Is.EqualTo(new HexCoord(2, 0)));
        }

        [Test]
        public void EconomyRecoveryPolicy_ScoutResumesOnlyWhenBuildHexIsSafe()
        {
            MissionIntent scout = ScoutDonor(CommitmentTier.Soft, ScoutTargetKind.Explore);

            Assert.That(MissionContinuityLayer.RequiresEconomyBuilderRecovery(
                EconomyTaskKind.BuildExtraction, scout, underImmediateThreat: false,
                alreadyProtected: false, hasRecoveryTarget: true), Is.False);
            Assert.That(MissionContinuityLayer.RequiresEconomyBuilderRecovery(
                EconomyTaskKind.BuildExtraction, scout, underImmediateThreat: true,
                alreadyProtected: false, hasRecoveryTarget: true), Is.True);
            Assert.That(MissionContinuityLayer.RequiresEconomyBuilderRecovery(
                EconomyTaskKind.BuildExtraction, lender: null, underImmediateThreat: false,
                alreadyProtected: false, hasRecoveryTarget: true), Is.True);
            Assert.That(MissionContinuityLayer.RequiresEconomyBuilderRecovery(
                EconomyTaskKind.FoundBase, lender: null, underImmediateThreat: false,
                alreadyProtected: true, hasRecoveryTarget: true), Is.False);
        }

        [Test]
        public void EconomyRecoveryMission_UsesOnlyItsPreferredBuilder()
        {
            MissionProposal recovery = EconomyMission(
                EconomyTaskKind.ReturnBuilder, new HexCoord(0, 0), null);
            recovery.PreferredMoverArmyId = 19;
            ArmySnapshot preferred = new ArmySnapshot { ArmyId = 19, HasHero = true };
            ArmySnapshot substitute = new ArmySnapshot { ArmyId = 20, HasHero = true };

            Assert.That(ProvisioningManager.IsEligibleEconomyRecoveryActor(
                recovery, preferred), Is.True);
            Assert.That(ProvisioningManager.IsEligibleEconomyRecoveryActor(
                recovery, substitute), Is.False);
        }

        [Test]
        public void ProduceResource_GlobalValueTracksMatchingEconomyDeficit()
        {
            WorldSnapshot humanScarce = SnapshotForRecurringResource(ResourceType.Human);
            WorldSnapshot materialsScarce = SnapshotForRecurringResource(ResourceType.Materials);

            EffectContribution useful = StrategicEffectRegistry.Contributions(
                IntendedRole.CombatBody,
                new[] { Game.Cards.UnitAbilities.ProduceHuman },
                0,
                new EffectEvaluationContext(humanScarce));
            EffectContribution mismatched = StrategicEffectRegistry.Contributions(
                IntendedRole.CombatBody,
                new[] { Game.Cards.UnitAbilities.ProduceHuman },
                0,
                new EffectEvaluationContext(materialsScarce));
            EffectContribution matchingMaterials = StrategicEffectRegistry.Contributions(
                IntendedRole.CombatBody,
                new[] { Game.Cards.UnitAbilities.ProduceMaterials },
                0,
                new EffectEvaluationContext(materialsScarce));

            Assert.That(useful.GlobalRoleFit, Is.GreaterThan(mismatched.GlobalRoleFit));
            Assert.That(matchingMaterials.GlobalRoleFit, Is.GreaterThan(mismatched.GlobalRoleFit));
            Assert.That(useful.RoleFit, Is.Zero,
                "Player-global production must not become a placement/role-local contribution.");
        }

        private static WorldSnapshot SnapshotForRecurringResource(ResourceType scarce)
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.9f, 0.05f, actionable: true);
            var perType = new List<EconomyResourceStanding>();
            foreach (ResourceType type in ResourceBundle.All)
            {
                bool isScarce = type == scarce;
                perType.Add(EconomyStanding.CalculateResource(
                    type,
                    ownIncome: isScarce ? 0f : 4f,
                    opponentMedianIncome: 4f,
                    handNeed: isScarce ? 8f : 0f,
                    remainingDeckNeed: isScarce ? 8f : 0f,
                    reservedOperationalNeed: isScarce ? 2f : 0f,
                    spendableStockpile: isScarce ? 0f : 12f,
                    starvationPressure: 0f));
            }
            snapshot.Economy.PerType = perType;
            snapshot.Economy.MaxDeficitScore = perType.Max(x => x.DeficitScore);
            snapshot.Economy.MeanDeficitScore = perType.Average(x => x.DeficitScore);
            snapshot.Economy.EconomicSecurity = 1f - snapshot.Economy.MaxDeficitScore;
            return snapshot;
        }

        private static EconomyExtractionOpportunity ExtractionOpportunity(
            HexCoord hex, ResourceType type, int gain) => new EconomyExtractionOpportunity
        {
            Hex = hex,
            ResourceType = type,
            EffectiveYield = gain,
            CurrentBuildingCollection = 0,
            MarginalIncomeGain = gain,
            BaseNetworkSynergy = 1f,
            NearbyResourceClusterValue = 0f,
        };

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
