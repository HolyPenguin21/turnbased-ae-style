// Compile together with the runtime sources and NUnit; outside Assets by design.
// Diagnostic timings have no pass/fail threshold and do not replace Unity profiling.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class CachePerformanceProbe
    {
        private static float sink;

        internal static CardDefinition[] Deck(int count) => Enumerable.Range(0, count)
            .Select(i => new CardDefinition { resourceCost = new ResourceCost(1 + i % 4,
                i % 3, i % 2, i % 5) }).ToArray();

        internal static WorldSnapshot Snapshot(CardDefinition[] deck) => new WorldSnapshot
        {
            Self = new SelfSnapshot { Deck = deck, Hand = Array.Empty<CardData>(),
                Stockpile = new ResourceBundle { Human = 30, Energy = 30, Materials = 30, Tech = 30 } }
        };

        internal static void Measure(string name, int iterations, Func<int, Action> prepare, bool windows = false)
        {
            Action warmup = prepare(32);
            for (int i = 0; i < 32; i++) warmup();
            var timings = new double[7];
            var bytes = new double[7];
            for (int sample = 0; sample < timings.Length; sample++)
            {
                Action action = prepare(iterations);
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                int gen0 = GC.CollectionCount(0);
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) action();
                timings[sample] = (Stopwatch.GetTimestamp() - start) * 1e6
                    / Stopwatch.Frequency / iterations;
                bytes[sample] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)iterations;
                if (windows)
                    TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "CACHE_SAMPLE|{0}|{1}|{2}|{3:F6}|{4:F2}|{5}", name, sample, iterations,
                        timings[sample], bytes[sample], GC.CollectionCount(0) - gen0));
            }
            Array.Sort(timings);
            Array.Sort(bytes);
            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "CACHE_BENCH|{0}|{1}|{2:F3}|{3:F1}", name, iterations, timings[3], bytes[3]));
        }

        [Test]
        public void MeasureManagedCaches()
        {
            // Seven independent windows; snapshots, profiles, delegates and priming are outside
            // each measured interval. Cold means first read of a previously prepared snapshot.
            TestContext.WriteLine("CACHE_BENCH|name|iterations|median_us_per_call|median_bytes_per_call");
            foreach (int count in new[] { 0, 16, 64, 256 })
            {
                CardDefinition[] deck = Deck(count);
                Measure("scarcity/warm/deck=" + count, 10000, _ =>
                {
                    var snap = Snapshot(deck);
                    ActionPrice.ScarcityProfile(snap);
                    return () => sink = ActionPrice.ScarcityProfile(snap)[0];
                });
                Measure("scarcity/cold/deck=" + count, 256, n =>
                {
                    var snapshots = Enumerable.Range(0, n).Select(_ => Snapshot(deck)).ToArray();
                    int index = 0;
                    return () => sink = ActionPrice.ScarcityProfile(snapshots[index++])[0];
                });
            }
            CardDefinition[] cards = Deck(64);
            Measure("scarcity/supply-churn/deck=64", 2000, _ =>
            {
                var snap = Snapshot(cards);
                float amount = 0;
                Func<Game.Economy.ResourceType, float> available = _ => amount;
                return () => { amount = (amount + 1) % 128; sink = ActionPrice.ScarcityProfile(snap, available)[0]; };
            });
            Measure("scarcity/content-churn/deck=64", 2000, _ =>
            {
                var deck = Deck(64);
                var snap = Snapshot(deck);
                return () => { deck[0].resourceCost.human = 1 + deck[0].resourceCost.human % 4;
                    sink = ActionPrice.ScarcityProfile(snap)[0]; };
            });
            Measure("scarcity/hot-set-with-occasional-new-supply/deck=64", 8000, _ =>
            {
                var snap = Snapshot(cards);
                float amount = 0;
                int call = 0;
                Func<Game.Economy.ResourceType, float> available = _ => amount;
                return () => { call++; amount = call % 17 == 0 ? 1000 + call : call % 16;
                    sink = ActionPrice.ScarcityProfile(snap, available)[0]; };
            });
            // Delta lookup overhead only: the fake compute is intentionally cheap. This is
            // not a benchmark of the expensive real equipment scoring body.
            int computed = 0;
            Measure("equipment-delta/warm/lookup-only", 10000, _ =>
            {
                EquipmentDeltaCache.Clear();
                computed = 0;
                var player = new Game.Players.PlayerSetupData();
                var snap = new WorldSnapshot { Observer = player, TurnNumber = 1 };
                var scope = new EquipmentDeltaCache.ScopeKeys { Player = player, Snap = snap,
                    FullKey = "benchmark-full", CoreKey = EquipmentDeltaCache.InternCore(player, 1, "benchmark-core") };
                var equipment = new CardDefinition();
                var recipient = new object();
                Func<string> stamp = () => "recipient";
                Func<EquipmentUpgradeValuation.EquipmentDelta> compute = () =>
                    new EquipmentUpgradeValuation.EquipmentDelta(++computed, 0);
                EquipmentDeltaCache.GetOrCompute(scope, snap, null, equipment, recipient, false, stamp, compute);
                return () => sink = EquipmentDeltaCache.GetOrCompute(scope, snap, null,
                    equipment, recipient, false, stamp, compute).Combat;
            });
            TestContext.WriteLine($"CACHE_COUNTS|equipment-delta/last-window|computations={computed}");
            EquipmentDeltaCache.Clear();
            try
            {
                var attackers = new[] { Unit(3, 2, 4), Unit(2, 2, 3), Unit(4, 1, 3) };
                var defenders = new[] { Unit(2, 3, 4), Unit(3, 2, 3) };
                var armies = new[] { new WorthIt.DefendingArmy(defenders, default) };
                Measure("worthit/warm/3v2", 2000, _ =>
                {
                    WorthIt.BeginEstimateCacheScope();
                    WorthIt.EstimateSequential(attackers, default, armies, 0);
                    return () => { WorthIt.EstimateSequential(attackers, default, armies, 0); };
                });
                var stats = WorthIt.CurrentEstimateCacheStats;
                TestContext.WriteLine($"CACHE_COUNTS|worthit/warm|hits={stats.Hits}|misses={stats.Misses}|entries={stats.Entries}");
                Measure("worthit/uncached/3v2", 64, _ =>
                {
                    WorthIt.EndEstimateCacheScope();
                    return () => { WorthIt.EstimateSequential(attackers, default, armies, 0); };
                });
                Measure("aviation/warm/3v2", 2000, _ =>
                {
                    WorthIt.BeginEstimateCacheScope();
                    AviationCombatEstimator.EstimateAirStrike(attackers, defenders, default);
                    return () => { AviationCombatEstimator.EstimateAirStrike(attackers, defenders, default); };
                });
                TestContext.WriteLine($"CACHE_COUNTS|aviation/warm|hits={AviationCombatEstimator.CacheHits}|misses={AviationCombatEstimator.CacheMisses}");
                Measure("aviation/uncached/3v2", 64, _ =>
                {
                    WorthIt.EndEstimateCacheScope();
                    return () => { AviationCombatEstimator.EstimateAirStrike(attackers, defenders, default); };
                });
            }
            finally { WorthIt.EndEstimateCacheScope(); }
            MeasurePathCaches();
            GC.KeepAlive(sink);
        }

        private static Game.Map.HexMap NewMap()
        {
            return new UnityEngine.GameObject("cache-benchmark-map").AddComponent<Game.Map.HexMap>();
        }

        private static void MeasurePathCaches()
        {
            var maps = new List<Game.Map.HexMap>();
            var terrain = Game.HexGrid.HexGridMath.HexesInRange(new Game.HexGrid.HexCoord(0, 0), 12)
                .ToDictionary(h => h, _ => new Game.Terrain.TerrainTypeEntry { moveCost = 1 });
            Game.Map.HexMap PrepareMap()
            {
                var map = NewMap();
                maps.Add(map);
                map.SetData(12, 1, terrain);
                return map;
            }
            try
            {
                Measure("path-grid/warm/469-cells", 10000, _ =>
                {
                    var map = PrepareMap();
                    Game.HexGrid.HexPathfinder.GridFor(map);
                    return () => sink = Game.HexGrid.HexPathfinder.GridFor(map).Count;
                });
                Measure("path-grid/cold/469-cells", 256, _ =>
                {
                    var map = PrepareMap();
                    // Alternating versions forces a rebuild; SetTerrainAt is preparation-only
                    // and is included in this interval (one existing-cell dictionary write).
                    var at = new Game.HexGrid.HexCoord(0, 0);
                    var value = terrain[at];
                    return () => { map.SetTerrainAt(at, value); sink = Game.HexGrid.HexPathfinder.GridFor(map).Count; };
                });
                Measure("safe-route/warm/cost-only/24-steps", 10000, _ =>
                {
                    var map = PrepareMap();
                    var player = new Game.Players.PlayerSetupData();
                    var from = new Game.HexGrid.HexCoord(-12, 0);
                    var to = new Game.HexGrid.HexCoord(12, 0);
                    Game.Ai.SafeStepPathing.FindSafePathCost(map, player, from, to, 3);
                    return () => sink = Game.Ai.SafeStepPathing.FindSafePathCost(map, player, from, to, 3);
                });
                Measure("safe-route/cold/cost-only/unique-endpoints", 256, n =>
                {
                    var map = PrepareMap();
                    var player = new Game.Players.PlayerSetupData();
                    var targets = terrain.Keys.Take(n).ToArray();
                    var from = new Game.HexGrid.HexCoord(-12, 0);
                    int at = 0;
                    return () => sink = Game.Ai.SafeStepPathing.FindSafePathCost(map, player, from, targets[at++], 3);
                });
            }
            finally
            {
                foreach (var map in maps) UnityEngine.Object.DestroyImmediate(map.gameObject);
            }
        }

        internal static WorthIt.DefenderProfile Unit(float attack, float defense, float hp) =>
            new WorthIt.DefenderProfile(defense, false, Array.Empty<UnitTypeTag>(), attack, hp,
                2, Array.Empty<string>(), hp);
    }
}
