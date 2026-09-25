#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    // D5 — one rule for "a met objective is only a waypoint of a durable ground role".
    public class AiReconWaypointRoleTests
    {
        [TestCase(ScoutTargetKind.Explore, true, true, false, ExpectedResult = true)]
        [TestCase(ScoutTargetKind.Refresh, false, true, true, ExpectedResult = true)]
        [TestCase(ScoutTargetKind.Explore, false, true, false, ExpectedResult = false)]
        [TestCase(ScoutTargetKind.Refresh, true, false, true, ExpectedResult = false)]
        [TestCase(ScoutTargetKind.Surveil, true, true, true, ExpectedResult = false)]
        [TestCase(ScoutTargetKind.AirSweep, true, true, true, ExpectedResult = false)]
        public bool RoleContinuesAtWaypoint(ScoutTargetKind kind, bool durable, bool actorStillScout,
            bool acted) =>
            ScoutObjectiveEvaluator.RoleContinuesAtWaypoint(kind, durable, actorStillScout, acted);
    }
}
#endif
