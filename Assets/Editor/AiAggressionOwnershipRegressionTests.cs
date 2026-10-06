#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiAggressionOwnershipRegressionTests
    {
        private static readonly HexCoord Home = new HexCoord(0, 0);
        private static readonly HexCoord Away = new HexCoord(3, 0);
        private PlayerSetupData _us, _enemy;

        [SetUp]
        public void SetUp()
        {
            _us = new PlayerSetupData { Nickname = "Ownership", ColorIndex = 1,
                CitadelHexQ = Home.Q, CitadelHexR = Home.R };
            _enemy = new PlayerSetupData { Nickname = "Enemy", ColorIndex = 2 };
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            MissionIntentRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            MissionIntentRegistry.Clear();
        }

        private ArmySnapshot Actor(int id, HexCoord hex, float power = 81f, int movement = 3) =>
            new ArmySnapshot
            {
                ArmyId = id, Owner = _us, Hex = hex, EffectiveArmyPower = power,
                IsStructuralRaidActor = true, CurrentMovement = movement, MaxMovement = 3,
                Members = Array.Empty<WorthIt.DefenderProfile>(), MemberCount = 1, Capacity = 4,
                ReachableOwnBaseHexes = new[] { Home },
            };

        private WorldSnapshot Snap(params ArmySnapshot[] actors) => new WorldSnapshot
        {
            Observer = _us, TurnNumber = 10,
            Self = new SelfSnapshot { Armies = actors, BaseHexes = new[] { Home }, Citadel = Home,
                AttackPeak = 100f },
            Known = new KnownSnapshot { Buildings = Array.Empty<AiMapMemory.KnownBuilding>(),
                EnemySightings = Array.Empty<AiMapMemory.KnownEnemySighting>(),
                NeutralSightings = Array.Empty<AiMapMemory.KnownEnemySighting>() },
            Threat = new ThreatModel { Contacts = Array.Empty<EnemyContactSnapshot>(),
                Threats = Array.Empty<AssetThreatSnapshot>() },
        };

        private static IReadOnlyList<WorthIt.DefendingArmy> EmptySite() =>
            Array.Empty<WorthIt.DefendingArmy>();

        private static IReadOnlyList<WorthIt.DefendingArmy> DefendedSite() => new[]
        {
            new WorthIt.DefendingArmy(new[] { new WorthIt.DefenderProfile(2f, false, null, 2f, 6f, 2) }, default),
        };

        [TestCase(79f, false)]
        [TestCase(80f, false)]
        [TestCase(80.01f, true)]
        public void PreparationAndForcePolicyAgreeAtTheStrictBoundary(float power, bool ready)
        {
            ArmySnapshot host = Actor(7, Home, power);
            AttackPreparationAssessment assessment = AttackPreparationReadiness.Assess(host,
                100f, EmptySite(), 0f);
            Assert.That(assessment.Ready, Is.EqualTo(ready));
            Assert.That(assessment.PowerReady, Is.EqualTo(AttackForceReadiness.ForceReady(power, 100f)));
            Assert.That(assessment.RequiredPower, Is.EqualTo(80f));
        }

        [Test]
        public void PreparationRereadsDynamicPeakWithoutKeepingAnOldReadyResult()
        {
            ArmySnapshot host = Actor(7, Home);
            Assert.That(AttackPreparationReadiness.Assess(host, 100f, EmptySite(), 0f).Ready, Is.True);
            Assert.That(AttackPreparationReadiness.Assess(host, 110f, EmptySite(), 0f).Ready, Is.False);
            Assert.That(AttackPreparationReadiness.Assess(host, 90f, EmptySite(), 0f).Ready, Is.True);
        }

        // Coverage gates the march only while AiConfigV2.attackRequiresDefenderCoverage is on
        // (2026-10-04 test behavior: off).
        [TestCase(true)]
        [TestCase(false)]
        public void AbovePowerBarWithoutDefenderCoverage_IsReadyOnlyWhenCoverageIsWaived(bool required)
        {
            bool previous = AiConfigV2.attackRequiresDefenderCoverage;
            AiConfigV2.attackRequiresDefenderCoverage = required;
            try
            {
                AttackPreparationAssessment result = AttackPreparationReadiness.Assess(Actor(7, Home),
                    100f, DefendedSite(), 0f);
                Assert.That(result.PowerReady, Is.True);
                Assert.That(result.CoversAllDefenders, Is.False);
                Assert.That(result.Ready, Is.EqualTo(!required));
                Assert.That(result.Reason, Is.EqualTo(required ? "coverage_missing" : "ready"));
            }
            finally
            {
                AiConfigV2.attackRequiresDefenderCoverage = previous;
            }
        }

        [Test]
        public void MissingPreparationHostDefersRegardlessOfWhetherCommitmentsWereRebuilt()
        {
            WorldSnapshot snap = Snap();
            AttackTargetRef target = AttackTargetRef.For(Away, _enemy, AttackTargetKind.Base);
            var intent = new MissionIntent
            {
                Kind = MissionKind.Attack, Status = IntentStatus.Active,
                IntentKey = MissionIntentKey.ForAttack(target), PreferredMoverArmyId = 7,
                Objective = new AttackIntent { Target = target, Preparation = true,
                    Phase = AttackMissionPhase.Gather, PrimaryArmyId = 7 },
            };
            foreach (ActorCommitments commitments in new[] { new ActorCommitments(), null })
            {
                var diagnostics = new List<string>();
                var demands = new List<AxisDemand>();
                AggressionDemandEvaluator.AppendAttackDemands(snap, new[] { intent }, commitments,
                    new CapabilityInventory(), diagnostics, demands);
                Assert.That(demands, Is.Empty);
                Assert.That(diagnostics.Any(d => d.Contains("decision=DEFER")
                    && d.Contains("preparation_host_missing")), Is.True);
                Assert.That(diagnostics.Any(d => d.Contains("decision=SATISFIED")
                    && d.Contains("preparation_host_clears_power")), Is.False);
                Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Gather));
                Assert.That(intent.Attack.PrimaryArmyId, Is.EqualTo(7));
            }
        }

        [TestCase(81f, false, true, "preparation_host_clears_power")]
        [TestCase(70f, true, true, "existing_supports_en_route")]
        [TestCase(70f, false, false, "preparation_host_not_on_own_base")]
        public void PreparationDemandKeepsReadinessSupportTimingAndBoundedDeliveryDistinct(
            float power, bool supportEnRoute, bool onBase, string expectedReason)
        {
            var liveHost = new ArmyData { Owner = _us, Hex = onBase ? Home : Away };
            ArmyRegistry.Register(liveHost);
            int hostId = liveHost.Id;
            WorldSnapshot snap = Snap(Actor(hostId, liveHost.Hex, power));
            AttackTargetRef target = AttackTargetRef.For(new HexCoord(8, 0), _enemy, AttackTargetKind.Base);
            var intent = new MissionIntent
            {
                Kind = MissionKind.Attack, Status = IntentStatus.Active,
                IntentKey = MissionIntentKey.ForAttack(target), PreferredMoverArmyId = hostId,
                Objective = new AttackIntent { Target = target, Preparation = true,
                    Phase = AttackMissionPhase.Gather, PrimaryArmyId = hostId },
            };
            if (supportEnRoute) intent.Attack.GatherSupportArmyIds.Add(99);
            ActorCommitments commitments = ActorCommitments.FromIntents(new[] { intent }, snap, null);
            Assert.That(commitments.IsPreparationHost(hostId), Is.True);
            var diagnostics = new List<string>();
            var demands = new List<AxisDemand>();
            AggressionDemandEvaluator.AppendAttackDemands(snap, new[] { intent }, commitments,
                new CapabilityInventory(), diagnostics, demands);
            Assert.That(demands, Is.Empty);
            Assert.That(diagnostics.Any(d => d.Contains(expectedReason)), Is.True);
            Assert.That(intent.Attack.Phase, Is.EqualTo(AttackMissionPhase.Gather));
        }

        [Test]
        public void GarrisonConsumerIsIndependentOfAttackButKeepsItsOriginalApAuthority()
        {
            ArmySnapshot garrison = Actor(8, Home, 0f);
            garrison.IsGarrison = true;
            garrison.IsStructuralRaidActor = false;
            garrison.MemberCount = 0;
            WorldSnapshot snap = Snap(garrison);
            AggressionDemandEvaluation result = AggressionDemandEvaluator.Build(snap,
                Array.Empty<RaidObjective>(), Array.Empty<MissionIntent>(), new ActorCommitments(), _us);
            AxisDemand demand = result.Demands.Single(d => d.DeliveryShape == CapabilityDeliveryShape.Garrison);
            Assert.That(demand.ConsumerMissionKind, Is.Null);
            Assert.That(demand.ConsumerIntentKey, Is.Null);
            Assert.That(demand.ConsumerPurpose, Is.EqualTo(CapabilityConsumerPurpose.HeldBaseGarrison));
            Assert.That(demand.UsesAttackContinuationAp, Is.True);
            Assert.That(new AxisDemand { ConsumerMissionKind = MissionKind.Attack }.UsesAttackContinuationAp, Is.True);
            Assert.That(new AxisDemand { ConsumerMissionKind = MissionKind.Raid }.UsesAttackContinuationAp, Is.False);
        }

        [Test]
        public void TrackedAttackLookupAgreesWithEnumerationAndRejectsChangedOwner()
        {
            WorldSnapshot snap = Snap();
            snap.Known.Buildings = new[]
            {
                new AiMapMemory.KnownBuilding(Away, _enemy, false, null, isBase: true),
                new AiMapMemory.KnownBuilding(new HexCoord(7, 0), _enemy, true, null),
            };
            foreach (AttackObjective objective in AttackObjectiveEvaluator.Enumerate(snap))
            {
                AttackObjective tracked = AttackObjectiveEvaluator.ForTrackedTarget(snap, objective.Target);
                Assert.That(tracked, Is.Not.Null);
                Assert.That(tracked.TaskScore.Value, Is.EqualTo(objective.TaskScore.Value));
                Assert.That(tracked.TargetPower, Is.EqualTo(objective.TargetPower));
            }
            Assert.That(AttackObjectiveEvaluator.ForTrackedTarget(snap,
                AttackTargetRef.For(Away, _us, AttackTargetKind.Base)), Is.Null);
        }

        private ActiveDefenceObjective DefenceObjective() => new ActiveDefenceObjective
        {
            Target = new ActiveDefenceMissionTarget { EnemyArmyId = 99, LastKnownHex = Away,
                ProtectedAssetKind = AssetKind.Facility, ProtectedAssetHex = Home, EstimatedEta = 1 },
        };

        private void SetEnemy(WorldSnapshot snap, IReadOnlyCollection<WorthIt.DefenderProfile> members)
        {
            snap.Threat.Contacts = new[]
            {
                new EnemyContactSnapshot { Army = new ArmySnapshot { ArmyId = 99, Owner = _enemy,
                    Members = members.ToArray() }, Position = Away },
            };
        }

        [TestCase(3, ActiveDefenceResponseKind.Intercept)]
        [TestCase(0, ActiveDefenceResponseKind.Defer)]
        public void DefenceSeparatesDirectInterceptFromTemporarilyUnavailableCapability(
            int movement, ActiveDefenceResponseKind expected)
        {
            WorldSnapshot snap = Snap(Actor(7, Away, movement: movement));
            SetEnemy(snap, Array.Empty<WorthIt.DefenderProfile>());
            ActiveDefenceResponse response = ActiveDefenceObjectiveEvaluator.AssessResponse(snap,
                DefenceObjective(), null, null, null);
            Assert.That(response.Kind, Is.EqualTo(expected));
        }

        [TestCase(false, 100f, ActiveDefenceResponseKind.Regroup, "regroup_required")]
        [TestCase(true, 100f, ActiveDefenceResponseKind.Shortage, "regroup_exhausted")]
        [TestCase(false, 0f, ActiveDefenceResponseKind.Shortage, "insufficient_power")]
        public void DefenceFallbackKeepsDistributionAndRealShortageDistinct(bool allHome,
            float power, ActiveDefenceResponseKind expected, string reason)
        {
            WorldSnapshot snap = Snap(Actor(7, Home, power), Actor(8, allHome ? Home : Away, power));
            SetEnemy(snap, DefendedSite()[0].Units);
            ActiveDefenceResponse response = ActiveDefenceObjectiveEvaluator.AssessResponse(snap,
                DefenceObjective(), null, null, null);
            Assert.That(response.Kind, Is.EqualTo(expected));
            Assert.That(response.Reason, Is.EqualTo(reason));
            if (expected == ActiveDefenceResponseKind.Regroup)
                Assert.That(response.Movers.Select(a => a.ArmyId), Is.EquivalentTo(new[] { 8 }));
        }

        [Test]
        public void AssetThatAlreadyHoldsRequiresNeitherRetreatNorNewPower()
        {
            ArmySnapshot garrison = Actor(8, Home);
            garrison.IsStructuralRaidActor = false;
            garrison.IsGarrison = true;
            garrison.Members = new[] { new WorthIt.DefenderProfile(50f, false, null, 50f, 20f, 5) };
            WorldSnapshot snap = Snap(garrison);
            SetEnemy(snap, new[] { new WorthIt.DefenderProfile(2f, false, null, 0f, 6f, 2) });
            ActiveDefenceObjective objective = DefenceObjective();
            objective.Target.ProtectedAssetKind = AssetKind.Base;
            ActiveDefenceResponse result = ActiveDefenceObjectiveEvaluator.AssessResponse(snap,
                objective, null, null, null);
            Assert.That(result.Kind, Is.EqualTo(ActiveDefenceResponseKind.Defer));
            Assert.That(result.Reason, Does.StartWith("asset_holds"));
            Assert.That(result.Movers, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CommanderUpgradePreservesGarrisonHeroExclusion(bool garrisonHero)
        {
            var host = new ArmyData { Owner = _us, Hex = Home };
            host.Members.Add(new UnitData { Owner = _us, IsHero = true, CommandRating = 2 });
            var donor = new ArmyData { Owner = _us, Hex = Away, IsGarrison = true };
            var hero = new UnitData { Owner = _us, IsHero = true, CommandRating = 5 };
            if (garrisonHero) hero.TypeTags.Add(UnitTypeTag.Support);
            donor.Members.Add(hero);
            donor.Members.Add(new UnitData { Owner = _us, Attack = 50, Defense = 50,
                HitPointsMax = 20, HitPointsCurrent = 20, Initiative = 5 });
            ArmyRegistry.Register(host);
            ArmyRegistry.Register(donor);
            WorldSnapshot snap = Snap(Actor(host.Id, Home));
            snap.Self.AttackPeak = 60f;
            snap.Self.StrikePool = Enumerable.Range(0, 3).Select(i => new StrikeRosterCandidate(
                new StrikeRosterSlot("body" + i, false, 20f, ForceSource.Deck),
                new AiPower.PowerUnit(20f, null, 1, false))).ToArray();
            var result = AttackPreparationPolicy.FindCommanderUpgrade(snap, snap.Self.Armies[0],
                out _, out _, out bool assessable);
            Assert.That(assessable, Is.True);
            Assert.That(result.hero, garrisonHero ? Is.Null : Is.SameAs(hero));
            Assert.That(donor.Members.Contains(hero), Is.True, "policy may never perform the extraction");
        }

        private static void Drain(IEnumerator routine)
        {
            while (routine.MoveNext())
                if (routine.Current is IEnumerator nested) Drain(nested);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExtractedExecutorsKeepMissingMoverOutcomeInBothExecutionModes(bool batch)
        {
            var mission = new ProvisionedMission { Kind = MissionKind.Raid, MoverArmyId = 12345 };
            var raid = new ExecutionResult();
            Drain(batch ? RaidExecutor.RunRaid(_us, null, null, mission, raid, 0, null)
                : RaidExecutor.RunRaidStep(_us, null, null, mission, raid, 0, null));
            Assert.That(raid.StopReason, Is.EqualTo(ExecutionStopReason.MoverLost));
            Assert.That(raid.NeedsReplan, Is.True);
            Assert.That(raid.ApSpent, Is.Zero);
            mission.Kind = MissionKind.ActiveDefence;
            var defence = new ExecutionResult();
            Drain(batch ? ActiveDefenceExecutor.RunActiveDefence(_us, null, null, mission, defence, 0)
                : ActiveDefenceExecutor.RunActiveDefenceStep(_us, null, null, mission, defence, 0));
            Assert.That(defence.StopReason, Is.EqualTo(ExecutionStopReason.MoverLost));
            Assert.That(defence.NeedsReplan, Is.True);
            Assert.That(defence.ApSpent, Is.Zero);
        }
    }
}
#endif
