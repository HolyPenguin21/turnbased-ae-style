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

public sealed class CampaignAttackDirectionGeometryTests
{
    [TestCase(1, 0)] [TestCase(-1, 0)] [TestCase(0, 1)] [TestCase(0, -1)]
    [TestCase(1, 1)] [TestCase(-1, 1)] [TestCase(1, -1)] [TestCase(-1, -1)]
    public void EndpointsFollowAllDirections(int x, int y)
    {
        var rect = new Rect(-680, -372, 1360, 744); var to = new Vector2(x * .8f, y * .8f);
        Assert.True(CampaignArrowGraphic.Endpoints(rect, Vector2.zero, to, 24, 24, 16, out var a, out var b));
        var desired = CampaignMapView.ToPixel(rect, to) - rect.center;
        Assert.That(Vector2.Dot((b - a).normalized, desired.normalized), Is.GreaterThan(.9999f));
        Assert.That((a - rect.center).magnitude, Is.EqualTo(24).Within(.001));
        Assert.That((CampaignMapView.ToPixel(rect, to) - b).magnitude, Is.EqualTo(24).Within(.001));
    }
    [Test] public void ShortSegmentAndViewportEdgeCannotInvertOrOverflow()
    {
        var rect = new Rect(-100, -50, 200, 100);
        foreach (var to in new[] { new Vector2(.001f, 0), new Vector2(2, 2) })
        {
            Assert.True(CampaignArrowGraphic.Endpoints(rect, Vector2.zero, to, 1000, 1000, 10, out var a, out var b));
            Assert.Greater((b - a).sqrMagnitude, 0); Assert.True(rect.Contains(a)); Assert.True(rect.Contains(b));
            Assert.Greater(Vector2.Dot(b - a, to), 0);
        }
        Assert.False(CampaignArrowGraphic.Endpoints(rect, Vector2.zero, Vector2.zero, 24, 24, 10, out _, out _));
    }
    [Test] public void TimelineDrawsHoldsFadesAndCompletesWithZeroDurations()
    {
        CampaignArrowGraphic.Evaluate(.175f, .35f, .8f, .35f, out var progress, out var alpha);
        Assert.That(progress, Is.EqualTo(.5f).Within(.0001)); Assert.AreEqual(1, alpha);
        CampaignArrowGraphic.Evaluate(.7f, .35f, .8f, .35f, out progress, out alpha);
        Assert.AreEqual(1, progress); Assert.AreEqual(1, alpha);
        CampaignArrowGraphic.Evaluate(1.325f, .35f, .8f, .35f, out progress, out alpha);
        Assert.That(alpha, Is.EqualTo(.5f).Within(.0001));
        CampaignArrowGraphic.Evaluate(1.5f, .35f, .8f, .35f, out _, out alpha); Assert.AreEqual(0, alpha);
        CampaignArrowGraphic.Evaluate(0, 0, 0, 0, out _, out alpha); Assert.AreEqual(0, alpha);
    }
    [Test] public void MeshRevealReuseResizeAndDisable()
    {
        var root = new GameObject("ArrowTest", typeof(RectTransform), typeof(CanvasRenderer));
        try
        {
            var arrow = root.AddComponent<CampaignArrowGraphic>(); arrow.Initialize(new MoveArrowStyle());
            using (var vh = new VertexHelper())
            {
                void Populate() => typeof(CampaignArrowGraphic).GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(arrow, new object[] { vh });
                foreach (var size in new[] { new Vector2(1360, 744), new Vector2(906, 496), new Vector2(1813, 992) })
                {
                    arrow.rectTransform.sizeDelta = size;
                    foreach (var target in new[] { new Vector2(.6f, 0), new Vector2(0, .6f), new Vector2(-.6f, .6f) })
                    {
                        arrow.Play(Vector2.zero, target, .3f, .3f, Color.white);
                        Populate(); Assert.AreEqual(0, vh.currentVertCount);
                        arrow.SetFrame(.175f); Populate(); int partialCount = vh.currentVertCount; Assert.Greater(partialCount, 0);
                        var v = new UIVertex(); var direction = new Vector2(target.x * size.x, target.y * size.y).normalized;
                        float partialFront = float.MinValue;
                        for (int i = 0; i < vh.currentVertCount; i++) { vh.PopulateUIVertex(ref v, i); partialFront = Math.Max(partialFront, Vector2.Dot(v.position, direction)); }
                        arrow.SetFrame(.7f); Populate(); Assert.Greater(vh.currentVertCount, 0);
                        float fullFront = float.MinValue;
                        for (int i = 0; i < vh.currentVertCount; i++)
                        {
                            vh.PopulateUIVertex(ref v, i); Assert.True(arrow.rectTransform.rect.Contains(v.position));
                            fullFront = Math.Max(fullFront, Vector2.Dot(v.position, direction));
                        }
                        Assert.Greater(fullFront, partialFront);
                        arrow.SetFrame(arrow.Duration); Populate(); Assert.False(arrow.IsPlaying); Assert.AreEqual(0, vh.currentVertCount);
                    }
                }
                arrow.Play(Vector2.zero, Vector2.one, 0, 0, Color.white); arrow.SetFrame(.7f);
                root.SetActive(false); Populate(); Assert.False(arrow.IsPlaying); Assert.AreEqual(0, vh.currentVertCount);
                Assert.False(arrow.raycastTarget);
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
    [Test] public void RefreshOfLoadedOperationDoesNotAnimateAndMapReusesOneGraphic()
    {
        var root = new GameObject("Attack map lifecycle", typeof(RectTransform));
        try
        {
            var state = new CampaignMapGenerator().Generate(851, Faction.Ashen);
            var source = state.Regions[0]; var target = state.Regions.Find(r => r.RegionId == source.NeighborIds[0]);
            state.PendingOperation = new CampaignOperation { OperationId = "loaded-operation", SourceRegionId = source.RegionId, TargetRegionId = target.RegionId, AttackerFaction = source.OwnerFaction };
            var map = root.AddComponent<CampaignMapView>(); map.Build(state.Regions, _ => { }, _ => { });
            map.Refresh(state, null, null, false); Assert.False(map.AttackAnimationPlaying);
            map.PlayAttack(state.PendingOperation); Assert.True(map.AttackAnimationPlaying);
            map.CancelAttackAnimation(); map.Refresh(state, null, null, false); Assert.False(map.AttackAnimationPlaying);
            map.PlayAttack(state.PendingOperation); root.SetActive(false); Assert.False(map.AttackAnimationPlaying);
            root.SetActive(true); map.Refresh(state, null, null, false); Assert.False(map.AttackAnimationPlaying);
            state.PendingOperation.ResultRecorded = true; map.PlayAttack(state.PendingOperation); Assert.False(map.AttackAnimationPlaying);
            Assert.AreEqual(1, root.GetComponentsInChildren<CampaignArrowGraphic>().Length);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
}

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
    [Test] public void DeferredAiSelectionHasNoResultAndResolvesIdenticallyToImmediateFlow()
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
