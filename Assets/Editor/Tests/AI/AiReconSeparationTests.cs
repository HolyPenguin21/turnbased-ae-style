#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Recon S3 — scoutTargetMinSeparation keeps NEW lanes apart; it must never starve a durable
    // role, and a re-focused waypoint keeps that spacing when it can.
    public class AiReconSeparationTests
    {
        [Test]
        public void TwoCloseDurableRoles_AreBothAssigned()
        {
            List<FundedEntry> open = Open(durable: true);
            ReconAssignmentResult r = ReconAssignmentPlanner.AssignFromCandidates(open, Cands(open), 0f, 0);
            Assert.That(r.Assigned, Has.Count.EqualTo(2));
        }

        [Test]
        public void TwoCloseFreshLanes_StillKeepTheirSpacing()
        {
            List<FundedEntry> open = Open(durable: false);
            ReconAssignmentResult r = ReconAssignmentPlanner.AssignFromCandidates(open, Cands(open), 0f, 0);
            Assert.That(r.Assigned, Has.Count.EqualTo(1));
        }

        [Test]
        public void Refocus_PrefersAWaypointSpacedFromOtherScouts()
        {
            var old = new HexCoord(3, 0);
            var other = new HexCoord(5, 0);
            var crowded = new HexCoord(4, 0);   // nearest to the old waypoint, but next to another scout
            var spaced = new HexCoord(1, 0);
            var hexes = new List<HexCoord>();
            for (int q = -1; q <= 7; q++) for (int rr = -1; rr <= 1; rr++) hexes.Add(new HexCoord(q, rr));
            var snap = new WorldSnapshot
            {
                TurnNumber = 5,
                Self = new SelfSnapshot { Citadel = new HexCoord(0, 0), Armies = new List<ArmySnapshot>() },
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    AllHexes = hexes,
                    VisitedHexSet = new HashSet<HexCoord>(),
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                    Frontier = new List<FrontierHexSnapshot>
                    {
                        new FrontierHexSnapshot { Hex = crowded },
                        new FrontierHexSnapshot { Hex = spaced },
                    },
                },
            };
            var intent = new ScoutIntent { Kind = ScoutTargetKind.Explore, FocusHex = old };
            MethodInfo refocus = typeof(MissionContinuityLayer).GetMethod("TryRefocusScoutIntent",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(refocus, Is.Not.Null);

            bool ok = (bool)refocus.Invoke(null, new object[] { snap, intent, new HashSet<HexCoord> { old, other } });

            Assert.That(ok, Is.True);
            Assert.That(intent.FocusHex, Is.EqualTo(spaced));
        }

        private static List<FundedEntry> Open(bool durable) => new[] { new HexCoord(4, 3), new HexCoord(4, 4) }
            .Select((h, i) => new FundedEntry
            {
                Priority = i,
                Mission = new MissionProposal
                {
                    Kind = MissionKind.Scout, FromDurableIntent = durable,
                    Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = h },
                },
            }).ToList();

        private static List<List<ScoutExecutionCandidate>> Cands(List<FundedEntry> open) =>
            open.Select((fe, i) => new List<ScoutExecutionCandidate>
            {
                new ScoutExecutionCandidate(new ArmySnapshot { ArmyId = 10 + i, IsSoloRecce = true },
                    ((ScoutMissionTarget)fe.Mission.Target).FocusHex, 1, 1, 1, 0f, 0, false, 1f),
            }).ToList();
    }
}
#endif
