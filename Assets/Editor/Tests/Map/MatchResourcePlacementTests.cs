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
                var layout = MatchResourcePlacement.Plan(_map, citadels, MapGenerationSettings.ResourceBudget(players, radius));
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
                var far = MatchResourcePlacement.PlanFar(_map, citadels, new List<HexCoord>(), MapGenerationSettings.ResourceBudget(2, 6));
                layouts.Add(string.Join(";", far.Hexes.OrderBy(h => h.Q).ThenBy(h => h.R)));
                techLayouts.Add(string.Join(";", far.TechHexes.OrderBy(h => h.Q).ThenBy(h => h.R)));
            }
            Assert.That(layouts.Count, Is.GreaterThan(40), "quality selection must preserve positional randomness");
            Assert.That(techLayouts.Count, Is.GreaterThan(20), "Tech positions must also vary");
            System.Console.WriteLine($"Fixed-map diversity: {layouts.Count}/80 far layouts, {techLayouts.Count}/80 Tech layouts.");
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
                    MapGenerationSettings.ResourceBudget(players, radius));
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
            var far = MatchResourcePlacement.PlanFar(_map, citadels, new List<HexCoord>(), MapGenerationSettings.ResourceBudget(2, 6));
            Assert.That(far.Hexes, Is.Empty, "a resource isolated from one player is not a fair candidate");
            Build(6);
            var reserved = new HexCoord(0, 0);
            var bonus = new ResourceYields { tech = 7 };
            HexResourceBonusRegistry.Set(reserved, bonus);
            far = MatchResourcePlacement.PlanFar(_map, citadels, new List<HexCoord>(), MapGenerationSettings.ResourceBudget(2, 6));
            Assert.That(far.Hexes.Contains(reserved), Is.False);
            Assert.That(HexResourceBonusRegistry.GetBonus(reserved), Is.SameAs(bonus));
            Assert.That(far.Hexes.All(h => HexResourceBonusRegistry.GetBonus(h) == null), Is.True);
        }

        [Test] public void CrowdedMapReturnsOnlyLegalPointsAndTechNeverAddsExtraHexes()
        {
            Build(2);
            var far = MatchResourcePlacement.PlanFar(_map, Citadels(2, 2), new List<HexCoord>(), MapGenerationSettings.ResourceBudget(2, 6));
            Assert.That(far.Hexes, Is.Empty);
            Assert.That(far.TechHexes, Is.Empty);
            Build(6);
            far = MatchResourcePlacement.PlanFar(_map, Citadels(6, 2), new List<HexCoord>(), new MatchResourceBudget(1, 5, ResourceSpecialization.Balanced));
            Assert.That(far.Hexes.Count, Is.EqualTo(1));
            Assert.That(far.TechHexes.Count, Is.EqualTo(1));
        }

        [Test] public void RegionalBudgetsAndActualQuotasPreserveEveryRareType()
        {
            for (int players = 2; players <= 4; players++)
            for (int radius = 5; radius <= 8; radius++)
            foreach (ResourceRichness richness in System.Enum.GetValues(typeof(ResourceRichness)))
            foreach (ResourceSpecialization profile in System.Enum.GetValues(typeof(ResourceSpecialization)))
            {
                var budget = MapGenerationSettings.ResourceBudget(players, radius, new RegionResourceConditions(profile, richness));
                Assert.That(budget.FarTarget, Is.EqualTo(System.Math.Max(3, System.Math.Max(budget.TechTarget,
                    players + radius - 4 + (int)richness - (int)ResourceRichness.Normal))));
                for (int actual = 3; actual <= budget.FarTarget; actual++)
                {
                    var q = budget.OrdinaryCounts(actual);
                    Assert.That(q.human + q.energy + q.materials, Is.EqualTo(actual));
                    Assert.That(System.Math.Min(q.human, System.Math.Min(q.energy, q.materials)), Is.GreaterThanOrEqualTo(1));
                    if (profile == ResourceSpecialization.Materials) Assert.That(q.materials, Is.EqualTo(actual - 2));
                }
            }
        }

        [Test] public void RegionalTypedPlansAcrossTerrainCostsAndShiftedStarts()
        {
            for (int radius = 5; radius <= 8; radius++)
            for (int players = 2; players <= 4; players++)
            foreach (ResourceRichness richness in System.Enum.GetValues(typeof(ResourceRichness)))
            foreach (ResourceSpecialization profile in System.Enum.GetValues(typeof(ResourceSpecialization)))
            for (int seed = 0; seed < 4; seed++)
            {
                // Different movement/obstacle samples plus starts one ring inward.
                Build(radius, h => 1 + System.Math.Abs(h.Q * 7 + h.R + seed) % 3, h => h.Q == 0 && h.R == 1);
                Random.InitState(seed); HexResourceBonusRegistry.Clear();
                var citadels = Citadels(radius - 1, players);
                var budget = MapGenerationSettings.ResourceBudget(players, radius, new RegionResourceConditions(profile, richness));
                var plan = MatchResourcePlacement.Plan(_map, citadels, budget);
                Assert.That(plan.Near.All(n => n.Count == 3), Is.True);
                foreach (var near in plan.Near) CollectionAssert.AreEquivalent(new[] { ResourceType.Human, ResourceType.Energy, ResourceType.Materials }, near.Select(p => p.Type));
                Assert.That(plan.Far.Points.Count, Is.EqualTo(budget.FarTarget), $"R{radius} P{players} {richness} {profile} seed{seed}");
                Assert.That(plan.Far.Points.Count(p => p.Tech), Is.EqualTo(budget.TechTarget));
                var counts = plan.Far.Points.GroupBy(p => p.Type).ToDictionary(g => g.Key, g => g.Count());
                Assert.That(counts.Count, Is.EqualTo(3));
                if (profile == ResourceSpecialization.Balanced) Assert.That(counts.Values.Max() - counts.Values.Min(), Is.LessThanOrEqualTo(1));
                else Assert.That(counts[(ResourceType)((int)profile - (int)ResourceSpecialization.Human)], Is.EqualTo(budget.FarTarget - 2));
                AssertSpacing(plan.Near.SelectMany(n => n).Select(p => p.Hex).Concat(plan.Far.Hexes).ToList());
                foreach (var c in citadels)
                {
                    var costs = HexPathfinder.FindCosts(_map, new[] { c });
                    Assert.That(plan.Far.Points.All(p => costs.ContainsKey(p.Hex)), Is.True);
                }
                Assert.That(_map.AllCoords.All(h => HexResourceBonusRegistry.GetBonus(h) == null), Is.True);
            }
        }

        [Test] public void SingletonTypeAccessibilityImprovesAfterTypedAssignment()
        {
            Build(6, h => h.Q < 0 ? 3 : 1);
            var citadels = Citadels(6, 2);
            var fields = citadels.Select(c => HexPathfinder.FindCosts(_map, new[] { c })).ToList();
            double typed = 0, shuffled = 0;
            var random = new System.Random(42);
            for (int seed = 0; seed < 80; seed++)
            {
                Random.InitState(seed);
                var plan = MatchResourcePlacement.PlanFar(_map, citadels, new List<HexCoord>(),
                    new MatchResourceBudget(5, 2, ResourceSpecialization.Materials));
                var rare = plan.Points.Where(p => p.Type != ResourceType.Materials).Select(p => p.Hex).ToList();
                typed += rare.Sum(h => System.Math.Abs(fields[0][h] - fields[1][h]));
                shuffled += plan.Points.OrderBy(_ => random.Next()).Take(2).Sum(p => System.Math.Abs(fields[0][p.Hex] - fields[1][p.Hex]));
            }
            System.Console.WriteLine($"Rare-type access gap typed={typed / 160:F3}, shuffled={shuffled / 160:F3}");
            Assert.That(typed, Is.LessThan(shuffled), "typed plans must reduce rare-resource approach imbalance");
        }

        [Test] public void JointPlannerRetainsCompleteRichLayoutWhenIndependentFarQualityWouldBlockNearBands()
        {
            // Actual Desert seed-1 obstacle footprint. R5 with four starts one ring inward
            // has full legal layouts, but independently selecting high-quality far shapes
            // before checking near feasibility can repeatedly return only five far points.
            var blocked = new HashSet<HexCoord> { new HexCoord(-3, 0), new HexCoord(-2, 3),
                new HexCoord(2, 0), new HexCoord(2, 1), new HexCoord(3, -1) };
            Build(5, blocked: blocked.Contains);
            for (int seed = 0; seed < 16; seed++)
            {
                Random.InitState(seed);
                var budget = MapGenerationSettings.ResourceBudget(4, 5,
                    new RegionResourceConditions(ResourceSpecialization.Human, ResourceRichness.Rich));
                var layout = MatchResourcePlacement.Plan(_map, Citadels(4, 4), budget);
                Assert.That(layout.Near.All(n => n.Count == 3), Is.True);
                Assert.That(layout.Far.Points.Count, Is.EqualTo(6), $"seed {seed}");
                AssertSpacing(layout.Near.SelectMany(n => n).Select(p => p.Hex).Concat(layout.Far.Hexes).ToList());
            }
        }

        [Test] public void ActualStartGeometryCanLimitAnOrdinarySmallFourPlayerMapToThreeFarPoints()
        {
            var citadels = new List<HexCoord> { new HexCoord(-2, 5), new HexCoord(-3, -1),
                new HexCoord(2, -4), new HexCoord(3, 1) };
            var allowed = new HashSet<HexCoord> { new HexCoord(-5, 3), new HexCoord(-5, 4), new HexCoord(-4, 3),
                new HexCoord(-1, 1), new HexCoord(0, 0), new HexCoord(5, -3) };
            Build(5, blocked: h => citadels.All(c => HexGridMath.Distance(c, h) >= 4) && !allowed.Contains(h));
            // Exhaustive upper bound on this six-cell witness, independent of planner order.
            var points = allowed.ToArray(); int maximum = 0;
            for (int mask = 0; mask < (1 << points.Length); mask++)
            {
                var subset = Enumerable.Range(0, points.Length).Where(i => (mask & (1 << i)) != 0).ToList();
                bool legal = subset.All(i => subset.All(j => i == j || HexGridMath.Distance(points[i], points[j]) >= 2));
                if (legal) maximum = System.Math.Max(maximum, subset.Count);
            }
            Assert.AreEqual(3, maximum);
            Random.InitState(2);
            var budget = MapGenerationSettings.ResourceBudget(4, 5);
            Assert.AreEqual(5, budget.FarTarget, "the ordinary budget must not be silently reduced");
            var layout = MatchResourcePlacement.Plan(_map, citadels, budget);
            Assert.IsTrue(layout.Near.All(n => n.Count == 3));
            Assert.AreEqual(maximum, layout.Far.Points.Count);
            Assert.AreEqual(3, layout.Far.Points.Select(p => p.Type).Distinct().Count());
            Assert.AreEqual(3, layout.Far.TechHexes.Count);
            AssertSpacing(layout.Near.SelectMany(n => n).Select(p => p.Hex).Concat(layout.Far.Hexes).ToList());
        }

        [Test] public void PartialPlanRebuildsTypesForActualCountInsteadOfTruncatingQuotas()
        {
            Build(6, blocked: h => h.R != 0 && h.R != 1 && h.R != -1);
            Random.InitState(14);
            var budget = new MatchResourceBudget(50, 2, ResourceSpecialization.Materials);
            var far = MatchResourcePlacement.PlanFar(_map, Citadels(6, 2), new List<HexCoord>(), budget);
            Assert.That(far.Points.Count, Is.InRange(3, 49));
            Assert.That(far.Points.Select(p => p.Type).Distinct().Count(), Is.EqualTo(3));
            Assert.That(far.Points.Count(p => p.Type == ResourceType.Materials), Is.EqualTo(far.Points.Count - 2));
        }

        [Test] public void NeutralProposalsImproveCoverageSpacingAndStartBalanceAtTheSameCount()
        {
            var all = HexGridMath.HexesInRange(new HexCoord(0, 0), 6).ToList();
            var citadels = Citadels(6, 2); var ruins = new List<HexCoord> { new HexCoord(0, 3) };
            var candidates = all.Where(h => citadels.Concat(ruins).All(c => HexGridMath.Distance(c, h) >= 2)).ToList();
            double spacing = 0, oldSpacing = 0, coverage = 0, oldCoverage = 0, imbalance = 0, oldImbalance = 0;
            var layouts = new HashSet<string>();
            for (int seed = 0; seed < 80; seed++)
            {
                Random.InitState(seed);
                var plan = CitadelSetupController.PlanNeutralArmyHexes(candidates, citadels, ruins, 6, 6);
                Assert.That(plan.Count, Is.EqualTo(6)); AssertSpacing(plan);
                Assert.That(plan.All(candidates.Contains), Is.True);
                layouts.Add(string.Join(";", plan.OrderBy(h => h.Q).ThenBy(h => h.R)));
                // Historical random sequential algorithm, reference for distribution only.
                Random.InitState(seed); var old = new List<HexCoord>();
                while (old.Count < 6)
                {
                    var pool = candidates.Where(h => old.All(p => HexGridMath.Distance(p, h) >= 2)).ToList();
                    if (pool.Count == 0) break;
                    old.Add(pool[Random.Range(0, pool.Count)]);
                }
                Assert.That(old.Count, Is.EqualTo(plan.Count));
                spacing += MeanNearest(plan); oldSpacing += MeanNearest(old);
                coverage += candidates.Average(h => plan.Min(p => HexGridMath.Distance(h, p)));
                oldCoverage += candidates.Average(h => old.Min(p => HexGridMath.Distance(h, p)));
                imbalance += NeutralPressureGap(plan, citadels); oldImbalance += NeutralPressureGap(old, citadels);
            }
            System.Console.WriteLine($"Neutral mean nearest: {spacing / 80:F3} vs {oldSpacing / 80:F3}; coverage distance {coverage / 80:F3} vs {oldCoverage / 80:F3}; start gap {imbalance / 80:F3} vs {oldImbalance / 80:F3}; {layouts.Count}/80 layouts");
            Assert.Greater(spacing, oldSpacing); Assert.Less(coverage, oldCoverage); Assert.Less(imbalance, oldImbalance);
            Assert.Greater(layouts.Count, 50);
        }
        private static double MeanNearest(List<HexCoord> points) => points.Average(h => points.Where(p => !p.Equals(h)).Min(p => HexGridMath.Distance(h, p)));
        private static double NeutralPressureGap(List<HexCoord> points, List<HexCoord> starts)
        {
            var pressure = starts.Select(c => points.Sum(h => 1.0 / HexGridMath.Distance(c, h))).ToArray();
            return (pressure.Max() - pressure.Min()) / pressure.Max();
        }

#if TERRAIN_MANAGED_HARNESS
        [Test] public void BonusWritePublishesAfterCommitAndPlanningDoesNotPublish()
        {
            Build(6); var hex = new HexCoord(0, 0); int notifications = 0;
            void Observed(HexCoord changed)
            {
                notifications++; Assert.AreEqual(hex, changed);
                Assert.AreEqual(1, HexResourceBonusRegistry.GetBonus(changed).tech);
            }
            VisionSystem.ContentChangedForTest += Observed;
            try
            {
                HexResourceBonusRegistry.Set(hex, new ResourceYields { tech = 1 });
                Assert.AreEqual(1, notifications);
                MatchResourcePlacement.Plan(_map, Citadels(6, 2), MapGenerationSettings.ResourceBudget(2, 6));
                Assert.AreEqual(1, notifications, "rejected/sample plans must never publish content");
                HexResourceBonusRegistry.Clear(); Assert.AreEqual(1, notifications, "world reset is silent");
            }
            finally { VisionSystem.ContentChangedForTest -= Observed; }
        }
#endif

#if !TERRAIN_MANAGED_HARNESS
        [Test] public void ResourceWriteRefreshesVisibleKnowledgeWithoutVisionChangeOrHiddenLeak()
        {
            Build(6); VisionSystem.Clear(); Game.Ai.AiMapMemory.Clear();
            ArmyRegistry.Clear(); BuildingRegistry.Clear();
            var observer = new Game.Players.PlayerSetupData { Nickname = "resource observer" };
            var unrelated = new Game.Players.PlayerSetupData { Nickname = "unrelated" };
            var hex = new HexCoord(0, 0); var hidden = new HexCoord(4, 0);
            Game.Ai.AiMapMemory.EnsureSubscribed(_map);
            var visible = (Dictionary<Game.Players.PlayerSetupData, HashSet<HexCoord>>)typeof(VisionSystem)
                .GetField("Visible", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
            visible[observer] = new HashSet<HexCoord> { hex };
            visible[unrelated] = new HashSet<HexCoord> { new HexCoord(-4, 0) };
            int visionChanges = 0;
            void VisionChanged(Game.Players.PlayerSetupData player) => visionChanges++;
            VisionSystem.VisibilityChanged += VisionChanged;
            try
            {
                int before = Game.Ai.AiMapMemory.KnowledgeVersionFor(observer);
                int unrelatedBefore = Game.Ai.AiMapMemory.KnowledgeVersionFor(unrelated);
                HexResourceBonusRegistry.Set(hex, new ResourceYields { materials = 1, tech = 1 });
                var known = Game.Ai.AiMapMemory.AllKnownResourceHexes(observer).Single();
                Assert.AreEqual(hex, known.Hex); Assert.AreEqual(1, known.Yield.materials); Assert.AreEqual(1, known.Yield.tech);
                Assert.Greater(Game.Ai.AiMapMemory.KnowledgeVersionFor(observer), before);
                int observed = Game.Ai.AiMapMemory.KnowledgeVersionFor(observer);
                HexResourceBonusRegistry.Set(hidden, new ResourceYields { human = 1 });
                Assert.AreEqual(observed, Game.Ai.AiMapMemory.KnowledgeVersionFor(observer));
                Assert.IsFalse(Game.Ai.AiMapMemory.AllKnownResourceHexes(observer).Any(r => r.Hex.Equals(hidden)));
                Assert.AreEqual(unrelatedBefore, Game.Ai.AiMapMemory.KnowledgeVersionFor(unrelated));
                Assert.IsEmpty(Game.Ai.AiMapMemory.AllKnownResourceHexes(unrelated));
                HexResourceBonusRegistry.Set(hex, new ResourceYields { materials = 1, tech = 1 });
                Assert.AreEqual(observed, Game.Ai.AiMapMemory.KnowledgeVersionFor(observer), "same fact must not invalidate knowledge twice");
                Assert.AreEqual(0, visionChanges); CollectionAssert.AreEquivalent(new[] { hex }, visible[observer]);
            }
            finally
            {
                VisionSystem.VisibilityChanged -= VisionChanged;
                VisionSystem.Clear(); Game.Ai.AiMapMemory.Clear(); ArmyRegistry.Clear(); BuildingRegistry.Clear();
            }
        }

        [Test] public void ResourcePassStampsExactRegionalPlanAndPreservesCitadelYields()
        {
            Build(6);
            Random.InitState(47);
            Game.Core.GameSession.ClearMapConditions();
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
                Game.Core.GameSession.SelectedResourceConditions = new RegionResourceConditions(ResourceSpecialization.Materials, ResourceRichness.Rich);
                var before = Random.state;
                var expected = MatchResourcePlacement.Plan(_map, citadels,
                    MapGenerationSettings.ResourceBudget(2, 6, Game.Core.GameSession.SelectedResourceConditions));
                Random.state = before;
                typeof(CitadelSetupController).GetMethod("GenerateResources", flags).Invoke(controller, null);
                foreach (var point in expected.Near.SelectMany(n => n).Concat(expected.Far.Points))
                {
                    var y = HexResourceBonusRegistry.GetBonus(point.Hex);
                    Assert.That(y, Is.Not.Null);
                    Assert.That(new[] { y.human, y.energy, y.materials, y.tech }, Is.EqualTo(new[] {
                        point.Yields.human, point.Yields.energy, point.Yields.materials, point.Yields.tech }));
                }
                var far = _map.AllCoords.Where(h => citadels.All(c => HexGridMath.Distance(c, h) >= 4)
                    && HexResourceBonusRegistry.GetBonus(h) != null).ToList();
                Assert.That(far.Count, Is.EqualTo(5));
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
                Assert.That(ordinary, Is.EqualTo(new[] { 1, 1, 3 }));
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
            finally { Game.Core.GameSession.ClearMapConditions(); Object.DestroyImmediate(config); }
        }
#endif
    }
}
#endif
