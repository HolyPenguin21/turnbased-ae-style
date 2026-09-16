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
            // Frozen turn-start AP is 3. An earlier successful scout provision claims 2;
            // a new Hard raid asking for 3 must not pass the global AP ceiling on re-pack.
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

            var commitments = new List<Commitment>();
            AllocationSession session = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal> { scout }, commitments, player);
            TentativeAllocation first = session.Pack();
            FundedEntry scoutFund = first.Funded.Single(f => f.Mission == scout);
            session.RegisterProvisionSuccess(scoutFund, claimedAp: 2f);
            commitments.Add(new Commitment
            {
                Mission = raid,
                Tier = CommitmentTier.Hard,
            });

            TentativeAllocation repacked = session.Pack();
            Assert.That(repacked.LockedClaim.Ap, Is.EqualTo(2f));
            Assert.That(repacked.Funded.Any(f => f.Mission == raid), Is.False,
                "a hard commitment cannot conjure AP already claimed by a locked mission");
            Assert.That(repacked.Deferred.Any(d => d.Mission == raid
                && d.Reason == DeferReason.CommitmentPoolExhausted), Is.True);
            Assert.That(repacked.GlobalOverdraft.Ap, Is.Zero);
        }
    }
}
#endif
