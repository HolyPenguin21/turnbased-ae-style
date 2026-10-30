#if UNITY_INCLUDE_TESTS
using System;
using System.IO;
using System.Linq;
using Game.Campaign;
using Game.Cards;
using Game.Core;
using Game.Players;
using Game.Progression;
using Game.Terrain;
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
    [Test] public void RejectedMatchPreparationPreservesRandomAndExplicitAiChoices()
    {
        var previous = ProgressionContext.Collection;
        var config = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Config/GameConfig.asset");
        try
        {
            typeof(ProgressionContext).GetProperty("Collection").SetValue(null, collection);
            foreach (string requested in new[] { null, "Aldric Voss" })
            {
                var ai = new PlayerSetupData { Faction = Faction.IronConcord, AiDeckName = requested };
                var players = new System.Collections.Generic.List<PlayerSetupData> {
                    new PlayerSetupData { Faction = Faction.Ashen, IsHuman = true, SelectedDeckId = "missing-deck" }, ai };
                var random = UnityEngine.Random.state;
                Assert.IsFalse(GameSession.TryPrepareMatch(players, config, out _));
                Assert.AreEqual(requested, ai.AiDeckName);
                Assert.AreEqual(random, UnityEngine.Random.state, "Rejected setup must not consume the random deck roll.");
                Assert.IsNull(ai.AiBlueprints);
            }
        }
        finally { typeof(ProgressionContext).GetProperty("Collection").SetValue(null, previous); }
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

    [TestCase(false)] [TestCase(true)]
    public void AttackDefenceAndInterruptedRetryUseTargetConditionsAndOrdinarySetupClearsThem(bool defence)
    {
        var state = new CampaignMapGenerator().Generate(851, Faction.Ashen);
        var attack = state.Regions.SelectMany(r => r.NeighborIds.Select(n => (source: r, target: state.Regions.Find(t => t.RegionId == n))))
            .First(p => p.source.OwnerFaction != p.target.OwnerFaction
                && (defence ? p.target.OwnerFaction == state.HumanFaction : p.source.OwnerFaction == state.HumanFaction));
        state.CurrentTurnIndex = state.TurnOrder.IndexOf(attack.source.OwnerFaction);
        attack.source.Biome = Biome.Arid; attack.source.Specialization = ResourceSpecialization.Human; attack.source.Richness = ResourceRichness.Poor;
        attack.target.Biome = Biome.Desert; attack.target.Specialization = ResourceSpecialization.Materials; attack.target.Richness = ResourceRichness.Rich;
        var controller = new CampaignController(state, new CampaignStore(directory));
        controller.BeginAttack(attack.source.RegionId, attack.target.RegionId, true);
        var previous = CampaignMatchBridge.Controller; var previousCollection = ProgressionContext.Collection;
        var config = AssetDatabase.LoadAssetAtPath<GameConfig>("Assets/Config/GameConfig.asset");
        var players = GameSession.Players;
        try
        {
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, controller);
            typeof(ProgressionContext).GetProperty("Collection").SetValue(null, collection);
            string deck = collection.DefaultDeck(state.HumanFaction).deckId;
            Assert.IsTrue(CampaignMatchBridge.PrepareMatch(config, deck, out var error), error);
            string match = GameSession.MatchId;
            var ai = GameSession.Players.Single(p => !p.IsHuman);
            string aiDeck = ai.AiDeckName;
            Assert.IsNotNull(config.collectionDeckCatalog.ResolveAiLeader(ai.Faction, aiDeck));
            Assert.AreEqual(aiDeck, ai.Faction == controller.View.PendingOperation.AttackerFaction
                ? controller.View.PendingOperation.AttackerDeckName : controller.View.PendingOperation.DefenderDeckName);
            Assert.AreEqual(Biome.Desert, GameSession.SelectedBiome);
            Assert.AreEqual(ResourceSpecialization.Materials, GameSession.SelectedResourceConditions.Value.Specialization);
            Assert.AreEqual(ResourceRichness.Rich, GameSession.SelectedResourceConditions.Value.Richness);
            Assert.IsTrue(CampaignMatchBridge.PrepareMatch(config, deck, out error), error);
            Assert.AreEqual(match, GameSession.MatchId);
            Assert.AreEqual(aiDeck, GameSession.Players.Single(p => !p.IsHuman).AiDeckName);
            Assert.AreEqual(Biome.Desert, GameSession.ResolveBiome());
            // Registry cleanup on entering Game cannot erase prepared inputs.
            Game.Setup.MatchStateInventory.ResetForNewMatch();
            Assert.AreEqual(ResourceRichness.Rich, GameSession.SelectedResourceConditions.Value.Richness);
            var ordinary = new System.Collections.Generic.List<PlayerSetupData>();
            Assert.IsTrue(GameSession.TryPrepareMatch(ordinary, config, out error), error);
            Assert.IsNull(GameSession.SelectedResourceConditions); Assert.IsNull(GameSession.SelectedBiome);
            Assert.IsNull(GameSession.CampaignContext);
        }
        finally
        {
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, previous);
            typeof(ProgressionContext).GetProperty("Collection").SetValue(null, previousCollection);
            GameSession.Players = players; GameSession.ClearMapConditions();
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
