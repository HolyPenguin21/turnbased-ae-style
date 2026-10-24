#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // 2026-10-17 — FindPath is A* on a flat grid, BuildCostTree answers many targets from one
    // source, and SafeStepPathing drops only the cached routes a blocker change can affect.
    // The reference for every cost is the independent cost-only Dijkstra (FindCosts).
    public sealed class HexPathfinderAStarTests
    {
        private GameObject _object;
        private HexMap _map;
        private List<HexCoord> _all;

        [SetUp] public void SetUp()
        {
            AiMapMemory.Clear();
            _object = new GameObject("HexPathfinderAStarTests");
            _map = _object.AddComponent<HexMap>();
        }

        [TearDown] public void TearDown()
        {
            AiMapMemory.Clear();
            Object.DestroyImmediate(_object);
        }

        private void Build(int radius, System.Random rng, bool withWalls)
        {
            var data = new Dictionary<HexCoord, TerrainTypeEntry>();
            foreach (HexCoord h in HexGridMath.HexesInRange(new HexCoord(0, 0), radius))
                data[h] = new TerrainTypeEntry
                {
                    terrainName = "T",
                    moveCost = 1 + rng.Next(3),
                    blocksGroundMovement = withWalls && rng.Next(8) == 0,
                };
            _map.SetData(radius, 1, data);
            _all = data.Keys.Where(h => _map.CanEnter(h)).ToList();
        }

        private int StepCost(HexCoord h)
        {
            _map.TryGetTerrainAt(h, out TerrainTypeEntry e);
            return Mathf.Max(1, e.moveCost);
        }

        private void AssertLegal(HexPath path, HexCoord from, HexCoord to,
            System.Func<HexCoord, bool> transitBlocked = null, int? maxStep = null)
        {
            Assert.That(path.Hexes[0], Is.EqualTo(from));
            Assert.That(path.Hexes[path.Hexes.Count - 1], Is.EqualTo(to));
            int cost = 0;
            for (int i = 1; i < path.Hexes.Count; i++)
            {
                Assert.That(HexGridMath.Distance(path.Hexes[i - 1], path.Hexes[i]), Is.EqualTo(1));
                Assert.That(_map.CanEnter(path.Hexes[i]), Is.True);
                if (maxStep.HasValue) Assert.That(StepCost(path.Hexes[i]), Is.LessThanOrEqualTo(maxStep.Value));
                if (transitBlocked != null && i < path.Hexes.Count - 1)
                    Assert.That(transitBlocked(path.Hexes[i]), Is.False, "a blocked hex may only end a path");
                cost += StepCost(path.Hexes[i]);
            }
            Assert.That(path.TotalCost, Is.EqualTo(cost));
        }

        [Test]
        public void AStarCostEqualsDijkstraCost_OnRandomTerrainWithWalls()
        {
            var rng = new System.Random(5);
            Build(7, rng, withWalls: true);
            foreach (HexCoord from in _all.Where((_, i) => i % 9 == 0))
            {
                Dictionary<HexCoord, int> reference = HexPathfinder.FindCosts(_map, new[] { from });
                foreach (HexCoord to in _all.Where((_, i) => i % 5 == 0))
                {
                    HexPath path = HexPathfinder.FindPath(_map, from, to);
                    if (!reference.TryGetValue(to, out int expected))
                    {
                        Assert.That(path, Is.Null);
                        continue;
                    }
                    Assert.That(path, Is.Not.Null);
                    Assert.That(path.TotalCost, Is.EqualTo(expected));
                    AssertLegal(path, from, to);
                }
            }
        }

        [Test]
        public void MaxStepCost_NeverEntersCostlierTerrain_AndEqualsTheBlockedDijkstra()
        {
            var rng = new System.Random(8);
            Build(6, rng, withWalls: false);
            foreach (HexCoord from in _all.Where((_, i) => i % 11 == 0))
            {
                Dictionary<HexCoord, int> reference = HexPathfinder.FindCosts(_map, new[] { from }, maxMovement: 2);
                foreach (HexCoord to in _all.Where((_, i) => i % 7 == 0))
                {
                    HexPath path = HexPathfinder.FindPath(_map, from, to, maxStepCost: 2);
                    bool reachable = reference.TryGetValue(to, out int expected) && (from.Equals(to) || StepCost(to) <= 2);
                    if (!reachable) { Assert.That(path, Is.Null); continue; }
                    Assert.That(path.TotalCost, Is.EqualTo(expected));
                    AssertLegal(path, from, to, maxStep: 2);
                }
            }
        }

        [Test]
        public void CostTree_MatchesPointToPointRoutes_WithTheDestinationExemption()
        {
            var rng = new System.Random(13);
            Build(7, rng, withWalls: true);
            var blocked = new HashSet<HexCoord>(_all.Where(_ => rng.Next(6) == 0));
            foreach (HexCoord from in _all.Where((_, i) => i % 10 == 0))
            {
                HexPathfinder.CostTree tree = HexPathfinder.BuildCostTree(_map, from, blocked, 3);
                foreach (HexCoord to in _all.Where((_, i) => i % 4 == 0))
                {
                    HexPath direct = HexPathfinder.FindPath(_map, from, to,
                        blockHex: h => !h.Equals(to) && blocked.Contains(h), maxStepCost: 3);
                    HexPath viaTree = tree.PathTo(to);
                    if (direct == null) { Assert.That(viaTree, Is.Null); continue; }
                    Assert.That(viaTree, Is.Not.Null);
                    Assert.That(viaTree.TotalCost, Is.EqualTo(direct.TotalCost));
                    Assert.That(tree.CostTo(to), Is.EqualTo(direct.TotalCost));
                    AssertLegal(viaTree, from, to, blocked.Contains, 3);
                }
            }
        }

        // Every answer the route cache gives after a blocker change equals a fresh search on the
        // new blocker set — whether the entry survived the targeted invalidation or not.
        [Test]
        public void CachedRoutes_AfterBlockersAppearAndExpire_EqualFreshSearches()
        {
            var rng = new System.Random(21);
            Build(6, rng, withWalls: false);
            var owner = new PlayerSetupData();
            var pairs = Enumerable.Range(0, 40)
                .Select(_ => (_all[rng.Next(_all.Count)], _all[rng.Next(_all.Count)])).ToList();
            // Known enemy armies block a Standard route and expire after enemySightingMemoryTurns.
            var enemy = new PlayerSetupData();
            var sightings = new List<(HexCoord Hex, int Seen)>();
            int nextArmyId = 1;
            for (int turn = 1; turn <= 8; turn++)
            {
                AiMapMemory.OnTurnStarted(owner, turn);
                sightings.RemoveAll(x => turn - x.Seen > Game.Ai.AiConfig.enemySightingMemoryTurns);
                for (int k = 0; k < 2; k++)
                {
                    HexCoord hex = _all[rng.Next(_all.Count)];
                    AiMapMemory.RememberForTest(owner, new AiMapMemory.KnownEnemySighting(hex, enemy, "enemy",
                        1, 1f, 1f, null, seenTurn: turn, armyId: nextArmyId++));
                    sightings.Add((hex, turn));
                }
                var blocked = new HashSet<HexCoord>(sightings.Select(x => x.Hex));
                foreach ((HexCoord from, HexCoord to) in pairs)
                {
                    HexPath fresh = HexPathfinder.FindPath(_map, from, to,
                        blockHex: h => !h.Equals(to) && blocked.Contains(h), maxStepCost: 3);
                    HexPath cached = SafeStepPathing.FindSafePath(_map, owner, from, to, 3);
                    HexPath tree = SafeStepPathing.FindSafePathFromSource(_map, owner, from, to, 3);
                    if (fresh == null)
                    {
                        Assert.That(cached, Is.Null, $"turn {turn} {from}->{to}");
                        Assert.That(tree, Is.Null, $"turn {turn} {from}->{to}");
                        continue;
                    }
                    Assert.That(cached?.TotalCost, Is.EqualTo(fresh.TotalCost), $"turn {turn} {from}->{to}");
                    Assert.That(tree?.TotalCost, Is.EqualTo(fresh.TotalCost), $"turn {turn} {from}->{to}");
                    AssertLegal(cached, from, to, blocked.Contains, 3);
                    AssertLegal(tree, from, to, blocked.Contains, 3);
                }
            }
        }
    }
}
#endif
