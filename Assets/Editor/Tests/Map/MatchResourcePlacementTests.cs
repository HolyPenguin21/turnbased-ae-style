#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Setup;
using Game.Terrain;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class MatchResourcePlacementTests
    {
        private GameObject _object;
        private HexMap _map;
        private Random.State _randomState;

        [SetUp] public void SetUp()
        {
            _randomState = Random.state;
            HexResourceBonusRegistry.Clear();
            _object = new GameObject("MatchResourcePlacementTests");
            _map = _object.AddComponent<HexMap>();
        }

        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(_object);
            HexResourceBonusRegistry.Clear();
            Random.state = _randomState;
        }

        private void Build(int radius, System.Func<HexCoord, int> cost = null,
            System.Func<HexCoord, bool> blocked = null)
        {
            var data = HexGridMath.HexesInRange(new HexCoord(0, 0), radius).ToDictionary(h => h,
                h => new TerrainTypeEntry { moveCost = cost?.Invoke(h) ?? 1,
                    blocksGroundMovement = blocked?.Invoke(h) ?? false });
            _map.SetData(radius, 1f, data);
        }

        private static List<HexCoord> Citadels(int radius, int players)
        {
            if (players == 2) return new List<HexCoord> { new HexCoord(-radius, 0), new HexCoord(radius, 0) };
            if (players == 3) return new List<HexCoord>
                { new HexCoord(radius, 0), new HexCoord(-radius, radius), new HexCoord(0, -radius) };
            return new List<HexCoord> { new HexCoord(radius, 0), new HexCoord(-radius, 0),
                new HexCoord(0, radius), new HexCoord(0, -radius) };
        }

        private static void AssertSpacing(IReadOnlyList<HexCoord> hexes)
        {
            Assert.That(hexes.Distinct().Count(), Is.EqualTo(hexes.Count));
            for (int i = 0; i < hexes.Count; i++)
                for (int j = i + 1; j < hexes.Count; j++)
                    Assert.That(HexGridMath.Distance(hexes[i], hexes[j]), Is.GreaterThanOrEqualTo(2));
        }

        [Test] public void AllSizesAndPlayerCountsKeepCountsSpacingAndCentralTechAcrossSeeds()
        {
            for (int radius = 5; radius <= 8; radius++)
            for (int players = 2; players <= 4; players++)
            for (int seed = 0; seed < 24; seed++)
            {
                Random.InitState(seed);
                Build(radius, h => 1 + System.Math.Abs(h.Q * 7 + h.R * 3 + seed) % 3,
                    h => h.Q == 0 && h.R == 1);
                HexResourceBonusRegistry.Clear();
                var citadels = Citadels(radius, players);
                foreach (HexCoord c in citadels) HexResourceBonusRegistry.Set(c, new ResourceYields { human = 1 });
                int farTarget = players + radius - 4, techTarget = players == 2 ? 2 : players - 1;
                var layout = MatchResourcePlacement.Plan(_map, citadels, farTarget, techTarget);
                var near = layout.NearHexes;
                Assert.That(near.Count, Is.EqualTo(players));
                var nearHexes = near.SelectMany(x => x).ToList();
                AssertSpacing(nearHexes);
                for (int p = 0; p < players; p++)
                {
                    Assert.That(near[p].Count, Is.EqualTo(3), $"R{radius} P{players} seed {seed}");
                    foreach (HexCoord h in near[p])
                    {
                        Assert.That(HexGridMath.Distance(citadels[p], h), Is.InRange(2, 3));
                        Assert.That(citadels.All(c => HexGridMath.Distance(c, h) >= 2), Is.True);
                        Assert.That(_map.CanEnter(h), Is.True);
                        HexResourceBonusRegistry.Set(h, new ResourceYields { materials = 1 });
                    }
                }
                var far = layout.Far;
                Assert.That(far.Hexes.Count, Is.EqualTo(farTarget), $"R{radius} P{players} seed {seed}");
                Assert.That(far.TechHexes.Count, Is.EqualTo(techTarget));
                AssertSpacing(nearHexes.Concat(far.Hexes).ToList());
                foreach (HexCoord h in far.Hexes)
                {
                    Assert.That(citadels.All(c => HexGridMath.Distance(c, h) >= 4), Is.True);
                    Assert.That(_map.CanEnter(h), Is.True);
                }
                Assert.That(far.TechHexes.All(far.Hexes.Contains), Is.True);
                int furthestTech = far.TechHexes.Max(h => HexGridMath.Distance(new HexCoord(0, 0), h));
                Assert.That(far.Hexes.Except(far.TechHexes).All(h =>
                    HexGridMath.Distance(new HexCoord(0, 0), h) >= furthestTech), Is.True);
            }
        }

        [Test] public void SameTerrainAndCitadelsProduceManyDifferentLayouts()
        {
            Build(6);
            var citadels = Citadels(6, 2);
            var layouts = new HashSet<string>();
            var techLayouts = new HashSet<string>();
            for (int seed = 0; seed < 80; seed++)
            {
                Random.InitState(seed);
                var far = MatchResourcePlacement.PlanFar(_map, citadels, new List<HexCoord>(), 4, 2);
                layouts.Add(string.Join(";", far.Hexes.OrderBy(h => h.Q).ThenBy(h => h.R)));
                techLayouts.Add(string.Join(";", far.TechHexes.OrderBy(h => h.Q).ThenBy(h => h.R)));
            }
            Assert.That(layouts.Count, Is.GreaterThan(40), "quality selection must preserve positional randomness");
            Assert.That(techLayouts.Count, Is.GreaterThan(20), "Tech positions must also vary");
            TestContext.WriteLine($"Fixed-map diversity: {layouts.Count}/80 far layouts, {techLayouts.Count}/80 Tech layouts.");
        }

        [Test] public void InwardShiftedCitadelsStillGetCompleteNearAndFarLayouts()
        {
            for (int radius = 5; radius <= 8; radius++)
            for (int players = 2; players <= 4; players++)
            for (int seed = 0; seed < 16; seed++)
            {
                Build(radius);
                HexResourceBonusRegistry.Clear();
                Random.InitState(seed);
                var citadels = Citadels(radius - 1, players);
                var layout = MatchResourcePlacement.Plan(_map, citadels,
                    players + radius - 4, players == 2 ? 2 : players - 1);
                var near = layout.NearHexes;
                Assert.That(near.All(n => n.Count == 3), Is.True);
                var far = layout.Far;
                Assert.That(far.Hexes.Count, Is.EqualTo(players + radius - 4),
                    $"inward R{radius} P{players} seed {seed}");
            }
        }

        [Test] public void UnreachableAndAlreadyBonusedHexesAreExcludedWithoutMutation()
        {
            Build(6, blocked: h => h.Q == 0);
            var citadels = Citadels(6, 2);
            var far = MatchResourcePlacement.PlanFar(_map, citadels, new List<HexCoord>(), 4, 2);
            Assert.That(far.Hexes, Is.Empty, "a resource isolated from one player is not a fair candidate");
            Build(6);
            var reserved = new HexCoord(0, 0);
            var bonus = new ResourceYields { tech = 7 };
            HexResourceBonusRegistry.Set(reserved, bonus);
            far = MatchResourcePlacement.PlanFar(_map, citadels, new List<HexCoord>(), 4, 2);
            Assert.That(far.Hexes.Contains(reserved), Is.False);
            Assert.That(HexResourceBonusRegistry.GetBonus(reserved), Is.SameAs(bonus));
            Assert.That(far.Hexes.All(h => HexResourceBonusRegistry.GetBonus(h) == null), Is.True);
        }

        [Test] public void CrowdedMapReturnsOnlyLegalPointsAndTechNeverAddsExtraHexes()
        {
            Build(2);
            var far = MatchResourcePlacement.PlanFar(_map, Citadels(2, 2), new List<HexCoord>(), 4, 2);
            Assert.That(far.Hexes, Is.Empty);
            Assert.That(far.TechHexes, Is.Empty);
            Build(6);
            far = MatchResourcePlacement.PlanFar(_map, Citadels(6, 2), new List<HexCoord>(), 1, 5);
            Assert.That(far.Hexes.Count, Is.EqualTo(1));
            Assert.That(far.TechHexes.Count, Is.EqualTo(1));
        }

#if !TERRAIN_MANAGED_HARNESS
        [Test] public void ResourcePassStampsBalancedOrdinaryYieldsAndAddsTechOnThoseSameHexes()
        {
            Build(6);
            Random.InitState(47);
            var citadels = Citadels(6, 2);
            var controller = _object.AddComponent<CitadelSetupController>();
            var config = ScriptableObject.CreateInstance<Game.Core.GameConfig>();
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(CitadelSetupController).GetField("map", flags).SetValue(controller, _map);
                typeof(CitadelSetupController).GetField("gameConfig", flags).SetValue(controller, config);
                var players = (List<Game.Players.PlayerSetupData>)typeof(CitadelSetupController)
                    .GetField("_allPlayers", flags).GetValue(controller);
                foreach (HexCoord c in citadels)
                {
                    players.Add(new Game.Players.PlayerSetupData { CitadelHexQ = c.Q, CitadelHexR = c.R });
                    HexResourceBonusRegistry.Set(c, new ResourceYields { human = 2 });
                }
                typeof(CitadelSetupController).GetMethod("GenerateResources", flags).Invoke(controller, null);
                var far = _map.AllCoords.Where(h => citadels.All(c => HexGridMath.Distance(c, h) >= 4)
                    && HexResourceBonusRegistry.GetBonus(h) != null).ToList();
                Assert.That(far.Count, Is.EqualTo(4));
                Assert.That(far.Sum(h => HexResourceBonusRegistry.GetBonus(h).tech), Is.EqualTo(2));
                foreach (HexCoord h in far)
                {
                    ResourceYields y = HexResourceBonusRegistry.GetBonus(h);
                    Assert.That(y.human + y.energy + y.materials, Is.EqualTo(1));
                    Assert.That(y.tech, Is.InRange(0, 1));
                }
                int[] ordinary = { far.Sum(h => HexResourceBonusRegistry.GetBonus(h).human),
                    far.Sum(h => HexResourceBonusRegistry.GetBonus(h).energy),
                    far.Sum(h => HexResourceBonusRegistry.GetBonus(h).materials) };
                Assert.That(ordinary.Max() - ordinary.Min(), Is.LessThanOrEqualTo(1));
                foreach (HexCoord c in citadels)
                {
                    var near = _map.AllCoords.Where(h => HexGridMath.Distance(c, h) >= 2
                        && HexGridMath.Distance(c, h) <= 3 && HexResourceBonusRegistry.GetBonus(h) != null).ToList();
                    Assert.That(near.Count, Is.EqualTo(3));
                    Assert.That(near.Sum(h => HexResourceBonusRegistry.GetBonus(h).human), Is.EqualTo(1));
                    Assert.That(near.Sum(h => HexResourceBonusRegistry.GetBonus(h).energy), Is.EqualTo(1));
                    Assert.That(near.Sum(h => HexResourceBonusRegistry.GetBonus(h).materials), Is.EqualTo(1));
                    Assert.That(near.Sum(h => HexResourceBonusRegistry.GetBonus(h).tech), Is.Zero);
                    Assert.That(HexResourceBonusRegistry.GetBonus(c).human, Is.EqualTo(2));
                }
            }
            finally { Object.DestroyImmediate(config); }
        }
#endif
    }
}
#endif
