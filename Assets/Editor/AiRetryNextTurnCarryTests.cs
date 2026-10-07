#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Playtest 2026-10-07 T1 — a Scout job rejected with RetryNextTurn was re-run through the batch
    // solve by every later admission of the same turn (211 of 596 rejections were such repeats).
    public class AiRetryNextTurnCarryTests
    {
        [TearDown]
        public void ClearState() => CapabilityPoolExhaustionRegistry.Clear();

        [Test]
        public void RetriedScout_IsSkippedWhileItsPoolIsStillProvenEmpty()
        {
            var player = new PlayerSetupData { Nickname = "Carry" };
            MissionProposal job = AiReconAuditBugTests.Scout(ScoutTargetKind.Explore, new HexCoord(5, 1));
            WorldSnapshot snap = AiReconAuditBugTests.Snapshot(player, 3, new HexCoord(5, 1));
            foreach (ArmySnapshot a in snap.Self.Armies)
                a.CurrentMovement = 0;
            CapabilityPoolExhaustionRegistry.BeginTurn(player, 3);

            Assert.That(CapabilityPoolExhaustionRegistry.ShouldSkipRetried(player, job, snap), Is.False,
                "nothing failed yet");
            CapabilityPoolExhaustionRegistry.CarryRetryNextTurn(player, job);
            Assert.That(CapabilityPoolExhaustionRegistry.ShouldSkipRetried(player, job, snap), Is.False,
                "a contended job whose pool was not proven empty is attempted again");

            CapabilityPoolExhaustionRegistry.MarkExhausted(player, CapabilityPoolKind.Scout, "test");
            Assert.That(CapabilityPoolExhaustionRegistry.ShouldSkipRetried(player, job, snap), Is.True);
        }

        [Test]
        public void RetriedScout_IsAttemptedAgainOnceThePoolRecovers()
        {
            var player = new PlayerSetupData { Nickname = "Carry" };
            MissionProposal job = AiReconAuditBugTests.Scout(ScoutTargetKind.Explore, new HexCoord(5, 1));
            WorldSnapshot snap = AiReconAuditBugTests.Snapshot(player, 3, new HexCoord(5, 1));
            CapabilityPoolExhaustionRegistry.BeginTurn(player, 3);
            CapabilityPoolExhaustionRegistry.CarryRetryNextTurn(player, job);
            CapabilityPoolExhaustionRegistry.MarkExhausted(player, CapabilityPoolKind.Scout, "test");

            Assert.That(CapabilityPoolExhaustionRegistry.ShouldSkipRetried(player, job, snap), Is.False,
                "an eligible scout exists again: the mark is lifted and the job re-enters the batch");
            Assert.That(CapabilityPoolExhaustionRegistry.IsExhausted(player, CapabilityPoolKind.Scout), Is.False);
        }

        [Test]
        public void NewTurn_ForgetsTheCarriedRetry()
        {
            var player = new PlayerSetupData { Nickname = "Carry" };
            MissionProposal job = AiReconAuditBugTests.Scout(ScoutTargetKind.Explore, new HexCoord(5, 1));
            WorldSnapshot snap = AiReconAuditBugTests.Snapshot(player, 3, new HexCoord(5, 1));
            foreach (ArmySnapshot a in snap.Self.Armies)
                a.CurrentMovement = 0;
            CapabilityPoolExhaustionRegistry.BeginTurn(player, 3);
            CapabilityPoolExhaustionRegistry.CarryRetryNextTurn(player, job);
            CapabilityPoolExhaustionRegistry.MarkExhausted(player, CapabilityPoolKind.Scout, "test");

            CapabilityPoolExhaustionRegistry.BeginTurn(player, 4);
            CapabilityPoolExhaustionRegistry.MarkExhausted(player, CapabilityPoolKind.Scout, "test");
            Assert.That(CapabilityPoolExhaustionRegistry.ShouldSkipRetried(player, job, snap), Is.False);
        }
    }
}
#endif
