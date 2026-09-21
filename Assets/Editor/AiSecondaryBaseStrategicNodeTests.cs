#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Combat;
using Game.Ai;
using Game.Aviation;
using Game.Economy;
using Game.Terrain;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class AiSecondaryBaseStrategicNodeTests
    {
        [Test]
        public void BuildingProjection_PreservesAuthoritativeBaseIdentity()
        {
            var building = new BuildingData
            {
                Hex = new HexCoord(-2, 3),
                IsBase = true,
                IsStartingCitadel = false,
            };

            BuildingSnapshot snapshot = WorldAnalysis.ToBuildingSnapshot(building);

            Assert.That(snapshot.IsBase, Is.True);
            Assert.That(snapshot.IsStartingCitadel, Is.False);
            Assert.That(WorldAnalysis.ClassifyBuildingAsset(snapshot), Is.EqualTo(AssetKind.Base));
        }

        [Test]
        public void BarracksAbility_DoesNotTurnFacilityIntoBase()
        {
            var building = new BuildingData { IsBase = false };
            var installed = new FacilityData();
            installed.Abilities.Add(UnitAbilities.Barracks);
            building.FacilitySlots[0] = installed;

            BuildingSnapshot snapshot = WorldAnalysis.ToBuildingSnapshot(building);

            Assert.That(snapshot.HasFacilityAbility(UnitAbilities.Barracks), Is.True);
            Assert.That(snapshot.IsBase, Is.False);
            Assert.That(WorldAnalysis.ClassifyBuildingAsset(snapshot), Is.EqualTo(AssetKind.Facility));
        }

        [Test]
        public void StartingCitadel_RemainsCitadelEvenWhenItIsAlsoBase()
        {
            var building = new BuildingData { IsBase = true, IsStartingCitadel = true };

            Assert.That(WorldAnalysis.ClassifyBuildingAsset(
                WorldAnalysis.ToBuildingSnapshot(building)), Is.EqualTo(AssetKind.Citadel));
        }

        [Test]
        public void OwnedBaseHexes_ComesFromBuildingsWithoutGarrisonEvidence()
        {
            var owner = new PlayerSetupData();
            var enemy = new PlayerSetupData();
            var citadel = new HexCoord(0, 0);
            var forward = new HexCoord(-2, 3);
            var buildings = new[]
            {
                new BuildingData { Owner = owner, Hex = citadel, IsBase = true, IsStartingCitadel = true },
                new BuildingData { Owner = owner, Hex = forward, IsBase = true },
                new BuildingData { Owner = owner, Hex = new HexCoord(4, 4), IsBase = false },
                new BuildingData { Owner = enemy, Hex = new HexCoord(5, 5), IsBase = true },
            };

            List<HexCoord> result = WorldAnalysis.OwnedBaseHexes(buildings, owner, citadel);

            Assert.That(result, Is.EquivalentTo(new[] { citadel, forward }));
        }

        [Test]
        public void OwnedBaseHexes_DropsBaseAfterOwnershipLoss()
        {
            var oldOwner = new PlayerSetupData();
            var newOwner = new PlayerSetupData();
            var citadel = new HexCoord(0, 0);
            var lost = new HexCoord(-2, 3);
            var buildings = new[]
            {
                new BuildingData { Owner = oldOwner, Hex = citadel, IsBase = true, IsStartingCitadel = true },
                new BuildingData { Owner = newOwner, Hex = lost, IsBase = true },
            };

            Assert.That(WorldAnalysis.OwnedBaseHexes(buildings, oldOwner, citadel),
                Is.EqualTo(new[] { citadel }));
        }

        [Test]
        public void OwnedBaseHexes_DoesNotResurrectDestroyedCitadelFromConfiguredCoordinates()
        {
            var owner = new PlayerSetupData();
            Assert.That(WorldAnalysis.OwnedBaseHexes(System.Array.Empty<BuildingData>(),
                owner, new HexCoord(0, 0)), Is.Empty);
        }

        [Test]
        public void DefensiveReserve_CountsOneEnemyOnceAcrossManyAssets()
        {
            EnemyContactSnapshot enemy = Contact(20, 12f);
            var threats = new List<AssetThreatSnapshot>
            {
                Threat(enemy, AssetKind.Citadel, new HexCoord(0, 0), 1f),
                Threat(enemy, AssetKind.Base, new HexCoord(2, 0), 0.8f),
                Threat(enemy, AssetKind.Facility, new HexCoord(3, 0), 0.5f),
            };

            float reserve = StrategyLayer.DefensiveReserveForThreats(threats);

            Assert.That(reserve, Is.EqualTo(12f * AiConfigV2.aggDefenceConfidenceMargin).Within(0.001f));
        }

        [Test]
        public void DefensiveReserve_AddsIndependentEnemyForces()
        {
            EnemyContactSnapshot first = Contact(20, 12f);
            EnemyContactSnapshot second = Contact(21, 8f);
            var threats = new List<AssetThreatSnapshot>
            {
                Threat(first, AssetKind.Base, new HexCoord(2, 0), 0.9f),
                Threat(first, AssetKind.Citadel, new HexCoord(0, 0), 1f),
                Threat(second, AssetKind.Base, new HexCoord(2, 0), 0.7f),
            };

            float reserve = StrategyLayer.DefensiveReserveForThreats(threats);

            Assert.That(reserve, Is.EqualTo(20f * AiConfigV2.aggDefenceConfidenceMargin).Within(0.001f));
        }

        [Test]
        public void DefensiveReserve_CorrelatesHonestAndHiddenViewsOfSamePhysicalArmy()
        {
            EnemyContactSnapshot honest = Contact(20, 12f);
            honest.PhysicalArmyId = 20;
            EnemyContactSnapshot hidden = Contact(-1, 12f);
            hidden.Source = ContactSource.Cheat;
            hidden.PhysicalArmyId = 20;

            float reserve = StrategyLayer.DefensiveReserveForThreats(new[]
            {
                Threat(honest, AssetKind.Citadel, new HexCoord(0, 0), 1f),
                Threat(hidden, AssetKind.Base, new HexCoord(2, 0), 0.8f),
            });

            Assert.That(reserve, Is.EqualTo(12f * AiConfigV2.aggDefenceConfidenceMargin).Within(0.001f));
        }

        [Test]
        public void ActiveDefenceShortage_CreatesTargetBoundFieldPowerDemand()
        {
            var owner = new PlayerSetupData();
            var target = new HexCoord(-2, 3);
            WorldSnapshot snap = DefenceSnapshot(owner, Contact(28, 10f),
                System.Array.Empty<ArmySnapshot>());
            ActiveDefenceObjective objective = DefenceObjective(28, target);

            IReadOnlyList<AxisDemand> demands = AggressionDemandEvaluator.BuildActiveDefenceDemands(
                snap, new[] { objective }, System.Array.Empty<MissionIntent>(),
                new ActorCommitments(), owner, out _);

            Assert.That(demands, Has.Count.EqualTo(1));
            Assert.That(demands[0].Capability, Is.EqualTo(CapabilityKind.FieldCombatPower));
            Assert.That(demands[0].TargetHex, Is.EqualTo(target));
            Assert.That(demands[0].ConsumerIntentKey,
                Is.EqualTo(MissionIntentKey.ForActiveDefence(28)));
            Assert.That(demands[0].ConsumerMissionKind, Is.EqualTo(MissionKind.ActiveDefence));
        }

        [Test]
        public void ActiveDefence_WithSuitableFieldArmyCreatesNoProductionDemand()
        {
            var owner = new PlayerSetupData();
            var actor = new ArmySnapshot
            {
                ArmyId = 7, Owner = owner, IsStructuralRaidActor = true,
                EffectiveArmyPower = 30f, MemberCount = 2, CurrentMovement = 4,
                Members = new[] { default(WorthIt.DefenderProfile), default(WorthIt.DefenderProfile) },
            };
            WorldSnapshot snap = DefenceSnapshot(owner, Contact(28, 10f), new[] { actor });

            IReadOnlyList<AxisDemand> demands = AggressionDemandEvaluator.BuildActiveDefenceDemands(
                snap, new[] { DefenceObjective(28, new HexCoord(-2, 3)) },
                System.Array.Empty<MissionIntent>(), new ActorCommitments(), owner, out _);

            Assert.That(demands, Is.Empty);
        }

        [Test]
        public void ActiveDefenceShortage_DoesNotBuyForMoverContention()
        {
            var owner = new PlayerSetupData();
            EnemyContactSnapshot enemy = Contact(28, 10f);
            var actor = new ArmySnapshot
            {
                ArmyId = 7, Owner = owner, IsStructuralRaidActor = true,
                EffectiveArmyPower = 20f, MemberCount = 1,
                Members = System.Array.Empty<WorthIt.DefenderProfile>(),
            };
            WorldSnapshot snap = DefenceSnapshot(owner, enemy, new[] { actor });
            var commitments = new ActorCommitments();
            commitments.Claim(actor.ArmyId);

            IReadOnlyList<AxisDemand> demands = AggressionDemandEvaluator.BuildActiveDefenceDemands(
                snap, new[] { DefenceObjective(28, new HexCoord(-2, 3)) },
                System.Array.Empty<MissionIntent>(), commitments, owner, out _);

            Assert.That(demands, Is.Empty);
        }

        [Test]
        public void ActiveDefenceShortage_DoesNotBuyForSpentMovement()
        {
            var owner = new PlayerSetupData();
            EnemyContactSnapshot enemy = Contact(28, 10f);
            var actor = new ArmySnapshot
            {
                ArmyId = 7, Owner = owner, IsStructuralRaidActor = true,
                EffectiveArmyPower = 20f, MemberCount = 1, CurrentMovement = 0,
                HasActivatedThisTurn = true,
                Members = System.Array.Empty<WorthIt.DefenderProfile>(),
            };
            WorldSnapshot snap = DefenceSnapshot(owner, enemy, new[] { actor });

            IReadOnlyList<AxisDemand> demands = AggressionDemandEvaluator.BuildActiveDefenceDemands(
                snap, new[] { DefenceObjective(28, new HexCoord(-2, 3)) },
                System.Array.Empty<MissionIntent>(), new ActorCommitments(), owner, out _);

            Assert.That(demands, Is.Empty);
        }

        [Test]
        public void ActiveDefence_DoesNotTurnEnemyBaseIntoImplicitAttack()
        {
            var owner = new PlayerSetupData();
            var enemyOwner = new PlayerSetupData();
            EnemyContactSnapshot enemy = Contact(28, 10f);
            WorldSnapshot snap = DefenceSnapshot(owner, enemy,
                System.Array.Empty<ArmySnapshot>());
            snap.Known = new KnownSnapshot
            {
                Buildings = new[]
                {
                    new AiMapMemory.KnownBuilding(enemy.Position.Value, enemyOwner, false,
                        System.Array.Empty<string>(), isBase: true),
                },
            };

            IReadOnlyList<AxisDemand> demands = AggressionDemandEvaluator.BuildActiveDefenceDemands(
                snap, new[] { DefenceObjective(28, new HexCoord(-2, 3)) },
                System.Array.Empty<MissionIntent>(), new ActorCommitments(), owner, out _);

            Assert.That(demands, Is.Empty);
        }

        [Test]
        public void NonAttackRouting_BlocksKnownForeignStructuresButNotOwnBase()
        {
            var owner = new PlayerSetupData();
            var enemy = new PlayerSetupData();
            var hostileHex = new HexCoord(2, 0);
            var ownHex = new HexCoord(0, 0);
            var known = new[]
            {
                new AiMapMemory.KnownBuilding(hostileHex, enemy, false,
                    System.Array.Empty<string>(), isBase: true),
                new AiMapMemory.KnownBuilding(ownHex, owner, true,
                    System.Array.Empty<string>(), isBase: true),
            };

            HashSet<HexCoord> blocked = SafeStepPathing.KnownForeignStructureHexes(owner, known);

            Assert.That(blocked, Does.Contain(hostileHex));
            Assert.That(blocked, Has.No.Member(ownHex));
            Assert.That(new AiDecision().AllowHostileStructureCapture, Is.False);
        }

        [Test]
        public void AviationFeasibility_ReturnsEveryOwnedAirfieldWithCapacity()
        {
            var citadel = new HexCoord(0, 0);
            var forward = new HexCoord(-2, 3);

            List<HexCoord> options = PlacementRules.FeasibleAviationAirfields(
                new[] { citadel, forward }, _ => 1);

            Assert.That(options, Is.EquivalentTo(new[] { citadel, forward }));
        }

        [Test]
        public void AviationFeasibility_ExcludesFullForwardBase()
        {
            var citadel = new HexCoord(0, 0);
            var forward = new HexCoord(-2, 3);

            List<HexCoord> options = PlacementRules.FeasibleAviationAirfields(
                new[] { citadel, forward }, hex => hex.Equals(forward) ? 0 : 1);

            Assert.That(options, Is.EqualTo(new[] { citadel }));
        }

        [Test]
        public void AviationPlacement_NoObjectivesAddsNoArtificialForwardBonus()
        {
            TaskScore score = NonCombatCardPlayer.BestAirfieldServiceTaskScore(
                null, null, null, new CardDefinition { isAviation = true },
                new HexCoord(-2, 3), System.Array.Empty<ReconObjective>(),
                out int coverage, out string witness);

            Assert.That(coverage, Is.Zero);
            Assert.That(witness, Is.Null);
            Assert.That(score.Value, Is.Zero);
        }

        [Test]
        public void ActiveDefenceCompletion_HoldsLocalActorForUnderGarrisonedBase()
        {
            HexCoord forward = new HexCoord(2, 0);
            var actor = new ArmySnapshot
            {
                ArmyId = 7, Hex = forward,
                Members = new[] { default(WorthIt.DefenderProfile) },
            };
            var snap = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    BaseHexes = new[] { forward },
                    Armies = new[]
                    {
                        actor,
                        new ArmySnapshot
                        {
                            ArmyId = 8, Hex = forward, IsGarrison = true,
                            Members = System.Array.Empty<WorthIt.DefenderProfile>(),
                        },
                    },
                },
            };
            var defence = new ActiveDefenceIntent
            {
                ProtectedAssetKind = AssetKind.Base,
                ProtectedAssetHex = forward,
            };

            Assert.That(MissionContinuityLayer.RequiresLocalBaseStabilization(
                snap, defence, actor), Is.True);
            snap.Self.Armies = new[]
            {
                actor,
                new ArmySnapshot
                {
                    ArmyId = 8, Hex = forward, IsGarrison = true,
                    Members = new WorthIt.DefenderProfile[AiConfig.secureBaseMinNonHeroUnits],
                },
            };
            Assert.That(MissionContinuityLayer.RequiresLocalBaseStabilization(
                snap, defence, actor), Is.False);

            snap.Self.BaseHexes = System.Array.Empty<HexCoord>();
            Assert.That(MissionContinuityLayer.ProtectedBaseWasLost(snap, defence), Is.True);
        }

        [Test]
        public void AviationRebaseScore_RequiresOperationalGainBeyondRealLaunchCost()
        {
            TaskScore noGain = AviationRebasePlanner.ScoreImprovement(
                new TaskScore(strategicRelevance: 5f),
                new TaskScore(strategicRelevance: 5f), 1, 1);
            TaskScore materialGain = AviationRebasePlanner.ScoreImprovement(
                default, new TaskScore(strategicRelevance: 8f), 1, 1);

            Assert.That(noGain.Value, Is.LessThanOrEqualTo(0f));
            Assert.That(materialGain.Value, Is.GreaterThan(0f));
        }

        [Test]
        public void AviationRebase_KnownAaExposureIsHardVoluntaryBlock()
        {
            Assert.That(AiAirSortiePlanner.IsVoluntaryRebaseRouteSafe(0), Is.True);
            Assert.That(AiAirSortiePlanner.IsVoluntaryRebaseRouteSafe(1), Is.False);
        }

        [Test]
        public void AviationRebaseCandidate_UsesRealRouteCapacityAndCurrentReconObjective()
        {
            var owner = new PlayerSetupData();
            GameObject mapObject = new GameObject("secondary-base-rebase-map");
            GameObject rootObject = new GameObject("secondary-base-rebase-root");
            try
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AiMapMemory.Clear();
                AirSortieRegistry.Clear();
                AiResourceReservation.Clear();

                HexMap map = mapObject.AddComponent<HexMap>();
                var terrain = new TerrainTypeEntry { moveCost = 1 };
                map.SetData(3, 1f, new Dictionary<HexCoord, TerrainTypeEntry>
                {
                    [new HexCoord(0, 0)] = terrain,
                    [new HexCoord(1, 0)] = terrain,
                    [new HexCoord(2, 0)] = terrain,
                    [new HexCoord(3, 0)] = terrain,
                });
                HexCoord sourceHex = new HexCoord(0, 0);
                HexCoord forwardHex = new HexCoord(2, 0);
                BuildingRegistry.Register(sourceHex, new BuildingData
                {
                    Owner = owner, Hex = sourceHex, IsBase = true,
                    IsStartingCitadel = true, AirfieldCapacity = 2,
                });
                BuildingRegistry.Register(forwardHex, new BuildingData
                {
                    Owner = owner, Hex = forwardHex, IsBase = true, AirfieldCapacity = 2,
                });
                var aircraft = new UnitData
                {
                    Owner = owner, IsAviation = true, MoveMax = 2, MoveCurrent = 2,
                    ActivationApCost = 1, LaunchEnergyCost = 1,
                };
                var storage = new ArmyData
                {
                    Owner = owner, Hex = sourceHex, IsAirfield = true, Name = "Airfield",
                };
                storage.AddMemberSorted(aircraft);
                ArmyRegistry.Register(storage);

                PlayerRoot root = rootObject.AddComponent<PlayerRoot>();
                root.ActionPoints = 2;
                root.AddResource(ResourceType.Energy, 2);
                var ctx = new AiTurnContext { Map = map, TurnNumber = 1 };
                var objective = new ReconObjective
                {
                    Kind = ReconObjectiveKind.Explore,
                    FocusHex = new HexCoord(3, 0),
                    TaskScore = new TaskScore(strategicRelevance: 10f),
                };

                AviationRebasePlan plan = AviationRebasePlanner.BuildPlan(
                    new WorldSnapshot(), owner, root, ctx, new[] { objective });

                Assert.That(plan, Is.Not.Null);
                Assert.That(plan.SourceHex, Is.EqualTo(sourceHex));
                Assert.That(plan.DestinationHex, Is.EqualTo(forwardHex));
                Assert.That(plan.Route.TotalCost, Is.EqualTo(2));
                Assert.That(plan.Route.RequiredTurns, Is.EqualTo(1));

                root.ActionPoints = 0;
                Assert.That(AviationRebasePlanner.BuildPlan(
                    new WorldSnapshot(), owner, root, ctx, new[] { objective }), Is.Null);

                root.ActionPoints = 2;
                root.AddResource(ResourceType.Energy, -2);
                Assert.That(AviationRebasePlanner.BuildPlan(
                    new WorldSnapshot(), owner, root, ctx, new[] { objective }), Is.Null);

                root.AddResource(ResourceType.Energy, 2);
                objective.FocusHex = new HexCoord(1, 0); // equal two-hex round trips
                Assert.That(AviationRebasePlanner.BuildPlan(
                    new WorldSnapshot(), owner, root, ctx, new[] { objective }), Is.Null);
            }
            finally
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AiMapMemory.Clear();
                AirSortieRegistry.Clear();
                AiResourceReservation.Clear();
                Object.DestroyImmediate(mapObject);
                Object.DestroyImmediate(rootObject);
            }
        }

        [Test]
        public void AviationLandingCapacity_CountsStoredLandedAndIncomingAircraft()
        {
            var owner = new PlayerSetupData();
            HexCoord airfieldHex = new HexCoord(2, 0);
            try
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AirSortieRegistry.Clear();
                BuildingRegistry.Register(airfieldHex, new BuildingData
                {
                    Owner = owner, Hex = airfieldHex, IsBase = true, AirfieldCapacity = 4,
                });
                var stored = new ArmyData
                    { Owner = owner, Hex = airfieldHex, IsAirfield = true };
                stored.AddMemberSorted(Aircraft(owner));
                ArmyRegistry.Register(stored);
                var landed = new ArmyData
                    { Owner = owner, Hex = airfieldHex, IsAirArmy = true };
                landed.AddMemberSorted(Aircraft(owner));
                ArmyRegistry.Register(landed);
                var incoming = new ArmyData
                    { Owner = owner, Hex = new HexCoord(1, 0), IsAirArmy = true };
                incoming.AddMemberSorted(Aircraft(owner));
                ArmyRegistry.Register(incoming);
                AirSortieRegistry.Add(owner, new AirSortie
                {
                    Army = incoming, Kind = AirSortieKind.Recon,
                    TargetHex = airfieldHex, LandingHex = airfieldHex, Outbound = false,
                });

                Assert.That(AiAirSortiePlanner.FreeLandingCapacity(airfieldHex, owner), Is.EqualTo(1));
            }
            finally
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AirSortieRegistry.Clear();
            }
        }

        [Test]
        public void AviationRecovery_PrefersForwardBaseAndReplansAfterOwnershipLoss()
        {
            var owner = new PlayerSetupData();
            var enemy = new PlayerSetupData();
            GameObject mapObject = new GameObject("secondary-base-recovery-map");
            try
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AiMapMemory.Clear();
                AirSortieRegistry.Clear();
                HexMap map = mapObject.AddComponent<HexMap>();
                var terrain = new TerrainTypeEntry { moveCost = 1 };
                map.SetData(3, 1f, new Dictionary<HexCoord, TerrainTypeEntry>
                {
                    [new HexCoord(0, 0)] = terrain,
                    [new HexCoord(1, 0)] = terrain,
                    [new HexCoord(2, 0)] = terrain,
                    [new HexCoord(3, 0)] = terrain,
                });
                HexCoord citadel = new HexCoord(0, 0);
                HexCoord forward = new HexCoord(2, 0);
                BuildingRegistry.Register(citadel, new BuildingData
                {
                    Owner = owner, Hex = citadel, IsBase = true,
                    IsStartingCitadel = true, AirfieldCapacity = 2,
                });
                var forwardBuilding = new BuildingData
                {
                    Owner = owner, Hex = forward, IsBase = true, AirfieldCapacity = 2,
                };
                BuildingRegistry.Register(forward, forwardBuilding);
                var wing = new ArmyData
                    { Owner = owner, Hex = new HexCoord(3, 0), IsAirArmy = true };
                wing.AddMemberSorted(Aircraft(owner, move: 4));
                ArmyRegistry.Register(wing);

                Assert.That(AiAirSortiePlanner.TryReplan(wing, map, owner), Is.EqualTo(forward));

                forwardBuilding.Owner = enemy;
                Assert.That(AiAirSortiePlanner.TryReplan(wing, map, owner), Is.EqualTo(citadel));
            }
            finally
            {
                BuildingRegistry.Clear();
                ArmyRegistry.Clear();
                AiMapMemory.Clear();
                AirSortieRegistry.Clear();
                Object.DestroyImmediate(mapObject);
            }
        }

        [Test]
        public void BaseOwnershipTransfer_UpdatesBaseAndAirfieldViewsForBothOwners()
        {
            var oldOwner = new PlayerSetupData();
            var newOwner = new PlayerSetupData();
            HexCoord hex = new HexCoord(2, 0);
            var building = new BuildingData
            {
                Owner = oldOwner, Hex = hex, IsBase = true, AirfieldCapacity = 2,
            };
            try
            {
                BuildingRegistry.Clear();
                BuildingRegistry.Register(hex, building);
                Assert.That(WorldAnalysis.OwnedBaseHexes(new[] { building }, oldOwner, null),
                    Does.Contain(hex));
                Assert.That(AiAirSortiePlanner.OwnedAirfieldHexes(oldOwner), Does.Contain(hex));

                building.Owner = newOwner;
                Assert.That(WorldAnalysis.OwnedBaseHexes(new[] { building }, oldOwner, null), Is.Empty);
                Assert.That(AiAirSortiePlanner.OwnedAirfieldHexes(oldOwner), Has.No.Member(hex));
                Assert.That(WorldAnalysis.OwnedBaseHexes(new[] { building }, newOwner, null),
                    Does.Contain(hex));
                Assert.That(AiAirSortiePlanner.OwnedAirfieldHexes(newOwner), Does.Contain(hex));
            }
            finally
            {
                BuildingRegistry.Clear();
            }
        }

        [Test]
        public void Housekeeping_PackagesEligibleLocalSingletonButProtectsCommittedActor()
        {
            var owner = new PlayerSetupData { CitadelHexQ = 0, CitadelHexR = 0 };
            HexCoord baseHex = new HexCoord(2, 0);
            try
            {
                ArmyRegistry.Clear();
                var garrison = new ArmyData
                    { Owner = owner, Hex = baseHex, IsGarrison = true };
                var field = new ArmyData { Owner = owner, Hex = baseHex };
                field.AddMemberSorted(new UnitData
                {
                    Owner = owner, Attack = 2, Defense = 2,
                    HitPointsCurrent = 2, HitPointsMax = 2, MoveMax = 3, MoveCurrent = 3,
                });
                ArmyRegistry.Register(garrison);
                ArmyRegistry.Register(field);
                var ctx = new AiTurnContext();

                ArmyReorgAnalysis free = ArmyReorgAnalyzer.Analyze(
                    owner, new ActorCommitments(), new WorldSnapshot(), ctx);
                ReorganizationPlan freePlan = ArmyReorganizationPlanner.Plan(free.Groups[0]);
                Assert.That(freePlan.Transfers.Any(t =>
                    t.FromArmyId == field.Id && t.ToArmyId == garrison.Id), Is.True);

                var committed = new ActorCommitments();
                committed.Claim(field.Id);
                ArmyReorgAnalysis protectedAnalysis = ArmyReorgAnalyzer.Analyze(
                    owner, committed, new WorldSnapshot(), ctx);
                ReorganizationPlan protectedPlan = ArmyReorganizationPlanner.Plan(
                    protectedAnalysis.Groups[0]);
                Assert.That(protectedPlan.Transfers.Any(t =>
                    t.FromArmyId == field.Id), Is.False);
            }
            finally
            {
                ArmyRegistry.Clear();
            }
        }

        private static UnitData Aircraft(PlayerSetupData owner, int move = 3) => new UnitData
        {
            Owner = owner, IsAviation = true, MoveMax = move, MoveCurrent = move,
            ActivationApCost = 1, LaunchEnergyCost = 1,
        };

        private static EnemyContactSnapshot Contact(int id, float power) =>
            new EnemyContactSnapshot
            {
                Army = new ArmySnapshot
                {
                    ArmyId = id, EffectiveArmyPower = power,
                    Members = System.Array.Empty<WorthIt.DefenderProfile>(),
                },
                Source = ContactSource.Honest,
                Position = new HexCoord(4, 4),
            };

        private static AssetThreatSnapshot Threat(EnemyContactSnapshot contact, AssetKind kind,
            HexCoord hex, float severity) => new AssetThreatSnapshot
        {
            Contact = contact,
            Asset = new StrategicAssetSnapshot { Kind = kind, Hex = hex, Value = 10f },
            Severity = severity,
        };

        private static WorldSnapshot DefenceSnapshot(PlayerSetupData owner,
            EnemyContactSnapshot contact, IReadOnlyList<ArmySnapshot> armies) =>
            new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Armies = armies,
                    BaseHexes = new[] { new HexCoord(0, 0), new HexCoord(-2, 3) },
                },
                Threat = new ThreatModel
                {
                    Contacts = new[] { contact },
                    Threats = System.Array.Empty<AssetThreatSnapshot>(),
                },
            };

        private static ActiveDefenceObjective DefenceObjective(int enemyId, HexCoord assetHex) =>
            new ActiveDefenceObjective
            {
                Target = new ActiveDefenceMissionTarget
                {
                    EnemyArmyId = enemyId,
                    LastKnownHex = new HexCoord(4, 4),
                    ProtectedAssetHex = assetHex,
                    ProtectedAssetKind = AssetKind.Base,
                },
                TaskScore = new TaskScore(strategicRelevance: 10f),
            };
    }
}
#endif
