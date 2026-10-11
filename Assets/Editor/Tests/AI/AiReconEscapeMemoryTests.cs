#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // T09 — the escape memory an ordinary Recon step reads is bounded: it ends on expiry and as
    // soon as the escaped cause is gone from honest memory. Never a permanent ban.
    public class AiReconEscapeMemoryTests
    {
        private static ReconPatrolState Patrol(ReconEscape escape) => new ReconPatrolState
        {
            Mode = ReconMode.Refresh,
            LastEscape = escape,
        };

        [Test]
        public void ExpiredEscape_NoLongerBlocks_AndIsCleared()
        {
            var player = new PlayerSetupData();
            ReconPatrolState patrol = Patrol(new ReconEscape
            {
                FromHex = new HexCoord(1, 0), Cause = ReconEscapeCause.Detector, Risk = 0.5f,
                Turn = 3,
            });
            bool blocked = ReconGroundStepPlanner.ReentryBlocked(player, new ArmyData { Owner = player },
                patrol, 3 + AiConfigV2.scoutEscapeMemoryTurns + 1, new HexCoord(1, 0), 0f, 0.5f, out _);
            Assert.That(blocked, Is.False);
            Assert.That(patrol.LastEscape, Is.Null);
        }

        [Test]
        public void ThreatNoLongerKnown_EndsTheEscape()
        {
            var player = new PlayerSetupData();
            ReconPatrolState patrol = Patrol(new ReconEscape
            {
                FromHex = new HexCoord(0, 0), Cause = ReconEscapeCause.Threat, ThreatArmyId = 77,
                Turn = 5,
            });
            bool blocked = ReconGroundStepPlanner.ReentryBlocked(player, new ArmyData { Owner = player },
                patrol, 5, new HexCoord(1, 0), 0f, 0f, out _);
            Assert.That(blocked, Is.False);
            Assert.That(patrol.LastEscape, Is.Null);
        }
    }
}
#endif
