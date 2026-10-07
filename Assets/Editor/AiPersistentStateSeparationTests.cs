#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiPersistentStateSeparationTests
    {
        [TearDown]
        public void Reset() => MissionIntentRegistry.Clear();

        [Test]
        public void EconomySuppressionPersistsButReconUsageDoesNot()
        {
            var p = new PlayerSetupData(); var site = new HexCoord(4, 3);
            var persistent = MissionIntentRegistry.GetOrCreate(p);
            int stall = Math.Max(1, AiConfigV2.commitmentStallTurns);
            for (int t = 1; t <= stall; t++)
                persistent.Economy.RecordExtractionDeliveryFailure(t, ResourceType.Energy, site);
            using (var session = AiTurnSession.Begin(p, null, null, null, stall))
                session.Recon.MarkReconGroundActorUsed(stall, 0);
            using var next = AiTurnSession.Begin(p, null, null, null, stall + 1);
            Assert.That(next.PersistentState.Economy, Is.SameAs(persistent.Economy));
            Assert.That(next.PersistentState.Economy.IsExtractionDeliverySuppressed(stall + 1, ResourceType.Energy, site), Is.True);
            Assert.That(next.Recon.ReconGroundActorsUsedThisTurn(stall + 1), Is.Empty);
        }

        [Test]
        public void DevelopmentClaimsKeepIdentityUniquenessAndOriginalExpiry()
        {
            var p = new PlayerSetupData(); var site = new HexCoord(4, 3);
            var persistent = MissionIntentRegistry.GetOrCreate(p);
            var previous = new CardData(new CardDefinition { cardType = CardType.Hero });
            var current = new CardData(new CardDefinition { cardType = CardType.Hero });
            persistent.Development.RememberGeneratedDevelopmentOperator(previous, site, ResearchProductionMode.Production, 4);
            persistent.Development.RememberGeneratedDevelopmentOperator(current, site, ResearchProductionMode.Production, 4);
            using (var first = AiTurnSession.Begin(p, null, null, null, 4)) { }
            using var next = AiTurnSession.Begin(p, null, null, null, 5);
            Assert.That(next.PersistentState.Development.ReconcileGeneratedDevelopmentOperators(5, (c, h, m) => true),
                Is.EqualTo(new[] { current }));
            Assert.That(persistent.Development.ReconcileGeneratedDevelopmentOperators(
                4 + Math.Max(1, AiConfigV2.commitmentStallTurns) + 1, (c, h, m) => true), Is.Empty);
        }

        [Test]
        public void DetachedCompatibilityStoresNeverShareReconState()
        {
            var a = new MissionIntentState(); var b = new MissionIntentState();
            a.ReconTurn(3).MarkReconGroundActorUsed(3, 0);
            Assert.That(b.ReconTurn(3).ReconGroundActorsUsedThisTurn(3), Is.Empty);
            Assert.That(a.ReconTurn(3).ReconGroundActorsUsedThisTurn(3), Is.EqualTo(new[] { 0 }));
        }
    }
}
#endif
