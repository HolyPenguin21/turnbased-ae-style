#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Game.Ai;
using Game.Ai.V2;
using Game.Aviation;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public class AiAviationMissionRegressionTests
    {
        private PlayerSetupData owner;
        private readonly HexCoord target = new HexCoord(4, 2);

        [SetUp]
        public void SetUp()
        {
            owner = new PlayerSetupData();
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AirSortieRegistry.Clear();
            VisionSystem.Clear(); VisionSystem.Configure(null); AiMapMemory.Clear();
            ReconIntelSnapshotRegistry.Clear(); AiReconIntelMemory.Clear();
            MissionIntentRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); AirSortieRegistry.Clear();
            VisionSystem.Clear(); VisionSystem.Configure(null); AiMapMemory.Clear();
            ReconIntelSnapshotRegistry.Clear(); AiReconIntelMemory.Clear();
            MissionIntentRegistry.Clear();
        }

        private UnitData Plane(int ends = 1) => new UnitData
        {
            Owner = owner, IsAviation = true, ActivationApCost = 1, LaunchEnergyCost = 2,
            MoveMax = 6, MoveCurrent = 6, HitPointsMax = 4, HitPointsCurrent = 4,
            Attack = 3, TurnsWithoutRefuel = ends,
        };

        [Test]
        public void AdjacentAiAirOrders_DoNotSpendAStrikeOnTheApproachOrReturn()
        {
            UnitData plane = Plane();
            var wing = new ArmyData { Owner = owner, IsAirArmy = true };
            wing.AddMemberSorted(plane);
            // No scene/popup is needed: Transit must exit before target discovery or combat.
            var presenter = (AviationCombatPresenter)FormatterServices.GetUninitializedObject(typeof(AviationCombatPresenter));
            foreach (HexCoord step in new[] { new HexCoord(3, 0), new HexCoord(3, 1), new HexCoord(3, 0) })
            {
                var order = AiDecision.Move(wing, step, "flight", 0);
                var result = new AviationCombatPresenter.AirStrikeResult();
                IEnumerator resolution = presenter.ResolveAirStrikeAtCurrentHex(wing, step, order.AirStrikePolicy, result);
                Assert.That(resolution.MoveNext(), Is.False);
                Assert.That(result.Attacked, Is.False);
                Assert.That(plane.HasAirAttackedThisTurn, Is.False);
                Assert.That(plane.MoveCurrent, Is.EqualTo(6));
            }
            Assert.That(AviationRange.StrikeTurns(6, 6, 1, 4, 4), Is.EqualTo(2),
                "transit must leave the full planned strike series available");
        }

        [Test]
        public void AuthorizedSupportPolicy_KeepsItsExactArmyAndSurvivorFloor()
        {
            var order = AiDecision.Move(null, target, "target", 0);
            order.AirStrikePolicy = AirStrikePolicy.RaidSupport(7);
            Assert.That(order.AirStrikePolicy.AllowsStrike, Is.True);
            Assert.That(order.AirStrikePolicy.ExactTargetArmyId, Is.EqualTo(7));
            Assert.That(order.AirStrikePolicy.MinimumSurvivors, Is.EqualTo(1));
            Assert.That(AirStrikePolicy.Standard.AllowsStrike, Is.True,
                "ordinary gameplay endpoint strikes remain enabled");
        }

        private WorldSnapshot Observed(int revision, int? observedTurn, params AiMapMemory.KnownAirSighting[] sightings)
        {
            var snap = new WorldSnapshot { Observer = owner, TurnNumber = 27, KnowledgeVersion = revision,
                Known = new KnownSnapshot { AirSightings = sightings }, Self = new SelfSnapshot() };
            var stamps = new Dictionary<HexCoord, int>();
            if (observedTurn.HasValue) stamps[target] = observedTurn.Value;
            ReconIntelSnapshotRegistry.Capture(owner, 27, revision, stamps);
            return snap;
        }

        [TestCase(null, false)]
        [TestCase(26, false)]
        [TestCase(27, true)]
        public void MissingDefenders_AreEmptyOnlyWithFreshObservation(int? observedTurn, bool empty)
        {
            Assert.That(GroundCombatAirSupport.TargetKnownEmpty(Observed(1, observedTurn), target,
                AirStrikePolicy.Standard), Is.EqualTo(empty));
        }

        [Test]
        public void FreshDefenders_PreventEmptyCancellation_AndExactThreatDoesNotTargetItsNeighbour()
        {
            var foe = new PlayerSetupData();
            var roster = new AviationCombatEstimator.DefendingAirArmy(7,
                new[] { new WorthIt.DefenderProfile(2, false, null, hitPoints: 4) }, new[] { 0 }, -1);
            var snap = Observed(1, 27, new AiMapMemory.KnownAirSighting(target, foe, 27, true, roster));
            Assert.That(GroundCombatAirSupport.TargetKnownEmpty(snap, target, AirStrikePolicy.Standard), Is.False);
            Assert.That(GroundCombatAirSupport.TargetKnownEmpty(snap, target, AirStrikePolicy.DefenceSupport(7)), Is.False);
            Assert.That(GroundCombatAirSupport.TargetKnownEmpty(snap, target, AirStrikePolicy.DefenceSupport(8)), Is.True);
            Assert.That(GroundCombatAirSupport.TargetKnownEmpty(snap, target, AirStrikePolicy.RaidSupport(7)), Is.True,
                "the last neutral defender is protected by the Raid survivor floor");
        }

        [Test]
        public void EmptyTargetKnowledge_IsFrozenPerRevisionAndIsolatedPerObserver()
        {
            WorldSnapshot unknown = Observed(1, null);
            WorldSnapshot empty = Observed(2, 27);
            Assert.That(GroundCombatAirSupport.TargetKnownEmpty(empty, target, AirStrikePolicy.Standard), Is.True);
            Assert.That(GroundCombatAirSupport.TargetKnownEmpty(unknown, target, AirStrikePolicy.Standard), Is.False);
            var other = new WorldSnapshot { Observer = new PlayerSetupData(), TurnNumber = 27, KnowledgeVersion = 2,
                Known = new KnownSnapshot { AirSightings = Array.Empty<AiMapMemory.KnownAirSighting>() } };
            Assert.That(GroundCombatAirSupport.TargetKnownEmpty(other, target, AirStrikePolicy.Standard), Is.False);
        }

        [TestCase(0, 1)]
        [TestCase(1, 2)]
        [TestCase(2, 3)]
        public void StrikeSeries_IncludesAFreeStationaryStrikeBeforeTheLastReturn(int ends, int strikes)
        {
            Assert.That(AviationRange.StrikeTurns(6, 6, ends, 2, 2), Is.EqualTo(strikes));
            Assert.That(AviationRange.StrikeTurns(6, 6, ends, 2, 2), Is.EqualTo(strikes),
                "unchanged flight conditions yield the same calendar");
        }

        [Test]
        public void MixedWing_CannotBorrowAnotherAircraftsEndurance()
        {
            var group = new[] { Plane(2), Plane(0) };
            Assert.That(AviationRange.StrikeTurns(6, 6, AviationRange.SafeUnlandedEndsRemaining(group), 2, 2), Is.EqualTo(1));
        }

        private WorldSnapshot Stored(int energy = 2) => new WorldSnapshot
        {
            Observer = owner, TurnNumber = 27, MapKnowledge = new MapKnowledgeSnapshot(),
            Self = new SelfSnapshot { ActionPoints = 3, Stockpile = new ResourceBundle { Energy = energy },
                Armies = new[] { new ArmySnapshot { ArmyId = 61, Owner = owner, IsAirfield = true,
                    StoredAircraft = new[] { new StoredAircraftLaunchCost(9, 1, 2, 6) } } } },
        };

        private ScoutMissionTarget Sweep() => new ScoutMissionTarget { Kind = ScoutTargetKind.AirSweep, FocusHex = target };

        [Test]
        public void FreshSweep_PublishesExactStoredLaunchEnergyAsAMandatoryMinimum()
        {
            ScoutCostEstimate cost = ScoutCostModel.Estimate(Stored(), Sweep());
            Assert.That(cost.MoverKnown, Is.True);
            Assert.That(cost.PreferredMoverArmyId, Is.EqualTo(61));
            Assert.That(cost.ApMinimum, Is.EqualTo(1));
            Assert.That(cost.ApMaximum, Is.EqualTo(1));
            Assert.That(cost.EnergyMinimum, Is.EqualTo(2));
            Assert.That(cost.EnergyMaximum, Is.EqualTo(2));
            Assert.That(ArmyRegistry.AllForOwner(owner), Is.Empty, "pricing cannot form a wing");
            Assert.That(AirSortieRegistry.For(owner), Is.Empty, "pricing cannot reserve landing slots");
        }

        [Test]
        public void FreshSweep_FundingCannotAdmitAZeroEnergyEnvelope()
        {
            WorldSnapshot snap = Stored(0);
            var objective = new ReconObjective { Kind = ReconObjectiveKind.AirSweep, FocusHex = target,
                TaskScore = new TaskScore(infoGain: 30), BaseValue = 30 };
            MissionProposal proposal = ReconMissionPlanner.Propose(snap, new DesireBreakdown(),
                Array.Empty<MissionIntent>(), new[] { objective }).Single();
            TentativeAllocation allocation = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { proposal }, new List<Commitment>(), owner).Pack();
            Assert.That(allocation.Funded, Is.Empty);
            Assert.That(proposal.Requirements.EnergyMinimum, Is.EqualTo(2));
        }

        [Test]
        public void FreshSweep_FirstAllocationFundsTheFullLaunchWithoutRepricing()
        {
            WorldSnapshot snap = Stored();
            var objective = new ReconObjective { Kind = ReconObjectiveKind.AirSweep, FocusHex = target,
                TaskScore = new TaskScore(infoGain: 30), BaseValue = 30 };
            MissionProposal proposal = ReconMissionPlanner.Propose(snap, new DesireBreakdown(),
                Array.Empty<MissionIntent>(), new[] { objective }).Single();
            FundedEntry funded = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { proposal }, new List<Commitment>(), owner).Pack().Funded.Single();
            Assert.That(funded.Tentative.Ap, Is.GreaterThanOrEqualTo(1));
            Assert.That(funded.PhysicalDraw.Energy, Is.EqualTo(2));
        }

        [Test]
        public void StoredSweepClaim_CannotBeSpentAgainByAHardMissionOnRepack()
        {
            WorldSnapshot snap = Stored();
            var scout = new MissionProposal { Kind = MissionKind.Scout, Target = Sweep(), BaseValue = 30,
                LocalAdmissionScore = 30, Requirements = new MissionRequirements {
                    ApMinimum = 1, ApDesired = 1, ApMaximum = 1,
                    EnergyMinimum = 2, EnergyDesired = 2, EnergyMaximum = 2 } };
            scout.Axes.Value[DesireAxis.Recon] = 1;
            var raid = new MissionProposal { Kind = MissionKind.Raid, BaseValue = 10, LocalAdmissionScore = 10,
                Target = new RaidMissionTarget { Target = RaidTargetRef.ForNeutralArmy(8) },
                Requirements = new MissionRequirements { ApMinimum = 1, ApDesired = 1, ApMaximum = 1,
                    EnergyMinimum = 1, EnergyDesired = 1, EnergyMaximum = 1 } };
            raid.Axes.Value[DesireAxis.Aggression] = 1;
            AllocationSession session = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal>(), new List<Commitment> {
                    new Commitment { Mission = scout, Tier = CommitmentTier.Hard },
                    new Commitment { Mission = raid, Tier = CommitmentTier.Hard } }, owner);
            // This is the exact physical claim a storage preparation passes to the generic bank.
            FundedEntry funded = session.Pack().Funded.Single(f => f.Mission == scout);
            session.RegisterProvisionSuccess(funded, 1, new ResourceVector(0, 0, 2, 0, 0));
            Assert.That(session.Pack().Funded.Any(f => f.Mission == raid), Is.False,
                "a hard commitment cannot borrow the launch Energy already physically claimed");
        }

        [Test]
        public void PaidIncumbent_RemainsFreeAcrossTurns_AndTaskKeySurvivesMaterialization()
        {
            WorldSnapshot snap = Stored();
            var wing = new ArmySnapshot { ArmyId = 49, Owner = owner, IsAir = true, MemberCount = 1,
                CurrentMovement = 6, ActivationApCost = 1, ActivationEnergyCost = 2,
                PendingActivationApCost = 0, PendingActivationEnergyCost = 0 };
            snap.Self.Armies = new[] { wing };
            var m = new MissionProposal { Kind = MissionKind.Scout, Target = Sweep(), PreferredMoverArmyId = 61 };
            var key = StableMissionKey.For(m);
            m.PreferredMoverArmyId = 49;
            Assert.That(StableMissionKey.For(m), Is.EqualTo(key));
            foreach (int turn in new[] { 27, 28 })
            {
                snap.TurnNumber = turn;
                ScoutCostEstimate cost = ScoutCostModel.Estimate(snap, Sweep(), 49);
                Assert.That(cost.ApMinimum, Is.Zero);
                Assert.That(cost.EnergyMinimum, Is.Zero);
            }
        }

        [Test]
        public void MissionBoundRouteWitness_PricesTheAircraftThatCanActuallyServeTheTask()
        {
            WorldSnapshot snap = Stored();
            ArmySnapshot source = snap.Self.Armies.Single();
            var proven = new ScoutExecutionCandidate(source, target, 2, 1, 0, 0, 0, false, 2,
                ScoutExecutorKind.AirStored, requiredEnergy: 5, aircraftRuntimeIds: new[] { 10 });
            ScoutCostEstimate cost = ScoutCostModel.Estimate(snap, Sweep(), plannedAir: proven);
            Assert.That(cost.ApMinimum, Is.EqualTo(2));
            Assert.That(cost.EnergyMinimum, Is.EqualTo(5),
                "the cheaper stored aircraft is not the route witness for this task");
        }

        [Test]
        public void ACombatOrRecoveryWing_CannotUnderpriceAFreshStoredReconLaunch()
        {
            WorldSnapshot snap = Stored();
            snap.Self.Armies = snap.Self.Armies.Concat(new[] { new ArmySnapshot {
                ArmyId = 20, Owner = owner, IsAir = true, MemberCount = 1, CurrentMovement = 6,
                PendingActivationApCost = 0, PendingActivationEnergyCost = 0, CanServeReconAir = false } }).ToArray();
            ScoutCostEstimate cost = ScoutCostModel.Estimate(snap, Sweep());
            Assert.That(cost.PreferredMoverArmyId, Is.EqualTo(61));
            Assert.That(cost.EnergyMinimum, Is.EqualTo(2),
                "a paid but unavailable wing is not a free alternative for this task");
        }

        [Test]
        public void StoredCost_DoesNotLeakBetweenSnapshotsOrAfterRosterChanges()
        {
            WorldSnapshot before = Stored();
            WorldSnapshot after = Stored();
            ((ArmySnapshot[])after.Self.Armies)[0].StoredAircraft = new[] { new StoredAircraftLaunchCost(10, 3, 5, 6) };
            Assert.That(ScoutCostModel.Estimate(after, Sweep()).EnergyMinimum, Is.EqualTo(5));
            Assert.That(ScoutCostModel.Estimate(before, Sweep()).EnergyMinimum, Is.EqualTo(2));
        }

        [Test]
        public void SpentStoredAircraft_IsNotThePlanningWitness()
        {
            WorldSnapshot snap = Stored();
            ((ArmySnapshot[])snap.Self.Armies)[0].StoredAircraft = new[] {
                new StoredAircraftLaunchCost(9, 1, 1, 0), new StoredAircraftLaunchCost(10, 2, 3, 4) };
            ScoutCostEstimate cost = ScoutCostModel.Estimate(snap, Sweep());
            Assert.That(cost.ApMinimum, Is.EqualTo(2));
            Assert.That(cost.EnergyMinimum, Is.EqualTo(3));
        }

        [Test]
        public void AviationOutsideTheMissionBeam_LeavesStoredAircraftAndLandingReservationsUntouched()
        {
            WorldSnapshot snap = Stored();
            var jobs = Enumerable.Range(0, AiConfigV2.scoutCandidateBeamWidth).Select(i => new ReconObjective {
                Kind = ReconObjectiveKind.Refresh, FocusHex = new HexCoord(i, 0),
                TaskScore = new TaskScore(infoGain: 100), BaseValue = 100 }).ToList();
            jobs.Add(new ReconObjective { Kind = ReconObjectiveKind.AirSweep, FocusHex = target,
                TaskScore = new TaskScore(infoGain: 1), BaseValue = 1 });
            var proposals = ReconMissionPlanner.Propose(snap, new DesireBreakdown(), Array.Empty<MissionIntent>(), jobs);
            Assert.That(proposals.Any(p => ((ScoutMissionTarget)p.Target).Kind == ScoutTargetKind.AirSweep), Is.False);
            Assert.That(((ArmySnapshot[])snap.Self.Armies)[0].StoredAircraft.Count, Is.EqualTo(1));
            Assert.That(AirSortieRegistry.For(owner), Is.Empty);
        }

        [Test]
        public void FullLanding_IsACompletionFact_AndBoardingClearsItForTheNextSortie()
        {
            BuildingRegistry.Register(default, new BuildingData { Owner = owner, Hex = default,
                IsBase = true, AirfieldCapacity = 2 });
            var airfield = new ArmyData { Owner = owner, Hex = default, IsAirfield = true };
            ArmyRegistry.Register(airfield);
            UnitData plane = Plane();
            var wing = new ArmyData { Owner = owner, Hex = default, IsAirArmy = true };
            wing.AddMemberSorted(plane); ArmyRegistry.Register(wing);
            Assert.That(AviationActions.LandInSlotOrder(wing, null), Is.EqualTo(1));
            Assert.That(GroundCombatAirSupport.WingLanded(owner, wing.Id), Is.True);
            Assert.That(AviationRules.IsValidAirArmy(wing), Is.False);
            Assert.That(ArmyActions.TransferMember(plane, airfield, wing, null, out _), Is.True);
            Assert.That(GroundCombatAirSupport.WingLanded(owner, wing.Id), Is.False);
        }

        [Test]
        public void PartialLanding_StillHasAnAirborneObligation()
        {
            BuildingRegistry.Register(default, new BuildingData { Owner = owner, Hex = default,
                IsBase = true, AirfieldCapacity = 1 });
            var airfield = new ArmyData { Owner = owner, Hex = default, IsAirfield = true };
            ArmyRegistry.Register(airfield);
            var wing = new ArmyData { Owner = owner, Hex = default, IsAirArmy = true };
            wing.AddMemberSorted(Plane()); wing.AddMemberSorted(Plane()); ArmyRegistry.Register(wing);
            Assert.That(AviationActions.LandInSlotOrder(wing, null), Is.EqualTo(1));
            Assert.That(AviationRules.IsValidAirArmy(wing), Is.True);
            Assert.That(GroundCombatAirSupport.WingLanded(owner, wing.Id), Is.False);
        }

        [Test]
        public void Casualty_DoesNotPublishFullLandingCompletion()
        {
            var wing = new ArmyData { Owner = owner, IsAirArmy = true };
            wing.AddMemberSorted(Plane()); ArmyRegistry.Register(wing);
            wing.Members.Clear();
            AviationRules.SyncAirArmyShell(wing, null);
            Assert.That(GroundCombatAirSupport.WingLanded(owner, wing.Id), Is.False);
        }
    }
}
#endif
