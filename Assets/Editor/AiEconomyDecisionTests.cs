#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
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
            float safe = DemandLayer.ScoreEconomySite(
                0.7f, 1f, 0.5f, 0.5f, 2f, 0f, 0.2f, 1f, 1f, 2f);
            float dangerous = DemandLayer.ScoreEconomySite(
                0.7f, 1f, 0.5f, 0.5f, 2f, 1f, 0.2f, 1f, 1f, 2f);

            Assert.That(safe, Is.GreaterThan(dangerous));
        }

        [Test]
        public void EconomyDemand_ProfitableSiteDoesNotRequireRelativeDeficit()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0f, 0f, actionable: true);
            snapshot.Economy.ExtractionOpportunities = new[]
            {
                ExtractionOpportunity(new HexCoord(2, 0), ResourceType.Materials, 2),
            };

            AxisDemand demand = DemandLayer.EconomyDemands(
                snapshot, new DesireBreakdown(), null, null, null).Single();

            Assert.That(demand.EconomyResourceType, Is.EqualTo(ResourceType.Materials));
            Assert.That(demand.Value, Is.GreaterThan(0f));
        }

        [Test]
        public void EconomyPayback_RejectsExcessiveConstructionHorizon()
        {
            float payback = DemandLayer.EconomyPaybackTurns(
                expectedIncomeGain: 1f, resourceCost: 7f, assignmentApCost: 3f);

            Assert.That(payback, Is.GreaterThan(AiConfigV2.economyExtractionMaxPaybackTurns));
        }

        [Test]
        public void EconomyDemand_ExcessivePaybackSiteIsRejected()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0f, 0f, actionable: true);
            snapshot.Economy.ExtractionOpportunities = new[]
            {
                ExtractionOpportunity(new HexCoord(7, 0), ResourceType.Materials, 1),
            };
            Game.Core.GameConfig config = UnityEngine.ScriptableObject
                .CreateInstance<Game.Core.GameConfig>();
            config.extractionFacilityCards[(int)ResourceType.Materials] = new CardDefinition
            {
                cardType = CardType.Facility,
                apCost = 3,
                resourceCost = new ResourceCost { materials = 7 },
            };

            try
            {
                Assert.That(DemandLayer.EconomyDemands(snapshot, new DesireBreakdown(),
                    null, new Game.Ai.AiTurnContext { GameConfig = config }, null), Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void EconomyDemand_ExtractionCannotStarveBaseCategory()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.9f, 0.2f, actionable: true);
            var baseDef = new CardDefinition
            {
                cardType = CardType.Base, authoredKey = "test-base", displayName = "Test Base",
            };
            CardData baseCard = new CardData(baseDef);
            snapshot.Self.Hand = new[] { baseCard };
            snapshot.Economy.ExtractionOpportunities = new[]
            {
                ExtractionOpportunity(new HexCoord(2, 0), ResourceType.Human, 3),
            };
            snapshot.Economy.BaseOpportunities = new[]
            {
                new EconomyBaseOpportunity
                {
                    Hex = new HexCoord(3, 0), CapacityValue = 1f,
                    InfrastructurePressure = 1f,
                    NearbyResourceClusterValue = 2f,
                },
            };

            List<AxisDemand> demands = DemandLayer.EconomyDemands(
                snapshot, new DesireBreakdown(), null, null, null).ToList();

            Assert.That(demands.Count, Is.EqualTo(2));
            Assert.That(demands.Count(d => d.EconomyResourceType.HasValue), Is.EqualTo(1));
            Assert.That(demands.Count(d => d.EconomyBuildCard == baseCard), Is.EqualTo(1));
        }

        [Test]
        public void EconomyDemand_ActiveBaseCommitmentKeepsItsSite()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.2f, 0.1f, actionable: true);
            var baseDef = new CardDefinition
            {
                cardType = CardType.Base, authoredKey = "base", displayName = "Base",
            };
            CardData card = new CardData(baseDef);
            snapshot.Self.Hand = new[] { card };
            HexCoord incumbentHex = new HexCoord(3, 0);
            snapshot.Economy.BaseOpportunities = new[]
            {
                new EconomyBaseOpportunity
                {
                    Hex = incumbentHex, CapacityValue = 0.5f,
                },
                new EconomyBaseOpportunity
                {
                    Hex = new HexCoord(6, 0), CapacityValue = 1f,
                    NearbyResourceClusterValue = 5f,
                },
            };
            var incumbent = new MissionIntent
            {
                Kind = MissionKind.Economy,
                Status = IntentStatus.Active,
                Objective = new EconomyIntent
                {
                    Kind = EconomyTaskKind.FoundBase,
                    TargetHex = incumbentHex,
                    BuildCard = card,
                },
            };

            AxisDemand selected = DemandLayer.EconomyDemands(snapshot,
                new DesireBreakdown(), null, null, null, new[] { incumbent }, null).Single();

            Assert.That(selected.TargetHex, Is.EqualTo(incumbentHex));
        }

        [Test]
        public void BaseExpansionDirection_UsesFrontBaseAndOnlyForwardRing()
        {
            var self = new Game.Players.PlayerSetupData();
            var enemy = new Game.Players.PlayerSetupData();
            WorldSnapshot snapshot = SnapshotWithDeficits(0f, 0f, actionable: true);
            snapshot.Self.BaseHexes = new[] { new HexCoord(0, 0), new HexCoord(3, 0) };
            snapshot.Known.Buildings = new[]
            {
                new Game.Ai.AiMapMemory.KnownBuilding(
                    new HexCoord(9, 0), enemy, true, null),
            };

            Assert.That(WorldAnalysis.TrySelectBaseExpansionDirection(
                snapshot, self, out HexCoord target, out HexCoord anchor), Is.True);
            Assert.That(anchor, Is.EqualTo(new HexCoord(3, 0)));
            Assert.That(target, Is.EqualTo(new HexCoord(9, 0)));
            Assert.That(WorldAnalysis.IsForwardBaseCandidate(snapshot.Self.BaseHexes,
                anchor, target, new HexCoord(6, 0)), Is.True);
            Assert.That(WorldAnalysis.IsForwardBaseCandidate(snapshot.Self.BaseHexes,
                anchor, target, new HexCoord(4, 0)), Is.False);
            Assert.That(WorldAnalysis.IsForwardBaseCandidate(snapshot.Self.BaseHexes,
                anchor, target, new HexCoord(3, 3)), Is.False);
        }

        [Test]
        public void BaseExpansionDirection_TiesEnemyCitadelsByCoordinates()
        {
            var self = new Game.Players.PlayerSetupData();
            var enemyA = new Game.Players.PlayerSetupData();
            var enemyB = new Game.Players.PlayerSetupData();
            WorldSnapshot snapshot = SnapshotWithDeficits(0f, 0f, actionable: true);
            snapshot.Self.BaseHexes = new[] { new HexCoord(0, 0) };
            snapshot.Known.Buildings = new[]
            {
                new Game.Ai.AiMapMemory.KnownBuilding(
                    new HexCoord(6, -3), enemyA, true, null),
                new Game.Ai.AiMapMemory.KnownBuilding(
                    new HexCoord(3, 3), enemyB, true, null),
            };

            Assert.That(WorldAnalysis.TrySelectBaseExpansionDirection(
                snapshot, self, out HexCoord target, out _), Is.True);
            Assert.That(target, Is.EqualTo(new HexCoord(3, 3)));
        }

        [Test]
        public void ExtractionYield_EnumeratesEveryPositiveResourceOnKnownHex()
        {
            var yield = new ResourceBundle { Materials = 2f, Tech = 1f };

            var types = WorldAnalysis.PositiveResourceYields(new HexCoord(2, 1), yield)
                .ToDictionary(x => x.Type, x => x.Yield);

            Assert.That(types[ResourceType.Materials], Is.EqualTo(2));
            Assert.That(types[ResourceType.Tech], Is.EqualTo(1));
            Assert.That(types.Count, Is.EqualTo(2));
        }

        [Test]
        public void ExtractionDemand_OneFacilitySlotFundsOnlyOneResourceType()
        {
            HexCoord hex = new HexCoord(2, 1);
            WorldSnapshot snapshot = SnapshotWithDeficits(0.8f, 0.8f, actionable: true);
            snapshot.Economy.ExtractionOpportunities = new[]
            {
                ExtractionOpportunity(hex, ResourceType.Materials, 2),
                ExtractionOpportunity(hex, ResourceType.Tech, 1),
            };

            List<AxisDemand> demands = DemandLayer.EconomyDemands(
                snapshot, new DesireBreakdown(), null, null, null).ToList();

            Assert.That(demands.Count, Is.EqualTo(1));
            Assert.That(demands[0].EconomyResourceType, Is.EqualTo(ResourceType.Materials));
        }

        [Test]
        public void EconomyBuilderSelection_PrefersLowerFullAssignmentCostAndPropagatesId()
        {
            HexCoord target = new HexCoord(4, 0);
            ArmySnapshot solo = EconomyBuilder(11, 1, 3f);
            ArmySnapshot stack = EconomyBuilder(12, 5, 20f);
            WorldSnapshot snapshot = SnapshotWithDeficits(0.5f, 0.1f, actionable: true);
            snapshot.Self.Armies = new[] { solo, stack };
            var routes = new[]
            {
                BuilderRoute(solo, travel: 4, back: 4, activation: 1),
                BuilderRoute(stack, travel: 2, back: 2, activation: 5),
            };

            DemandLayer.EconomyBuilderChoice choice = DemandLayer.SelectEconomyBuilder(
                snapshot, target, routes, null, null, 50f, 1f, includeReturn: true);
            Assert.That(choice.Army.ArmyId, Is.EqualTo(11));

            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicInfrastructure,
                TargetHex = target,
                EconomyResourceType = ResourceType.Materials,
                EconomyPreferredBuilderArmyId = choice.Army.ArmyId,
                EconomyBuilderRoutes = routes,
                Value = 50f,
            };
            MissionProposal mission = EconomyMissionPlanner.Propose(
                snapshot, new DesireBreakdown(), null, new[] { demand }).Single();
            Assert.That(mission.PreferredMoverArmyId, Is.EqualTo(11));
        }

        [Test]
        public void EconomyBuilderSelection_ActiveEconomyCommitmentWinsContinuity()
        {
            HexCoord target = new HexCoord(4, 0);
            ArmySnapshot incumbent = EconomyBuilder(20, 2, 8f);
            ArmySnapshot cheaper = EconomyBuilder(21, 1, 2f);
            WorldSnapshot snapshot = SnapshotWithDeficits(0.5f, 0.1f, actionable: true);
            snapshot.Self.Armies = new[] { incumbent, cheaper };
            EconomyBuilderRouteSnapshot incumbentRoute = BuilderRoute(
                incumbent, travel: 4, back: 4, activation: 3);
            incumbentRoute.HasActiveEconomyCommitment = true;

            DemandLayer.EconomyBuilderChoice choice = DemandLayer.SelectEconomyBuilder(
                snapshot, target,
                new[] { incumbentRoute, BuilderRoute(cheaper, 1, 1, 1) },
                null, null, 50f, 1f, includeReturn: true);

            Assert.That(choice.Army.ArmyId, Is.EqualTo(20));
        }

        [Test]
        public void EconomyArmyLightening_RetainsEscortForKnownRouteThreat()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0f, 0f, actionable: true);
            snapshot.Known.EnemySightings = new[]
            {
                new Game.Ai.AiMapMemory.KnownEnemySighting(
                    new HexCoord(2, 0), new Game.Players.PlayerSetupData(), "enemy", 2,
                    defenseSum: 3f, attackSum: 5f, defenders: null),
            };

            float required = ProvisioningManager.EconomyRouteEscortPower(
                snapshot, new HexCoord(0, 0), new HexCoord(4, 0));

            Assert.That(required, Is.EqualTo(8f * AiConfigV2.defenceReserveMargin).Within(0.001f));
        }

        [Test]
        public void EconomyReservation_ReducesSharedPhysicalStockWithoutAxisWallet()
        {
            var player = new Game.Players.PlayerSetupData();
            StrategicResourceReservationLedger.BeginTurn(player, 3);
            StrategicResourceReservationLedger.Upsert(player, 3,
                new StrategicResourceReservation
                {
                    Owner = "Economy:test",
                    Reason = StrategicReservationReason.EconomyBuildFollowup,
                    Resource = StrategicReservedResource.Human,
                    Amount = 2f,
                    ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                });

            Assert.That(StrategicResourceReservationLedger.Spendable(
                player, 3, StrategicReservedResource.Human, 5f), Is.EqualTo(3f));
            Assert.That(StrategicResourceReservationLedger.Active(
                player, 3, StrategicReservedResource.Human), Is.EqualTo(2f));
            StrategicResourceReservationLedger.BeginTurn(player, 4);
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
                    IsAirfield = false, IsMobileEconomyBuilder = true, MemberCount = 2,
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
                EconomyBuildResourceCost = new Game.Cards.ResourceCost(),
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

        private static ArmySnapshot EconomyBuilder(int id, int size, float power) =>
            new ArmySnapshot
            {
                ArmyId = id,
                Hex = new HexCoord(0, 0),
                HasHero = true,
                IsMobileEconomyBuilder = true,
                MemberCount = size,
                EffectiveArmyPower = power,
                CurrentMovement = 3,
                MaxMovement = 3,
            };

        private static EconomyBuilderRouteSnapshot BuilderRoute(ArmySnapshot army,
            int travel, int back, int activation) => new EconomyBuilderRouteSnapshot
        {
            ArmyId = army.ArmyId,
            TravelCost = travel,
            ReturnTravelCost = back,
            CurrentMovement = army.CurrentMovement,
            MaxMovement = army.MaxMovement,
            ActivationApCost = activation,
            ArmySize = army.MemberCount,
            EffectiveArmyPower = army.EffectiveArmyPower,
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
