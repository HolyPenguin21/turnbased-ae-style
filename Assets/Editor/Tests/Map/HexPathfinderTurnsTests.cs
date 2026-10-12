#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // 2026-10-02 — HexPathfinder.FindPathByTurns: a ground army cannot enter a hex costing more than
    // the movement it has left, so the fastest route is judged in TURNS, not in total cost.
    public sealed class HexPathfinderTurnsTests
    {
        private GameObject _object;
        private HexMap _map;

        [SetUp] public void SetUp()
        {
            _object = new GameObject("HexPathfinderTurnsTests");
            _map = _object.AddComponent<HexMap>();
        }

        [TearDown] public void TearDown() => Object.DestroyImmediate(_object);

        private void Build(int radius, System.Func<HexCoord, int> costOf)
        {
            var data = new Dictionary<HexCoord, TerrainTypeEntry>();
            foreach (HexCoord h in HexGridMath.HexesInRange(new HexCoord(0, 0), radius))
                data[h] = new TerrainTypeEntry { terrainName = "T" + costOf(h), moveCost = costOf(h) };
            _map.SetData(radius, 1, data);
        }

        // Turns a mover needs to walk `hexes` from `start` with `max` movement and `left` left now.
        private int TurnsFor(IReadOnlyList<HexCoord> hexes, int left, int max)
        {
            int turns = 0;
            for (int i = 1; i < hexes.Count; i++)
            {
                _map.TryGetTerrainAt(hexes[i], out TerrainTypeEntry e);
                int c = Mathf.Max(1, e.moveCost);
                if (left < c) { turns++; left = max; }
                left -= c;
            }
            return turns;
        }

        [Test]
        public void NeverNeedsMoreTurnsThanTheCheapestRoute()
        {
            var rng = new System.Random(11);
            Build(4, h => 1 + rng.Next(2));
            List<HexCoord> all = HexGridMath.HexesInRange(new HexCoord(0, 0), 4).ToList();
            for (int i = 0; i < 60; i++)
            {
                HexCoord a = all[rng.Next(all.Count)], b = all[rng.Next(all.Count)];
                if (a.Equals(b)) continue;
                int left = rng.Next(0, 3);
                HexPath cheap = HexPathfinder.FindPath(_map, a, b);
                HexPath fast = HexPathfinder.FindPathByTurns(_map, a, b, left, 2);
                Assert.That(fast, Is.Not.Null);
                Assert.That(fast.Hexes.First(), Is.EqualTo(a));
                Assert.That(fast.Hexes.Last(), Is.EqualTo(b));
                for (int k = 1; k < fast.Hexes.Count; k++)
                    Assert.That(HexGridMath.Distance(fast.Hexes[k - 1], fast.Hexes[k]), Is.EqualTo(1));
                Assert.That(TurnsFor(fast.Hexes, left, 2), Is.LessThanOrEqualTo(TurnsFor(cheap.Hexes, left, 2)),
                    $"{a} -> {b} with {left} left");
            }
        }

        [Test]
        public void AvoidsWastingAPointBeforeACostTwoHex()
        {
            // A single corridor row r=0 (cost 1,2,1) with a free cost-1 bypass would otherwise tie on
            // total cost; with 2 movement and 1 left the bypass saves a whole turn.
            Build(2, h => (h.R == 0 && h.Q == 0) ? 2 : 1);
            HexPath p = HexPathfinder.FindPathByTurns(_map, new HexCoord(-2, 0), new HexCoord(2, 0), 2, 2);
            Assert.That(p, Is.Not.Null);
            Assert.That(TurnsFor(p.Hexes, 2, 2), Is.LessThanOrEqualTo(
                TurnsFor(HexPathfinder.FindPath(_map, new HexCoord(-2, 0), new HexCoord(2, 0)).Hexes, 2, 2)));
        }

        [Test]
        public void ImpassableForThisMoverIsRoutedAround()
        {
            Build(2, h => h.Equals(new HexCoord(0, 0)) ? 3 : 1);
            HexPath p = HexPathfinder.FindPathByTurns(_map, new HexCoord(-1, 0), new HexCoord(1, 0), 2, 2);
            Assert.That(p, Is.Not.Null);
            Assert.That(p.Hexes.Contains(new HexCoord(0, 0)), Is.False, "a hex costing more than MaxMovement is never entered");
        }
    }
}
#endif
