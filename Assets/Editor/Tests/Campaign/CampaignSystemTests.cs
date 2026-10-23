#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Campaign;
using Game.Cards;
using Game.Players;
using Game.Terrain;
using NUnit.Framework;
using UnityEngine;

public sealed class CampaignSystemTests
{
    private string directory;
    [SetUp] public void Setup() { directory = Path.Combine(Path.GetTempPath(), "campaign-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); }
    [TearDown] public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private CampaignState Planet(int seed = 37, int count = 24) => new CampaignMapGenerator().Generate(seed, Faction.Ashen, new CampaignGenerationSettings { RegionCount = count });
    private static string Geometry(CampaignState s) => string.Join("|", s.Regions.Select(r => r.RegionId + ":" + string.Join(";", r.PolygonVertices.Select(p => p.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + p.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + ":" + string.Join(",", r.NeighborIds) + ":" + r.OwnerFaction));
    private CampaignController Controller(CampaignState s) => new CampaignController(s, new CampaignStore(directory));
    private static CampaignDeck Deck(int attack = 3, int command = 5)
    {
        var hero = new CardDefinition { authoredKey = "hero", cardType = CardType.Hero, commandRating = command, fate = 3, initiative = 3 };
        var unit = new CardDefinition { authoredKey = "unit", cardType = CardType.Unit, attack = attack, defenseRating = 3, hitPoints = 3, range = 2 };
        return new CampaignDeck("test", new[] { hero, unit, unit, unit, unit });
    }
    private static CampaignState HumanTurn(CampaignState s)
    { int index = s.TurnOrder.IndexOf(s.HumanFaction); s.CurrentTurnIndex = index; return s; }
    private static string Properties(CampaignState s) => string.Join("|", s.Regions.OrderBy(r => r.RegionId)
        .Select(r => $"{r.RegionId}:{r.Biome}:{r.Specialization}:{r.Richness}"));

    [Test] public void RegionPropertiesAreSeededDiverseAndIndependentOfOwnership()
    {
        var s = new CampaignMapGenerator().Generate(37, Faction.Ashen);
        Assert.AreEqual(Properties(s), Properties(new CampaignMapGenerator().Generate(37, Faction.Ashen)));
        Assert.AreNotEqual(Properties(s), Properties(new CampaignMapGenerator().Generate(38, Faction.Ashen)));
        Assert.AreEqual(2, s.Regions.Select(r => r.Biome).Distinct().Count());
        Assert.AreEqual(4, s.Regions.Select(r => r.Specialization).Distinct().Count());
        Assert.AreEqual(3, s.Regions.Select(r => r.Richness).Distinct().Count());
        var store = new CampaignStore(directory); store.Save(s);
        Assert.AreEqual(Properties(s), Properties(store.Load(out _)));
        var copy = s.Copy(); copy.Regions[0].OwnerFaction = Faction.IronConcord;
        Assert.AreEqual(Properties(s), Properties(copy));
        var small = Planet(37, 3);
        Assert.AreEqual(3, small.Regions.Select(r => r.Specialization).Distinct().Count());
    }

    private static (double same, double largest) ClusterMetrics(CampaignState state, int[] values)
    {
        var index = state.Regions.Select((r, i) => (r.RegionId, i)).ToDictionary(p => p.RegionId, p => p.i);
        double edges = 0, same = 0; int largest = 0;
        for (int i = 0; i < values.Length; i++)
            foreach (int n in state.Regions[i].NeighborIds) { edges++; if (values[i] == values[index[n]]) same++; }
        var seen = new HashSet<int>();
        for (int i = 0; i < values.Length; i++)
        {
            if (!seen.Add(i)) continue;
            var queue = new Queue<int>(); queue.Enqueue(i); int count = 0;
            while (queue.Count > 0)
            {
                int next = queue.Dequeue(); count++;
                foreach (int n in state.Regions[next].NeighborIds.Select(id => index[id]))
                    if (values[n] == values[next] && seen.Add(n)) queue.Enqueue(n);
            }
            largest = Math.Max(largest, count);
        }
        return (same / edges, largest);
    }

    [Test] public void GeographicZonesClusterMoreThanIndependentShufflesOfTheSameCounts()
    {
        double[] same = new double[3], randomSame = new double[3], largest = new double[3], randomLargest = new double[3];
        for (int seed = 0; seed < 24; seed++)
        {
            var state = new CampaignMapGenerator().Generate(seed, Faction.Ashen);
            int[][] fields = { state.Regions.Select(r => (int)r.Biome).ToArray(), state.Regions.Select(r => (int)r.Specialization).ToArray(), state.Regions.Select(r => (int)r.Richness).ToArray() };
            for (int f = 0; f < fields.Length; f++)
            {
                var m = ClusterMetrics(state, fields[f]); same[f] += m.same; largest[f] += m.largest;
                var rng = new System.Random(seed * 3 + f);
                for (int sample = 0; sample < 12; sample++)
                {
                    var shuffled = fields[f].OrderBy(_ => rng.Next()).ToArray(); var r = ClusterMetrics(state, shuffled);
                    randomSame[f] += r.same / 12; randomLargest[f] += r.largest / 12;
                }
            }
        }
        for (int f = 0; f < 3; f++)
        {
            System.Console.WriteLine($"Property {f}: same neighbors {same[f] / 24:F3} vs shuffle {randomSame[f] / 24:F3}; largest {largest[f] / 24:F2} vs {randomLargest[f] / 24:F2}");
            Assert.Greater(same[f], randomSame[f] + 24 * .08);
            Assert.Greater(largest[f], randomLargest[f]);
        }
    }

    [Test] public void ObsoleteCampaignPairResetsWithoutTouchingCollectionOrDecks()
    {
        var state = Planet(); state.SchemaVersion = 1;
        string file = Path.Combine(directory, "campaign-v1.json");
        File.WriteAllText(file, JsonUtility.ToJson(state)); File.WriteAllText(file + ".bak", JsonUtility.ToJson(state));
        string collection = Path.Combine(directory, "collection.json"); File.WriteAllText(collection, "keep deck data");
        var store = new CampaignStore(directory);
        Assert.IsNull(store.Load(out var notice)); Assert.IsNotEmpty(notice); Assert.IsFalse(store.Exists);
        Assert.AreEqual("keep deck data", File.ReadAllText(collection));
        var fresh = Planet(); store.Save(fresh); store.Save(fresh);
        File.WriteAllText(file + ".bak", JsonUtility.ToJson(state)); File.WriteAllText(file, "broken");
        Assert.IsNull(store.Load(out _)); Assert.IsFalse(store.Exists);
    }

    [Test] public void FutureSchemaIsPreservedBeforeReadingCurrentIdentityAndRegionShape()
    {
        string file = Path.Combine(directory, "campaign-v1.json");
        var store = new CampaignStore(directory); store.Save(Planet()); store.Save(Planet());
        const string future = "{\"SchemaVersion\":99,\"Regions\":{\"newShape\":true}}";
        File.WriteAllText(file, future);
        Assert.Throws<NotSupportedException>(() => store.Load(out _));
        Assert.AreEqual(future, File.ReadAllText(file));
        File.WriteAllText(file, "{\"SchemaVersion\":1}"); File.WriteAllText(file + ".bak", future);
        Assert.Throws<NotSupportedException>(() => store.Load(out _));
        Assert.AreEqual(future, File.ReadAllText(file + ".bak"));
        Assert.IsTrue(File.Exists(file), "obsolete cleanup must preserve a pair containing a future schema");
    }

    [Test] public void ObsoleteSchemaDoesNotRequireCurrentIdentityOrRegionShape()
    {
        string file = Path.Combine(directory, "campaign-v1.json");
        File.WriteAllText(file, "{\"SchemaVersion\":1,\"Regions\":{\"oldShape\":true}}");
        File.WriteAllText(file + ".bak", "{\"SchemaVersion\":1}");
        var store = new CampaignStore(directory);
        Assert.IsNull(store.Load(out var notice)); Assert.IsNotEmpty(notice);
        Assert.IsFalse(store.Exists);
    }

    [Test] public void ObsoletePrimaryCannotDeleteAValidCurrentBackup()
    {
        var state = Planet(); var store = new CampaignStore(directory); store.Save(state); store.Save(state);
        string file = Path.Combine(directory, "campaign-v1.json");
        string backup = File.ReadAllText(file + ".bak");
        var obsolete = state.Copy(); obsolete.SchemaVersion = 1;
        File.WriteAllText(file, JsonUtility.ToJson(obsolete));
        Assert.AreEqual(state.CampaignId, store.Load(out var notice).CampaignId);
        Assert.IsNotEmpty(notice); Assert.AreEqual(backup, File.ReadAllText(file + ".bak"));
        store.Save(state); Assert.AreEqual(state.CampaignId, store.Load(out _).CampaignId);
    }

    [Test] public void MissingOrInvalidRegionalValuesCannotLoadAsAridDefaults()
    {
        var state = Planet(); state.Regions[0].BiomeId = 0;
        Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(state));
        state = Planet(); state.Regions[0].Specialization = 0;
        Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(state));
        state = Planet(); state.Regions[0].Richness = (ResourceRichness)99;
        Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(state));
    }

    [Test] public void G00_DefaultCampaignUsesThirtyThreeRegions()
    {
        var s = new CampaignMapGenerator().Generate(37, Faction.Ashen);
        Assert.AreEqual(33, s.Regions.Count);
        CampaignGeometry.Validate(s.Regions, true);
        foreach (var faction in s.Factions)
            Assert.AreEqual(11, s.Regions.Count(r => r.OwnerFaction == faction.Faction));
    }
    [Test] public void G01_SeedReproducesGeometryAndInitialOwnership() => Assert.AreEqual(Geometry(Planet()), Geometry(Planet()));
    [Test] public void G02_DifferentSeedsChangeGeometry() => Assert.AreNotEqual(Geometry(Planet(11)), Geometry(Planet(12)));
    [Test] public void G13_BackgroundChoiceIsSeededAndUsesEveryVariant()
    {
        var ids = new HashSet<int>();
        for (int seed = 0; seed < 1000; seed++)
        {
            int id = CampaignMapGenerator.BackgroundForSeed(seed);
            Assert.That(id, Is.InRange(1, CampaignMapGenerator.BackgroundCount));
            Assert.AreEqual(id, CampaignMapGenerator.BackgroundForSeed(seed)); ids.Add(id);
        }
        Assert.AreEqual(CampaignMapGenerator.BackgroundCount, ids.Count);
        Assert.AreEqual(CampaignMapGenerator.BackgroundForSeed(37), Planet().BackgroundId);
    }
    [Test] public void BackgroundSurvivesSaveReloadAndTurnAdvance()
    {
        var s = Planet(); var store = new CampaignStore(directory); store.Save(s);
        var restored = store.Load(out _); Assert.AreEqual(s.BackgroundId, restored.BackgroundId);
        var controller = new CampaignController(restored, store); controller.EndTurn();
        Assert.AreEqual(s.BackgroundId, controller.Snapshot.BackgroundId);
        Assert.AreEqual(s.BackgroundId, store.Load(out _).BackgroundId);
    }
    [Test] public void OldSaveWithoutBackgroundFieldRetainsOriginalSurface()
    {
        var s = Planet();
        var json = System.Text.RegularExpressions.Regex.Replace(JsonUtility.ToJson(s), "\"BackgroundId\"\\s*:\\s*\\d+\\s*,", "");
        Assert.False(json.Contains("\"BackgroundId\""));
        var restored = CampaignState.Deserialize(json); Assert.AreEqual(0, restored.BackgroundId);
        CampaignStore.Validate(restored); Assert.AreEqual(Geometry(s), Geometry(restored));
    }
    [TestCase(-1)] [TestCase(11)] public void InvalidBackgroundIdsAreRejected(int id)
    { var s = Planet(); s.BackgroundId = id; Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(s)); }
    [TestCase(3)] [TestCase(7)] [TestCase(24)] [TestCase(31)] [TestCase(36)] [TestCase(48)]
    public void G03_RegionCountAndBalancedConnectedOwnership(int count)
    {
        var s = Planet(31, count); Assert.AreEqual(count, s.Regions.Count); CampaignGeometry.Validate(s.Regions, true);
        foreach (var f in s.Factions) Assert.That(s.Regions.Count(r => r.OwnerFaction == f.Faction), Is.InRange(count / 3, (count + 2) / 3));
    }
    [Test] public void G12_ThousandSeedsHaveValidSharedGeometryAndConnectedBalancedTerritories()
    { for (int seed = 0; seed < 1000; seed++) { var s = Planet(seed, 33); CampaignGeometry.Validate(s.Regions, true); } }
    [Test] public void InvalidGeometryAndAdjacencyAreRejected()
    {
        var s = Planet(); s.Regions[0].NeighborIds.Add(s.Regions[0].RegionId); Assert.Throws<InvalidDataException>(() => CampaignGeometry.Validate(s.Regions));
        s = Planet(); s.Regions[0].PolygonVertices[0] = new Vector2(float.NaN, 1); Assert.Throws<InvalidDataException>(() => CampaignGeometry.Validate(s.Regions));
        s = Planet(); s.Regions[0].NeighborIds.Clear(); Assert.Throws<InvalidDataException>(() => CampaignGeometry.Validate(s.Regions));
    }
    [Test] public void PolygonTouchingItsOwnNonAdjacentEdgeIsRejected()
    {
        // The notch at (.5, 0) touches the first edge; every vertex is distinct,
        // areas are positive and there is no strict edge crossing.
        var regions = new List<RegionState>
        {
            new RegionState { RegionId = 0, Name = "Pinched", OwnerFaction = Faction.Ashen,
                PolygonVertices = new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(.5f, 0), new Vector2(0, 1) }, NeighborIds = new List<int> { 1 } },
            new RegionState { RegionId = 1, Name = "Middle", OwnerFaction = Faction.IronConcord,
                PolygonVertices = new List<Vector2> { new Vector2(1, 0), new Vector2(1.5f, 0), new Vector2(1.5f, 1), new Vector2(1, 1) }, NeighborIds = new List<int> { 0, 2 } },
            new RegionState { RegionId = 2, Name = "Right", OwnerFaction = Faction.Vessels,
                PolygonVertices = new List<Vector2> { new Vector2(1.5f, 0), new Vector2(2, 0), new Vector2(2, 1), new Vector2(1.5f, 1) }, NeighborIds = new List<int> { 1 } }
        };
        Assert.Throws<InvalidDataException>(() => CampaignGeometry.Validate(regions));
    }
    [TestCase(1f)] [TestCase(.0001f)]
    public void CrossingDetectionDoesNotLoseShortEdges(float size)
    {
        Assert.IsTrue(CampaignGeometry.ProperIntersection(new Vector2(0, 0), new Vector2(size, size), new Vector2(0, size), new Vector2(size, 0)));
        Assert.IsFalse(CampaignGeometry.ProperIntersection(new Vector2(0, 0), new Vector2(size, 0), new Vector2(size, 0), new Vector2(size, size)));
    }
    [TestCase("early reward")] [TestCase("missing deck")] [TestCase("fast in Game")]
    public void ImpossiblePendingOperationPhaseIsRejected(string corruption)
    {
        var s = HumanTurn(Planet()); var c = Controller(s);
        var pair = CampaignRules.LegalAttacks(s).First(); c.BeginAttack(pair.Source, pair.Target, true);
        if (corruption == "missing deck") c.RegisterMatch("deck", "Human", "AI");
        var invalid = c.Snapshot;
        if (corruption == "early reward") invalid.PendingOperation.RewardAcknowledged = true;
        else if (corruption == "missing deck") invalid.PendingOperation.SelectedHumanDeckId = null;
        else
        {
            s = Planet(); var aiPair = s.Regions.SelectMany(r => r.NeighborIds.Select(id => (Source: r, Target: s.Regions.Find(x => x.RegionId == id))))
                .First(p => p.Source.OwnerFaction != s.HumanFaction && p.Target.OwnerFaction != s.HumanFaction && p.Source.OwnerFaction != p.Target.OwnerFaction);
            s.CurrentTurnIndex = s.TurnOrder.IndexOf(aiPair.Source.OwnerFaction); c = Controller(s);
            c.BeginAttack(aiPair.Source.RegionId, aiPair.Target.RegionId, false); invalid = c.Snapshot;
            invalid.Phase = CampaignPhase.BattleInProgress;
        }
        Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(invalid));
    }
    [TestCase("different outcome")] [TestCase("duplicate match")]
    public void CorruptHistoryReceiptIsRejected(string corruption)
    {
        var s = HumanTurn(Planet()); var c = Controller(s); var pair = CampaignRules.LegalAttacks(s).First();
        c.BeginAttack(pair.Source, pair.Target, true); c.RegisterMatch("deck", "Human", "AI"); var op = c.Snapshot.PendingOperation;
        c.RecordManualResult(s.CampaignId, op.OperationId, op.MatchId, CampaignOutcome.AttackerVictory); c.AcknowledgeReward(op.MatchId);
        var invalid = c.Snapshot;
        if (corruption == "different outcome") { invalid.BattleHistory[0].Outcome = CampaignOutcome.DefenderVictory; invalid.BattleHistory[0].Captured = false; }
        else
        {
            var receipt = JsonUtility.FromJson<CampaignBattleRecord>(JsonUtility.ToJson(invalid.BattleHistory[0]));
            receipt.OperationId = Guid.NewGuid().ToString("N"); invalid.BattleHistory.Add(receipt);
        }
        Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(invalid));
    }
    [Test] public void C01_C03_OnlyAdjacentEnemyOwnedByActiveFactionCanBeAttacked()
    {
        var s = HumanTurn(Planet()); var pairs = CampaignRules.LegalAttacks(s).ToList(); Assert.IsNotEmpty(pairs);
        foreach (var pair in pairs) Assert.IsTrue(s.Regions.Find(r => r.RegionId == pair.Source).NeighborIds.Contains(pair.Target));
        Assert.IsFalse(CampaignRules.CanAttack(s, s.HumanFaction, pairs[0].Source, pairs[0].Source));
        int distant = s.Regions.First(r => !s.Regions.Find(x => x.RegionId == pairs[0].Source).NeighborIds.Contains(r.RegionId)).RegionId;
        Assert.IsFalse(CampaignRules.CanAttack(s, s.HumanFaction, pairs[0].Source, distant));
        Assert.IsFalse(CampaignRules.CanAttack(s, s.Factions.First(f => f.Faction != s.HumanFaction).Faction, pairs[0].Source, pairs[0].Target));
    }
    [Test] public void C04_C05_OneTurnPerFactionRegardlessOfRegionCount()
    {
        var controller = Controller(Planet()); var first = controller.Snapshot; var visited = new List<Faction>();
        for (int i = 0; i < 3; i++) { visited.Add(controller.Snapshot.CurrentFaction); controller.EndTurn(); }
        Assert.AreEqual(3, visited.Distinct().Count()); Assert.AreEqual(first.RoundNumber + 1, controller.Snapshot.RoundNumber);
    }
    [TestCase(CampaignOutcome.AttackerVictory)] [TestCase(CampaignOutcome.DefenderVictory)] [TestCase(CampaignOutcome.Draw)]
    public void C06_C12_ResultIsAppliedOnceAfterRewardAcknowledgment(CampaignOutcome outcome)
    {
        var s = HumanTurn(Planet()); var c = Controller(s); var pair = CampaignRules.LegalAttacks(s).First(); var defender = s.Regions.Find(r => r.RegionId == pair.Target).OwnerFaction;
        c.BeginAttack(pair.Source, pair.Target, true); c.RegisterMatch("deck", "Human", "AI"); var op = c.Snapshot.PendingOperation;
        c.RecordManualResult(s.CampaignId, op.OperationId, op.MatchId, outcome); c.RecordManualResult(s.CampaignId, op.OperationId, op.MatchId, outcome);
        Assert.AreEqual(defender, c.Snapshot.Regions.Find(r => r.RegionId == pair.Target).OwnerFaction); Assert.AreEqual(0, c.Snapshot.BattleHistory.Count);
        c.AcknowledgeReward(op.MatchId); c.AcknowledgeReward(op.MatchId); c.ApplyResult();
        Assert.AreEqual(1, c.Snapshot.BattleHistory.Count); Assert.AreEqual(outcome == CampaignOutcome.AttackerVictory ? s.HumanFaction : defender, c.Snapshot.Regions.Find(r => r.RegionId == pair.Target).OwnerFaction);
        int index = c.Snapshot.CurrentTurnIndex; c.Continue(op.OperationId); var after = c.Snapshot; c.Continue(op.OperationId);
        Assert.AreEqual(after.CurrentTurnIndex, c.Snapshot.CurrentTurnIndex); Assert.AreEqual(after.RoundNumber, c.Snapshot.RoundNumber);
    }
    [Test] public void ResultIdentityAndConflictingResultAreRejected()
    {
        var s = HumanTurn(Planet()); var c = Controller(s); var pair = CampaignRules.LegalAttacks(s).First(); c.BeginAttack(pair.Source, pair.Target, true); c.RegisterMatch("d", "Human", "AI"); var op = c.Snapshot.PendingOperation;
        Assert.Throws<InvalidOperationException>(() => c.RecordManualResult(s.CampaignId, op.OperationId, Guid.NewGuid().ToString("N"), CampaignOutcome.AttackerVictory));
        c.RecordManualResult(s.CampaignId, op.OperationId, op.MatchId, CampaignOutcome.Draw);
        Assert.Throws<InvalidOperationException>(() => c.RecordManualResult(s.CampaignId, op.OperationId, op.MatchId, CampaignOutcome.AttackerVictory));
    }
    [Test] public void CompletedManualReceiptRemainsIdempotentAfterContinue()
    {
        var s = HumanTurn(Planet()); var c = Controller(s); var pair = CampaignRules.LegalAttacks(s).First();
        c.BeginAttack(pair.Source, pair.Target, true); c.RegisterMatch("deck", "Human", "AI"); var op = c.Snapshot.PendingOperation;
        c.RecordManualResult(s.CampaignId, op.OperationId, op.MatchId, CampaignOutcome.AttackerVictory); c.AcknowledgeReward(op.MatchId); c.Continue(op.OperationId);
        string before = JsonUtility.ToJson(c.Snapshot);
        Assert.DoesNotThrow(() => c.RecordManualResult(s.CampaignId, op.OperationId, op.MatchId, CampaignOutcome.AttackerVictory));
        Assert.AreEqual(before, JsonUtility.ToJson(c.Snapshot));
        Assert.Throws<InvalidOperationException>(() => c.RecordManualResult(s.CampaignId, op.OperationId, op.MatchId, CampaignOutcome.DefenderVictory));
    }
    [TestCase("loser")] [TestCase("context")] [TestCase("late")] [TestCase("draw")] [TestCase("normal")]
    public void MatchBridgeChecksAuthoritativeContextAndLateReplay(string corruption)
    {
        var s = HumanTurn(Planet()); var c = Controller(s); var pair = CampaignRules.LegalAttacks(s).First();
        c.BeginAttack(pair.Source, pair.Target, true); c.RegisterMatch("deck", "Human", "AI"); var op = c.Snapshot.PendingOperation;
        var priorController = CampaignMatchBridge.Controller;
        string priorMatch = Game.Core.GameSession.MatchId;
        var priorContext = Game.Core.GameSession.CampaignContext; var priorResult = Game.Core.GameSession.FinalResult;
        try
        {
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, c);
            Game.Core.GameSession.SetCampaignContext(new CampaignMatchContext { CampaignId = s.CampaignId, OperationId = op.OperationId, MatchId = op.MatchId,
                AttackerFaction = op.AttackerFaction, DefenderFaction = op.DefenderFaction, HumanFaction = s.HumanFaction, SourceRegionId = op.SourceRegionId,
                TargetRegionId = corruption == "context" ? op.SourceRegionId : op.TargetRegionId, SelectedHumanDeckId = "deck" });
            Game.Core.GameSession.CompleteMatch(new CompletedMatchResult(op.MatchId, corruption == "draw" ? Faction.None : op.AttackerFaction,
                corruption == "loser" ? op.AttackerFaction : op.DefenderFaction, corruption == "draw"));
            if (corruption == "normal")
            {
                typeof(Game.Core.GameSession).GetProperty("CampaignContext").SetValue(null, null);
                Assert.IsTrue(CampaignMatchBridge.RecordFinalResult(out _)); Assert.IsFalse(c.Snapshot.PendingOperation.ResultRecorded);
            }
            else if (corruption == "loser" || corruption == "context")
            { Assert.IsFalse(CampaignMatchBridge.RecordFinalResult(out _)); Assert.IsFalse(c.Snapshot.PendingOperation.ResultRecorded); }
            else
            {
                Assert.IsTrue(CampaignMatchBridge.RecordFinalResult(out _)); c.AcknowledgeReward(op.MatchId); c.Continue(op.OperationId);
                string before = JsonUtility.ToJson(c.Snapshot);
                Assert.IsTrue(CampaignMatchBridge.RecordFinalResult(out _)); Assert.AreEqual(before, JsonUtility.ToJson(c.Snapshot));
            }
        }
        finally
        {
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, priorController);
            typeof(Game.Core.GameSession).GetProperty("MatchId").SetValue(null, priorMatch);
            typeof(Game.Core.GameSession).GetProperty("CampaignContext").SetValue(null, priorContext);
            typeof(Game.Core.GameSession).GetProperty("FinalResult").SetValue(null, priorResult);
        }
    }

    [Test]
    public void LeavingTacticalMatchPreservesThePendingCampaignRetry()
    {
        var s = HumanTurn(Planet()); var c = Controller(s);
        var pair = CampaignRules.LegalAttacks(s).First();
        c.BeginAttack(pair.Source, pair.Target, true);
        c.RegisterMatch("deck", "Human", "AI");
        var op = c.Snapshot.PendingOperation;
        var priorController = CampaignMatchBridge.Controller;
        var session = typeof(Game.Core.GameSession).GetProperties(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)
            .Where(p => p.CanRead && p.CanWrite).ToDictionary(p => p, p => p.GetValue(null));
        string setupError = Game.Core.GameSession.SetupError;
        try
        {
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, c);
            Game.Core.GameSession.SetCampaignContext(new CampaignMatchContext { CampaignId = s.CampaignId,
                OperationId = op.OperationId, MatchId = op.MatchId });
            Assert.That(CampaignMatchBridge.ReturnScene(op.MatchId), Is.EqualTo(Game.Core.SceneNames.Campaign));
            string before = JsonUtility.ToJson(c.Snapshot);

            Game.Setup.MatchStateInventory.EndMatch();

            Assert.That(Game.Core.GameSession.CampaignContext, Is.Null);
            Assert.That(CampaignMatchBridge.Controller, Is.SameAs(c));
            Assert.That(JsonUtility.ToJson(c.Snapshot), Is.EqualTo(before));
            Assert.DoesNotThrow(() => c.RegisterMatch("deck", "Human", "AI"));
            Assert.That(c.Snapshot.PendingOperation.MatchId, Is.EqualTo(op.MatchId));
            Assert.That(c.Snapshot.PendingOperation.ResultRecorded, Is.False);
        }
        finally
        {
            foreach (var saved in session) saved.Key.SetValue(null, saved.Value);
            Game.Core.GameSession.SetupError = setupError;
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, priorController);
        }
    }
    [Test] public void CancelAttackDoesNotConsumeTurnAndDefenceCannotBeCancelled()
    {
        var s = HumanTurn(Planet()); var c = Controller(s); var pair = CampaignRules.LegalAttacks(s).First(); c.BeginAttack(pair.Source, pair.Target, true); c.CancelHumanAttack();
        Assert.IsNull(c.Snapshot.PendingOperation); Assert.AreEqual(s.CurrentTurnIndex, c.Snapshot.CurrentTurnIndex);
        s = Planet(); s.CurrentTurnIndex = s.TurnOrder.FindIndex(f => f != s.HumanFaction); c = Controller(s);
        pair = CampaignRules.LegalAttacks(s).First(p => s.Regions.Find(r => r.RegionId == p.Target).OwnerFaction == s.HumanFaction);
        c.BeginAttack(pair.Source, pair.Target, true); c.CancelHumanAttack(); Assert.IsNotNull(c.Snapshot.PendingOperation);
    }
    [Test] public void S01_S06_RoundOrderGeometryAndInterruptedMatchSurviveReload()
    {
        var s = HumanTurn(Planet()); var store = new CampaignStore(directory); store.Save(s); var c = new CampaignController(s, store); var pair = CampaignRules.LegalAttacks(s).First();
        c.BeginAttack(pair.Source, pair.Target, true); c.RegisterMatch("selected-deck", "Human", "AI"); var persisted = store.Load(out _);
        Assert.AreEqual(Geometry(s), Geometry(persisted)); CollectionAssert.AreEqual(s.TurnOrder, persisted.TurnOrder);
        Assert.AreEqual(CampaignPhase.BattleInProgress, persisted.Phase); Assert.IsFalse(persisted.PendingOperation.ResultRecorded);
        Assert.AreEqual("selected-deck", persisted.PendingOperation.SelectedHumanDeckId); Assert.AreEqual(s.CurrentTurnIndex, persisted.CurrentTurnIndex);
    }
    [TestCase(CampaignPhase.AwaitingFactionAction)] [TestCase(CampaignPhase.CampaignFinished)]
    public void NoOperationSurvivesNativeJsonCopyAndReload(CampaignPhase phase)
    {
        var s = HumanTurn(Planet());
        if (phase == CampaignPhase.CampaignFinished)
        {
            foreach (var r in s.Regions) r.OwnerFaction = s.HumanFaction;
            foreach (var f in s.Factions) f.Eliminated = f.Faction != s.HumanFaction;
            s.Phase = phase;
        }
        Assert.IsNull(s.Copy().PendingOperation);
        var store = new CampaignStore(directory); store.Save(s);
        var restored = store.Load(out _); Assert.IsNull(restored.PendingOperation); CampaignStore.Validate(restored);
        if (phase == CampaignPhase.AwaitingFactionAction) Assert.DoesNotThrow(() => Controller(restored).EndTurn());
    }
    [Test] public void ExistingUnitySaveWithEmptyInlineOperationLoadsWithoutReset()
    {
        var s = HumanTurn(Planet()); s.PendingOperation = new CampaignOperation();
        File.WriteAllText(Path.Combine(directory, "campaign-v1.json"), JsonUtility.ToJson(s));
        var restored = new CampaignStore(directory).Load(out var notice);
        Assert.IsNull(notice); Assert.IsNull(restored.PendingOperation);
        Assert.AreEqual(s.CampaignId, restored.CampaignId); Assert.AreEqual(Geometry(s), Geometry(restored));
        Assert.AreEqual(s.CurrentTurnIndex, restored.CurrentTurnIndex); CollectionAssert.AreEqual(s.TurnOrder, restored.TurnOrder);
    }
    [TestCase("id")] [TestCase("flag")] [TestCase("deck")] [TestCase("score")] [TestCase("region")]
    public void UnexpectedNonEmptyOperationIsNotSilentlyDiscarded(string field)
    {
        var s = HumanTurn(Planet()); s.PendingOperation = new CampaignOperation();
        if (field == "id") s.PendingOperation.OperationId = Guid.NewGuid().ToString("N");
        else if (field == "flag") s.PendingOperation.ResultRecorded = true;
        else if (field == "deck") s.PendingOperation.AttackerDeckName = "Unexpected deck";
        else if (field == "score") s.PendingOperation.AttackerScore = 1;
        else s.PendingOperation.TargetRegionId = 1;
        Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(s.Copy()));
    }
    [Test] public void BattlePhaseWithEmptyInlineOperationIsStillRejected()
    {
        var s = HumanTurn(Planet()); s.Phase = CampaignPhase.PreparingBattle; s.PendingOperation = new CampaignOperation();
        Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(s.Copy()));
    }
    [Test] public void AiOperationAndHistoryAcceptEmptyMatchIdFromUnityJson()
    {
        var s = Planet(); var pair = s.Regions.SelectMany(r => r.NeighborIds.Select(id => (Source: r, Target: s.Regions.Find(x => x.RegionId == id))))
            .First(p => p.Source.OwnerFaction != s.HumanFaction && p.Target.OwnerFaction != s.HumanFaction && p.Source.OwnerFaction != p.Target.OwnerFaction);
        s.CurrentTurnIndex = s.TurnOrder.IndexOf(pair.Source.OwnerFaction); var c = Controller(s);
        c.BeginAttack(pair.Source.RegionId, pair.Target.RegionId, false);
        var pending = c.Snapshot; pending.PendingOperation.MatchId = ""; CampaignStore.Validate(pending);
        c = Controller(pending); c.ResolveFast(Deck(), Deck(), new CampaignBattleResolver());
        var result = c.Snapshot; result.PendingOperation.MatchId = ""; result.BattleHistory[0].MatchId = "";
        CampaignStore.Validate(result); var id = result.PendingOperation.OperationId;
        c = Controller(result); c.Continue(id); Assert.IsNull(c.Snapshot.PendingOperation);
    }
    [Test] public void S07_S11_FastResultAndNotificationReplayWithoutReroll()
    {
        var s = Planet(); var pair = s.Regions.SelectMany(r => r.NeighborIds.Select(id => (Source: r, Target: s.Regions.Find(x => x.RegionId == id))))
            .First(p => p.Source.OwnerFaction != s.HumanFaction && p.Target.OwnerFaction != s.HumanFaction && p.Source.OwnerFaction != p.Target.OwnerFaction);
        s.CurrentTurnIndex = s.TurnOrder.IndexOf(pair.Source.OwnerFaction); var store = new CampaignStore(directory); var c = new CampaignController(s, store);
        c.BeginAttack(pair.Source.RegionId, pair.Target.RegionId, false); c.ResolveFast(Deck(), Deck(4), new CampaignBattleResolver());
        var result = c.Snapshot; var restored = store.Load(out _); Assert.AreEqual(CampaignPhase.ShowingResult, restored.Phase);
        Assert.AreEqual(result.PendingOperation.Roll, restored.PendingOperation.Roll); Assert.AreEqual(result.PendingNotification, restored.PendingNotification);
        var replay = new CampaignController(restored, store); Assert.Throws<InvalidOperationException>(() => replay.ResolveFast(Deck(), Deck(), new CampaignBattleResolver()));
        replay.ApplyResult(); Assert.AreEqual(1, replay.Snapshot.BattleHistory.Count);
    }
    [Test] public void S13_CorruptPrimaryRecoversBackupAndFutureSchemaIsNotDowngraded()
    {
        var s = Planet(); var store = new CampaignStore(directory); store.Save(s); store.Save(s); File.WriteAllText(Path.Combine(directory, "campaign-v1.json"), "broken");
        Assert.AreEqual(Geometry(s), Geometry(store.Load(out var notice))); Assert.IsNotNull(notice);
        s.SchemaVersion = 99; File.WriteAllText(Path.Combine(directory, "campaign-v1.json"), JsonUtility.ToJson(s)); Assert.Throws<NotSupportedException>(() => store.Load(out _));
    }
    [Test] public void FailedSaveDoesNotPublishOrConsumeAction()
    {
        var s = Planet(); string file = Path.Combine(directory, "file"); File.WriteAllText(file, "x"); var c = new CampaignController(s, new CampaignStore(file));
        Assert.Throws<IOException>(() => c.EndTurn()); Assert.AreEqual(s.CurrentTurnIndex, c.Snapshot.CurrentTurnIndex); Assert.AreEqual(s.RoundNumber, c.Snapshot.RoundNumber);
    }
    [TestCase(null, false)] [TestCase("", false)] [TestCase(null, true)] [TestCase("", true)]
    public void MissingMatchIdDoesNotRouteToCampaignOrAcknowledgeAnything(string matchId, bool aiPending)
    {
        var priorController = CampaignMatchBridge.Controller; var priorContext = Game.Core.GameSession.CampaignContext;
        try
        {
            CampaignController c = null;
            if (aiPending)
            {
                var s = Planet(); var pair = s.Regions.SelectMany(r => r.NeighborIds.Select(id => (Source: r, Target: s.Regions.Find(x => x.RegionId == id))))
                    .First(p => p.Source.OwnerFaction != s.HumanFaction && p.Target.OwnerFaction != s.HumanFaction && p.Source.OwnerFaction != p.Target.OwnerFaction);
                s.CurrentTurnIndex = s.TurnOrder.IndexOf(pair.Source.OwnerFaction); c = Controller(s);
                c.BeginAttack(pair.Source.RegionId, pair.Target.RegionId, false);
            }
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, c);
            typeof(Game.Core.GameSession).GetProperty("CampaignContext").SetValue(null, null);
            string before = c == null ? null : JsonUtility.ToJson(c.Snapshot);
            Assert.AreEqual(Game.Core.SceneNames.MainMenu, CampaignMatchBridge.ReturnScene(matchId));
            Assert.IsTrue(CampaignMatchBridge.RewardDismissed(matchId, out string error), error);
            Assert.AreEqual(before, c == null ? null : JsonUtility.ToJson(c.Snapshot));
        }
        finally
        {
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, priorController);
            typeof(Game.Core.GameSession).GetProperty("CampaignContext").SetValue(null, priorContext);
        }
    }
    [TestCase("manual")] [TestCase("AI")] [TestCase("test")]
    public void RewardAcknowledgedBeforeCaptureRecoversOnce(string mode)
    {
        var s = HumanTurn(Planet()); var pair = CampaignRules.LegalAttacks(s).First();
        if (mode == "AI")
        {
            var ai = s.Regions.SelectMany(r => r.NeighborIds.Select(id => (Source: r, Target: s.Regions.Find(x => x.RegionId == id))))
                .First(p => p.Source.OwnerFaction != s.HumanFaction && p.Target.OwnerFaction != s.HumanFaction && p.Source.OwnerFaction != p.Target.OwnerFaction);
            s.CurrentTurnIndex = s.TurnOrder.IndexOf(ai.Source.OwnerFaction); pair = (ai.Source.RegionId, ai.Target.RegionId);
        }
        var store = new CampaignStore(directory); var c = new CampaignController(s, store);
        c.BeginAttack(pair.Source, pair.Target, mode != "AI");
        if (mode == "manual")
        {
            c.RegisterMatch("deck", "Human", "AI"); var p = c.Snapshot.PendingOperation;
            c.RecordManualResult(s.CampaignId, p.OperationId, p.MatchId, CampaignOutcome.AttackerVictory);
        }
        var interrupted = c.Snapshot; var op = interrupted.PendingOperation;
        op.RewardAcknowledged = true; op.ResultRecorded = true; op.Outcome = CampaignOutcome.AttackerVictory;
        op.AttackerDeckName = "Attacker"; op.DefenderDeckName = "Defender";
        if (mode == "test") { op.TestAutoResolve = true; op.SelectedHumanDeckId = "deck"; }
        interrupted.Phase = CampaignPhase.BattleResolved;
        store.Save(interrupted); c = new CampaignController(store.Load(out _), store);
        c.ApplyResult(); Assert.AreEqual(CampaignPhase.ShowingResult, c.Phase); Assert.AreEqual(1, c.Snapshot.BattleHistory.Count);
        string before = JsonUtility.ToJson(c.Snapshot); c.ApplyResult(); Assert.AreEqual(before, JsonUtility.ToJson(c.Snapshot));
        Assert.AreEqual(op.AttackerFaction, store.Load(out _).Regions.Find(r => r.RegionId == op.TargetRegionId).OwnerFaction);
    }
    [Test] public void PendingManualMatchCannotReuseAnEarlierCompletedMatchId()
    {
        var s = HumanTurn(Planet()); var c = Controller(s); var pair = CampaignRules.LegalAttacks(s).First();
        c.BeginAttack(pair.Source, pair.Target, true); c.RegisterMatch("deck", "Human", "AI"); var first = c.Snapshot.PendingOperation;
        c.RecordManualResult(s.CampaignId, first.OperationId, first.MatchId, CampaignOutcome.DefenderVictory);
        c.AcknowledgeReward(first.MatchId); c.Continue(first.OperationId);
        s = HumanTurn(c.Snapshot); c = Controller(s); pair = CampaignRules.LegalAttacks(s).First();
        c.BeginAttack(pair.Source, pair.Target, true); var invalid = c.Snapshot;
        invalid.PendingOperation.MatchId = first.MatchId;
        Assert.Throws<InvalidDataException>(() => CampaignStore.Validate(invalid));
    }
    [Test] public void B01_B03_B04_B08_B09_ResolverIsSymmetricBoundedAndDeterministic()
    {
        var r = new CampaignBattleResolver(); var a = Deck(); var b = Deck(9);
        Assert.AreEqual(.5, r.Compare(a, a).WinChance, 1e-8); Assert.Greater(r.Compare(b, a).WinChance, .5); Assert.LessOrEqual(r.Compare(b, a).WinChance, .9);
        Assert.Greater(r.Evaluate(a).CommanderPotential, 0); Assert.AreEqual(r.Resolve(a, b, 51).Roll, r.Resolve(a, b, 51).Roll);
        Assert.AreEqual(r.Evaluate(a).Total, r.Evaluate(new CampaignDeck("reordered", a.Main.Reverse())).Total, 1e-6);
        Assert.GreaterOrEqual(r.Evaluate(b).Total, r.Evaluate(a).Total);
    }
    [Test] public void B05_B07_IndependentCompatibleSlotsAndProductionPrerequisites()
    {
        var unit = new CardDefinition { authoredKey = "bio", cardType = CardType.Unit, attack = 4, defenseRating = 3, hitPoints = 3, unitTypeTags = new List<UnitTypeTag> { UnitTypeTag.Bio } };
        var hero = new CardDefinition { authoredKey = "hero", cardType = CardType.Hero, commandRating = 5, grantedAbilities = new List<string> { UnitAbilities.Assembler, UnitAbilities.Researcher } };
        var facility = new CardDefinition { authoredKey = "site", cardType = CardType.Facility, grantedAbilities = new List<string> { UnitAbilities.Production, UnitAbilities.Research } };
        CardDefinition Attachment(string key, AttachmentSlot slot) => new CardDefinition { authoredKey = key, cardType = CardType.Equipment, attachmentSlot = slot,
            equipment = new EquipmentGrant { hostKinds = new List<EquipmentHostKind> { EquipmentHostKind.Unit }, statChanges = new List<EquipmentStatChange> { new EquipmentStatChange { stat = EquipmentStat.Attack, amount = 1 } } } };
        var eq = Attachment("equipment", AttachmentSlot.Equipment); var mut = Attachment("mutator", AttachmentSlot.Mutator); var resolver = new CampaignBattleResolver();
        var main = new[] { hero, unit, unit, facility };
        double e = resolver.Evaluate(new CampaignDeck("e", main, new[] { eq })).AttachmentPotential;
        double m = resolver.Evaluate(new CampaignDeck("m", main, new[] { mut })).AttachmentPotential;
        double both = resolver.Evaluate(new CampaignDeck("both", main, new[] { eq, mut })).AttachmentPotential;
        Assert.Greater(e, 0); Assert.Greater(m, 0); Assert.GreaterOrEqual(both, Math.Max(e, m));
        Assert.AreEqual(0, resolver.Evaluate(new CampaignDeck("missing site", new[] { hero, unit }, new[] { eq })).AttachmentPotential);
        unit.unitTypeTags.Clear(); Assert.AreEqual(0, resolver.Evaluate(new CampaignDeck("non bio", main, new[] { mut })).AttachmentPotential);
        Assert.AreEqual(0, resolver.Evaluate(new CampaignDeck("no host", new[] { hero, facility }, new[] { eq })).AttachmentPotential);
    }
    [Test] public void FastResolutionFrequencyMatchesPredictionWithoutGuaranteedWins()
    {
        var r = new CampaignBattleResolver(); var a = Deck(); var b = Deck(5); double chance = r.Compare(a, b).WinChance;
        int wins = Enumerable.Range(0, 1000).Count(i => r.Resolve(a, b, CampaignRandom.Derive(71, i)).Outcome == CampaignOutcome.AttackerVictory);
        Assert.That(wins / 1000.0, Is.InRange(chance - .06, chance + .06)); Assert.That(wins, Is.InRange(1, 999));
    }
    [TestCase(true)] [TestCase(false)]
    public void FullCampaignRulesReachHumanVictoryAndDefeat(bool humanWins)
    {
        var c = Controller(Planet(24));
        var resolver = new CampaignBattleResolver();
        for (int step = 0; step < 1200 && c.Snapshot.Phase != CampaignPhase.CampaignFinished; step++)
        {
            var state = c.Snapshot;
            var legal = CampaignRules.LegalAttacks(state).ToList();
            if (legal.Count == 0 || (!humanWins && state.CurrentFaction == state.HumanFaction)) { c.EndTurn(); continue; }
            var pair = legal[0];
            var defender = state.Regions.Find(r => r.RegionId == pair.Target).OwnerFaction;
            bool manual = state.CurrentFaction == state.HumanFaction || defender == state.HumanFaction;
            c.BeginAttack(pair.Source, pair.Target, manual);
            if (manual)
            {
                c.RegisterMatch("deck", "Human", "AI"); var op = c.Snapshot.PendingOperation;
                var outcome = humanWins ? (op.AttackerFaction == state.HumanFaction ? CampaignOutcome.AttackerVictory : CampaignOutcome.DefenderVictory) : CampaignOutcome.AttackerVictory;
                c.RecordManualResult(state.CampaignId, op.OperationId, op.MatchId, outcome); c.AcknowledgeReward(op.MatchId);
            }
            else c.ResolveFast(Deck(), Deck(), resolver);
            string id = c.Snapshot.PendingOperation.OperationId; c.Continue(id); c.Continue(id);
            CampaignStore.Validate(c.Snapshot);
        }
        var final = c.Snapshot;
        Assert.AreEqual(CampaignPhase.CampaignFinished, final.Phase);
        Assert.AreEqual(!humanWins, final.Factions.Find(f => f.Faction == final.HumanFaction).Eliminated);
        if (humanWins) { Assert.AreEqual(1, final.Factions.Count(f => !f.Eliminated)); Assert.IsTrue(final.Regions.All(r => r.OwnerFaction == final.HumanFaction)); }
        Assert.AreEqual(final.BattleHistory.Count, final.BattleHistory.Select(h => h.OperationId).Distinct().Count());
        Assert.Greater(final.BattleHistory.Count, 0);
    }
    [Test] public void TestAutoResolveAppliesTerritoryButRequiresNoCollectionReward()
    {
        var s = HumanTurn(Planet()); var c = Controller(s); var pair = CampaignRules.LegalAttacks(s).First();
        c.BeginAttack(pair.Source, pair.Target, true); c.SelectTestDeck("deck"); c.ResolveHumanTest(Deck(), Deck(), new CampaignBattleResolver());
        Assert.AreEqual(CampaignPhase.ShowingResult, c.Snapshot.Phase); Assert.IsTrue(c.Snapshot.PendingOperation.TestAutoResolve);
        Assert.IsTrue(c.Snapshot.PendingOperation.RewardAcknowledged); Assert.AreEqual(1, c.Snapshot.BattleHistory.Count);
        Assert.Throws<InvalidOperationException>(() => c.ResolveHumanTest(Deck(), Deck(), new CampaignBattleResolver()));
    }
}
#endif

