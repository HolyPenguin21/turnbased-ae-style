#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiTaskScoreAllocatorRegressionTests
    {
        [Test]
        public void Repack_HardCommitmentCannotSpendApAlreadyLockedByProvisionedMission()
        {
            // Both intents exist BEFORE the first pass. Initially the 2-AP Scout fits and
            // the 3-AP Raid defers; locking the Scout must not make the Raid affordable.
            var player = new PlayerSetupData { Nickname = "LockedApAudit" };
            var snap = new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot { ActionPoints = 3 },
            };
            var scout = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget
                {
                    Kind = ScoutTargetKind.Explore,
                    FocusHex = new HexCoord(1, 0),
                },
                BaseValue = 9f,
                LocalAdmissionScore = 9f,
                Requirements = new MissionRequirements
                {
                    ApMinimum = 2f, ApDesired = 2f, ApMaximum = 2f,
                },
            };
            scout.Axes.Value[DesireAxis.Recon] = 1f;

            var raid = new MissionProposal
            {
                Kind = MissionKind.Raid,
                Target = new RaidMissionTarget
                {
                    Target = RaidTargetRef.ForNeutralArmy(42),
                    Phase = RaidMissionPhase.Assault,
                },
                BaseValue = 15f,
                LocalAdmissionScore = 15f,
                Requirements = new MissionRequirements
                {
                    ApMinimum = 3f, ApDesired = 3f, ApMaximum = 3f,
                },
            };
            raid.Axes.Value[DesireAxis.Aggression] = 1f;

            var commitments = new List<Commitment>
            {
                new Commitment { Mission = scout, Tier = CommitmentTier.Hard },
                new Commitment { Mission = raid, Tier = CommitmentTier.Hard },
            };
            AllocationSession session = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal>(), commitments, player);
            TentativeAllocation first = session.Pack();
            FundedEntry scoutFund = first.Funded.Single(f => f.Mission == scout);
            Assert.That(first.Deferred.Any(d => d.Mission == raid
                && d.Reason == DeferReason.CommitmentPoolExhausted), Is.True);
            session.RegisterProvisionSuccess(scoutFund, claimedAp: 2f);

            TentativeAllocation repacked = session.Pack();
            Assert.That(repacked.LockedClaim.Ap, Is.EqualTo(2f));
            Assert.That(repacked.Funded.Any(f => f.Mission == raid), Is.False,
                "a hard commitment cannot conjure AP already claimed by a locked mission");
            Assert.That(repacked.Deferred.Any(d => d.Mission == raid
                && d.Reason == DeferReason.CommitmentPoolExhausted), Is.True);
            Assert.That(repacked.GlobalOverdraft.Ap, Is.Zero);
        }

        [Test]
        public void Pack_CrossLaneMissionsWithSamePlannedActor_CannotBothBeFunded()
        {
            var player = new PlayerSetupData { Nickname = "CrossLaneActorAudit" };
            var snap = new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot { ActionPoints = 5 },
            };

            var scout = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget
                {
                    Kind = ScoutTargetKind.Explore,
                    FocusHex = new HexCoord(2, 0),
                },
                BaseValue = 20f,
                LocalAdmissionScore = 20f,
                PreferredMoverArmyId = 7,
                Requirements = new MissionRequirements
                {
                    ApMinimum = 1f, ApDesired = 1f, ApMaximum = 1f,
                },
            };
            scout.Axes.Value[DesireAxis.Recon] = 1f;

            var economy = new MissionProposal
            {
                Kind = MissionKind.Economy,
                Target = new EconomyMissionTarget
                {
                    Kind = EconomyTaskKind.FoundBase,
                    TargetHex = new HexCoord(4, 0),
                    BuilderArmyId = 7,
                },
                BaseValue = 10f,
                LocalAdmissionScore = 10f,
                PreferredMoverArmyId = 7,
                Requirements = new MissionRequirements
                {
                    ApMinimum = 1f, ApDesired = 1f, ApMaximum = 1f,
                },
            };
            economy.Axes.Value[DesireAxis.Economy] = 1f;

            AllocationSession session = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { scout, economy }, new List<Commitment>(), player);
            TentativeAllocation allocation = session.Pack();

            Assert.That(allocation.Funded.Count(f => f.Mission == scout || f.Mission == economy), Is.EqualTo(1));
            Assert.That(allocation.Funded.Any(f => f.Mission == scout), Is.True,
                "the higher-value mission should keep the shared actor");
            Assert.That(allocation.Deferred.Any(d => d.Mission == economy
                && d.Reason == DeferReason.MissionConflict), Is.True,
                "cross-lane actor exclusivity must be enforced before financing");
        }

        [Test]
        public void RaidCostEstimate_AlreadyActivatedMover_HasZeroCurrentTurnApButKeepsRecurringAp()
        {
            var player = new PlayerSetupData { Nickname = "RaidRecurringApAudit" };
            var mover = new ArmySnapshot
            {
                ArmyId = 17,
                Owner = player,
                Hex = new HexCoord(0, 0),
                MemberCount = 1,
                MaxMovement = 4,
                CurrentMovement = 4,
                ActivationApCost = 3,
                HasActivatedThisTurn = true,
                IsPrison = false,
                IsAir = false,
                IsGarrison = false,
                IsSoloRecce = false,
            };
            var snap = new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot
                {
                    ActionPoints = 5,
                    Armies = new List<ArmySnapshot> { mover },
                },
            };
            var target = new RaidMissionTarget
            {
                Phase = RaidMissionPhase.Assault,
                Target = RaidTargetRef.ForNeutralArmy(99),
                LastKnownHex = new HexCoord(8, 0),
                EstimatedEta = 2,
            };

            RaidCostEstimate estimate = RaidCostModel.Estimate(snap, target, selectedMoverArmyId: 17);

            Assert.That(estimate.PlannedMoverArmyId, Is.EqualTo(17));
            Assert.That(estimate.Requirements.ApMinimum, Is.Zero,
                "an already activated mover does not need another activation this turn");
            Assert.That(estimate.Requirements.ApDesired, Is.Zero,
                "allocator envelope must contain current-turn AP only");
            Assert.That(estimate.RecurringActivationAp, Is.EqualTo(3f),
                "future route turns must still price the mover's normal activation AP");
            Assert.That(estimate.Requirements.EtaTurns, Is.EqualTo(2));
        }
    }
}
#endif
