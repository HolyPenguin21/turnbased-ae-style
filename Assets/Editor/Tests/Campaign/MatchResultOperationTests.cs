#if UNITY_INCLUDE_TESTS
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Campaign;
using Game.Cards;
using Game.Core;
using Game.Players;
using Game.Progression;
using Game.Turns;
using NUnit.Framework;
using UnityEngine;

public sealed class MatchResultOperationTests
{
    private string directory, campaignWrite, profileWrite;
    private CampaignStore campaignStore;
    private CollectionProfileStore profileStore;
    private CollectionService collection;
    private CampaignController campaign;
    private CampaignMatchContext context;
    private StartingDeckCatalog starting;
    private ResearchProductionCatalog research;
    private FactionCardCatalog catalog;
    private DeckRules rules;
    private const string CardKey = "reward.test";

    [SetUp] public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "match-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        campaignStore = new CampaignStore(directory);
        profileStore = new CollectionProfileStore(directory);
        campaignWrite = Path.Combine(directory, "campaign-v1.json.tmp");
        profileWrite = Path.Combine(directory, "collection-v1.json.tmp");
        starting = ScriptableObject.CreateInstance<StartingDeckCatalog>();
        research = ScriptableObject.CreateInstance<ResearchProductionCatalog>();
        catalog = ScriptableObject.CreateInstance<FactionCardCatalog>();
        catalog.faction = Faction.Ashen;
        catalog.cards.Add(new CardDefinition { authoredKey = CardKey, faction = Faction.Ashen, cardType = CardType.Unit, deckCopyLimit = 1 });
        starting.catalogs.Add(catalog);
        rules = new DeckRules(starting, research);
        profileStore.Save(new CollectionProfile());
        ReloadProfile();
    }

    [TearDown] public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(starting);
        UnityEngine.Object.DestroyImmediate(research);
        UnityEngine.Object.DestroyImmediate(catalog);
        Directory.Delete(directory, true);
    }

    private void ReloadProfile() => collection = new CollectionService(rules, profileStore, profileStore.Load(out _));
    private void ReloadCampaign() => campaign = new CampaignController(campaignStore.Load(out _), campaignStore);
    private RewardService Rewards() => new RewardService(collection);

    private void BeginCampaignBattle(bool defence = false)
    {
        var state = new CampaignMapGenerator().Generate(37, Faction.Ashen);
        var attack = state.Regions.SelectMany(r => r.NeighborIds.Select(n => (source: r, target: state.Regions.Find(t => t.RegionId == n))))
            .First(p => p.source.OwnerFaction != p.target.OwnerFaction
                && (defence ? p.target.OwnerFaction == state.HumanFaction : p.source.OwnerFaction == state.HumanFaction));
        state.CurrentTurnIndex = state.TurnOrder.IndexOf(attack.source.OwnerFaction);
        campaign = new CampaignController(state, campaignStore);
        campaign.BeginAttack(attack.source.RegionId, attack.target.RegionId, true);
        campaign.RegisterMatch("deck", "Human", "AI");
        var op = campaign.View.PendingOperation;
        context = new CampaignMatchContext
        {
            CampaignId = campaign.View.CampaignId, OperationId = op.OperationId, MatchId = op.MatchId,
            HumanFaction = campaign.View.HumanFaction, AttackerFaction = op.AttackerFaction, DefenderFaction = op.DefenderFaction,
            SourceRegionId = op.SourceRegionId, TargetRegionId = op.TargetRegionId, SelectedHumanDeckId = op.SelectedHumanDeckId,
        };
    }

    private MatchResultOperation GameResult(MatchOutcome outcome)
    {
        var human = context.HumanFaction;
        var enemy = context.AttackerFaction == human ? context.DefenderFaction : context.AttackerFaction;
        bool draw = outcome == MatchOutcome.Draw;
        var final = new CompletedMatchResult(context.MatchId, draw ? Faction.None : outcome == MatchOutcome.Victory ? human : enemy,
            outcome == MatchOutcome.Victory ? enemy : human, draw);
        return new MatchResultOperation(MatchResultOrigin.Game, () => campaign, Rewards, context, final);
    }

    private MatchResultOperation CampaignResult() =>
        new MatchResultOperation(MatchResultOrigin.Campaign, () => campaign, Rewards);

    [Test] public void InterruptedCampaignWritesFailBeforePublishAndResumeFromReceipts()
    {
        BeginCampaignBattle();
        var game = GameResult(MatchOutcome.Victory);
        Directory.CreateDirectory(campaignWrite);
        var before = campaign.View;
        Assert.That(game.Advance().Stage, Is.EqualTo(MatchResultStage.Retry));
        Assert.That(campaign.View, Is.SameAs(before));
        Assert.That(collection.Snapshot.pendingRewards, Is.Empty);
        Directory.Delete(campaignWrite);

        Directory.CreateDirectory(profileWrite);
        Assert.That(game.Advance().Stage, Is.EqualTo(MatchResultStage.Retry));
        Assert.That(campaign.View.PendingOperation.ResultRecorded, Is.True);
        Assert.That(collection.Snapshot.pendingRewards, Is.Empty);
        Directory.Delete(profileWrite);

        ReloadCampaign();
        ReloadProfile();
        var resumed = CampaignResult();
        var offer = resumed.Advance().Reward;
        CollectionAssert.AreEqual(new[] { CardKey }, offer.Keys);
        ReloadProfile();
        resumed = CampaignResult();
        CollectionAssert.AreEqual(offer.Keys, resumed.Advance().Reward.Keys);

        Directory.CreateDirectory(profileWrite);
        Assert.That(resumed.Claim(context.MatchId, offer.Keys).Error, Is.Not.Empty);
        Assert.That(resumed.Current.Reward.Claimed, Is.False);
        Assert.That(collection.Owned(CardKey), Is.Zero);
        Directory.Delete(profileWrite);
        Assert.That(resumed.Claim(context.MatchId, offer.Keys).Reward.Claimed, Is.True);
        Assert.That(resumed.Claim(context.MatchId, offer.Keys).Reward.Claimed, Is.True);

        ReloadProfile();
        resumed = CampaignResult();
        Assert.That(resumed.Advance().Reward.Claimed, Is.True);
        Directory.CreateDirectory(profileWrite);
        Assert.That(resumed.Dismiss(context.MatchId).Error, Is.Not.Empty);
        Assert.That(resumed.Current.Reward, Is.Not.Null);
        Directory.Delete(profileWrite);

        Directory.CreateDirectory(campaignWrite);
        Assert.That(resumed.Dismiss(context.MatchId).Stage, Is.EqualTo(MatchResultStage.Retry));
        Assert.That(campaign.View.PendingOperation.RewardAcknowledged, Is.False);
        Assert.That(collection.Snapshot.pendingRewards, Is.Empty);
        Assert.That(collection.Snapshot.claimedMatchIds, Is.EqualTo(new[] { context.MatchId }));
        Directory.Delete(campaignWrite);

        // Simulate a restart between the two campaign writes, then fail the capture write.
        var acknowledged = campaignStore.Load(out _);
        acknowledged.PendingOperation.RewardAcknowledged = true;
        campaignStore.Save(acknowledged);
        ReloadCampaign();
        ReloadProfile();
        resumed = CampaignResult();
        Directory.CreateDirectory(campaignWrite);
        Assert.That(resumed.Advance().Stage, Is.EqualTo(MatchResultStage.Retry));
        Assert.That(campaign.View.BattleHistory, Is.Empty);
        Assert.That(campaign.View.FindRegion(context.TargetRegionId).OwnerFaction, Is.EqualTo(context.DefenderFaction));
        Directory.Delete(campaignWrite);
        Assert.That(resumed.Advance().Stage, Is.EqualTo(MatchResultStage.CampaignResult));
        Assert.That(resumed.Dismiss(context.MatchId).Stage, Is.EqualTo(MatchResultStage.CampaignResult));
        Assert.That(game.Advance().Stage, Is.EqualTo(MatchResultStage.ReturnToCampaign));
        Assert.That(campaign.View.BattleHistory.Count, Is.EqualTo(1));
        Assert.That(campaign.View.FindRegion(context.TargetRegionId).OwnerFaction, Is.EqualTo(context.AttackerFaction));
        Assert.That(collection.Owned(CardKey), Is.EqualTo(1));

        Directory.CreateDirectory(campaignWrite);
        Assert.That(resumed.ContinueCampaign(context.OperationId, out var error), Is.False);
        Assert.That(error, Is.Not.Empty);
        Assert.That(campaign.View.PendingOperation.OperationId, Is.EqualTo(context.OperationId));
        Directory.Delete(campaignWrite);
        ReloadCampaign();
        resumed = CampaignResult();
        Assert.That(resumed.Advance().Stage, Is.EqualTo(MatchResultStage.CampaignResult));
        Assert.That(resumed.ContinueCampaign(context.OperationId, out error), Is.True, error);
        Assert.That(resumed.ContinueCampaign(context.OperationId, out error), Is.True, error);
        Assert.That(game.Advance().Stage, Is.EqualTo(MatchResultStage.ReturnToCampaign));
        Assert.That(campaign.View.BattleHistory.Count, Is.EqualTo(1));
        Assert.That(campaign.View.PendingOperation, Is.Null);
        var nextState = campaign.Snapshot;
        var next = CampaignRules.LegalAttacks(nextState).First();
        bool manual = nextState.CurrentFaction == nextState.HumanFaction
            || nextState.Regions.Find(region => region.RegionId == next.Target).OwnerFaction == nextState.HumanFaction;
        campaign.BeginAttack(next.Source, next.Target, manual);
        var nextView = campaign.View;
        Assert.That(game.Advance().Stage, Is.EqualTo(MatchResultStage.ReturnToCampaign));
        Assert.That(resumed.ContinueCampaign(context.OperationId, out error), Is.True, error);
        Assert.That(campaign.View, Is.SameAs(nextView));
    }

    [TestCase(MatchOutcome.Victory, true, false)]
    [TestCase(MatchOutcome.Defeat, false, false)]
    [TestCase(MatchOutcome.Defeat, true, false)]
    [TestCase(MatchOutcome.Draw, false, false)]
    [TestCase(MatchOutcome.Victory, false, true)]
    public void ReturnsToCampaignAfterVictoryDefeatDrawOrExhaustedReward(MatchOutcome outcome, bool defence, bool exhausted)
    {
        if (exhausted)
        {
            var full = collection.Snapshot;
            full.ownedCards.Add(new DeckCardEntry { cardKey = CardKey, count = 1 });
            profileStore.Save(full);
            ReloadProfile();
        }
        BeginCampaignBattle(defence);
        var game = GameResult(outcome);
        var stage = game.Advance();
        if (outcome == MatchOutcome.Draw)
        {
            Assert.That(stage.Reward, Is.Null);
            Assert.That(stage.Stage, Is.EqualTo(MatchResultStage.ReturnToCampaign));
            Assert.That(collection.Snapshot.claimedMatchIds, Is.Empty);
        }
        else
        {
            Assert.That(stage.Stage, Is.EqualTo(MatchResultStage.Reward));
            Assert.That(stage.Reward.Keys.Count, Is.EqualTo(exhausted ? 0 : 1));
            Assert.That(stage.Reward.Claimed, Is.EqualTo(outcome == MatchOutcome.Defeat));
            if (!stage.Reward.Claimed) Assert.That(game.Claim(context.MatchId, stage.Reward.Keys).Reward.Claimed, Is.True);
            Assert.That(game.Dismiss(context.MatchId).Stage, Is.EqualTo(MatchResultStage.ReturnToCampaign));
        }
        ReloadCampaign();
        ReloadProfile();
        Assert.That(CampaignResult().Advance().Stage, Is.EqualTo(MatchResultStage.CampaignResult));
        Assert.That(campaign.View.BattleHistory.Count, Is.EqualTo(1));
        Assert.That(campaign.View.BattleHistory[0].Captured, Is.EqualTo((!defence && outcome == MatchOutcome.Victory) || (defence && outcome == MatchOutcome.Defeat)));
        Assert.That(collection.Owned(CardKey), Is.EqualTo(outcome == MatchOutcome.Draw ? 0 : 1));
    }

    [TestCase(false)] [TestCase(true)] [Category("UnityNative")]
    public void GameTurnPersistsResultWithNoRewardPopup(bool campaignMatch)
    {
        if (campaignMatch) BeginCampaignBattle();
        var previousController = CampaignMatchBridge.Controller;
        var previousCollection = ProgressionContext.Collection;
        var previousRewards = ProgressionContext.Rewards;
        var previousPlayers = GameSession.Players;
        var previousContext = GameSession.CampaignContext;
        var previousFinal = GameSession.FinalResult;
        var previousId = GameSession.MatchId;
        var previousStage = GameSession.Stage;
        bool previousEligibility = GameSession.RewardsEligible;
        var root = new GameObject("Match result without reward UI");
        root.SetActive(false);
        try
        {
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, campaign);
            typeof(ProgressionContext).GetProperty("Collection").SetValue(null, collection);
            typeof(ProgressionContext).GetProperty("Rewards").SetValue(null, Rewards());
            typeof(GameSession).GetProperty("Stage").SetValue(null, MatchSessionStage.Running);
            typeof(GameSession).GetProperty("CampaignContext").SetValue(null, campaignMatch ? context : null);
            typeof(GameSession).GetProperty("FinalResult").SetValue(null, null);
            typeof(GameSession).GetProperty("RewardsEligible").SetValue(null, true);
            string matchId = campaignMatch ? context.MatchId : "ordinary-without-popup";
            typeof(GameSession).GetProperty("MatchId").SetValue(null, matchId);
            var human = new PlayerSetupData { Faction = Faction.Ashen, IsHuman = true, Nickname = "Human" };
            var enemy = new PlayerSetupData { Faction = campaignMatch ? context.DefenderFaction : Faction.Vessels, Nickname = "Enemy" };
            GameSession.Players = new System.Collections.Generic.List<PlayerSetupData> { human, enemy };
            var turns = root.AddComponent<GameTurnController>();
            typeof(GameTurnController).GetMethod("EliminatePlayer", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(turns, new object[] { enemy });

            Assert.That(root.GetComponent<Game.UI.CollectionRewardUI>(), Is.Null);
            Assert.That(turns.ResultOperation.Current.Reward.MatchId, Is.EqualTo(matchId));
            Assert.That(profileStore.Load(out _).pendingRewards.Single().matchId, Is.EqualTo(matchId));
            if (campaignMatch) Assert.That(campaignStore.Load(out _).PendingOperation.ResultRecorded, Is.True);
            typeof(GameSession).GetProperty("Stage").SetValue(null, MatchSessionStage.Prepared);
            Assert.That(turns.ResultOperation, Is.Null, "A same-ID restore request invalidates the previous UI callback.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            GameSession.Players = previousPlayers;
            typeof(GameSession).GetProperty("Stage").SetValue(null, previousStage);
            typeof(GameSession).GetProperty("CampaignContext").SetValue(null, previousContext);
            typeof(GameSession).GetProperty("MatchId").SetValue(null, previousId);
            typeof(GameSession).GetProperty("FinalResult").SetValue(null, previousFinal);
            typeof(GameSession).GetProperty("RewardsEligible").SetValue(null, previousEligibility);
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, previousController);
            typeof(ProgressionContext).GetProperty("Collection").SetValue(null, previousCollection);
            typeof(ProgressionContext).GetProperty("Rewards").SetValue(null, previousRewards);
        }
    }

    [Test, Category("UnityNative")]
    public void DisablingCampaignClearsTheHeldRewardPresentation()
    {
        BeginCampaignBattle();
        var operation = GameResult(MatchOutcome.Victory);
        operation.Advance();
        var root = new GameObject("Disabled campaign result");
        root.SetActive(false);
        try
        {
            var ui = root.AddComponent<CampaignUI>();
            var rewards = root.AddComponent<Game.UI.CollectionRewardUI>();
            rewards.Present(null, operation, () => Assert.Fail("Clearing the presentation called its close callback."));
            typeof(CampaignUI).GetField("rewardPresentation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(ui, rewards);
            typeof(CampaignUI).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ui, null);
            root.SetActive(true);

            var heldOperation = typeof(Game.UI.CollectionRewardUI).GetField("operation", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(heldOperation.GetValue(rewards), Is.Null);
            Assert.That(profileStore.Load(out _).pendingRewards.Single().matchId, Is.EqualTo(context.MatchId));
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    [Test, Category("UnityNative")]
    public void StaleContinueCallbackLeavesTheDisplayedCampaignResultIntact()
    {
        BeginCampaignBattle();
        Assert.That(GameResult(MatchOutcome.Draw).Advance().Stage, Is.EqualTo(MatchResultStage.ReturnToCampaign));
        var previous = CampaignMatchBridge.Controller;
        var root = new GameObject("Campaign result callback");
        var window = new GameObject("Current result", typeof(RectTransform));
        window.transform.SetParent(root.transform);
        try
        {
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, campaign);
            var ui = root.AddComponent<CampaignUI>();
            typeof(CampaignUI).GetField("resultWindow", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(ui, window);
            typeof(CampaignUI).GetField("modalContent", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(ui, window.transform);
            var before = campaign.View;
            var continueResult = typeof(CampaignUI).GetMethod("ContinueResult", BindingFlags.Instance | BindingFlags.NonPublic);
            continueResult.Invoke(ui, new object[] { campaign, Guid.NewGuid().ToString("N") });

            Assert.That(campaign.View, Is.SameAs(before));
            Assert.That(campaign.View.PendingOperation.OperationId, Is.EqualTo(context.OperationId));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
            typeof(CampaignMatchBridge).GetProperty("Controller").SetValue(null, previous);
        }
    }

    [Test] public void RecoveryRejectsAnOfferForADifferentResultWithTheSameMatchId()
    {
        BeginCampaignBattle();
        var differentResult = new ParticipantResult(context.MatchId,
            new PlayerSetupData { IsHuman = true, Faction = Faction.Ashen }, MatchOutcome.Defeat);
        Assert.That(Rewards().Record(differentResult, out var error), Is.True, error);
        var operation = GameResult(MatchOutcome.Victory);
        Assert.That(operation.Advance().Stage, Is.EqualTo(MatchResultStage.Retry));
        Assert.That(campaign.View.PendingOperation.ResultRecorded, Is.True);
        Assert.That(campaign.View.PendingOperation.RewardAcknowledged, Is.False);
        Assert.That(campaign.View.BattleHistory, Is.Empty);
    }

    [Test] public void FailedMenuReconciliationLeavesContinueAvailableAndCanBeRetriedFromCampaign()
    {
        BeginCampaignBattle();
        var game = GameResult(MatchOutcome.Victory);
        var reward = game.Advance().Reward;
        Assert.That(game.Claim(context.MatchId, reward.Keys).Reward.Claimed, Is.True);
        var menu = new MatchResultOperation(MatchResultOrigin.MainMenu, () => campaign, Rewards);
        Assert.That(menu.Advance().Reward.MatchId, Is.EqualTo(context.MatchId));

        Directory.CreateDirectory(campaignWrite);
        var failed = menu.Dismiss(context.MatchId);
        Assert.That(failed.Stage, Is.EqualTo(MatchResultStage.MenuNotice));
        Assert.That(failed.Error, Is.Not.Empty);
        Assert.That(campaign.View.PendingOperation.RewardAcknowledged, Is.False);
        Directory.Delete(campaignWrite);

        ReloadCampaign();
        ReloadProfile();
        Assert.That(CampaignResult().Advance().Stage, Is.EqualTo(MatchResultStage.CampaignResult));
        Assert.That(campaign.View.BattleHistory.Count, Is.EqualTo(1));
        Assert.That(collection.Owned(CardKey), Is.EqualTo(1));
    }

    [Test] public void ReplayedOrdinarySaveWithDifferentOutcomeKeepsTheOriginalReward()
    {
        var human = new PlayerSetupData { IsHuman = true, Faction = Faction.Ashen };
        var first = new ParticipantResult("replay", human, MatchOutcome.Victory);
        Assert.That(Rewards().Record(first, out var error), Is.True, error);
        ReloadProfile();

        var replay = new MatchResultOperation(MatchResultOrigin.Game, () => null, Rewards,
            participant: new ParticipantResult(first.MatchId, human, MatchOutcome.Defeat));
        var stage = replay.Advance();
        Assert.That(stage.Stage, Is.EqualTo(MatchResultStage.Reward));
        Assert.That(stage.Reward.Outcome, Is.EqualTo(MatchOutcome.Victory));
        Assert.That(collection.Owned(CardKey), Is.Zero);
        Assert.That(replay.Claim(first.MatchId, stage.Reward.Keys).Reward.Claimed, Is.True);
        Assert.That(replay.Dismiss(first.MatchId).Stage, Is.EqualTo(MatchResultStage.ReturnToMenu));
        Assert.That(collection.Owned(CardKey), Is.EqualTo(1));
    }

    private sealed class ResultCatalogs : IProgressionContent
    {
        public StartingDeckCatalog CollectionDeckCatalog { get; set; }
        public ResearchProductionCatalog CollectionResearchCatalog { get; set; }
    }

    [Test] public void CatalogOnlyInitializationRetriesAndOpenReusesDurableReward()
    {
        var savedStatics = new[] { nameof(ProgressionContext.Collection), nameof(ProgressionContext.Rewards),
            nameof(ProgressionContext.Notice), nameof(ProgressionContext.Error) }
            .Select(name => typeof(ProgressionContext).GetProperty(name)).ToArray();
        var previous = savedStatics.Select(property => property.GetValue(null)).ToArray();
        try
        {
            foreach (var property in savedStatics) property.SetValue(null, null);
            var catalogs = new ResultCatalogs { CollectionDeckCatalog = starting, CollectionResearchCatalog = research };
            Directory.CreateDirectory(profileWrite);
            Assert.That(ProgressionContext.Initialize(catalogs, () => profileStore), Is.False);
            Assert.That(ProgressionContext.Collection, Is.Null);
            Assert.That(ProgressionContext.Rewards, Is.Null);
            Directory.Delete(profileWrite);
            Assert.That(ProgressionContext.Initialize(catalogs, () => profileStore), Is.True, ProgressionContext.Error);
            var human = new PlayerSetupData { IsHuman = true, Faction = Faction.Ashen };
            var result = new ParticipantResult("catalog-only", human, MatchOutcome.Victory);
            var first = MatchResultOperation.Open(catalogs, MatchResultOrigin.Game, participant: result);
            var view = first.Advance();
            Assert.That(view.Reward.Keys, Is.EqualTo(new[] { CardKey }));
            Assert.That(first.Claim(result.MatchId, view.Reward.Keys).Reward.Claimed, Is.True);

            foreach (var property in savedStatics) property.SetValue(null, null);
            Assert.That(ProgressionContext.Initialize(catalogs, () => profileStore), Is.True, ProgressionContext.Error);
            var resumed = MatchResultOperation.Open(catalogs, MatchResultOrigin.Game, participant: result);
            Assert.That(resumed.Advance().Reward.Claimed, Is.True);
            Assert.That(ProgressionContext.Collection.Owned(CardKey), Is.EqualTo(1));
            Assert.That(profileStore.Load(out _).pendingRewards.Count, Is.EqualTo(1));
        }
        finally
        {
            for (int i = 0; i < savedStatics.Length; i++) savedStatics[i].SetValue(null, previous[i]);
        }
    }

    [Test] public void OrdinaryDrawReturnsToMenuWithoutOpeningTheProfile()
    {
        var player = new PlayerSetupData { IsHuman = true, Faction = Faction.Ashen };
        var operation = new MatchResultOperation(MatchResultOrigin.Game,
            () => throw new InvalidOperationException("Unexpected campaign access"),
            () => throw new InvalidOperationException("Unexpected profile access"),
            participant: new ParticipantResult("draw", player, MatchOutcome.Draw));

        Assert.That(operation.Advance().Stage, Is.EqualTo(MatchResultStage.ReturnToMenu));
        Assert.That(operation.Current.Reward, Is.Null);
        Assert.That(profileStore.Load(out _).pendingRewards, Is.Empty);
    }

    [Test] public void ProfileQueueDoesNotReplaceActiveMatchOrCampaignReward()
    {
        var human = new PlayerSetupData { IsHuman = true, Faction = Faction.Ashen };
        var unrelated = new ParticipantResult("old-match", human, MatchOutcome.Victory);
        Assert.That(Rewards().Record(unrelated, out var error), Is.True, error);
        BeginCampaignBattle();
        var tactical = GameResult(MatchOutcome.Victory);
        Assert.That(tactical.Advance().Reward.MatchId, Is.EqualTo(context.MatchId));
        Assert.That(CampaignResult().Advance().Reward.MatchId, Is.EqualTo(context.MatchId));

        var ordinary = new MatchResultOperation(MatchResultOrigin.Game, () => campaign, Rewards,
            participant: new ParticipantResult("ordinary", human, MatchOutcome.Defeat));
        Assert.That(ordinary.Advance().Reward.MatchId, Is.EqualTo("ordinary"));
        Assert.That(ordinary.Dismiss("old-match").Reward.MatchId, Is.EqualTo("ordinary"));
        Assert.That(ordinary.Dismiss("ordinary").Stage, Is.EqualTo(MatchResultStage.ReturnToMenu));
        Assert.That(campaign.View.PendingOperation.RewardAcknowledged, Is.False);
        ReloadProfile();
        var menu = new MatchResultOperation(MatchResultOrigin.MainMenu, () => campaign, Rewards);
        Assert.That(menu.Advance().Reward.MatchId, Is.EqualTo("old-match"));
        Assert.That(menu.Claim("old-match", Array.Empty<string>()).Reward.Claimed, Is.True);
        Assert.That(menu.Dismiss("old-match").Reward.MatchId, Is.EqualTo(context.MatchId));
    }
}
#endif
