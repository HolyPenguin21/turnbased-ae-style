#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai.V2;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Combat;
using Game.Ai;
using NUnit.Framework;

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
            Assert.That(blocked, Does.Not.Contain(ownHex));
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
