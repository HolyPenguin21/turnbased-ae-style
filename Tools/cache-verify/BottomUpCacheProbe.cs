// Diagnostics only; deliberately outside Assets. Reflection is preparation/dry-run only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class BottomUpCacheProbe
    {
        private static float sink;
        private const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private static void Measure(string name, int count, Func<int, Action> prepare) =>
            CachePerformanceProbe.Measure("bottomup/" + name, count, prepare, true);

        [Test]
        public void MeasureLayersAndRealCallers()
        {
            Measure("control/delegate-store", 1000000, _ => () => sink = 1);
            MeasureResourceLayers();
            MeasureCombatLayers();
            MeasureAdmissionKeys();
            MeasureRouteLayers();
            TraceSupplyCapacity(64);
            TraceSupplyCapacity(65);
            TraceSupplyCapacity(63, occasionalNew: true);
            GC.KeepAlive(sink);
        }

        private sealed class Counted<T> : IReadOnlyList<T>
        {
            private readonly IReadOnlyList<T> values;
            internal int CountReads, IndexReads;
            public Counted(IReadOnlyList<T> values) => this.values = values;
            public int Count { get { CountReads++; return values.Count; } }
            public T this[int index] { get { IndexReads++; return values[index]; } }
            public IEnumerator<T> GetEnumerator() => values.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static void MeasureResourceLayers()
        {
            var deck = CachePerformanceProbe.Deck(64).ToList();
            var snap = CachePerformanceProbe.Snapshot(deck.ToArray());
            snap.Self.Deck = deck;
            snap.Self.Hand = CachePerformanceProbe.Deck(16).Select(d => new CardData(d)).ToList();
            var bill = new ResourceCost(1, 1, 1, 1);
            ActionPrice.ScarcityProfile(snap);
            Measure("scarcity/list-hand16-deck64/hit", 30000,
                _ => () => sink = ActionPrice.ScarcityProfile(snap)[0]);
            Measure("resources/cost-overload/no-callback", 30000,
                _ => () => sink = ActionPrice.Resources(bill, snap));
            var bare = CachePerformanceProbe.Snapshot(deck.ToArray());
            ActionPrice.ScarcityProfile(bare);
            Measure("scarcity/deck64/raw-hit", 30000,
                _ => () => sink = ActionPrice.ScarcityProfile(bare)[0]);
            foreach (bool occasionalNew in new[] { false, true })
                Measure("scarcity/hot63/rare-new=" + occasionalNew, 6000, _ =>
                {
                    var world = CachePerformanceProbe.Snapshot(deck.ToArray());
                    float value = 0;
                    Func<ResourceType, float> available = _ => value;
                    for (int i = 0; i < 63; i++)
                    {
                        value = i;
                        ActionPrice.ScarcityProfile(world, available);
                    }
                    int call = 0;
                    return () =>
                    {
                        value = occasionalNew && call % 64 == 63 ? 1000 + call : call % 63;
                        call++;
                        sink = ActionPrice.ScarcityProfile(world, available)[0];
                    };
                });

            var countedHand = new Counted<CardData>(snap.Self.Hand);
            var countedDeck = new Counted<CardDefinition>(deck);
            snap.Self.Hand = countedHand; snap.Self.Deck = countedDeck;
            ActionPrice.ScarcityProfile(snap);
            countedHand.CountReads = countedHand.IndexReads = countedDeck.CountReads = countedDeck.IndexReads = 0;
            ActionPrice.ScarcityProfile(snap);
            TestContext.WriteLine($"CACHE_READS|one-warm-profile|hand-count={countedHand.CountReads}|hand-index={countedHand.IndexReads}|deck-count={countedDeck.CountReads}|deck-index={countedDeck.IndexReads}");

            var roots = new List<PlayerRoot>();
            try
            {
                foreach (int holds in new[] { 0, 16, 64 })
                {
                    Func<int, Action> Prepare(bool cardCaller) => count =>
                    {
                        StrategicResourceReservationLedger.ClearAll();
                        var player = new PlayerSetupData();
                        var root = PlayerRoot.Create(player, "bottom-up-resource");
                        roots.Add(root);
                        foreach (ResourceType type in ResourceBundle.All) root.AddResource(type, 30);
                        var context = new AiTurnContext { TurnNumber = 1 };
                        for (int i = 0; i < holds; i++)
                            StrategicResourceReservationLedger.Upsert(player, 1, new StrategicResourceReservation
                            {
                                Identity = ReservationOwner.ForPass("probe-" + i),
                                Resource = (StrategicReservedResource)(1 + i % 4), Amount = 1,
                                Reason = StrategicReservationReason.StrategicReactionPass,
                                ExpirationStage = StrategicReservationExpiry.EndOfTurn,
                            });
                        var world = CachePerformanceProbe.Snapshot(deck.ToArray());
                        Func<ResourceType, float> available = type => StrategicSpendability.SpendableAmount(player, root, context, type);
                        ActionPrice.ScarcityProfile(world, available);
                        if (count == 32)
                        {
                            int reads = 0;
                            Func<ResourceType, float> counted = type => { reads++; return available(type); };
                            if (cardCaller) StrategicCardEvaluator.StrategicResourceCostValue(bill, world, counted, player);
                            else ActionPrice.ScarcityProfile(world, counted);
                            TestContext.WriteLine($"CACHE_READS|live-holds={holds}|card-caller={cardCaller}|spendable-callbacks={reads}");
                        }
                        if (cardCaller)
                            return () => sink = StrategicCardEvaluator.StrategicResourceCostValue(bill, world, available, player);
                        return () => sink = ActionPrice.ScarcityProfile(world, available)[0];
                    };
                    Measure("scarcity/live-spendable/holds=" + holds, 5000, Prepare(false));
                    Measure("card-resource-value/live-spendable/holds=" + holds, 5000, Prepare(true));
                }
            }
            finally
            {
                StrategicResourceReservationLedger.ClearAll();
                foreach (var root in roots) UnityEngine.Object.DestroyImmediate(root.gameObject);
            }
        }

        private static void MeasureCombatLayers()
        {
            var attackers = new[] { CachePerformanceProbe.Unit(3, 2, 4), CachePerformanceProbe.Unit(2, 2, 3), CachePerformanceProbe.Unit(4, 1, 3) };
            var defenders = new[] { CachePerformanceProbe.Unit(2, 3, 4), CachePerformanceProbe.Unit(3, 2, 3) };
            var legacyKey = typeof(AviationCombatEstimator).GetMethod("KeyString", HiddenStatic);
            try
            {
                if (legacyKey != null)
                {
                    var keyString = (Func<int[], string>)Delegate.CreateDelegate(typeof(Func<int[], string>), legacyKey);
                    var key = AviationCombatEstimator.BuildKey(attackers, defenders, default, 1, 0, AbilityMagnitudes.Default);
                    Measure("aviation/build-numeric-key/3v2", 20000, _ => () => sink =
                        AviationCombatEstimator.BuildKey(attackers, defenders, default, 1, 0, AbilityMagnitudes.Default).Length);
                    Measure("aviation/key-string/prebuilt-numeric-key", 20000,
                        _ => () => sink = keyString(key).Length);
                    Measure("aviation/dictionary-only/prebuilt-string", 1000000, _ =>
                    {
                        WorthIt.BeginEstimateCacheScope();
                        AviationCombatEstimator.EstimateAirStrike(attackers, defenders, default);
                        var cache = (IDictionary<string, AviationCombatEstimator.AirStrikeEstimate>)typeof(AviationCombatEstimator)
                            .GetField("Cache", HiddenStatic).GetValue(null);
                        string text = keyString(key);
                        Assert.That(cache.ContainsKey(text), Is.True);
                        return () => sink = cache.TryGetValue(text, out var found) ? 1 : 0;
                    });
                }
                Measure("aviation/3v2/warm", 5000, _ =>
                {
                    WorthIt.BeginEstimateCacheScope();
                    AviationCombatEstimator.EstimateAirStrike(attackers, defenders, default);
                    return () => sink = AviationCombatEstimator.EstimateAirStrike(attackers, defenders, default).ExpectedDamage;
                });
                foreach (bool timing in new[] { false, true })
                {
                    var armies = new[] { new WorthIt.DefendingArmy(defenders, default), new WorthIt.DefendingArmy(defenders, default) };
                    Measure("worthit/3v-two-armies/warm/timing=" + timing, 3000, _ =>
                    {
                        WorthIt.BeginEstimateCacheScope();
                        if (timing) ScopeTiming.Begin();
                        WorthIt.EstimateSequential(attackers, default, armies, 0);
                        return () => sink = WorthIt.EstimateSequential(attackers, default, armies, 0).WinChance;
                    });
                    var stats = WorthIt.CurrentEstimateCacheStats;
                    TestContext.WriteLine($"CACHE_COUNTS|multi-army/timing={timing}|hits={stats.Hits}|misses={stats.Misses}|entries={stats.Entries}");
                    ScopeTiming.Take(0);
                }
                var twoArmies = new[] { new WorthIt.DefendingArmy(defenders, default), new WorthIt.DefendingArmy(defenders, default) };
                Measure("worthit/3v-two-armies/uncached", 64, _ =>
                {
                    WorthIt.EndEstimateCacheScope();
                    return () => sink = WorthIt.EstimateSequential(attackers, default, twoArmies, 0).WinChance;
                });
                var groups = new[] { new AviationCombatEstimator.DefendingAirArmy(1, defenders, null),
                    new AviationCombatEstimator.DefendingAirArmy(2, defenders, null) };
                Measure("aviation/3v-two-armies/warm", 3000, _ =>
                {
                    WorthIt.BeginEstimateCacheScope();
                    AviationCombatEstimator.EstimateAirStrikeAgainstArmies(attackers, groups, default);
                    return () => { AviationCombatEstimator.EstimateAirStrikeAgainstArmies(attackers, groups, default); };
                });
                Measure("aviation/3v-two-armies/uncached", 64, _ =>
                {
                    WorthIt.EndEstimateCacheScope();
                    return () => { AviationCombatEstimator.EstimateAirStrikeAgainstArmies(attackers, groups, default); };
                });
                foreach (int size in new[] { 3, 12 })
                {
                    var own = Enumerable.Repeat(attackers[0], size).ToArray();
                    var enemy = new[] { new WorthIt.DefendingArmy(Enumerable.Repeat(defenders[0], size).ToArray(), default) };
                    Measure("worthit/warm/roster=" + size, 5000, _ =>
                    {
                        WorthIt.BeginEstimateCacheScope();
                        WorthIt.EstimateSequential(own, default, enemy, 0);
                        return () => sink = WorthIt.EstimateSequential(own, default, enemy, 0).WinChance;
                    });
                }
            }
            finally { ScopeTiming.Take(0); WorthIt.EndEstimateCacheScope(); }
        }

        private static HexMap NewMap()
        {
            return new UnityEngine.GameObject("bottom-up-map").AddComponent<HexMap>();
        }

        private static void MeasureAdmissionKeys()
        {
            // Snapshot-side key cost only. No live recipients, air sightings, facilities or
            // active missions are registered; this is not a complete Development enumeration.
            var player = new PlayerSetupData();
            foreach (var (armies, withGeometry) in new[] { (0, false), (6, false), (6, true) })
            {
                string scenario = "snapshot-armies=" + armies + (withGeometry ? "/469-cells" : "");
                var snap = CachePerformanceProbe.Snapshot(CachePerformanceProbe.Deck(64));
                snap.Observer = player;
                snap.TurnNumber = 1;
                var profiles = Enumerable.Repeat(CachePerformanceProbe.Unit(3, 2, 4), 12).ToArray();
                snap.Self.Armies = Enumerable.Range(0, armies).Select(i => new ArmySnapshot
                {
                    ArmyId = i + 1, MemberCount = 12, Members = profiles, MembersWithHeroes = profiles,
                    Hex = new HexCoord(i, 0), CurrentMovement = 3, ActivationApCost = 1,
                }).ToArray();
                if (withGeometry)
                    snap.MapKnowledge = new MapKnowledgeSnapshot
                    {
                        AllHexes = HexGridMath.HexesInRange(new HexCoord(0, 0), 12).ToArray(),
                    };
                string full = DevelopmentAdmission.RecipientVerdictKey(snap, Array.Empty<MissionIntent>(), player, null);
                string core = DevelopmentAdmission.RecipientDeltaCoreKey(snap, player, null);
                TestContext.WriteLine($"CACHE_KEY_SIZE|snapshot-armies={armies}|bodies-per-army=12|full-chars={full.Length}|core-chars={core.Length}");
                Measure("development/full-key/" + scenario, 2000,
                    _ => () => sink = DevelopmentAdmission.RecipientVerdictKey(snap, Array.Empty<MissionIntent>(), player, null).Length);
                Measure("development/core-key/" + scenario, 2000,
                    _ => () => sink = DevelopmentAdmission.RecipientDeltaCoreKey(snap, player, null).Length);
            }
        }

        private static void MeasureRouteLayers()
        {
            var map = NewMap();
            var terrain = HexGridMath.HexesInRange(new HexCoord(0, 0), 12)
                .ToDictionary(h => h, _ => new Game.Terrain.TerrainTypeEntry { moveCost = 1 });
            map.SetData(12, 1, terrain);
            var owner = new PlayerSetupData();
            var from = new HexCoord(-12, 0); var to = new HexCoord(12, 0);
            try
            {
                Assert.That(SafeStepPathing.FindSafePathCost(map, owner, from, to, 3), Is.EqualTo(24));
                var players = (IDictionary)typeof(SafeStepPathing).GetField("_playerCaches", HiddenStatic).GetValue(null);
                object playerCache = players[owner];
                var routes = (IDictionary)playerCache.GetType().GetField("Routes").GetValue(playerCache);
                foreach (bool timing in new[] { false, true })
                {
                    Measure("route/same-24-steps/cost-hit/timing=" + timing, 100000, _ =>
                    {
                        if (timing) ScopeTiming.Begin();
                        return () => sink = SafeStepPathing.FindSafePathCost(map, owner, from, to, 3);
                    });
                    ScopeTiming.Take(0);
                }
                Measure("route/same-24-steps/witness-hit", 30000,
                    _ => () => sink = SafeStepPathing.FindSafePath(map, owner, from, to, 3).Hexes.Count);
                Measure("route/same-24-steps/miss-with-route-clear", 3000,
                    _ => () => { routes.Clear(); sink = SafeStepPathing.FindSafePathCost(map, owner, from, to, 3); });
                Measure("path-grid/warm/loop-control-comparison", 1000000,
                    _ => () => sink = HexPathfinder.GridFor(map).Count);
                routes.Clear();
                foreach (HexCoord target in terrain.Keys.Take(128))
                    SafeStepPathing.FindSafePathCost(map, owner, from, target, 3);
                int before = routes.Count;
                Game.Setup.MatchStateInventory.EndMatch();
                object pinned = typeof(SafeStepPathing).GetField("_cacheMap", HiddenStatic).GetValue(null);
                TestContext.WriteLine($"CACHE_LIFETIME|route-before-end={before}|route-after-end={(players.Contains(owner) ? routes.Count : 0)}|static-players-after-end={players.Count}|map-still-pinned={ReferenceEquals(pinned, map)}");
            }
            finally { ScopeTiming.Take(0); UnityEngine.Object.DestroyImmediate(map.gameObject); }
        }

        private static void TraceSupplyCapacity(int workingSet, bool occasionalNew = false)
        {
            var snap = CachePerformanceProbe.Snapshot(CachePerformanceProbe.Deck(64));
            object table = typeof(ActionPrice).GetField("ProfileMemo", HiddenStatic).GetValue(null);
            object memo = table.GetType().GetMethod("GetOrCreateValue").Invoke(table, new object[] { snap });
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var profiles = memo as IDictionary ?? (IDictionary)(memo.GetType().GetField("Profiles", flags)
                ?? memo.GetType().GetField("profiles", flags)).GetValue(memo);
            MethodInfo of = typeof(ActionPrice).GetNestedType("ProfileKey", BindingFlags.NonPublic).GetMethod("Of", HiddenStatic);
            int hits = 0, misses = 0, clears = 0;
            float value = 0;
            Func<ResourceType, float> supply = _ => value;
            for (int i = 0; i < 1024; i++)
            {
                value = occasionalNew && i % 64 == 63 ? 1000 + i : i % workingSet;
                object key = of.GetParameters().Length == 1
                    ? of.Invoke(null, new object[] { new ResourceBundle { Human = value, Energy = value, Materials = value, Tech = value } })
                    : of.Invoke(null, new object[] { snap, new[] { value, value, value, value } });
                if (profiles.Contains(key)) hits++; else misses++;
                int before = profiles.Count;
                ActionPrice.ScarcityProfile(snap, supply);
                if (before == 64 && profiles.Count == 1) clears++;
            }
            TestContext.WriteLine($"CACHE_CAPACITY|set={workingSet}|occasional-new={occasionalNew}|calls=1024|hits={hits}|misses={misses}|clears={clears}|entries={profiles.Count}");
        }
    }
}
