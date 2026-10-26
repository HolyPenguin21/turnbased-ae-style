#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiEndgameRegressionTests
    {
        [Test]
        public void RefreshCannotBuyAZeroPurposeDepartureWithLookahead()
        {
            Assert.That(ReconGroundStepPlanner.CompleteStepScore(0f, 10f, 1f, 1f, 1f,
                requireImmediatePurpose: true), Is.Zero);
            Assert.That(ReconGroundStepPlanner.CompleteStepScore(1f, 10f, 1f, 1f, 1f,
                requireImmediatePurpose: true), Is.GreaterThan(1f), "Useful safe-route transit keeps lookahead");
            Assert.That(ReconGroundStepPlanner.CompleteStepScore(0f, 10f, 1f, 1f, 1f),
                Is.GreaterThan(0f), "Explore may forecast an informative continuation");
        }

        [TestCase(ScoutTargetKind.Hunt, "Hunt")]
        [TestCase(ScoutTargetKind.CaptureStructure, "CaptureStructure")]
        public void ScoutDiagnosticsNameRealSubtypesWithoutChangingIdentity(ScoutTargetKind kind, string name)
        {
            var key = MissionIntentKey.ForScout(kind, new HexCoord(2, -1));
            StringAssert.Contains(name, key.ToString());
            Assert.That(key, Is.EqualTo(MissionIntentKey.ForScout(kind, new HexCoord(2, -1))));
            Assert.That(key, Is.Not.EqualTo(MissionIntentKey.ForScout(kind, new HexCoord(3, -1))));
        }
    }
}
#endif
