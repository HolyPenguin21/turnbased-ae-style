#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiReconTrimEligibilityTests
    {
        [TearDown]
        public void ClearMissionState() => MissionIntentRegistry.Clear();

        [Test]
        public void UnfundedLiveIncumbent_SurvivesFreshCandidateBeam_WithoutPromotion()
        {
            var player = new PlayerSetupData { Nickname = "Recon regression" };
            HexCoord incumbentHex = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, turn: 11, incumbentHex);
            MissionIntent incumbent = Incumbent(incumbentHex, preferredMover: 10);

            var fresh = new List<ReconObjective>();
            for (int i = 0; i < AiConfigV2.scoutCandidateBeamWidth + 2; i++)
                fresh.Add(new ReconObjective
                {
                    Kind = ReconObjectiveKind.Explore,
                    FocusHex = new HexCoord(20 + i, 0),
                    FreshNeighbors = 6,
                    DistanceFromBase = 1,
                    TaskScore = new TaskScore(infoGain: 30f),
                    BaseValue = 30f,
                });
            // A fresh copy of the incumbent's own target must not create a second proposal.
            fresh.Add(ReconObjectiveEvaluator.ExploreAt(snap, incumbentHex));

            List<MissionProposal> proposals = ReconMissionPlanner.Propose(snap,
                new DesireBreakdown { ReconExplorePressure = 1f },
                new[] { incumbent }, fresh);

            List<MissionProposal> retained = proposals.Where(p =>
                ((ScoutMissionTarget)p.Target).FocusHex.Equals(incumbentHex)).ToList();
            Assert.That(retained, Has.Count.EqualTo(1));
            Assert.That(retained[0].FromDurableIntent, Is.True);
            Assert.That(retained[0].DurableFundingTier, Is.EqualTo(CommitmentTier.None));
            Assert.That(retained[0].PreferredMoverArmyId, Is.EqualTo(10));
            Assert.That(proposals.Count, Is.LessThanOrEqualTo(AiConfigV2.scoutCandidateBeamWidth),
                "a retained ordinary incumbent still consumes one existing beam slot");
        }

        [Test]
        public void TrimmedScout_CannotReplaceSpentMoverOfAnotherIncumbent()
        {
            var player = new PlayerSetupData { Nickname = "Recon regression" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, turn: 11, focus);
            // The continuing incumbent owns #10 but #10 has exhausted its movement. #20's
            // separate surplus lane was retired by Continuity in this same turn (Mordak T12).
            ((List<ArmySnapshot>)snap.Self.Armies)[0].CurrentMovement = 0;
            MissionIntent incumbent = Incumbent(focus, preferredMover: 10);
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            state.Put(incumbent);
            state.MarkReconActorTrimmed(snap.TurnNumber, 20);

            MissionProposal mission = ReconMissionPlanner.Propose(snap,
                new DesireBreakdown { ReconExplorePressure = 1f },
                new[] { incumbent }, new List<ReconObjective>())[0];
            var open = new List<FundedEntry> { new FundedEntry { Mission = mission, Priority = 1 } };
            ReconAssignmentResult assignment = ReconAssignmentPlanner.AssignFunded(
                snap, null, player, open, new HashSet<int>());

            Assert.That(assignment.Assigned, Is.Empty,
                "a preferred actor on a DIFFERENT mission must never override the turn-wide trim of #20");
        }

        [Test]
        public void TrimmedScout_IsUnavailableInEligibilityNow_ButReturnsOnNextTurn()
        {
            var player = new PlayerSetupData { Nickname = "Recon regression" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, turn: 11, focus);
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            state.MarkReconActorTrimmed(11, 20);
            var target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = focus };

            Assert.That(ScoutMoverSelector.Eligible(snap, target, null).Select(a => a.ArmyId),
                Is.EquivalentTo(new[] { 10 }), "capacity and assignment must share turn-local actor eligibility");
            snap.TurnNumber = 12;
            Assert.That(ScoutMoverSelector.Eligible(snap, target, null).Select(a => a.ArmyId),
                Is.EquivalentTo(new[] { 10, 20 }), "surplus trim must not become a permanent ban");
        }

        private static MissionIntent Incumbent(HexCoord focus, int preferredMover)
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.Scout,
                Funding = CommitmentTier.None,
                Status = IntentStatus.Active,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore, FocusHex = focus },
                PreferredMoverArmyId = preferredMover,
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            return intent;
        }

        private static WorldSnapshot Snapshot(PlayerSetupData player, int turn, HexCoord focus)
        {
            var hexes = new HashSet<HexCoord>(HexGridMath.Neighbors(focus))
            {
                focus,
                new HexCoord(0, 0),
            };
            return new WorldSnapshot
            {
                TurnNumber = turn,
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new List<HexCoord> { new HexCoord(0, 0) },
                    Armies = new List<ArmySnapshot>
                    {
                        new ArmySnapshot
                        {
                            ArmyId = 10, Owner = player, Hex = new HexCoord(3, 3),
                            IsSoloRecce = true, MemberCount = 1, CurrentMovement = 3,
                            MaxMovement = 3, ActivationApCost = 1,
                        },
                        new ArmySnapshot
                        {
                            ArmyId = 20, Owner = player, Hex = new HexCoord(3, 2),
                            IsSoloRecce = true, MemberCount = 1, CurrentMovement = 3,
                            MaxMovement = 3, ActivationApCost = 1,
                        },
                    },
                },
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    AllHexes = hexes.ToList(),
                    VisitedHexSet = new HashSet<HexCoord>(),
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                },
            };
        }
    }
}
#endif
