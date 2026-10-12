#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
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

        [Test]
        public void ClearInvalidatesAReusedPlayersStrategicSnapshotRevision()
        {
            var player = new PlayerSetupData();
            AiMapMemory.MarkScoutDanger(player, new HexCoord(1, 1), radius: 1, avoidUntilTurn: 3);
            int beforeClear = AiMapMemory.KnowledgeVersionFor(player);

            AiMapMemory.Clear();

            Assert.That(AiMapMemory.KnowledgeVersionFor(player), Is.GreaterThan(beforeClear),
                "A previous session's strategic snapshot must not be reused after memory clears.");
        }

        // ---- 2026-10-08: route profiles are part of the cache key (needs a real HexMap) --------------

        private UnityEngine.GameObject _mapObject;

        private HexMap LineMap(int length, Dictionary<HexCoord, int> costs = null)
        {
            _mapObject = new UnityEngine.GameObject("route-profile-map");
            HexMap map = _mapObject.AddComponent<HexMap>();
            var data = new Dictionary<HexCoord, TerrainTypeEntry>();
            // a corridor two hexes wide so that a blocked hex can be walked around
            for (int q = 0; q <= length; q++)
                foreach (int r in new[] { 0, 1 })
                    data[new HexCoord(q, r)] = new TerrainTypeEntry
                    { moveCost = costs != null && costs.TryGetValue(new HexCoord(q, r), out int c) ? c : 1 };
            map.SetData(length + 2, 1f, data);
            return map;
        }

        [Test]
        public void OneEndpoint_ReturnsADifferentHonestRoutePerProfile_InAnyCallOrder()
        {
            var owner = new PlayerSetupData();
            HexCoord from = new HexCoord(0, 0), to = new HexCoord(6, 0);
            HexMap map = LineMap(6);
            try
            {
                // a scout-danger zone on the straight corridor: a scout detours, a fist does not care
                AiMapMemory.MarkScoutDanger(owner, new HexCoord(3, 0), radius: 0, avoidUntilTurn: 99);
                int standardFirst = SafeStepPathing.FindSafePathCost(map, owner, from, to, 3, SafeRouteProfile.Standard);
                int attack = SafeStepPathing.FindSafePathCost(map, owner, from, to, 3, SafeRouteProfile.Attack);
                int combat = SafeStepPathing.FindSafePathCost(map, owner, from, to, 3, SafeRouteProfile.Combat);
                Assert.That(attack, Is.EqualTo(6));
                Assert.That(combat, Is.EqualTo(6), "a scout danger zone is not a fist's blocker");
                Assert.That(standardFirst, Is.GreaterThan(attack), "the Standard route goes around the zone");
                // reversed order on a fresh cache state gives the same three answers
                AiMapMemory.MarkScoutDanger(owner, new HexCoord(5, 1), radius: 0, avoidUntilTurn: 99);
                Assert.That(SafeStepPathing.FindSafePathCost(map, owner, from, to, 3, SafeRouteProfile.Attack), Is.EqualTo(attack));
                Assert.That(SafeStepPathing.FindSafePathCost(map, owner, from, to, 3, SafeRouteProfile.Combat), Is.EqualTo(combat));
                Assert.That(SafeStepPathing.FindSafePathCost(map, owner, from, to, 3, SafeRouteProfile.Standard), Is.EqualTo(standardFirst));
                // the cached witness is a copy: editing it cannot change the next answer
                HexPath mine = SafeStepPathing.FindSafePath(map, owner, from, to, 3, SafeRouteProfile.Attack);
                mine.Hexes.Clear();
                Assert.That(SafeStepPathing.FindSafePath(map, owner, from, to, 3, SafeRouteProfile.Attack).Hexes.Count, Is.EqualTo(7));
            }
            finally { UnityEngine.Object.DestroyImmediate(_mapObject); }
        }

        [Test]
        public void AttackProfile_EntryCostAboveMaxMovementIsImpassable_ButLowCurrentMovementIsNot()
        {
            var owner = new PlayerSetupData();
            var wall = new HexCoord(2, 0);
            HexMap map = LineMap(4, new Dictionary<HexCoord, int> { [wall] = 2, [new HexCoord(2, 1)] = 2 });
            try
            {
                // MaxMovement 1 cannot enter a cost-2 hex at all: no route through the corridor
                Assert.That(SafeStepPathing.FindSafePathCost(map, owner, new HexCoord(0, 0), new HexCoord(4, 0), 1,
                    SafeRouteProfile.Attack), Is.EqualTo(int.MaxValue));
                // MaxMovement 2 can; it merely needs a turn with enough movement (cost is the sum)
                Assert.That(SafeStepPathing.FindSafePathCost(map, owner, new HexCoord(0, 0), new HexCoord(4, 0), 2,
                    SafeRouteProfile.Attack), Is.EqualTo(5));
            }
            finally { UnityEngine.Object.DestroyImmediate(_mapObject); }
        }

        [Test]
        public void NoOpVisibilityRefresh_DoesNotInvalidateKnowledgeVersion()
        {
            var player = new PlayerSetupData();
            int before = AiMapMemory.KnowledgeVersionFor(player);

            AiMapMemory.RefreshVisibleForTest(player);

            Assert.That(AiMapMemory.KnowledgeVersionFor(player), Is.EqualTo(before),
                "A visibility/stealth event with no observed data delta must not invalidate this player's strategic snapshot.");
        }
    }
}
#endif
