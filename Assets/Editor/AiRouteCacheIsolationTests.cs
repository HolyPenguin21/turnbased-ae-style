#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiRouteCacheIsolationTests
    {
        [SetUp]
        public void SetUp() => AiMapMemory.Clear();

        [TearDown]
        public void TearDown() => AiMapMemory.Clear();

        [Test]
        public void RouteMemoryRevisionChangesOnlyForOwningPlayer()
        {
            var a = new PlayerSetupData();
            var b = new PlayerSetupData();
            long a0 = AiMapMemory.RouteMemoryVersionFor(a);
            long b0 = AiMapMemory.RouteMemoryVersionFor(b);

            AiMapMemory.MarkScoutDanger(a, new HexCoord(3, -2), radius: 2, avoidUntilTurn: 5);

            Assert.That(AiMapMemory.RouteMemoryVersionFor(a), Is.Not.EqualTo(a0));
            Assert.That(AiMapMemory.RouteMemoryVersionFor(b), Is.EqualTo(b0),
                "Player A's route knowledge must not invalidate player B's path cache.");
        }

        [Test]
        public void ClearInvalidatesAReusedPlayersOldRouteRevision()
        {
            var player = new PlayerSetupData();
            AiMapMemory.MarkScoutDanger(player, new HexCoord(1, 1), radius: 1, avoidUntilTurn: 3);
            long beforeClear = AiMapMemory.RouteMemoryVersionFor(player);

            AiMapMemory.Clear();

            Assert.That(AiMapMemory.RouteMemoryVersionFor(player), Is.Not.EqualTo(beforeClear),
                "A new memory session must never reuse an old route-cache revision.");
        }
    }
}
#endif
