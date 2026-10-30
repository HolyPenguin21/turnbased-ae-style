#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Turns;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiPersistencePreparationTests
    {
        private static PlayerSetupData Player(string id) => new PlayerSetupData { PlayerId = id };

        // Also before each test: other fixtures may leave owners without a match-local ID.
        [SetUp, TearDown]
        public void ClearOwners()
        {
            AiMapMemory.Clear();
            ForceBaselineRegistry.Clear();
            PreparationDeliveryMemory.Clear();
            AiAllocatorStateRegistry.Clear();
            MissionIntentRegistry.Clear();
            OperationContinuationWindow.ClearAll();
            ReconAirSortieRegistry.ClearAll();
            AirReconCoverageRegistry.ClearAll();
            InitiativePublicHistory.Clear();
        }

        [Test]
        public void PublicInitiativeHistoryKeepsOrderAndWindowAcrossPlayerRebinding()
        {
            var oldPlayer = Player("opponent");
            foreach (int tokens in new[] { 0, 1, 2, 3, 4, 5, 3 })
                InitiativePublicHistory.RecordFinalBonusTokens(oldPlayer, tokens);
            var saved = InitiativePublicHistory.CapturePersistentState(p => p.PlayerId);
            int estimated = InitiativePublicHistory.EstimatedBonusTokens(oldPlayer);
            InitiativePublicHistory.RecordFinalBonusTokens(oldPlayer, 0);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 3 }, saved["opponent"]);
            var loaded = Player("opponent");
            InitiativePublicHistory.RestorePersistentState(saved, id => id == loaded.PlayerId ? loaded : null);
            Assert.AreEqual(estimated, InitiativePublicHistory.EstimatedBonusTokens(loaded));
            Assert.AreEqual(0, InitiativePublicHistory.EstimatedBonusTokens(oldPlayer));
            InitiativePublicHistory.RecordFinalBonusTokens(loaded, 2);
            CollectionAssert.AreEqual(new[] { 2, 3, 4, 5, 3, 2 },
                InitiativePublicHistory.CapturePersistentState(p => p.PlayerId)["opponent"]);
        }

        [Test]
        public void InvalidPlayerBindingFailsBeforeOverwritingHistoricalBaseline()
        {
            var player = Player("one");
            ForceBaselineRegistry.RecordStart(player, 81f);
            var saved = ForceBaselineRegistry.CapturePersistentState(p => p.PlayerId);
            Assert.Throws<ArgumentException>(() => ForceBaselineRegistry.RestorePersistentState(saved, _ => null));
            Assert.Throws<ArgumentException>(() => ForceBaselineRegistry.RestorePersistentState(saved, _ => Player("another")));
            Assert.Throws<ArgumentException>(() => ForceBaselineRegistry.CapturePersistentState(_ => "alias"));
            Assert.IsTrue(ForceBaselineRegistry.TryGetStart(player, out float current));
            Assert.AreEqual(81f, current);
            var loaded = Player("one");
            ForceBaselineRegistry.RestorePersistentState(saved, _ => loaded);
            ForceBaselineRegistry.RecordStart(loaded, 4f);
            Assert.IsTrue(ForceBaselineRegistry.TryGetStart(loaded, out current));
            Assert.AreEqual(81f, current, "The first-turn ceiling is historical, not current force");
        }

        [Test]
        public void PreparationFailureStillBlocksExactlyTheFollowingTurn()
        {
            var oldPlayer = Player("one");
            PreparationDeliveryMemory.MarkNoChain(oldPlayer, 401, 12, new[] { "held-card" });
            var saved = PreparationDeliveryMemory.CapturePersistentState(p => p.PlayerId);
            PreparationDeliveryMemory.MarkNoChain(oldPlayer, 401, 14, new[] { "other-card" });
            var loaded = Player("one");
            PreparationDeliveryMemory.RestorePersistentState(saved, _ => loaded);
            Assert.IsTrue(PreparationDeliveryMemory.NoChainRecently(loaded, 401, 13, "held-card"));
            Assert.IsFalse(PreparationDeliveryMemory.NoChainRecently(loaded, 401, 14, "held-card"));
            Assert.IsFalse(PreparationDeliveryMemory.NoChainRecently(loaded, 401, 13, "other-card"));
        }

        [Test]
        public void AllocatorCooldownRestoresOriginalReasonAndExpiry()
        {
            var player = Player("one");
            var key = new StableMissionKey(MissionKind.Scout, 1, 20, 3, 4);
            AiAllocatorStateRegistry.GetOrCreate(player).StartCooldown(key, 6, 9, "NoObservationVantage");
            var saved = AiAllocatorStateRegistry.CapturePersistentState(p => p.PlayerId);
            AiAllocatorStateRegistry.GetOrCreate(player).PurgeExpired(10);
            var loaded = Player("one");
            AiAllocatorStateRegistry.RestorePersistentState(saved, _ => loaded);
            var state = AiAllocatorStateRegistry.Peek(loaded);
            Assert.IsTrue(state.TryGetCooldown(key, 9, out var cooldown));
            Assert.AreEqual(6, cooldown.StartedTurn);
            Assert.AreEqual("NoObservationVantage", cooldown.Reason);
            Assert.IsFalse(state.OnCooldown(key, 10));
        }

        [Test]
        public void MobilizationRestoresHoldWithoutRestoringOutgoingTurnStamps()
        {
            var player = Player("one");
            OperationContinuationWindow.HoldMobilization(player, 10);
            OperationContinuationWindow.Settle(player, 10);
            OperationContinuationWindow.SetMobilizationOpen(player, 10, true);
            var saved = OperationContinuationWindow.CapturePersistentState(p => p.PlayerId);
            var loaded = Player("one");
            OperationContinuationWindow.RestorePersistentState(saved, _ => loaded);
            Assert.IsTrue(OperationContinuationWindow.IsMobilizationHeld(loaded, 12));
            Assert.IsFalse(OperationContinuationWindow.IsMobilizationHeld(loaded, 13));
            Assert.IsFalse(OperationContinuationWindow.IsSettled(loaded, 10));
            Assert.IsFalse(OperationContinuationWindow.IsMobilizationOpen(loaded, 10));
        }

        [Test]
        public void RetiredReconSortieIdentityIsNotReusedByFutureCoverage()
        {
            var player = Player("one");
            var hex = new HexCoord(3, 4);
            int retiredId = ReconAirSortieRegistry.GetOrCreate(player, 10, hex).SortieId;
            AirReconCoverageRegistry.Record(player, hex, 8, retiredId);
            ReconAirSortieRegistry.Retire(player, 10);
            var sorties = ReconAirSortieRegistry.CapturePersistentState(p => p.PlayerId);
            var coverage = AirReconCoverageRegistry.CapturePersistentState(p => p.PlayerId);
            ReconAirSortieRegistry.ClearAll();
            AirReconCoverageRegistry.ClearAll();
            var loaded = Player("one");
            ReconAirSortieRegistry.RestorePersistentState(sorties, _ => loaded);
            AirReconCoverageRegistry.RestorePersistentState(coverage, _ => loaded);
            int nextId = ReconAirSortieRegistry.GetOrCreate(loaded, 11, hex).SortieId;
            Assert.Greater(nextId, retiredId);
            Assert.IsTrue(AirReconCoverageRegistry.RecentlyCoveredByOther(loaded, hex, 9, 3, nextId));
        }

        [Test]
        public void MapRestoreKeepsStaleObservationAndRebindsItsForeignOwner()
        {
            var observer = Player("observer");
            var enemy = Player("enemy");
            int armyId = ArmyData.IdentityHighWaterMark + 40;
            var sighting = new AiMapMemory.KnownEnemySighting(new HexCoord(2, 7), enemy,
                "last observed", 2, 30f, 12f, Array.Empty<WorthIt.DefenderProfile>(),
                seenTurn: 5, armyId: armyId, maxMovement: 3);
            AiMapMemory.RememberForTest(observer, sighting);
            AiMapMemory.RememberBuildingForTest(observer, new AiMapMemory.KnownBuilding(
                sighting.Hex, enemy, false, null, collectedAmounts: null, isBase: true, defense: 18.5f, seenTurn: 5));
            var saved = AiMapMemory.CapturePersistentState(p => p.PlayerId);
            var loadedObserver = Player("observer");
            var loadedEnemy = Player("enemy");
            using (MatchRestoreScope.Begin())
                AiMapMemory.RestorePersistentState(saved,
                    id => id == "observer" ? loadedObserver : id == "enemy" ? loadedEnemy : null);
            var restored = AiMapMemory.KnownSightingOf(loadedObserver, armyId);
            Assert.IsTrue(restored.HasValue);
            Assert.AreSame(loadedEnemy, restored.Value.Owner);
            var building = AiMapMemory.KnownBuildingAt(loadedObserver, sighting.Hex);
            Assert.IsTrue(building.HasValue);
            Assert.AreSame(loadedEnemy, building.Value.Owner);
            Assert.IsNull(building.Value.FacilityAbilities);
            Assert.IsNull(building.Value.CollectedAmounts);
            Assert.AreEqual(18.5f, building.Value.Defense);
            Assert.AreEqual(5, building.Value.SeenTurn);
            Assert.IsTrue(sighting.SameContent(new AiMapMemory.KnownEnemySighting(restored.Value.Hex, enemy,
                restored.Value.Name, restored.Value.MemberCount, restored.Value.DefenseSum, restored.Value.AttackSum,
                restored.Value.Defenders, seenTurn: restored.Value.SeenTurn, armyId: restored.Value.ArmyId,
                maxMovement: restored.Value.MaxMovement)));
            Assert.Greater(new ArmyData().Id, armyId, "Historical identities must not alias newly created armies");
            Assert.IsNull(AiMapMemory.KnownSightingOf(observer, armyId));
        }

        [Test]
        public void MissionRestoreKeepsClocksAndPrivateEconomyDevelopmentEvidence()
        {
            var owner = Player("one");
            var state = MissionIntentRegistry.GetOrCreate(owner);
            var focus = new HexCoord(5, 6);
            var objective = new ScoutIntent { Kind = ScoutTargetKind.Explore, RequiresStealth = true, FocusHex = focus };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Scout, Objective = objective, IntentKey = objective.IntentKey(17),
                PreferredMoverArmyId = 17, CreatedTurn = 3, TurnsActive = 7, LastReconciledTurn = 10,
                LastProgressTurn = 8, LastProtectedTurn = 9, StallTurns = 2, CumulativeApSpent = 13.5f,
                StepsMovedTotal = 6, Funding = CommitmentTier.Hard, Status = IntentStatus.Suspended,
                Suspended = SuspendReason.CapabilityUnavailable, LastIntrinsicValue = 23f,
            };
            state.Put(intent);
            state.Economy.RecordEndedOutboundBuilder(33, focus, 8f);
            state.Economy.SuppressExtractionDelivery(10, ResourceType.Tech, focus);
            var enemy = Player("enemy");
            enemy.ColorIndex = 7;
            var retreatTarget = AttackTargetRef.For(focus, enemy, AttackTargetKind.Base);
            state.PutRetreatWitness(new AttackRetreatWitness
            {
                Target = retreatTarget, OwnArmyId = 330, EnemyArmyId = 440, EnemyHex = focus,
                EnemyFingerprint = 34, OwnFingerprint = 56, Turn = 9, Reason = "unwinnable",
            });
            var generated = new CardData(null);
            state.Economy.RecordBaseExpansionDeliveryFailure(10, generated, focus);
            state.Development.RememberGeneratedDevelopmentOperator(generated, focus, ResearchProductionMode.Research, 10);
            var codec = new ScoutCodec();
            var retainedCards = new System.Collections.Generic.HashSet<CardData>();
            var saved = MissionIntentRegistry.CapturePersistentState(p => p.PlayerId, codec, c => retainedCards.Add(c));
            Assert.That(retainedCards, Does.Contain(generated), "Private evidence must expose cards retained outside the live hand.");
            intent.StallTurns = 99;
            objective.FocusHex = new HexCoord(99, 99);
            var loaded = Player("one");
            var reboundEnemy = Player("enemy");
            reboundEnemy.ColorIndex = 7;
            var reboundCard = CardData.WithRuntimeId(null, generated.RuntimeId);
            MissionIntentRegistry.RestorePersistentState(saved, id => id == "one" ? loaded : id == "enemy" ? reboundEnemy : null, codec,
                id => id == reboundCard.RuntimeId ? reboundCard : null);
            var restored = MissionIntentRegistry.Peek(loaded);
            Assert.AreSame(loaded, restored.Owner);
            Assert.IsFalse(restored.TryGet(objective.IntentKey(17), out _));
            var row = restored.All.Single();
            Assert.AreEqual(focus, row.Scout.FocusHex);
            Assert.AreEqual(2, row.StallTurns);
            Assert.AreEqual(10, row.LastReconciledTurn);
            Assert.AreEqual(9, row.LastProtectedTurn);
            Assert.AreEqual(13.5f, row.CumulativeApSpent);
            Assert.AreEqual(IntentStatus.Suspended, row.Status);
            Assert.IsTrue(restored.Economy.IsExtractionDeliverySuppressed(11, ResourceType.Tech, focus));
            Assert.AreEqual(33, restored.Economy.TakeEndedOutboundBuilders().Single().BuilderArmyId);
            Assert.AreSame(reboundCard, restored.Development.GeneratedOperatorFor(focus, ResearchProductionMode.Research, 11));
            Assert.AreEqual(1, restored.Economy.CapturePersistentState().BaseDelivery.Failures[(reboundCard.RuntimeId, focus)].Count);
            Assert.IsTrue(restored.TryGetRetreatWitness(AttackTargetRef.For(focus, reboundEnemy, AttackTargetKind.Base), out var retreat));
            Assert.AreSame(reboundEnemy, retreat.Target.ExpectedOwner);
            Assert.AreEqual(34, retreat.EnemyFingerprint);
            Assert.AreEqual(9, retreat.Turn);
        }

        private readonly struct ScoutState
        {
            internal readonly ScoutTargetKind Kind;
            internal readonly bool Stealth;
            internal readonly HexCoord Focus;
            internal ScoutState(ScoutIntent source) { Kind = source.Kind; Stealth = source.RequiresStealth; Focus = source.FocusHex; }
        }

        // A deliberately narrow test codec demonstrates the mandatory detached/tagged boundary;
        // production disk codecs for the other five kinds are explicitly a later stage.
        private sealed class ScoutCodec : IMissionObjectiveStateCodec<ScoutState>
        {
            public ScoutState CaptureScout(ScoutIntent x) => new ScoutState(x);
            public ScoutState CaptureRaid(RaidIntent x) => throw new NotSupportedException();
            public ScoutState CaptureEconomy(EconomyIntent x) => throw new NotSupportedException();
            public ScoutState CaptureDevelopment(DevelopmentIntent x) => throw new NotSupportedException();
            public ScoutState CaptureActiveDefence(ActiveDefenceIntent x) => throw new NotSupportedException();
            public ScoutState CaptureAttack(AttackIntent x) => throw new NotSupportedException();
            public IMissionObjective Restore(MissionKind kind, ScoutState state)
            {
                if (kind != MissionKind.Scout) throw new NotSupportedException();
                return new ScoutIntent { Kind = state.Kind, RequiresStealth = state.Stealth, FocusHex = state.Focus };
            }
        }
    }
}
#endif
