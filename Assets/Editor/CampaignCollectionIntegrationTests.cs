#if UNITY_INCLUDE_TESTS
using System;
using System.IO;
using System.Linq;
using Game.Campaign;
using Game.Cards;
using Game.Core;
using Game.Players;
using Game.Progression;
using NUnit.Framework;
using UnityEditor;

// Unity asset integration, isolated stores: never writes the player's persistent collection.
public sealed class CampaignCollectionIntegrationTests
{
    private string directory;
    private CollectionService collection;
    [SetUp] public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "campaign-integration-" + Guid.NewGuid().ToString("N"));
        var config = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Config/GameConfig.asset"); Assert.IsNotNull(config);
        collection = new CollectionService(new DeckRules(config.collectionDeckCatalog, config.collectionResearchCatalog), new CollectionProfileStore(directory), new CollectionProfile());
        Assert.IsTrue(collection.Transact(collection.InitializeStarters, out string error), error);
    }
    [TearDown] public void Cleanup() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    [Test] public void BuiltMapGraphicsHaveCanvasRenderersAndCanRebuild()
    {
        var root = new UnityEngine.GameObject("CampaignMapTest", typeof(UnityEngine.RectTransform));
        try
        {
            var state = new CampaignMapGenerator().Generate(851, Faction.Ashen);
            var map = root.AddComponent<CampaignMapView>();
            map.Build(state.Regions, _ => { }, _ => { });
            map.Refresh(state, null, null, true);
            var graphics = root.GetComponentsInChildren<UnityEngine.UI.Graphic>();
            Assert.AreEqual(state.Regions.Count * 2 + 1, graphics.Length);
            Assert.AreEqual(state.Regions.Count * 2, graphics.OfType<CampaignRegionGraphic>().Count());
            Assert.AreEqual(1, graphics.OfType<CampaignArrowGraphic>().Count());
            foreach (var graphic in graphics)
            {
                Assert.IsNotNull(graphic.GetComponent<UnityEngine.CanvasRenderer>(), graphic.name);
                Assert.DoesNotThrow(() => graphic.Rebuild(UnityEngine.UI.CanvasUpdate.PreRender), graphic.name);
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
    [Test] public void GeneratedRegionsCanBeTriangulatedAfterVisualProjection()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var state = new CampaignMapGenerator().Generate(seed, Faction.Ashen);
            foreach (var r in state.Regions)
            {
                var projected = r.PolygonVertices.Select(CampaignMapView.Project).ToList();
                var triangles = CampaignGeometry.Triangulate(projected);
                Assert.Greater(triangles.Count, 0);
                foreach (int index in triangles) Assert.That(index, Is.InRange(0, projected.Count - 1));
            }
        }
    }
    [Test] public void SelectedSavedDeckUsesExistingValidatorAndDetachedLoadout()
    {
        foreach (var faction in DeckRules.PlayableFactions)
        {
            var deck = collection.DefaultDeck(faction); Assert.IsNotNull(deck);
            var input = CampaignDeck.Saved(deck, collection);
            var loadout = new MatchLoadout(deck, collection.Rules, collection.Owned);
            Assert.IsTrue(loadout.TryBuildPool(collection.Rules, out var pool, out string error), error);
            CollectionAssert.AreEquivalent(pool.Select(c => c.authoredKey), input.Main.Select(c => c.authoredKey));
            deck.mainCards.Clear(); Assert.IsNotEmpty(input.Main);
            var invalid = collection.DefaultDeck(faction); invalid.mainCards[0].count = 10000;
            Assert.Throws<InvalidOperationException>(() => CampaignDeck.Saved(invalid, collection));
            var ai = CampaignDeck.Standard(faction, collection.Rules);
            CollectionAssert.AreEquivalent(collection.Rules.Starting.BuildDeckPool(faction).Select(c => c.authoredKey), ai.Main.Select(c => c.authoredKey));
        }
    }
    [TestCase(MatchOutcome.Victory)] [TestCase(MatchOutcome.Defeat)] [TestCase(MatchOutcome.Draw)]
    public void SeparateCampaignAndCollectionFilesRecoverWithoutDoubleGrantOrCapture(MatchOutcome humanOutcome)
    {
        var state = new CampaignMapGenerator().Generate(851, Faction.Ashen); state.CurrentTurnIndex = state.TurnOrder.IndexOf(state.HumanFaction);
        var store = new CampaignStore(directory); var campaign = new CampaignController(state, store);
        var attack = CampaignRules.LegalAttacks(state).First(); campaign.BeginAttack(attack.Source, attack.Target, true);
        campaign.RegisterMatch(collection.DefaultDeck(state.HumanFaction).deckId, "Human", "AI"); var op = campaign.Snapshot.PendingOperation;
        var outcome = humanOutcome == MatchOutcome.Victory ? CampaignOutcome.AttackerVictory : humanOutcome == MatchOutcome.Defeat ? CampaignOutcome.DefenderVictory : CampaignOutcome.Draw;
        campaign.RecordManualResult(state.CampaignId, op.OperationId, op.MatchId, outcome);
        // Crash boundary A: only campaign has the authoritative result.
        campaign = new CampaignController(store.Load(out _), store); Assert.IsFalse(campaign.Snapshot.PendingOperation.OwnershipApplied);
        var rewards = new RewardService(collection); var player = new PlayerSetupData { IsHuman = true, Faction = state.HumanFaction };
        int before = collection.Snapshot.ownedCards.Sum(c => c.count);
        Assert.IsTrue(rewards.Record(new ParticipantResult(op.MatchId, player, humanOutcome), out string error), error);
        var profileStore = new CollectionProfileStore(directory); var profile = profileStore.Load(out _);
        collection = new CollectionService(collection.Rules, profileStore, profile); rewards = new RewardService(collection);
        // Crash boundary B: replay Record preserves the original persisted offer.
        var offers = collection.Snapshot.pendingRewards.FirstOrDefault()?.offeredKeys.ToArray();
        Assert.IsTrue(rewards.Record(new ParticipantResult(op.MatchId, player, humanOutcome), out error), error);
        if (humanOutcome != MatchOutcome.Draw)
        {
            var reward = collection.Snapshot.pendingRewards.Single(); CollectionAssert.AreEqual(offers, reward.offeredKeys);
            if (humanOutcome == MatchOutcome.Victory) Assert.IsTrue(rewards.Claim(op.MatchId, rewards.AvailableOffers(reward).Take(2), out error), error);
            Assert.IsTrue(rewards.Dismiss(op.MatchId, out error), error);
        }
        // Crash boundary C: reward dismissal committed, territory has not yet been applied.
        campaign = new CampaignController(store.Load(out _), store); campaign.AcknowledgeReward(op.MatchId);
        int acquired = collection.Snapshot.ownedCards.Sum(c => c.count) - before;
        Assert.AreEqual(humanOutcome == MatchOutcome.Victory ? 2 : humanOutcome == MatchOutcome.Defeat ? 1 : 0, acquired);
        Assert.IsTrue(rewards.Record(new ParticipantResult(op.MatchId, player, humanOutcome), out error), error);
        campaign.AcknowledgeReward(op.MatchId); campaign.ApplyResult();
        Assert.AreEqual(1, campaign.Snapshot.BattleHistory.Count); Assert.AreEqual(before + acquired, collection.Snapshot.ownedCards.Sum(c => c.count));
    }
    [Test] public void AcknowledgedRewardRecoveryBridgeDoesNotGrantAgain()
    {
        var state = new CampaignMapGenerator().Generate(851, Faction.Ashen); state.CurrentTurnIndex = state.TurnOrder.IndexOf(state.HumanFaction);
        var store = new CampaignStore(directory); var campaign = new CampaignController(state, store);
        var attack = CampaignRules.LegalAttacks(state).First(); campaign.BeginAttack(attack.Source, attack.Target, true);
        campaign.RegisterMatch(collection.DefaultDeck(state.HumanFaction).deckId, "Human", "AI"); var op = campaign.Snapshot.PendingOperation;
        campaign.RecordManualResult(state.CampaignId, op.OperationId, op.MatchId, CampaignOutcome.AttackerVictory);
        var interrupted = campaign.Snapshot; interrupted.PendingOperation.RewardAcknowledged = true;
        store.Save(interrupted); campaign = new CampaignController(store.Load(out _), store);
        var previous = CampaignMatchBridge.Controller;
        var previousCollection = ProgressionContext.Collection; var previousRewards = ProgressionContext.Rewards;
        string collectionBefore = UnityEngine.JsonUtility.ToJson(collection.Snapshot);
        try
        {
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, campaign);
            typeof(ProgressionContext).GetProperty("Collection").SetValue(null, collection);
            typeof(ProgressionContext).GetProperty("Rewards").SetValue(null, new RewardService(collection));
            Assert.IsTrue(CampaignMatchBridge.RecoverReward(null, out string error), error);
            Assert.IsTrue(CampaignMatchBridge.RecoverReward(null, out error), error);
            Assert.AreEqual(1, campaign.Snapshot.BattleHistory.Count); Assert.AreEqual(CampaignPhase.ShowingResult, campaign.Phase);
            Assert.AreEqual(collectionBefore, UnityEngine.JsonUtility.ToJson(collection.Snapshot));
        }
        finally
        {
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, previous);
            typeof(ProgressionContext).GetProperty("Collection").SetValue(null, previousCollection);
            typeof(ProgressionContext).GetProperty("Rewards").SetValue(null, previousRewards);
        }
    }
    [Test] public void ActualUnitStatImprovementsNeverReduceDeckScoreOrAttackChance()
    {
        var resolver = new CampaignBattleResolver();
        foreach (var faction in DeckRules.PlayableFactions)
        {
            var deck = CampaignDeck.Standard(faction, collection.Rules);
            double before = resolver.Evaluate(deck).Total;
            for (int index = 0; index < deck.Main.Count; index++)
            {
                if (deck.Main[index].cardType != CardType.Unit) continue;
                foreach (string stat in new[] { "attack", "defenseRating", "hitPoints", "initiative" })
                {
                    var copy = (CardDefinition)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(deck.Main[index], null);
                    if (stat == "attack") copy.attack++; else if (stat == "defenseRating") copy.defenseRating++; else if (stat == "hitPoints") copy.hitPoints++; else copy.initiative++;
                    var pool = deck.Main.ToArray(); pool[index] = copy;
                    var improved = new CampaignDeck(deck.Name, pool, deck.Attachments);
                    Assert.GreaterOrEqual(resolver.Evaluate(improved).Total + 1e-5, before, copy.authoredKey + "/" + stat);
                    Assert.GreaterOrEqual(resolver.Compare(improved, deck).WinChance + 1e-7, .5);
                }
            }
        }
    }
    [Test] public void StandardDeckExperimentsUseActualAuthoredCards()
    {
        var resolver = new CampaignBattleResolver();
        var decks = DeckRules.PlayableFactions.Select(f => CampaignDeck.Standard(f, collection.Rules)).ToList();
        foreach (var d in decks)
        {
            Assert.Greater(resolver.Evaluate(d).Total, 0);
            Assert.AreEqual(resolver.Evaluate(d).Total, resolver.Evaluate(new CampaignDeck(d.Name, d.Main.Reverse(), d.Attachments.Reverse())).Total, 1e-6);
        }
        for (int a = 0; a < decks.Count; a++) for (int b = a + 1; b < decks.Count; b++)
        {
            double chance = resolver.Compare(decks[a], decks[b]).WinChance;
            int wins = Enumerable.Range(0, 1000).Count(i => resolver.Resolve(decks[a], decks[b], CampaignRandom.Derive(419, i)).Outcome == CampaignOutcome.AttackerVictory);
            Assert.That(wins / 1000.0, Is.InRange(chance - .06, chance + .06));
        }
    }
}
#endif
