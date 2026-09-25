#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Recon S4 — the patrol mode follows the kind of mission the actor is bound to; only the
    // one-turn hold stops same-pass ping-pong.
    public class AiReconPatrolModeTests
    {
        [TearDown]
        public void ClearState() => ReconPatrolStateRegistry.ClearAll();

        [Test]
        public void ModeFollowsTheMission_AfterTheOneTurnHold()
        {
            var player = new PlayerSetupData { Nickname = "Recon mode" };
            var at = new HexCoord(0, 0);
            ReconPatrolStateRegistry.GetOrCreate(player, 7, at, new HexCoord(5, 0), ReconMode.Explore, 3);

            ReconPatrolState sameTurn = ReconPatrolStateRegistry.GetOrCreate(
                player, 7, at, new HexCoord(5, 0), ReconMode.Refresh, 3);
            Assert.That(sameTurn.Mode, Is.EqualTo(ReconMode.Explore), "same-pass ping-pong is held");

            ReconPatrolState nextTurn = ReconPatrolStateRegistry.GetOrCreate(
                player, 7, at, new HexCoord(5, 0), ReconMode.Refresh, 4);
            Assert.That(nextTurn.Mode, Is.EqualTo(ReconMode.Refresh));
        }
    }
}
#endif
