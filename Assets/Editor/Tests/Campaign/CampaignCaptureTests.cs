#if UNITY_INCLUDE_TESTS
using Game.Core;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Campaign;
using Game.Cards;
using Game.Players;
using NUnit.Framework;
using UnityEngine;

public sealed class CampaignCaptureTests
{
    private string directory;
    [SetUp] public void Setup()
    { directory = Path.Combine(Path.GetTempPath(), "campaign-capture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); }
    [TearDown] public void Cleanup() { Directory.Delete(directory, true); }
    private CampaignController PrepareManual(out CampaignOperation operation)
    {
        var state = new CampaignMapGenerator().Generate(37, Faction.Ashen, new CampaignGenerationSettings { RegionCount = 24 });
        state.CurrentTurnIndex = state.TurnOrder.IndexOf(state.HumanFaction);
        var controller = new CampaignController(state, new CampaignStore(directory));
        var pair = CampaignRules.LegalAttacks(state).First();
        controller.BeginAttack(pair.Source, pair.Target, true);
        controller.RegisterMatch("deck", "Human", "AI"); operation = controller.View.PendingOperation;
        return controller;
    }
    [TestCase(CampaignOutcome.AttackerVictory, 1)]
    [TestCase(CampaignOutcome.DefenderVictory, 0)]
    [TestCase(CampaignOutcome.Draw, 0)]
    public void ManualCapturePublishesOnceAfterCommittedRewardAcknowledgement(CampaignOutcome outcome, int expected)
    {
        var c = PrepareManual(out var op); int events = 0;
        c.RegionOwnershipChanged += change =>
        {
            events++; Assert.True(change.Matches(c.View));
            Assert.True(change.Matches(new CampaignStore(directory).Load(out _)), "Publish must follow durable save");
            Assert.AreEqual(op.SourceRegionId, change.SourceRegionId); Assert.AreEqual(op.TargetRegionId, change.TargetRegionId);
            Assert.AreEqual(op.DefenderFaction, change.PreviousOwner); Assert.AreEqual(op.AttackerFaction, change.NewOwner);
        };
        c.RecordManualResult(c.View.CampaignId, op.OperationId, op.MatchId, outcome);
        c.ApplyResult(); Assert.AreEqual(0, events, "Manual reward gate remains unchanged");
        c.AcknowledgeReward(op.MatchId); c.AcknowledgeReward(op.MatchId); c.ApplyResult();
        c.RecordManualResult(c.View.CampaignId, op.OperationId, op.MatchId, outcome);
        Assert.AreEqual(expected, events);
        c.Continue(op.OperationId); c.ApplyResult(); Assert.AreEqual(expected, events);
    }
    [Test]
    public void FailedOwnershipSavePublishesNothingAndRetryPublishesOnce()
    {
        var c = PrepareManual(out var op); int events = 0;
        c.RegionOwnershipChanged += _ => events++;
        c.RecordManualResult(c.View.CampaignId, op.OperationId, op.MatchId, CampaignOutcome.AttackerVictory);
        // Separate reward acknowledgement already committed, as on interrupted recovery.
        var resolved = c.Snapshot; resolved.PendingOperation.RewardAcknowledged = true;
        new CampaignStore(directory).Save(resolved); c = new CampaignController(resolved, new CampaignStore(directory));
        c.RegionOwnershipChanged += _ => events++;
        string blocked = Path.Combine(directory, "campaign-v1.json.tmp"); Directory.CreateDirectory(blocked);
        var error = Assert.Catch(() => c.ApplyResult());
        Assert.That(error, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        Assert.AreEqual(0, events); Assert.False(c.View.PendingOperation.OwnershipApplied);
        Assert.AreEqual(op.DefenderFaction, c.View.Regions.Find(r => r.RegionId == op.TargetRegionId).OwnerFaction);
        Directory.Delete(blocked); c.ApplyResult(); c.ApplyResult(); Assert.AreEqual(1, events);
    }
    [Test]
    public void LiveManualCaptureSurvivesControllerHandoffButIsConsumedOnlyOnce()
    {
        var c = PrepareManual(out var op);
        var bridge = typeof(CampaignMatchBridge);
        var bind = bridge.GetMethod("SetController", BindingFlags.NonPublic | BindingFlags.Static);
        var slot = bridge.GetField("pendingOwnershipChange", BindingFlags.NonPublic | BindingFlags.Static);
        var priorController = CampaignMatchBridge.Controller; var priorNotice = slot.GetValue(null);
        try
        {
            slot.SetValue(null, null); bind.Invoke(null, new object[] { c });
            c.RecordManualResult(c.View.CampaignId, op.OperationId, op.MatchId, CampaignOutcome.AttackerVictory);
            c.AcknowledgeReward(op.MatchId);
            // Campaign scene loads a new controller over the durable snapshot, retaining only the live receipt.
            var loaded = new CampaignController(new CampaignStore(directory).Load(out _), new CampaignStore(directory));
            bind.Invoke(null, new object[] { loaded });
            Assert.True(CampaignMatchBridge.TryTakeOwnershipChange(out var change)); Assert.True(change.Matches(loaded.View));
            Assert.False(CampaignMatchBridge.TryTakeOwnershipChange(out _));
            loaded.ApplyResult(); Assert.False(CampaignMatchBridge.TryTakeOwnershipChange(out _), "Reload/refresh cannot recreate an event");
            loaded.Continue(op.OperationId); Assert.False(change.Matches(loaded.View));
            Assert.False(change.Matches(new CampaignMapGenerator().Generate(37, Faction.Ashen)));
        }
        finally { bind.Invoke(null, new object[] { priorController }); slot.SetValue(null, priorNotice); }
    }
    [Test]
    public void FastAiResultsUseTheSameTriggerForWinsAndLosses()
    {
        bool win = false, loss = false;
        var resolver = new CampaignBattleResolver();
        var hero = new CardDefinition { authoredKey = "hero", cardType = CardType.Hero, commandRating = 5, fate = 3 };
        var unit = new CardDefinition { authoredKey = "unit", cardType = CardType.Unit, attack = 3, defenseRating = 3, hitPoints = 3, range = 2 };
        var deck = new CampaignDeck("Equal test decks", new[] { hero, unit, unit, unit, unit });
        for (int seed = 1; seed <= 40 && (!win || !loss); seed++)
        {
            var s = new CampaignMapGenerator().Generate(seed, Faction.Ashen, new CampaignGenerationSettings { RegionCount = 24 });
            var pair = s.Regions.SelectMany(r => r.NeighborIds.Select(id => (Source: r, Target: s.Regions.Find(t => t.RegionId == id))))
                .FirstOrDefault(p => p.Source.OwnerFaction != s.HumanFaction && p.Target.OwnerFaction != s.HumanFaction && p.Source.OwnerFaction != p.Target.OwnerFaction);
            if (pair.Source == null) continue; // Some valid maps separate the two AI territories by the human faction.
            s.CurrentTurnIndex = s.TurnOrder.IndexOf(pair.Source.OwnerFaction);
            var c = new CampaignController(s, new CampaignStore(directory)); int events = 0;
            c.RegionOwnershipChanged += change => { events++; Assert.True(change.Matches(c.View)); };
            c.BeginAttack(pair.Source.RegionId, pair.Target.RegionId, false); c.ResolveFast(deck, deck, resolver);
            bool captured = c.View.PendingOperation.Outcome == CampaignOutcome.AttackerVictory;
            Assert.AreEqual(captured ? 1 : 0, events);
            Assert.AreEqual(captured ? pair.Source.OwnerFaction : pair.Target.OwnerFaction,
                c.View.Regions.Find(r => r.RegionId == pair.Target.RegionId).OwnerFaction);
            var reloaded = new CampaignController(new CampaignStore(directory).Load(out _), new CampaignStore(directory));
            reloaded.RegionOwnershipChanged += _ => events++; reloaded.ApplyResult();
            Assert.AreEqual(captured ? 1 : 0, events);
            win |= captured; loss |= !captured;
        }
        Assert.True(win && loss, "Exercise both actual fast resolver outcomes");
    }
    [TestCase(1)] [TestCase(37)] [TestCase(94)]
    public void RevealStartsInsideTheTargetNearTheSharedBorderAndReachesEveryVertex(int seed)
    {
        var s = new CampaignMapGenerator().Generate(seed, Faction.Ashen);
        foreach (var source in s.Regions)
            foreach (int id in source.NeighborIds)
            {
                var target = s.Regions.Find(r => r.RegionId == id);
                var origin = CampaignCaptureEffect.FindOrigin(source, target);
                var projected = CampaignMapView.Project(origin);
                Assert.True(CampaignGeometry.Contains(target.PolygonVertices, origin));
                var boundary = target.PolygonVertices.ConvertAll(CampaignMapView.Project);
                Assert.True(CampaignGeometry.Contains(boundary, projected));
                Assert.That(CampaignRegionGraphic.SelectionGlow(projected, boundary, .02f), Is.GreaterThan(.7f));
                foreach (var rect in new[] { new Rect(-680, -370, 1360, 740), new Rect(-400, -600, 800, 1200) })
                {
                    float radius = CampaignCaptureEffect.RevealRadius(rect, target, origin);
                    foreach (var vertex in target.PolygonVertices)
                        Assert.That((CampaignMapView.ToPixel(rect, CampaignMapView.Project(vertex))
                            - CampaignMapView.ToPixel(rect, projected)).magnitude, Is.LessThanOrEqualTo(radius + .001f));
                }
            }
        Assert.AreEqual(CampaignRegionGraphic.VisibleCenter(s.Regions[0]), CampaignCaptureEffect.FindOrigin(null, s.Regions[0]));
    }
}

#endif
