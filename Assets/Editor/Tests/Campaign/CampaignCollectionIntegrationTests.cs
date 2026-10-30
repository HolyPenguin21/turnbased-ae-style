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
    [Test, Category("Extended")] public void GeneratedRegionsCanBeTriangulatedAfterVisualProjection()
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
    [Test] public void CampaignBuilderUpgradesOldPreviewLayoutWithoutOverlapOrDuplicateConditions()
    {
        var root = new UnityEngine.GameObject("CampaignBuilderUpgradeTest", typeof(UnityEngine.RectTransform));
        try
        {
            var ui = root.AddComponent<CampaignUI>();
            ((UnityEngine.RectTransform)root.transform).sizeDelta = new UnityEngine.Vector2(950, 700);
            UnityEngine.RectTransform Rect(string name, UnityEngine.Transform parent, float x, float y, float w, float h)
            {
                var r = new UnityEngine.GameObject(name, typeof(UnityEngine.RectTransform)).GetComponent<UnityEngine.RectTransform>();
                r.SetParent(parent, false); r.anchorMin = r.anchorMax = r.pivot = new UnityEngine.Vector2(0, 1);
                r.anchoredPosition = new UnityEngine.Vector2(x, -y); r.sizeDelta = new UnityEngine.Vector2(w, h); return r;
            }
            var panel = Rect("RegionPanel", root.transform, 0, 0, 430, 768);
            var preview = Rect("RegionTerrainPreview", panel, 30, 344, 370, 122).gameObject.AddComponent<UnityEngine.UI.RawImage>();
            var logo = Rect("OwnerLogo", panel, 122, 478, 36, 36).gameObject.AddComponent<UnityEngine.UI.Image>();
            var owner = Rect("OwnerName", panel, 170, 482, 230, 34).gameObject.AddComponent<TMPro.TextMeshProUGUI>();
            Rect("OwnerCaption", panel, 30, 482, 88, 34).gameObject.AddComponent<TMPro.TextMeshProUGUI>();
            var confirm = Rect("ConfirmText", root.transform, 35, 110, 880, 90).gameObject.AddComponent<TMPro.TextMeshProUGUI>();
            // The authored scene can use stretched anchors: sizeDelta alone is NOT its height.
            confirm.rectTransform.anchorMin = new UnityEngine.Vector2(0, .2f);
            confirm.rectTransform.anchorMax = new UnityEngine.Vector2(0, .8f);
            var info = Rect("DeckInfo", root.transform, 35, 92, 880, 125).gameObject.AddComponent<TMPro.TextMeshProUGUI>();
            var scroll = Rect("DeckScroll", root.transform, 35, 232, 880, 310);
            var viewport = Rect("Viewport", scroll, 0, 0, 880, 310);
            var content = Rect("Content", viewport, 0, 0, 850, 1);
            var so = new SerializedObject(ui);
            void Set(string field, UnityEngine.Object value) => so.FindProperty(field).objectReferenceValue = value;
            Set("regionPanel", panel.gameObject); Set("terrainPreview", preview); Set("ownerLogo", logo); Set("ownerName", owner);
            Set("confirmText", confirm); Set("deckInfo", info); Set("deckListContent", content);
            // Other reference slots already exist; the test creates no prefab assets.
            Set("neighborEmblemsContent", Rect("ExistingNeighbors", panel, 28, 528, 374, 40));
            Set("attacksContent", Rect("ExistingAttacks", panel, 28, 620, 374, 134));
            so.FindProperty("standings").arraySize = 0; so.FindProperty("historyRows").arraySize = 0;
            var method = typeof(CampaignScreenBuilder).GetMethod("AddReferenceSlots", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            method.Invoke(null, new object[] { so }); so.ApplyModifiedProperties(); so.Update();
            var conditions = (TMPro.TMP_Text)so.FindProperty("regionConditionsText").objectReferenceValue;
            Assert.IsNotNull(conditions);
            UnityEngine.Rect Bounds(UnityEngine.RectTransform r) => new UnityEngine.Rect(r.anchoredPosition.x, -r.anchoredPosition.y, r.sizeDelta.x, r.sizeDelta.y);
            Assert.IsFalse(Bounds(conditions.rectTransform).Overlaps(Bounds(preview.rectTransform)));
            Assert.IsFalse(Bounds(conditions.rectTransform).Overlaps(Bounds(owner.rectTransform)));
            Assert.AreEqual(300, confirm.rectTransform.rect.height, .01);
            Assert.AreEqual(212, info.rectTransform.rect.height, .01);
            Assert.AreEqual(222, scroll.rect.height, .01);
            method.Invoke(null, new object[] { so }); so.ApplyModifiedProperties();
            Assert.AreEqual(1, panel.Cast<UnityEngine.Transform>().Count(t => t.name == "RegionConditions"));
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
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
