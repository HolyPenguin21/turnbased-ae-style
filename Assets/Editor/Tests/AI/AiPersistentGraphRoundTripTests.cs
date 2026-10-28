#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Game.Ai;
using Game.Ai.V2;
using Game.Ai.V2.Initiative;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Setup;
using Game.Turns;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Integration of the actual aggregate boundary; no file format and no native Unity objects.
    public sealed partial class AiPersistentGraphRoundTripTests
    {
        private static readonly HexCoord Focus = new HexCoord(5, 6);
        private static readonly HexCoord Home = new HexCoord(0, 1);
        private static PlayerSetupData Player(string id, int color = 0) => new PlayerSetupData { PlayerId = id, ColorIndex = color };

        [SetUp, TearDown]
        public void ClearOwners()
        {
            MissionIntentRegistry.Clear();
            AiMapMemory.Clear();
            AirSortieRegistry.Clear();
            AiRadarStateRegistry.Clear();
            ForceBaselineRegistry.Clear();
            PreparationDeliveryMemory.Clear();
            AiReconMemory.Clear(); // Includes Intel, patrols, recon sorties/coverage/deficits/trails.
            ResourceStarvationRegistry.Clear();
            DevelopmentInvestmentGate.Clear();
            AiAllocatorStateRegistry.Clear();
            InitiativeAnalyticsHistory.Clear();
            InitiativePublicHistory.Clear();
            OperationContinuationWindow.ClearAll();
            DevelopmentDiversity.ClearAll();
            ApTurnPressure.ClearAll();
            LifecycleReturnPolicy.ClearAll();
            VisionSystem.Clear();
        }

        [Test]
        public void AggregateCaptureResetRestoreRebindsEveryOwnerAndContinuesWithoutPlanning()
        {
            var oldOwner = Player("ai");
            var oldEnemy = Player("enemy", 7);
            var oldArmy = new ArmyData { Owner = oldOwner, Hex = Home };
            var oldHero = new UnitData();
            var evidenceCard = new CardData(null);
            var operatorCard = new CardData(null);
            var buildCard = new CardData(null);
            int historicalArmyId = ArmyData.IdentityHighWaterMark + 40;
            Seed(oldOwner, oldEnemy, oldArmy, oldHero, evidenceCard, operatorCard, buildCard, historicalArmyId);
            var oldCodec = new ObjectiveCodec(_ => buildCard, _ => oldHero);
            var retained = new HashSet<CardData>();
            var saved = new AiPersistentState<ObjectiveState>(p => p.PlayerId, oldCodec, c => retained.Add(c));
            Assert.That(retained, Does.Contain(evidenceCard), "Economy-only evidence must retain its card outside any hand.");
            Assert.That(retained, Does.Contain(operatorCard), "Development-only evidence must retain its card outside any hand.");
            AssertAllOwnersPopulated(saved);
            var before = Fields(saved);
            var originalObjectives = MissionIntentRegistry.Peek(oldOwner).All.ToDictionary(x => x.Kind, x => x.Objective);
            var expectedContinuation = Continue(oldOwner, oldArmy.Id);
            var expectedAfterContinuation = Fields(new AiPersistentState<ObjectiveState>(p => p.PlayerId, oldCodec));

            // Poison outgoing live state after capture, including nested mutable objectives.
            MissionIntentRegistry.Peek(oldOwner).All.First(x => x.Scout != null).Scout.FocusHex = new HexCoord(99, 99);
            MissionIntentRegistry.Peek(oldOwner).All.First(x => x.Development != null).Development.Hero = new UnitData();
            AiRadarStateRegistry.GetOrCreate(oldOwner).Smoothed[DesireAxis.Recon] = 999f;
            oldEnemy.IsEliminated = true;
            oldArmy.Hex = new HexCoord(88, 88);
            ClearOwners();
            Assert.IsNull(MissionIntentRegistry.Peek(oldOwner));
            Assert.IsEmpty(AirSortieRegistry.For(oldOwner));
            Assert.AreEqual(int.MinValue, AiReconMemory.ObservedTurn(oldOwner));

            var owner = Player("ai");
            var enemy = Player("enemy", 7);
            var army = ArmyData.WithIdentity(oldArmy.Id);
            army.Owner = owner; army.Hex = Home;
            var hero = UnitData.WithRuntimeId(oldHero.RuntimeId);
            var cards = new[] { evidenceCard, operatorCard, buildCard }.ToDictionary(x => x.RuntimeId,
                x => CardData.WithRuntimeId(null, x.RuntimeId));
            var codec = new ObjectiveCodec(id => cards[id], id => id == hero.RuntimeId ? hero : null);
            PlayerSetupData ResolvePlayer(string id) => id == owner.PlayerId ? owner : id == enemy.PlayerId ? enemy : null;
            Assert.Throws<InvalidOperationException>(() => saved.Restore(ResolvePlayer, _ => army, id => cards[id], codec, null));
            int visibilityNotifications = 0;
            Action<PlayerSetupData> onVisibility = _ => visibilityNotifications++;
            VisionSystem.VisibilityChanged += onVisibility;
            try
            {
                using (MatchRestoreScope.Begin())
                    saved.Restore(ResolvePlayer, id => id == army.Id ? army : null, id => cards[id], codec, null);
            }
            finally { VisionSystem.VisibilityChanged -= onVisibility; }
            Assert.AreEqual(0, visibilityNotifications);
            Assert.IsEmpty(VisionSystem.VisibleHexesFor(owner), "Restoring AI facts must not recompute or learn visibility.");
            Assert.IsNull(MissionIntentRegistry.Peek(oldOwner));
            Assert.IsEmpty(AirSortieRegistry.For(oldOwner));
            Assert.AreSame(army, AirSortieRegistry.For(owner).Single().Army);
            var missions = MissionIntentRegistry.Peek(owner);
            Assert.AreSame(owner, missions.Owner);
            foreach (var mission in missions.All)
                Assert.AreNotSame(originalObjectives[mission.Kind], mission.Objective, "The test codec must create fresh detached objectives.");
            Assert.AreNotSame(oldArmy, army);
            Assert.AreNotSame(oldHero, hero);
            foreach (var card in cards)
                Assert.AreNotSame(new[] { evidenceCard, operatorCard, buildCard }.Single(x => x.RuntimeId == card.Key), card.Value);
            Assert.AreSame(hero, missions.All.Single(x => x.Development != null).Development.Hero);
            Assert.AreSame(cards[buildCard.RuntimeId], missions.All.Single(x => x.Economy != null).Economy.BuildCard);
            Assert.AreSame(cards[operatorCard.RuntimeId], missions.Development.GeneratedOperatorFor(Focus, ResearchProductionMode.Research, 11));
            Assert.AreSame(enemy, AiMapMemory.KnownSightingOf(owner, historicalArmyId).Value.Owner);
            Assert.AreSame(enemy, AiReconMemory.Historical(owner, null).Single().Owner);
            Assert.AreEqual(4, AiReconMemory.Historical(owner, null).Single().LastObservedTurn);
            Assert.AreEqual(8, AiReconMemory.ObservedTurn(owner), "The restore must not scan the world at the resume turn.");
            Assert.IsTrue(AiReconIntelMemory.TryGetIntelAge(owner, Focus, 11, out int age));
            Assert.AreEqual(3, age);
            Assert.IsTrue(missions.TryGetRetreatWitness(AttackTargetRef.For(Focus, enemy, AttackTargetKind.Base), out var witness));
            Assert.AreSame(enemy, witness.Target.ExpectedOwner);
            Assert.AreEqual(34, witness.EnemyFingerprint);
            Assert.IsFalse(OperationContinuationWindow.IsSettled(owner, 10));
            Assert.IsFalse(OperationContinuationWindow.IsMobilizationOpen(owner, 10));
            Assert.Greater(new ArmyData().Id, historicalArmyId, "A stale observed army must reserve its retired identity.");

            var recaptured = new AiPersistentState<ObjectiveState>(p => p.PlayerId, codec);
            foreach (var field in before)
                Assert.AreEqual(field.Value, Fields(recaptured)[field.Key], "Aggregate owner changed: " + field.Key);
            // Full detached graph equality precedes reads that consume evidence/advance clocks.
            Assert.AreEqual(expectedContinuation, Continue(owner, army.Id));
            var afterContinuation = Fields(new AiPersistentState<ObjectiveState>(p => p.PlayerId, codec));
            foreach (var field in expectedAfterContinuation)
                Assert.AreEqual(field.Value, afterContinuation[field.Key], "Continuation changed owner: " + field.Key);
            // Reusing the canonical retained card must update the existing private dictionary
            // entry, rather than create another entry under a stale CardData reference.
            missions.Economy.RecordBaseExpansionDeliveryProgress(11, cards[evidenceCard.RuntimeId], Focus);
            var evidence = missions.Economy.CapturePersistentState().BaseDelivery;
            Assert.IsEmpty(evidence.Failures);
            Assert.AreEqual(1, evidence.ProgressTurns.Count);
            Assert.AreEqual(11, evidence.ProgressTurns[(evidenceCard.RuntimeId, Focus)]);
        }

        private static void Seed(PlayerSetupData owner, PlayerSetupData enemy, ArmyData army, UnitData hero,
            CardData evidence, CardData generated, CardData build, int historicalId)
        {
            var sighting = new AiMapMemory.KnownEnemySighting(Focus, enemy, "frozen old contact", 2, 30f, 12f,
                Array.Empty<WorthIt.DefenderProfile>(), seenTurn: 4, armyId: historicalId, maxMovement: 3);
            AiMapMemory.RememberForTest(owner, sighting);
            AiMapMemory.RememberBuildingForTest(owner, new AiMapMemory.KnownBuilding(Focus, enemy, false, null,
                collectedAmounts: null, isBase: true, defense: 18.5f, seenTurn: 4));
            VisionSystem.RestoreState(new VisionState { Players = new List<PlayerVisionState>
                { new PlayerVisionState { PlayerId = owner.PlayerId, Visible = new List<HexCoord> { Focus } } } }, _ => owner);
            AiReconMemory.Observe(owner, 8, 3, new[] { sighting });
            var missions = MissionIntentRegistry.GetOrCreate(owner);
            IMissionObjective[] objectives =
            {
                new ScoutIntent { Kind = ScoutTargetKind.Explore, RequiresStealth = true, FocusHex = Focus },
                new EconomyIntent { Kind = EconomyTaskKind.ReturnBuilder, TargetHex = Home, BuilderArmyId = army.Id,
                    BuildCard = build, SafeReturnHex = Home, ArrivalTurn = 8, LastIncomeOpportunityTurn = 9,
                    BuildApCost = 2.5f, IntrinsicValue = 25f, BuildValue = 9f, MinimumFollowupAp = 3f, Loaned = true },
                new DevelopmentIntent { FacilityHex = Focus, Mode = ResearchProductionMode.Research, Hero = hero,
                    HeroKey = "historical-operator", IntrinsicValue = 17.5f },
            };
            foreach (var objective in objectives)
            {
                var row = new MissionIntent { Kind = objective.Kind, Objective = objective, PreferredMoverArmyId = army.Id,
                    CreatedTurn = 3, TurnsActive = 7, LastReconciledTurn = 10, LastProgressTurn = 8,
                    LastProtectedTurn = 9, StallTurns = 2, CumulativeApSpent = 13.5f, StepsMovedTotal = 6,
                    Funding = CommitmentTier.Hard, Status = IntentStatus.Suspended, Suspended = SuspendReason.CapabilityUnavailable,
                    LastIntrinsicValue = 23f, LastAttemptKey = new StableMissionKey(objective.Kind, 0, army.Id, 5, 6) };
                row.IntentKey = objective.IntentKey(row.PreferredMoverArmyId);
                missions.Put(row);
            }
            missions.Economy.RecordEndedOutboundBuilder(army.Id, Focus, 8f);
            missions.Economy.SuppressExtractionDelivery(10, ResourceType.Tech, Focus);
            missions.Economy.RecordExtractionDeliveryFailure(9, ResourceType.Materials, Home);
            missions.Economy.RecordExtractionDeliveryProgress(10, ResourceType.Materials, Home);
            missions.Economy.RecordBaseExpansionDeliveryFailure(10, evidence, Focus);
            missions.Development.RememberGeneratedDevelopmentOperator(generated, Focus, ResearchProductionMode.Research, 10);
            missions.PutRetreatWitness(new AttackRetreatWitness { Target = AttackTargetRef.For(Focus, enemy, AttackTargetKind.Base),
                OwnArmyId = army.Id, EnemyArmyId = historicalId, EnemyHex = Focus, EnemyFingerprint = 34, OwnFingerprint = 56,
                Turn = 9, Reason = "unwinnable historical contact" });
            AirSortieRegistry.Add(owner, new AirSortie { Army = army, Kind = AirSortieKind.Rebase, TargetHex = Focus,
                LandingHex = Home, Outbound = false, HeldTurn = 10 });
            var radar = AiRadarStateRegistry.GetOrCreate(owner); radar.LastTurn = 10;
            radar.Smoothed.Add(DesireAxis.Recon, 0.375f); radar.Smoothed.Add(DesireAxis.Economy, 0.625f);
            ForceBaselineRegistry.RecordStart(owner, 81f);
            PreparationDeliveryMemory.MarkNoChain(owner, army.Id, 10, new[] { "held-card", "other-card" });
            var patrol = ReconPatrolStateRegistry.GetOrCreate(owner, army.Id, Home, Focus, ReconMode.Refresh, 7);
            patrol.LastProgressTurn = 8; patrol.LastModeSwitchTurn = 9; patrol.LastStrategicReassignmentTurn = 10;
            ReconPatrolStateRegistry.RecordEscape(owner, army.Id, new ReconEscape { FromHex = Focus,
                Cause = ReconEscapeCause.Threat, Risk = 0.6f, ThreatArmyId = historicalId, Turn = 9 });
            int retired = ReconAirSortieRegistry.GetOrCreate(owner, historicalId, Home).SortieId;
            AirReconCoverageRegistry.Record(owner, Focus, 10, retired);
            ReconAirSortieRegistry.Retire(owner, historicalId);
            var sortie = ReconAirSortieRegistry.GetOrCreate(owner, army.Id, Home);
            sortie.Phase = ReconAirPhase.Return; sortie.ChosenLandingHex = Home; sortie.HasChosenLanding = true;
            sortie.HasClaim = true; sortie.BestOutboundStepScore = 7.25f; sortie.LastProcessedTurn = 10;
            sortie.LastDecisionReason = "fuel margin"; sortie.RecordStep(Home); sortie.RecordStep(Focus);
            ReconCapacityDeficitRegistry.RegisterAndCheck(owner, 9, ReconDeficitKind.GroundTraversal, 1, out _);
            ReconCapacityDeficitRegistry.RegisterAndCheck(owner, 10, ReconDeficitKind.GroundTraversal, 1, out _);
            ScoutTrailRegistry.RecordStep(owner, army.Id, Home, Focus);
            ResourceStarvationRegistry.RecordVerifiedBlock(owner, ResourceType.Tech, 8f, 2f, 1f, 31f, 10);
            ResourceStarvationRegistry.ReplaceOperationalForecast(owner, 10, "sorties", new Dictionary<string, ResourceBundle>
                { ["held-wing"] = new ResourceBundle { Energy = 4f, Materials = 1f } });
            ResourceStarvationRegistry.DecayOncePerTurn(owner, 10);
            for (int turn = 8; turn <= 10; turn++)
            {
                DevelopmentInvestmentGate.Observe(owner, new WorldSnapshot { TurnNumber = turn,
                    Development = new DevelopmentReadiness { InvestmentSurplusByType = new ResourceBundle
                        { Human = 3f, Energy = 4f, Materials = 5f, Tech = 6f } } });
                InitiativeAnalyticsHistory.Record(owner, new InitiativeTurnRecord(5, 10 + turn, 8 + turn, 2, 4, 1, true));
                InitiativePublicHistory.RecordFinalBonusTokens(owner, turn - 7);
                ApTurnPressure.Record(owner, turn, new ApTurnMeasure(10, 0, turn, 1, 2f, 1, 3f, 1));
            }
            AiAllocatorStateRegistry.GetOrCreate(owner).StartCooldown(CooldownKey(army.Id), 9, 11, "NoObservationVantage");
            OperationContinuationWindow.HoldMobilization(owner, 10);
            OperationContinuationWindow.Settle(owner, 10); OperationContinuationWindow.SetMobilizationOpen(owner, 10, true);
            // The authored asset is deliberately not instantiated in this native-free fixture.
            DevelopmentDiversity.RestorePersistentState(new Dictionary<string, IReadOnlyList<(int, string)>>
                { [owner.PlayerId] = new List<(int, string)> { (8, "upgrade-A"), (9, "upgrade-B"), (10, "upgrade-A") } }, _ => owner);
            LifecycleReturnPolicy.RecordWait(owner, MissionIntentKey.ForScout(ScoutTargetKind.Explore, Focus), 10);
        }

        private static StableMissionKey CooldownKey(int armyId) => new StableMissionKey(MissionKind.Scout, 0, armyId, 5, 6);

        // Only continuation readers and bounded state machines: no scan, planner, allocator pass,
        // generation, turn startup or hidden-world query is used to supply a missing saved fact.
        private static string Continue(PlayerSetupData owner, int armyId)
        {
            var result = new List<object>();
            var missions = MissionIntentRegistry.Peek(owner);
            result.AddRange(missions.All.OrderBy(x => x.Kind).Select(x => (object)$"{x.IntentKey}/{x.LastReconciledTurn}/{x.LastProtectedTurn}/{x.StallTurns}/{x.CumulativeApSpent}"));
            var allocator = AiAllocatorStateRegistry.Peek(owner);
            Assert.IsTrue(allocator.OnCooldown(CooldownKey(armyId), 11));
            Assert.IsFalse(allocator.OnCooldown(CooldownKey(armyId), 12));
            Assert.IsTrue(allocator.TryGetCooldown(CooldownKey(armyId), 11, out var cooldown));
            Assert.AreEqual("NoObservationVantage", cooldown.Reason);
            result.Add(allocator.OnCooldown(CooldownKey(armyId), 11)); result.Add(allocator.OnCooldown(CooldownKey(armyId), 12));
            result.Add(PreparationDeliveryMemory.NoChainRecently(owner, armyId, 11, "held-card"));
            result.Add(PreparationDeliveryMemory.NoChainRecently(owner, armyId, 12, "held-card"));
            result.Add(OperationContinuationWindow.IsMobilizationHeld(owner, 12));
            result.Add(OperationContinuationWindow.IsMobilizationHeld(owner, 13));
            result.Add(LifecycleReturnPolicy.MayWait(owner, MissionIntentKey.ForScout(ScoutTargetKind.Explore, Focus), 11));
            result.Add(missions.Economy.IsExtractionDeliverySuppressed(11, ResourceType.Tech, Focus));
            result.Add(missions.Economy.TakeEndedOutboundBuilders().Single().BuilderArmyId);
            result.Add(ApTurnPressure.WitnessedDemand(owner, 11));
            result.Add(InitiativePublicHistory.EstimatedBonusTokens(owner));
            result.Add(DevelopmentInvestmentGate.OpenMask(owner, 10));
            result.Add(ScoutTrailRegistry.IsImmediateReversal(owner, armyId, Home));
            result.Add(ResourceStarvationRegistry.ForecastOperationalNeed(owner, 11).Energy);
            result.Add(ResourceStarvationRegistry.Pressure(owner, ResourceType.Tech));
            ReconCapacityDeficitRegistry.RegisterAndCheck(owner, 11, ReconDeficitKind.GroundTraversal, 1, out int streak);
            Assert.AreEqual(2, streak);
            result.Add(streak);
            ReconAirSortieRegistry.TryGet(owner, armyId, out var live);
            result.Add(live.BeginTurn(10)); result.Add(live.BeginTurn(11));
            int nextId = ReconAirSortieRegistry.GetOrCreate(owner, armyId + 500, Home).SortieId;
            Assert.Greater(nextId, live.SortieId);
            Assert.IsTrue(AirReconCoverageRegistry.RecentlyCoveredByOther(owner, Focus, 11, 3, nextId));
            result.Add(nextId);
            result.Add(AirReconCoverageRegistry.RecentlyCoveredByOther(owner, Focus, 11, 3, nextId));
            return string.Join("|", result.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
        }

        private static void AssertAllOwnersPopulated(AiPersistentState<ObjectiveState> state)
        {
            Assert.AreEqual(22, Fields(state).Count, "Keep the integration checklist in sync with aggregate additions.");
            foreach (var field in state.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                object value = field.GetValue(state);
                var rows = value as IEnumerable;
                if (rows == null)
                {
                    string nested = field.Name == "Map" ? "Players" : field.Name == "Recon" ? "Observations"
                        : field.Name == "Intel" ? "Observations" : "Players";
                    rows = (IEnumerable)value.GetType().GetField(nested, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
                }
                Assert.IsTrue(rows.Cast<object>().Any(), field.Name + " must contain a nonempty owner fact.");
            }
        }

        // This structural assertion inspects all captured fields, including private evidence DTOs.
        // It is an assertion helper, never an objective codec or a reconstruction mechanism.
        private static Dictionary<string, string> Fields(object value) => value.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .ToDictionary(f => f.Name, f => Describe(f.GetValue(value)));
        private static string Describe(object value)
        {
            if (value == null) return "null";
            var type = value.GetType();
            if (value is PlayerSetupData || value is ArmyData || value is UnitData || value is CardData)
                throw new AssertionException("Captured DTO retained live identity: " + type.Name);
            if (type.IsPrimitive || type.IsEnum || value is string || value is decimal)
                return type.Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
            if (value is IEnumerable rows)
            {
                var entries = rows.Cast<object>().Select(Describe);
                bool dictionary = type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
                return "[" + string.Join(",", dictionary ? entries.OrderBy(x => x, StringComparer.Ordinal) : entries) + "]";
            }
            return type.Name + "{" + string.Join(",", Fields(value).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value)) + "}";
        }

        // Narrow, explicitly tagged TEST DTOs. File codecs for all six production objective
        // types remain future work. These payloads contain copied values and numeric identities;
        // they never retain or return the source objective, CardData, or UnitData.
        private sealed class ObjectiveState
        {
            internal readonly MissionKind Tag;
            internal readonly ScoutTargetKind ScoutKind;
            internal readonly bool Stealth;
            internal readonly HexCoord Hex;
            internal readonly EconomyTaskKind EconomyKind;
            internal readonly ResourceType? Resource;
            internal readonly int? Builder, Collector, Source, CardId, HeroId;
            internal readonly int Yield, Arrival, IncomeTurn;
            internal readonly HexCoord? Return;
            internal readonly float Ap, BuildValue, MinimumFollowup, DevelopmentValue;
            internal readonly float? Intrinsic;
            internal readonly bool Loaned;
            internal readonly MissionIntentKey LoanSource;
            internal readonly ResearchProductionMode Mode;
            internal readonly string HeroKey;
            internal ObjectiveState(ScoutIntent x) { Tag = MissionKind.Scout; ScoutKind = x.Kind; Stealth = x.RequiresStealth; Hex = x.FocusHex; }
            internal ObjectiveState(EconomyIntent x)
            {
                if (x.BuildResourceCost != null) throw new NotSupportedException("This test payload does not exercise resource-cost codecs.");
                Tag = MissionKind.Economy; EconomyKind = x.Kind; Hex = x.TargetHex; Resource = x.ResourceType;
                Builder = x.BuilderArmyId; Collector = x.CollectorArmyId; Source = x.CollectorSourceArmyId; Yield = x.ExpectedMarginalYield;
                Return = x.SafeReturnHex; Arrival = x.ArrivalTurn; IncomeTurn = x.LastIncomeOpportunityTurn; CardId = x.BuildCard?.RuntimeId;
                Ap = x.BuildApCost; Intrinsic = x.IntrinsicValue; BuildValue = x.BuildValue; MinimumFollowup = x.MinimumFollowupAp;
                Loaned = x.Loaned; LoanSource = x.LoanSource;
            }
            internal ObjectiveState(DevelopmentIntent x) { Tag = MissionKind.Development; Hex = x.FacilityHex;
                Mode = x.Mode; HeroId = x.Hero?.RuntimeId; HeroKey = x.HeroKey; DevelopmentValue = x.IntrinsicValue; }
        }

        private sealed class ObjectiveCodec : IMissionObjectiveStateCodec<ObjectiveState>
        {
            private readonly Func<int, CardData> _card;
            private readonly Func<int, UnitData> _unit;
            internal ObjectiveCodec(Func<int, CardData> card, Func<int, UnitData> unit) { _card = card; _unit = unit; }
            public ObjectiveState CaptureScout(ScoutIntent x) => new ObjectiveState(x);
            public ObjectiveState CaptureEconomy(EconomyIntent x) => new ObjectiveState(x);
            public ObjectiveState CaptureDevelopment(DevelopmentIntent x) => new ObjectiveState(x);
            public ObjectiveState CaptureRaid(RaidIntent x) => throw new NotSupportedException();
            public ObjectiveState CaptureActiveDefence(ActiveDefenceIntent x) => throw new NotSupportedException();
            public ObjectiveState CaptureAttack(AttackIntent x) => throw new NotSupportedException();
            public IMissionObjective Restore(MissionKind kind, ObjectiveState x)
            {
                if (kind != x.Tag) throw new ArgumentException("Objective tag mismatch");
                switch (kind)
                {
                    case MissionKind.Scout: return new ScoutIntent { Kind = x.ScoutKind, RequiresStealth = x.Stealth, FocusHex = x.Hex };
                    case MissionKind.Economy: return new EconomyIntent { Kind = x.EconomyKind, TargetHex = x.Hex,
                        ResourceType = x.Resource, BuilderArmyId = x.Builder, CollectorArmyId = x.Collector, CollectorSourceArmyId = x.Source,
                        ExpectedMarginalYield = x.Yield, SafeReturnHex = x.Return, ArrivalTurn = x.Arrival, LastIncomeOpportunityTurn = x.IncomeTurn,
                        BuildCard = x.CardId.HasValue ? _card(x.CardId.Value) : null, BuildApCost = x.Ap, IntrinsicValue = x.Intrinsic,
                        BuildValue = x.BuildValue, MinimumFollowupAp = x.MinimumFollowup, Loaned = x.Loaned, LoanSource = x.LoanSource };
                    case MissionKind.Development: return new DevelopmentIntent { FacilityHex = x.Hex, Mode = x.Mode,
                        Hero = x.HeroId.HasValue ? _unit(x.HeroId.Value) : null, HeroKey = x.HeroKey, IntrinsicValue = x.DevelopmentValue };
                    default: throw new NotSupportedException();
                }
            }
        }
    }
}
#endif
