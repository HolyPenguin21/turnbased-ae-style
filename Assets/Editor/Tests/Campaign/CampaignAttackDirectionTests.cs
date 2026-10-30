#if UNITY_INCLUDE_TESTS
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Campaign;
using Game.Cards;
using Game.Players;
using Game.Styles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public sealed class CampaignAttackDirectionFlowTests
{
    private string directory;
    private StartingDeckCatalog starting;
    private ResearchProductionCatalog research;
    private FactionCardCatalog catalog;
    [SetUp] public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "campaign-arrow-" + Guid.NewGuid().ToString("N"));
        starting = ScriptableObject.CreateInstance<StartingDeckCatalog>(); research = ScriptableObject.CreateInstance<ResearchProductionCatalog>();
        catalog = ScriptableObject.CreateInstance<FactionCardCatalog>(); catalog.faction = Faction.IronConcord;
        catalog.cards.Add(new CardDefinition { authoredKey = "arrow-test-unit", displayName = "Unit", cardType = CardType.Unit, attack = 3, defenseRating = 3, hitPoints = 3 });
        starting.catalogs.Add(catalog);
        foreach (var faction in DeckRules.PlayableFactions)
            starting.decks.Add(new StartingDeck { faction = faction, deckName = faction.ToString(), cards = new System.Collections.Generic.List<DeckCardEntry> { new DeckCardEntry { cardKey = "arrow-test-unit", count = 4 } } });
    }
    [TearDown] public void Cleanup()
    {
        UnityEngine.Object.DestroyImmediate(starting); UnityEngine.Object.DestroyImmediate(research); UnityEngine.Object.DestroyImmediate(catalog);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
    [Test, Category("Extended")] public void DeferredAiSelectionHasNoResultAndResolvesIdenticallyToImmediateFlow()
    {
        var rules = new DeckRules(starting, research); var resolver = new CampaignBattleResolver(); int samples = 0, manualSamples = 0;
        for (int seed = 0; seed < 30; seed++)
        {
            var state = new CampaignMapGenerator().Generate(seed, Faction.Ashen);
            state.CurrentTurnIndex = state.TurnOrder.FindIndex(f => f != state.HumanFaction);
            var deferred = new CampaignController(state, new CampaignStore(Path.Combine(directory, "deferred")));
            var immediate = new CampaignController(state, new CampaignStore(Path.Combine(directory, "immediate")));
            deferred.TakeAiTurn(rules, resolver, false); immediate.TakeAiTurn(rules, resolver);
            var op = deferred.View.PendingOperation; if (op == null) continue;
            Assert.AreEqual(CampaignPhase.PreparingBattle, deferred.Phase); Assert.False(op.ResultRecorded); Assert.False(op.OwnershipApplied);
            Assert.AreEqual(op.SourceRegionId, immediate.View.PendingOperation.SourceRegionId); Assert.AreEqual(op.TargetRegionId, immediate.View.PendingOperation.TargetRegionId);
            if (op.Manual) { manualSamples++; continue; }
            samples++;
            Assert.AreEqual(CampaignPhase.ShowingResult, immediate.Phase);
            deferred.ResolveFast(CampaignDeck.Standard(op.AttackerFaction, rules), CampaignDeck.Standard(op.DefenderFaction, rules), resolver);
            Assert.AreEqual(immediate.View.PendingOperation.Outcome, deferred.View.PendingOperation.Outcome);
            Assert.AreEqual(immediate.View.PendingOperation.Roll, deferred.View.PendingOperation.Roll);
            Assert.AreEqual(immediate.View.RandomSequence, deferred.View.RandomSequence);
            Assert.AreEqual(1, deferred.View.BattleHistory.Count);
            CollectionAssert.AreEqual(immediate.View.Regions.Select(r => r.OwnerFaction), deferred.View.Regions.Select(r => r.OwnerFaction));
        }
        Assert.Greater(samples, 0, "Fixture must cover AI vs AI, not only attacks on the human");
        Assert.Greater(manualSamples, 0, "Fixture must also cover an AI attack on the human");
    }
    [Test] public void PendingSelectionIsDurableAndDoesNotConsumeASecondTurn()
    {
        var rules = new DeckRules(starting, research); var resolver = new CampaignBattleResolver();
        var state = new CampaignMapGenerator().Generate(37, Faction.Ashen);
        state.CurrentTurnIndex = state.TurnOrder.FindIndex(f => f != state.HumanFaction);
        var store = new CampaignStore(directory); var controller = new CampaignController(state, store);
        controller.TakeAiTurn(rules, resolver, false);
        var loaded = store.Load(out _); Assert.AreEqual(controller.View.PendingOperation.OperationId, loaded.PendingOperation.OperationId);
        Assert.False(loaded.PendingOperation.ResultRecorded); Assert.AreEqual(controller.View.RandomSequence, loaded.RandomSequence);
    }
}
#endif
