#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Campaign;
using Game.Cards;
using Game.Players;
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
    [Test] public void G01_SeedReproducesGeometryAndInitialOwnership() => Assert.AreEqual(Geometry(Planet()), Geometry(Planet()));
    [Test] public void G02_DifferentSeedsChangeGeometry() => Assert.AreNotEqual(Geometry(Planet(11)), Geometry(Planet(12)));
    [TestCase(3)] [TestCase(7)] [TestCase(24)] [TestCase(31)]
    public void G03_RegionCountAndBalancedConnectedOwnership(int count)
    {
        var s = Planet(31, count); Assert.AreEqual(count, s.Regions.Count); CampaignGeometry.Validate(s.Regions, true);
        foreach (var f in s.Factions) Assert.That(s.Regions.Count(r => r.OwnerFaction == f.Faction), Is.InRange(count / 3, (count + 2) / 3));
    }
    [Test] public void G12_ThousandSeedsHaveValidSharedGeometryAndConnectedBalancedTerritories()
    { for (int seed = 0; seed < 1000; seed++) { var s = Planet(seed); CampaignGeometry.Validate(s.Regions, true); } }
    [Test] public void InvalidGeometryAndAdjacencyAreRejected()
    {
        var s = Planet(); s.Regions[0].NeighborIds.Add(s.Regions[0].RegionId); Assert.Throws<InvalidDataException>(() => CampaignGeometry.Validate(s.Regions));
        s = Planet(); s.Regions[0].PolygonVertices[0] = new Vector2(float.NaN, 1); Assert.Throws<InvalidDataException>(() => CampaignGeometry.Validate(s.Regions));
        s = Planet(); s.Regions[0].NeighborIds.Clear(); Assert.Throws<InvalidDataException>(() => CampaignGeometry.Validate(s.Regions));
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
