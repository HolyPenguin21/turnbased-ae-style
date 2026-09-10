#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.UI;
using Game.Units;
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
        public void BaseExpansionDirection_UsesFrontBaseAndAcceptsKnownForwardHexBeyondMinimum()
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
                anchor, target, new HexCoord(7, 0)), Is.True,
                "Distance 4 from the front Base must not be cut off by the minimum-spacing rule.");
            Assert.That(WorldAnalysis.IsForwardBaseCandidate(snapshot.Self.BaseHexes,
                anchor, target, new HexCoord(4, 0)), Is.False);
            Assert.That(WorldAnalysis.IsForwardBaseCandidate(snapshot.Self.BaseHexes,
                anchor, target, new HexCoord(3, 3)), Is.False);
        }

        [Test]
        public void BaseExpansionDirection_UsesSanctionedCitadelCoordinateBeforeReconFindsIt()
        {
            var self = new Game.Players.PlayerSetupData();
            var enemy = new Game.Players.PlayerSetupData();
            WorldSnapshot snapshot = SnapshotWithDeficits(0f, 0f, actionable: true);
            snapshot.Self.BaseHexes = new[] { new HexCoord(0, 0) };
            snapshot.Known.Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>();
            snapshot.TrueWorld.AllBuildings = new[]
            {
                new BuildingSnapshot
                {
                    Hex = new HexCoord(10, -2), Owner = enemy, IsStartingCitadel = true,
                },
            };

            Assert.That(WorldAnalysis.TrySelectBaseExpansionDirection(
                snapshot, self, out HexCoord target, out HexCoord anchor), Is.True);
            Assert.That(target, Is.EqualTo(new HexCoord(10, -2)));
            Assert.That(anchor, Is.EqualTo(new HexCoord(0, 0)));
        }

        [Test]
        public void BaseExpansionSpacing_AppliesToActiveCommitmentToo()
        {
            IReadOnlyList<HexCoord> bases = new[] { new HexCoord(0, 0), new HexCoord(5, 0) };

            Assert.That(WorldAnalysis.MeetsBaseSpacing(bases, new HexCoord(6, 0)), Is.False);
            Assert.That(WorldAnalysis.MeetsBaseSpacing(bases, new HexCoord(8, 0)), Is.True);
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
        public void ExtractionYield_ReadsCompleteObservedMemoryWithoutLiveMap()
        {
            HexCoord hex = new HexCoord(2, 1);
            WorldSnapshot snapshot = SnapshotWithDeficits(0f, 0f, actionable: true);
            snapshot.Known.ResourceHexes = new[]
            {
                KnownResource(hex, ResourceType.Materials,
                    new ResourceBundle { Materials = 2f, Tech = 1f }),
            };

            var types = WorldAnalysis.KnownExtractionYields(snapshot)
                .ToDictionary(x => x.Type, x => x.Yield);

            Assert.That(types[ResourceType.Materials], Is.EqualTo(2));
            Assert.That(types[ResourceType.Tech], Is.EqualTo(1));
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
        public void EconomyArmyLightening_RefusesIncompleteThreatInsteadOfUsingFlatPower()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0f, 0f, actionable: true);
            var builder = new Game.Map.ArmyData();
            builder.Members.Add(Hero("Builder"));
            UnitData escort = Body("Escort", attack: 6, defense: 6);
            builder.Members.Add(escort);
            var threats = new[]
            {
                new Game.Ai.AiMapMemory.KnownEnemySighting(
                    new HexCoord(2, 0), new Game.Players.PlayerSetupData(), "enemy", 2,
                    defenseSum: 3f, attackSum: 5f, defenders: null),
            };

            IReadOnlyList<UnitData> retained = ProvisioningManager.SelectEconomyEscort(
                builder, new[] { escort }, threats);

            Assert.That(retained, Is.Null);
        }

        [Test]
        public void EconomyArmyLightening_TransfersBodiesPreservesHeroAndRecalculatesAp()
        {
            var player = new Game.Players.PlayerSetupData();
            HexCoord home = new HexCoord(0, 0);
            var builder = new ArmyData { Owner = player, Hex = home, Name = "Builder" };
            UnitData hero = Hero("Builder hero", activation: 1);
            UnitData heavy = Body("Heavy", 8, 8, activation: 3);
            UnitData light = Body("Light", 3, 3, activation: 2);
            builder.Members.AddRange(new[] { hero, heavy, light });
            var garrison = new ArmyData
                { Owner = player, Hex = home, Name = "Garrison", IsGarrison = true };
            var baseBuilding = new BuildingData
                { Owner = player, Hex = home, Name = "Base", IsBase = true };
            ArmyRegistry.Register(builder);
            ArmyRegistry.Register(garrison);
            BuildingRegistry.Register(home, baseBuilding);
            try
            {
                float before = ProvisioningManager.EconomyMissionClaimedAp(
                    builder, 1f, 1f, null);
                int moved = ProvisioningManager.TryLightenEconomyArmy(player, builder,
                    new HexCoord(4, 0), SnapshotWithDeficits(0f, 0f, true),
                    new Game.Ai.AiTurnContext());
                float after = ProvisioningManager.EconomyMissionClaimedAp(
                    builder, 1f, 1f, null);

                Assert.That(moved, Is.EqualTo(2));
                Assert.That(builder.Members, Is.EquivalentTo(new[] { hero }));
                Assert.That(garrison.Members, Is.EquivalentTo(new[] { heavy, light }));
                Assert.That(after, Is.LessThan(before));
                Assert.That(after, Is.EqualTo(2f));
            }
            finally
            {
                ArmyRegistry.Clear();
                BuildingRegistry.Clear();
            }
        }

        [Test]
        public void EconomyArmyLightening_FullGarrisonLeavesBothRostersUntouched()
        {
            var player = new Game.Players.PlayerSetupData();
            HexCoord home = new HexCoord(0, 0);
            var builder = new ArmyData { Owner = player, Hex = home, Name = "Builder" };
            UnitData hero = Hero("Hero");
            UnitData escort = Body("Escort", 5, 5);
            builder.Members.AddRange(new[] { hero, escort });
            var garrison = new ArmyData
                { Owner = player, Hex = home, Name = "Garrison", IsGarrison = true };
            for (int i = 0; i < 4; i++)
                garrison.Members.Add(Body("G" + i, 1, 1));
            ArmyRegistry.Register(builder);
            ArmyRegistry.Register(garrison);
            BuildingRegistry.Register(home, new BuildingData
                { Owner = player, Hex = home, Name = "Base", IsBase = true });
            try
            {
                int moved = ProvisioningManager.TryLightenEconomyArmy(player, builder,
                    new HexCoord(4, 0), SnapshotWithDeficits(0f, 0f, true),
                    new Game.Ai.AiTurnContext());

                Assert.That(moved, Is.Zero);
                Assert.That(builder.Members, Is.EquivalentTo(new[] { hero, escort }));
                Assert.That(garrison.Members, Has.Count.EqualTo(4));
            }
            finally
            {
                ArmyRegistry.Clear();
                BuildingRegistry.Clear();
            }
        }

        [Test]
        public void EconomyArmyLightening_DoesNotRunOutsideOwnBaseOrCitadel()
        {
            var player = new Game.Players.PlayerSetupData();
            var builder = new ArmyData
                { Owner = player, Hex = new HexCoord(0, 0), Name = "Builder" };
            UnitData hero = Hero("Hero");
            UnitData escort = Body("Escort", 5, 5);
            builder.Members.AddRange(new[] { hero, escort });
            var garrison = new ArmyData
                { Owner = player, Hex = builder.Hex, Name = "Garrison", IsGarrison = true };
            ArmyRegistry.Register(builder);
            ArmyRegistry.Register(garrison);
            try
            {
                int moved = ProvisioningManager.TryLightenEconomyArmy(player, builder,
                    new HexCoord(4, 0), SnapshotWithDeficits(0f, 0f, true),
                    new Game.Ai.AiTurnContext());

                Assert.That(moved, Is.Zero);
                Assert.That(builder.Members, Is.EquivalentTo(new[] { hero, escort }));
                Assert.That(garrison.Members, Is.Empty);
            }
            finally
            {
                ArmyRegistry.Clear();
                BuildingRegistry.Clear();
            }
        }

        [Test]
        public void EconomyArmyLightening_PreservesRosterOwnedByDurableMission()
        {
            var player = new Game.Players.PlayerSetupData();
            HexCoord home = new HexCoord(0, 0);
            var builder = new ArmyData { Owner = player, Hex = home, Name = "Builder" };
            UnitData hero = Hero("Hero");
            UnitData escort = Body("Escort", 5, 5);
            builder.Members.AddRange(new[] { hero, escort });
            var garrison = new ArmyData
                { Owner = player, Hex = home, Name = "Garrison", IsGarrison = true };
            ArmyRegistry.Register(builder);
            ArmyRegistry.Register(garrison);
            BuildingRegistry.Register(home, new BuildingData
                { Owner = player, Hex = home, Name = "Base", IsBase = true });
            MissionIntentRegistry.GetOrCreate(player).Put(new MissionIntent
            {
                Kind = MissionKind.Scout,
                Status = IntentStatus.Active,
                PreferredMoverArmyId = builder.Id,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore },
            });
            try
            {
                int moved = ProvisioningManager.TryLightenEconomyArmy(player, builder,
                    new HexCoord(4, 0), SnapshotWithDeficits(0f, 0f, true),
                    new Game.Ai.AiTurnContext());

                Assert.That(moved, Is.Zero);
                Assert.That(builder.Members, Is.EquivalentTo(new[] { hero, escort }));
                Assert.That(garrison.Members, Is.Empty);
            }
            finally
            {
                MissionIntentRegistry.Clear();
                ArmyRegistry.Clear();
                BuildingRegistry.Clear();
            }
        }

        [Test]
        public void EconomyArmyLightening_KeepsMinimalSkillAwareSafeEscort()
        {
            var builder = new ArmyData { Name = "Builder" };
            UnitData hero = Hero("Hero");
            UnitData counter = Body("Counter", 20, 20);
            counter.Abilities.Add(Game.Cards.UnitAbilities.Hyperkinetic);
            UnitData spare = Body("Spare", 2, 2);
            builder.Members.AddRange(new[] { hero, counter, spare });
            var enemyProfile = new Game.Combat.WorthIt.DefenderProfile(
                defense: 5f, hasCeramicArmor: false,
                typeTags: new[] { Game.Cards.UnitTypeTag.Armored },
                attack: 5f, hitPoints: 5f, initiative: 1);
            var threats = new[]
            {
                new Game.Ai.AiMapMemory.KnownEnemySighting(
                    new HexCoord(2, 0), new Game.Players.PlayerSetupData(), "enemy", 1,
                    defenseSum: 5f, attackSum: 5f, defenders: new[] { enemyProfile }),
            };

            IReadOnlyList<UnitData> retained = ProvisioningManager.SelectEconomyEscort(
                builder, new[] { counter, spare }, threats);

            Assert.That(retained, Is.Not.Null);
            Assert.That(retained, Has.Count.EqualTo(1));
            Assert.That(retained[0], Is.SameAs(counter));
        }

        [Test]
        public void BaseExpansionUrgency_GrowsAfterDeferralButStaysLaneLocal()
        {
            var player = new Game.Players.PlayerSetupData();
            var baseDef = new CardDefinition
                { cardType = CardType.Base, authoredKey = "base", displayName = "Base" };
            CardData card = new CardData(baseDef);
            WorldSnapshot snapshot = SnapshotWithDeficits(0.2f, 0.1f, actionable: true);
            snapshot.Self.Hand = new[] { card };
            ArmySnapshot builder = EconomyBuilder(31, 1, 1f);
            snapshot.Self.Armies = new[] { builder };
            snapshot.Economy.BaseOpportunities = new[]
            {
                new EconomyBaseOpportunity
                {
                    Hex = new HexCoord(4, 0), CapacityValue = 1f,
                    InfrastructurePressure = 1f,
                    BuilderRoutes = new[] { BuilderRoute(builder, 4, 0, 1) },
                },
            };
            try
            {
                AxisDemand first = DemandLayer.EconomyDemands(snapshot,
                    new DesireBreakdown(), player, null, null).Single();
                MissionIntentRegistry.GetOrCreate(player)
                    .ReconcileBaseExpansionWait(1, System.Array.Empty<MissionTurnOutcome>());
                snapshot.TurnNumber = 2;
                AxisDemand second = DemandLayer.EconomyDemands(snapshot,
                    new DesireBreakdown(), player, null, null).Single();
                MissionProposal mission = EconomyMissionPlanner.Propose(snapshot,
                    new DesireBreakdown(), null, new[] { second }).Single();

                Assert.That(first.EconomyStrategicUrgency, Is.Zero);
                Assert.That(second.EconomyStrategicUrgency,
                    Is.EqualTo(AiConfigV2.economyBaseUrgencyPerDeferredTurn));
                Assert.That(mission.BaseValue, Is.EqualTo(second.Value));
                Assert.That(mission.LocalAdmissionScore, Is.GreaterThan(mission.BaseValue));
                MissionIntentRegistry.GetOrCreate(player)
                    .MarkBaseExpansionCandidate(2, structurallyEligible: false);
                Assert.That(MissionIntentRegistry.GetOrCreate(player).BaseExpansionWaitTurns,
                    Is.Zero);
            }
            finally
            {
                MissionIntentRegistry.Clear();
            }
        }

        [Test]
        public void BaseExpansionUrgency_CanAdmitPositiveSiteInitiallyBelowDemandThreshold()
        {
            var player = new Game.Players.PlayerSetupData();
            var baseDef = new CardDefinition
                { cardType = CardType.Base, authoredKey = "base", displayName = "Base" };
            WorldSnapshot snapshot = SnapshotWithDeficits(0f, 0f, actionable: true);
            snapshot.TurnNumber = 1;
            snapshot.Self.Hand = new[] { new CardData(baseDef) };
            ArmySnapshot builder = EconomyBuilder(32, 1, 1f);
            snapshot.Self.Armies = new[] { builder };
            snapshot.Economy.BaseOpportunities = new[]
            {
                new EconomyBaseOpportunity
                {
                    Hex = new HexCoord(3, 0), CapacityValue = 1f,
                    BuilderRoutes = new[] { BuilderRoute(builder, 0, 0, 1) },
                },
            };
            try
            {
                Assert.That(DemandLayer.EconomyDemands(snapshot,
                    new DesireBreakdown(), player, null, null), Is.Empty,
                    "A merely positive site may remain below the normal admission threshold initially.");
                MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
                state.ReconcileBaseExpansionWait(1, System.Array.Empty<MissionTurnOutcome>());

                snapshot.TurnNumber = 2;
                AxisDemand admitted = DemandLayer.EconomyDemands(snapshot,
                    new DesireBreakdown(), player, null, null).Single();

                Assert.That(admitted.Value, Is.GreaterThan(0f));
                Assert.That(admitted.Value, Is.LessThan(AiConfigV2.economyBaseDemandMinValue));
                Assert.That(admitted.EconomyStrategicUrgency,
                    Is.EqualTo(AiConfigV2.economyBaseUrgencyPerDeferredTurn));
            }
            finally
            {
                MissionIntentRegistry.Clear();
            }
        }

        [Test]
        public void EconomyActorInvalidation_ReadmitsExistingInfrastructureOwner()
        {
            Assert.That(DesireAxes.InvalidationMaskFor(DesireAxis.Economy)
                .HasFlag(StrategicInvalidationReason.Actor), Is.True);
        }

        [Test]
        public void ExtractionBuilderConsequences_RevealHeroButNotHiddenEscort()
        {
            var army = new ArmyData();
            UnitData hero = Hero("Builder");
            UnitData escort = Body("Escort", 2, 2);
            hero.MoveCurrent = 2;
            escort.MoveCurrent = 2;
            hero.IsHidden = true;
            escort.IsHidden = true;
            army.Members.AddRange(new[] { hero, escort });

            HexSelectionController.ApplyExtractionBuilderConsequences(army);

            Assert.That(hero.MoveCurrent, Is.Zero);
            Assert.That(escort.MoveCurrent, Is.Zero);
            Assert.That(hero.IsHidden, Is.False);
            Assert.That(escort.IsHidden, Is.True);
        }

        [TestCase(true, true, false, true)]
        [TestCase(false, true, false, false)]
        [TestCase(true, false, false, false)]
        [TestCase(true, true, true, false)]
        public void ResearchProductionAutoAcceptDelay_OnlyFollowsFinalAutorollSpend(
            bool autoroll, bool spent, bool declined, bool expected)
        {
            Assert.That(BattleAttackPopupUI.NeedsResearchProductionAutoAcceptDelay(
                autoroll, spent, declined), Is.EqualTo(expected));
        }

        [Test]
        public void BaseExpansionUrgency_ResetsWhenBaseBuildCompletes()
        {
            var player = new Game.Players.PlayerSetupData();
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            try
            {
                state.MarkBaseExpansionCandidate(1, structurallyEligible: true);
                state.ReconcileBaseExpansionWait(1,
                    System.Array.Empty<MissionTurnOutcome>());
                Assert.That(state.BaseExpansionWaitTurns, Is.EqualTo(1));

                state.MarkBaseExpansionCandidate(2, structurallyEligible: true);
                state.ReconcileBaseExpansionWait(2, new[]
                {
                    new MissionTurnOutcome
                    {
                        MissionKind = MissionKind.Economy,
                        HasEconomyPayload = true,
                        EconomyTarget = new EconomyMissionTarget
                            { Kind = EconomyTaskKind.FoundBase },
                        EconomyBuildCompleted = true,
                    },
                });

                Assert.That(state.BaseExpansionWaitTurns, Is.Zero);
            }
            finally
            {
                MissionIntentRegistry.Clear();
            }
        }

        [Test]
        public void BaseExpansionUrgency_ResetsOnPreProvisionStructuralInvalidation()
        {
            var player = new Game.Players.PlayerSetupData();
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            try
            {
                state.MarkBaseExpansionCandidate(1, structurallyEligible: true);
                state.ReconcileBaseExpansionWait(1,
                    System.Array.Empty<MissionTurnOutcome>());
                state.MarkBaseExpansionCandidate(2, structurallyEligible: true);
                state.ReconcileBaseExpansionWait(2, new[]
                {
                    new MissionTurnOutcome
                    {
                        MissionKind = MissionKind.Economy,
                        Proposal = new MissionProposal
                        {
                            Kind = MissionKind.Economy,
                            Target = new EconomyMissionTarget
                                { Kind = EconomyTaskKind.FoundBase },
                        },
                        StructuralFailure = true,
                    },
                });

                Assert.That(state.BaseExpansionWaitTurns, Is.Zero);
            }
            finally
            {
                MissionIntentRegistry.Clear();
            }
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
            snapshot.Known.ResourceHexes = new List<Game.Ai.AiMapMemory.KnownResourceHex>
            {
                KnownResource(new HexCoord(-5, -5), ResourceType.Human, new ResourceBundle { Human = 1f }),
                KnownResource(new HexCoord(4, 4), ResourceType.Tech, new ResourceBundle { Tech = 1f }),
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
            snapshot.Known.ResourceHexes = new List<Game.Ai.AiMapMemory.KnownResourceHex>
            {
                KnownResource(site, ResourceType.Human, new ResourceBundle { Human = 1f }),
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
            snapshot.Known.ResourceHexes = new List<Game.Ai.AiMapMemory.KnownResourceHex>
            {
                KnownResource(site, ResourceType.Human, new ResourceBundle { Human = 1f }),
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

        [Test]
        public void EconomyDemand_ImmediateHandBottleneckBeatsConvenientDeckResource()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.65f, 0.1f, actionable: true);
            snapshot.Economy.PerType = new[]
            {
                new EconomyResourceStanding { Type = ResourceType.Human, DeficitScore = 0.1f },
                new EconomyResourceStanding
                {
                    Type = ResourceType.Energy, DeficitScore = 0.5f,
                    HandResourceNeed = 8f, SpendableStockpile = 0f,
                },
                new EconomyResourceStanding { Type = ResourceType.Materials, DeficitScore = 0.1f },
                new EconomyResourceStanding
                {
                    Type = ResourceType.Tech, DeficitScore = 0.65f,
                    RemainingDeckResourceNeed = 8f, SpendableStockpile = 0f,
                },
            };
            ArmySnapshot builder = EconomyBuilder(7, 1, 3f);
            snapshot.Self.Armies = new[] { builder };
            EconomyExtractionOpportunity energy = ExtractionOpportunity(
                new HexCoord(2, 0), ResourceType.Energy, 1);
            energy.BuilderRoutes = new[] { BuilderRoute(builder, 2, 2, 1) };
            EconomyExtractionOpportunity tech = ExtractionOpportunity(
                new HexCoord(0, 0), ResourceType.Tech, 1);
            tech.BuilderRoutes = new[] { BuilderRoute(builder, 0, 0, 1) };
            snapshot.Economy.ExtractionOpportunities = new[] { energy, tech };

            AxisDemand selected = DemandLayer.EconomyDemands(
                snapshot, new DesireBreakdown(), null, null, null).Single();

            Assert.That(selected.EconomyResourceType, Is.EqualTo(ResourceType.Energy));
            Assert.That(selected.TargetHex, Is.EqualTo(new HexCoord(2, 0)));
        }

        [Test]
        public void EconomyDemand_ProtectedExtractionCanBeatHigherYieldExposedPeer()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.7f, 0.1f, actionable: true);
            snapshot.Economy.ExtractionOpportunities = new[]
            {
                new EconomyExtractionOpportunity
                {
                    Hex = new HexCoord(2, 0), ResourceType = ResourceType.Human,
                    EffectiveYield = 1, MarginalIncomeGain = 1, BaseNetworkSynergy = 1f,
                },
                new EconomyExtractionOpportunity
                {
                    Hex = new HexCoord(7, 0), ResourceType = ResourceType.Human,
                    EffectiveYield = 2, MarginalIncomeGain = 2, BaseNetworkSynergy = 0f,
                },
            };

            AxisDemand selected = DemandLayer.EconomyDemands(
                snapshot, new DesireBreakdown(), null, null, null).Single();

            Assert.That(selected.TargetHex, Is.EqualTo(new HexCoord(2, 0)));
        }

        [Test]
        public void EconomyBaseDemand_StrategicResourceCorridorBeatsBuilderConvenience()
        {
            WorldSnapshot snapshot = SnapshotWithDeficits(0.8f, 0.2f, actionable: true);
            snapshot.Economy.PerType = new[]
            {
                new EconomyResourceStanding { Type = ResourceType.Human, DeficitScore = 0.2f },
                new EconomyResourceStanding { Type = ResourceType.Energy, DeficitScore = 0.8f },
                new EconomyResourceStanding { Type = ResourceType.Materials, DeficitScore = 0.8f },
                new EconomyResourceStanding { Type = ResourceType.Tech, DeficitScore = 0.2f },
            };
            var baseDef = new CardDefinition
            {
                cardType = CardType.Base, authoredKey = "base", displayName = "Base",
            };
            snapshot.Self.Hand = new[] { new CardData(baseDef) };
            ArmySnapshot builder = EconomyBuilder(9, 1, 3f);
            builder.Hex = new HexCoord(3, 1);
            snapshot.Self.Armies = new[] { builder };

            EconomyBaseOpportunity convenient = new EconomyBaseOpportunity
            {
                Hex = new HexCoord(3, 1), CapacityValue = 0.5f,
                InfrastructurePressure = 1f, LogisticsValue = 1f,
                SupportValue = 1f, ForwardProgressValue = 0.3f,
                CorridorAlignmentValue = 0.5f,
                BuilderRoutes = new[] { BuilderRoute(builder, 0, 3, 1) },
            };
            EconomyBaseOpportunity strategic = new EconomyBaseOpportunity
            {
                Hex = new HexCoord(6, 0), CapacityValue = 0.5f,
                HexYield = new ResourceBundle { Energy = 1f, Materials = 1f },
                NearbyResourceClusterValue = 0.5f,
                NetworkExpansionValue = 0.5f, LogisticsValue = 0.5f,
                SupportValue = 0.3f, ForwardProgressValue = 1f,
                CorridorAlignmentValue = 1f,
                BuilderRoutes = new[] { BuilderRoute(builder, 4, 3, 1) },
            };
            snapshot.Economy.BaseOpportunities = new[] { convenient, strategic };

            AxisDemand selected = DemandLayer.EconomyDemands(
                    snapshot, new DesireBreakdown(), null, null, null)
                .Single(x => x.EconomyBuildCard != null);

            Assert.That(selected.TargetHex, Is.EqualTo(new HexCoord(6, 0)));
        }

        [Test]
        public void Allocation_HighValueEconomyPreemptsSoftReconCommitment()
        {
            var player = new Game.Players.PlayerSetupData();
            AiAllocatorStateRegistry.Clear();
            WorldSnapshot snapshot = SnapshotWithDeficits(0.8f, 0.2f, actionable: true);
            snapshot.Self.ActionPoints = 1f;
            MissionProposal scout = AllocatorMission(
                MissionKind.Scout, 20f, DesireAxis.Recon, armyId: 7);
            MissionProposal economy = AllocatorMission(
                MissionKind.Economy, 80f, DesireAxis.Economy, armyId: 7);
            var commitment = new Commitment
            {
                IntentKey = MissionIntentKey.For(scout), Mission = scout,
                Tier = CommitmentTier.Soft, ContinuationValue = scout.BaseValue,
            };

            TentativeAllocation allocation = ResourceAllocator.BeginTurn(
                snapshot, Radar.Even(), new List<MissionProposal> { scout, economy },
                new List<Commitment> { commitment }, player).Pack();

            Assert.That(allocation.Funded.Select(x => x.Mission), Is.EqualTo(new[] { economy }));
        }

        [Test]
        public void Allocation_HardReconCommitmentRemainsProtectedFromEconomy()
        {
            var player = new Game.Players.PlayerSetupData();
            AiAllocatorStateRegistry.Clear();
            WorldSnapshot snapshot = SnapshotWithDeficits(0.8f, 0.2f, actionable: true);
            snapshot.Self.ActionPoints = 1f;
            MissionProposal scout = AllocatorMission(
                MissionKind.Scout, 20f, DesireAxis.Recon, armyId: 7);
            MissionProposal economy = AllocatorMission(
                MissionKind.Economy, 80f, DesireAxis.Economy, armyId: 7);
            var commitment = new Commitment
            {
                IntentKey = MissionIntentKey.For(scout), Mission = scout,
                Tier = CommitmentTier.Hard, ContinuationValue = scout.BaseValue,
            };

            TentativeAllocation allocation = ResourceAllocator.BeginTurn(
                snapshot, Radar.Even(), new List<MissionProposal> { scout, economy },
                new List<Commitment> { commitment }, player).Pack();

            Assert.That(allocation.Funded.Select(x => x.Mission), Is.EqualTo(new[] { scout }));
        }

        private static MissionProposal AllocatorMission(
            MissionKind kind, float value, DesireAxis axis, int armyId)
        {
            object target = kind == MissionKind.Scout
                ? (object)new ScoutMissionTarget
                {
                    Kind = ScoutTargetKind.Explore, FocusHex = new HexCoord(1, 0),
                }
                : new EconomyMissionTarget
                {
                    Kind = EconomyTaskKind.BuildExtraction,
                    TargetHex = new HexCoord(2, 0),
                    ResourceType = ResourceType.Energy,
                };
            var mission = new MissionProposal
            {
                Kind = kind, Target = target,
                BaseValue = value, EffectiveValue = value,
                LocalAdmissionScore = value,
                PreferredMoverArmyId = armyId,
                Requirements = new MissionRequirements
                {
                    MoverKnown = true,
                    ApMinimum = 1f, ApDesired = 1f, ApMaximum = 1f,
                },
            };
            mission.Axes.Value[axis] = 1f;
            return mission;
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

        private static Game.Ai.AiMapMemory.KnownResourceHex KnownResource(
            HexCoord hex, ResourceType dominant, ResourceBundle yield) =>
            new Game.Ai.AiMapMemory.KnownResourceHex(hex, dominant,
                new ResourceYields
                {
                    human = (int)yield.Human,
                    energy = (int)yield.Energy,
                    materials = (int)yield.Materials,
                    tech = (int)yield.Tech,
                });

        private static UnitData Hero(string name, int activation = 1) => new UnitData
        {
            Name = name,
            IsHero = true,
            CommandRating = 8,
            ActivationApCost = activation,
            Attack = 1,
            Defense = 1,
            HitPointsMax = 2,
            HitPointsCurrent = 2,
            Initiative = 1,
        };

        private static UnitData Body(string name, int attack, int defense,
            int activation = 1) => new UnitData
        {
            Name = name,
            ActivationApCost = activation,
            Attack = attack,
            Defense = defense,
            HitPointsMax = 5,
            HitPointsCurrent = 5,
            Initiative = 2,
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
                    ResourceHexes = new List<Game.Ai.AiMapMemory.KnownResourceHex>(),
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
