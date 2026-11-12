#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.EditorTests
{
    // The one movement-cost rule (spec 4): +1 MP leaving a storm hex, for ground and air, on
    // every search, the route sum and the real move. HexMap is a MonoBehaviour: Unity only.
    public sealed class MovementEnvironmentTests
    {
        private GameObject _object;
        private HexMap _map;

        [SetUp] public void SetUp()
        {
            _object = new GameObject("MovementEnvironmentTests");
            _map = _object.AddComponent<HexMap>();
            ArmyRegistry.Clear();
            VisionSystem.Clear();
        }

        [TearDown] public void TearDown()
        {
            ArmyRegistry.Clear();
            VisionSystem.Clear();
            Object.DestroyImmediate(_object);
        }

        private void Build(int radius, Func<HexCoord, int> costOf, Func<HexCoord, bool> blocked = null)
        {
            var data = new Dictionary<HexCoord, TerrainTypeEntry>();
            foreach (HexCoord h in HexGridMath.HexesInRange(new HexCoord(0, 0), radius))
                data[h] = new TerrainTypeEntry
                {
                    terrainName = "T" + costOf(h), moveCost = costOf(h), blocksGroundMovement = blocked?.Invoke(h) == true,
                };
            _map.SetData(radius, 1, data);
        }

        internal static SandstormSnapshot Storms(params HexCoord[][] bodies) =>
            new SandstormSnapshot(MatchWeatherProfile.TwoSandstorms, 1,
                bodies.Select((b, i) => new SandstormInstanceView(i, SandstormPhase.Active, 1, 0, b)));

        private void Storm(params HexCoord[] cells) => _map.PublishWeather(Storms(cells));

        private int Step(HexCoord from, HexCoord to, MovementProfile profile) =>
            MovementRules.TryGetStepCost(_map, from, to, profile, out int mp) ? mp : -1;

        private static readonly HexCoord A = new HexCoord(0, 0), B = new HexCoord(1, 0), C = new HexCoord(2, 0);

        [TestCase(MovementProfile.Ground)]
        [TestCase(MovementProfile.Air)]
        public void FourTransitions_PenaltyComesFromTheSourceHex(MovementProfile profile)
        {
            Build(3, _ => 1);
            Storm(B, C);
            Assert.That(Step(A, B, profile), Is.EqualTo(1), "clear -> storm");
            Assert.That(Step(B, A, profile), Is.EqualTo(2), "storm -> clear");
            Assert.That(Step(B, C, profile), Is.EqualTo(2), "storm -> storm");
            Assert.That(Step(new HexCoord(0, 1), A, profile), Is.EqualTo(1), "clear -> clear");
        }

        [Test]
        public void ExpensiveTarget_FromAStormHex_GroundFourAirTwo()
        {
            Build(3, h => h.Equals(A) ? 3 : 1);
            Storm(B);
            Assert.That(Step(B, A, MovementProfile.Ground), Is.EqualTo(4));
            Assert.That(Step(B, A, MovementProfile.Air), Is.EqualTo(2));
        }

        [Test]
        public void TwoOverlappingStorms_PenaltyCountsOnce()
        {
            Build(3, _ => 1);
            _map.PublishWeather(Storms(new[] { B, C }, new[] { B }));
            Assert.That(Step(B, A, MovementProfile.Ground), Is.EqualTo(2));
            Assert.That(Step(B, A, MovementProfile.Air), Is.EqualTo(2));
        }

        [Test]
        public void BlockingGroundTerrain_StaysBlockedInAStorm_AircraftStillFly()
        {
            Build(3, _ => 1, h => h.Equals(B));
            Storm(A, B);
            Assert.That(Step(A, B, MovementProfile.Ground), Is.EqualTo(-1));
            Assert.That(Step(A, B, MovementProfile.Air), Is.EqualTo(2));
            Assert.That(Step(A, new HexCoord(3, 0), MovementProfile.Air), Is.EqualTo(-1), "non-adjacent");
            Assert.That(Step(new HexCoord(3, 0), new HexCoord(4, 0), MovementProfile.Air), Is.EqualTo(-1), "off map");
        }

        [Test]
        public void RouteCost_IsTheSumOfTransitions_NotTheHexCount()
        {
            Build(3, _ => 1);
            Storm(B);
            Assert.That(MovementRules.TryGetRouteCost(_map, new[] { A, B, C }, MovementProfile.Air, out int mp), Is.True);
            Assert.That(mp, Is.EqualTo(3));
            Assert.That(MovementRules.TryGetRouteCost(_map, new[] { A }, MovementProfile.Ground, out mp), Is.True);
            Assert.That(mp, Is.Zero);
            Assert.That(MovementRules.TryGetRouteCost(_map, new[] { A, C }, MovementProfile.Ground, out _), Is.False);
            Assert.That(MovementRules.TryGetRouteCost(_map, new HexCoord[0], MovementProfile.Ground, out _), Is.False);
        }

        [Test]
        public void ForwardAndReverseFields_KeepEdgeDirection()
        {
            Build(3, _ => 1);
            Storm(B);
            // A -> B costs 1, B -> A costs 2.
            Dictionary<HexCoord, int> fromA = HexPathfinder.FindCosts(_map, new[] { A });
            Dictionary<HexCoord, int> toA = HexPathfinder.FindCosts(_map, new[] { A }, reverse: true);
            Assert.That(fromA[B], Is.EqualTo(1));
            Assert.That(toA[B], Is.EqualTo(2), "the reverse field prices the original edge B -> A");
            Assert.That(HexPathfinder.FindPath(_map, A, B).TotalCost, Is.EqualTo(1));
            Assert.That(HexPathfinder.FindPath(_map, B, A).TotalCost, Is.EqualTo(2));
            Assert.That(HexPathfinder.BuildCostTree(_map, B).CostTo(A), Is.EqualTo(2));
        }

        [Test]
        public void WeatherAreaChange_BumpsPathingVersion_AgeingDoesNot()
        {
            Build(3, _ => 1);
            int v0 = _map.PathingVersion;
            Storm(B);
            Assert.That(_map.PathingVersion, Is.EqualTo(v0 + 1));
            _map.PublishWeather(new SandstormSnapshot(MatchWeatherProfile.OneSandstorm, 9,
                new[] { new SandstormInstanceView(0, SandstormPhase.Active, 3, 2, new[] { B }) }));
            Assert.That(_map.PathingVersion, Is.EqualTo(v0 + 1), "same area, new age/revision");
            Assert.That(HexPathfinder.FindPath(_map, B, A).TotalCost, Is.EqualTo(2));
            _map.PublishWeather(SandstormSnapshot.Clear);
            Assert.That(_map.PathingVersion, Is.EqualTo(v0 + 2));
            Assert.That(HexPathfinder.FindPath(_map, B, A).TotalCost, Is.EqualTo(1), "a cached grid never keeps the old storm");
        }

        // Plain reference Dijkstra over MovementRules: every search must agree on the cost.
        private Dictionary<HexCoord, int> Reference(HexCoord source, MovementProfile profile, int? maxStep = null, bool reverse = false)
        {
            var dist = new Dictionary<HexCoord, int> { [source] = 0 };
            var open = new SortedSet<(int, int, int)> { (0, source.Q, source.R) };
            while (open.Count > 0)
            {
                var (d, q, r) = open.Min;
                open.Remove(open.Min);
                var at = new HexCoord(q, r);
                if (d > dist[at]) continue;
                foreach (HexCoord n in HexGridMath.Neighbors(at))
                {
                    bool ok = reverse ? MovementRules.TryGetStepCost(_map, n, at, profile, out int c)
                        : MovementRules.TryGetStepCost(_map, at, n, profile, out c);
                    if (!ok || (maxStep.HasValue && c > maxStep.Value)) continue;
                    if (reverse && !_map.CanEnter(n, profile == MovementProfile.Air)) continue;
                    int nd = d + c;
                    if (dist.TryGetValue(n, out int old) && old <= nd) continue;
                    if (dist.ContainsKey(n)) open.Remove((old, n.Q, n.R));
                    dist[n] = nd;
                    open.Add((nd, n.Q, n.R));
                }
            }
            return dist;
        }

        [Test]
        public void EverySearch_MatchesAnIndependentReferenceDijkstra()
        {
            var rng = new System.Random(5);
            for (int trial = 0; trial < 6; trial++)
            {
                Build(4, _ => 1 + rng.Next(3), h => rng.Next(9) == 0 && !h.Equals(A));
                var all = HexGridMath.HexesInRange(A, 4).ToList();
                Storm(all.Where(_ => rng.Next(3) == 0).ToArray());
                foreach (MovementProfile profile in new[] { MovementProfile.Ground, MovementProfile.Air })
                {
                    bool air = profile == MovementProfile.Air;
                    var forward = Reference(A, profile);
                    var reverse = Reference(A, profile, reverse: true);
                    var fields = HexPathfinder.FindCosts(_map, new[] { A }, airborne: air);
                    var reverseFields = HexPathfinder.FindCosts(_map, new[] { A }, reverse: true, airborne: air);
                    var tree = air ? null : HexPathfinder.BuildCostTree(_map, A);
                    var limited = Reference(A, profile, maxStep: 2);
                    foreach (HexCoord target in all)
                    {
                        string at = $"trial {trial} {profile} {target}";
                        int expected = forward.TryGetValue(target, out int e) ? e : int.MaxValue;
                        Assert.That(fields.TryGetValue(target, out int f) ? f : int.MaxValue, Is.EqualTo(expected), at);
                        Assert.That(reverseFields.TryGetValue(target, out int rf) ? rf : int.MaxValue,
                            Is.EqualTo(reverse.TryGetValue(target, out int re) ? re : int.MaxValue), "reverse " + at);
                        HexPath path = HexPathfinder.FindPath(_map, A, target, airborne: air);
                        Assert.That(path?.TotalCost ?? int.MaxValue, Is.EqualTo(expected), "FindPath " + at);
                        if (path != null)
                            Assert.That(MovementRules.RouteCostOrMax(_map, path.Hexes, profile), Is.EqualTo(path.TotalCost), at);
                        if (tree != null)
                            Assert.That(tree.CostTo(target), Is.EqualTo(expected), "tree " + at);
                        HexPath capped = HexPathfinder.FindPath(_map, A, target, airborne: air, maxStepCost: 2);
                        Assert.That(capped?.TotalCost ?? int.MaxValue,
                            Is.EqualTo(limited.TryGetValue(target, out int l) ? l : int.MaxValue), "maxStepCost " + at);
                    }
                }
            }
        }

        [Test]
        public void ByTurns_NeverSplitsATransitionOverTwoTurns()
        {
            Build(3, _ => 1);
            Storm(B);
            // MP 1 left, max 2: B -> C costs 2, so the mover waits a turn before it.
            HexPath path = HexPathfinder.FindPathByTurns(_map, B, C, 1, 2);
            Assert.That(path.TotalCost, Is.EqualTo(2));
            Assert.That(HexPathfinder.FindPathByTurns(_map, B, C, 1, 1), Is.Null,
                "a transition costing more than a full turn never happens");
        }

        [Test]
        public void RealMove_StormExitCostsTwo_AndIsNeverStartedOnOneMp()
        {
            Build(3, _ => 1);
            Storm(A);
            var owner = new PlayerSetupData();
            var army = new ArmyData { Owner = owner, Hex = A };
            army.Members.Add(new UnitData { Owner = owner, MoveMax = 3, MoveCurrent = 1, HitPointsCurrent = 4 });
            ArmyRegistry.Register(army);
            int activations = 0;
            var move = ArmyMovementOperation.ForMap(army, _map, new[] { A, B, C }, () => { activations++; return true; });
            Assert.That(move.TryEnterNextStep(out _, out _), Is.False);
            Assert.That(army.Hex, Is.EqualTo(A));
            Assert.That(army.Members[0].MoveCurrent, Is.EqualTo(1));
            Assert.That(activations, Is.Zero, "no activation for an impossible first step");

            army.Members[0].MoveCurrent = 3;
            move = ArmyMovementOperation.ForMap(army, _map, new[] { A, B, C });
            Assert.That(move.TryEnterNextStep(out _, out HexCoord to), Is.True);
            Assert.That(to, Is.EqualTo(B));
            Assert.That(army.Members[0].MoveCurrent, Is.EqualTo(1), "leaving the storm cost 2");
            move.TryEnterNextStep(out _, out to);
            Assert.That(army.Hex, Is.EqualTo(C));
            Assert.That(army.Members[0].MoveCurrent, Is.Zero, "B is clear: 1 MP");
            Assert.That(MovementRules.RouteCostOrMax(_map, new[] { A, B, C }, MovementProfile.Ground), Is.EqualTo(3),
                "forecast equals the real debit");
        }

        [Test]
        public void SameHexCountRoutes_AreRankedByRealCost()
        {
            Build(3, _ => 1);
            // Two 2-step routes A -> (1,-1)|(0,1)... -> (1,1)? Use A -> (1,0) -> (2,-1) vs A -> (1,-1) -> (2,-1).
            var target = new HexCoord(2, -1);
            Storm(B);
            HexPath path = HexPathfinder.FindPath(_map, A, target, airborne: true);
            Assert.That(path.Hexes.Count, Is.EqualTo(3));
            Assert.That(path.Hexes, Has.No.Member(B), "the route that leaves the storm costs more");
            Assert.That(path.TotalCost, Is.EqualTo(2));
        }
    }
}
#endif
