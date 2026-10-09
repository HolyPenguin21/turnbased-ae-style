#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Stage E5 of the decoupling task: the decision frame owns the snapshot and what is derived
    // from it, and the freshness protocol. The services are scripted (no world); the expected call
    // sequences are written from the inline code the frame replaced (Pipeline.RunTurn @ 2444710f):
    //   RefreshDecisionFrame ........ Warm -> Recon -> AggressionFacts -> Aggression -> ResolveActive
    //                                 (no turn context) -> RefreshActors
    //   iteration start ............. if (!credit) { RefreshKnowledge; RefreshDecisionFrame }; credit = false
    //   first Phase A changed ....... RefreshKnowledge; RefreshDecisionFrame; Generate(every axis)
    //   re-admission changed ........ RefreshKnowledge; RefreshDecisionFrame; credit = true
    //   cold residual changed ....... ObserveSettled; RefreshDecisionFrame; Generate(every axis); credit = true
    //   wing formed ................. RefreshKnowledge; Recon; ResolveActive (no context); RefreshActors
    //   Phase B round ............... RefreshKnowledge; Recon; RefreshActors(persistent state)
    //   final ownership ............. RefreshActors(persistent state)
    public class AiDecisionFrameTests
    {
        private sealed class Rig
        {
            internal readonly List<string> Calls = new List<string>();
            internal int Serial;
            internal readonly FrameServices Services;

            internal Rig()
            {
                Services = new FrameServices
                {
                    RefreshKnowledge = s => { Calls.Add("knowledge"); return new WorldSnapshot { TurnNumber = ++Serial }; },
                    ObserveSettled = (s, stamp, r) => { Calls.Add("observe"); return new WorldSnapshot { TurnNumber = ++Serial }; },
                    WarmEstimates = s => Warm(),
                    EnumerateRecon = s => { Calls.Add("recon"); return new List<ReconObjective>(); },
                    RefreshAggressionFacts = s => Calls.Add("aggressionFacts"),
                    EnumerateAggression = s => { Calls.Add("aggression"); return new List<RaidObjective>(); },
                    ResolveActive = (s, recon, aggr, withCtx) =>
                    {
                        Calls.Add(withCtx ? "resolve(ctx)" : "resolve");
                        return new List<MissionIntent>();
                    },
                    RefreshActors = (intents, s, recon) => { Calls.Add("actors"); return null; },
                    RefreshPersistentActors = (s, recon) => { Calls.Add("persistentActors"); return null; },
                    GenerateDemands = (s, recon, aggr, intents, commitments, axes) =>
                    {
                        Calls.Add("generate:" + string.Join(",", axes.OrderBy(a => a)));
                        return new List<AxisDemand> { new AxisDemand { RequestingAxis = axes.First() } };
                    },
                };
            }

            private IEnumerator Warm() { Calls.Add("warm"); yield break; }

            internal DecisionFrame NewFrame() => new DecisionFrame(new WorldSnapshot(), Services);

            internal string Take()
            {
                string s = string.Join(" > ", Calls);
                Calls.Clear();
                return s;
            }
        }

        private static void Run(IEnumerator e)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(e);
            while (stack.Count > 0)
            {
                if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                if (stack.Peek().Current is IEnumerator nested) stack.Push(nested);
            }
        }

        private static readonly HashSet<DesireAxis> All = new HashSet<DesireAxis>(DesireAxes.All);

        [Test]
        public void TheStartOfTheTurnEnumeratesThenResolvesWithTheTurnContext()
        {
            var rig = new Rig();
            DecisionFrame f = rig.NewFrame();
            f.EnumerateObjectives();
            Assert.That(rig.Take(), Is.EqualTo("recon > aggression"));
            f.ResolveInitialOwnership();
            Assert.That(rig.Take(), Is.EqualTo("resolve(ctx) > actors"));
        }

        [Test]
        public void TheOperationalRefreshWarmsFirstAndNeverSkipsAnyStage()
        {
            var rig = new Rig();
            DecisionFrame f = rig.NewFrame();
            WorldSnapshot snapshot = f.Snapshot;
            Run(f.RefreshOperationalDecision());
            Assert.That(rig.Take(), Is.EqualTo("warm > recon > aggressionFacts > aggression > resolve > actors"));
            Assert.That(f.Snapshot, Is.SameAs(snapshot), "it never refreshes the snapshot itself");
            var recon = f.Recon; var intents = f.Intents;
            // no "revision unchanged -> skip": ResolveActive and RefreshActors are not pure reads
            Run(f.RefreshOperationalDecision());
            Assert.That(rig.Take(), Is.EqualTo("warm > recon > aggressionFacts > aggression > resolve > actors"));
            Assert.That(f.Recon, Is.Not.SameAs(recon));
            Assert.That(f.Intents, Is.Not.SameAs(intents));
        }

        [Test]
        public void AnAdmissionRefreshesWithoutACreditAndSpendsTheCreditOtherwise()
        {
            var rig = new Rig();
            DecisionFrame f = rig.NewFrame();
            Run(f.PrepareAdmission());
            Assert.That(rig.Take(), Is.EqualTo("knowledge > warm > recon > aggressionFacts > aggression > resolve > actors"));

            f.StartWithCredit(true);
            Run(f.PrepareAdmission());
            Assert.That(rig.Take(), Is.Empty, "a credit means the derived part is already current");
            Run(f.PrepareAdmission());
            Assert.That(rig.Take(), Does.StartWith("knowledge > warm"), "the credit was spent by the first admission");

            f.StartWithCredit(false);
            Run(f.PrepareAdmission());
            Assert.That(rig.Take(), Does.StartWith("knowledge > warm"));
        }

        [Test]
        public void OnlyAChangedReentryAndAChangedColdResidualGrantTheCredit()
        {
            var rig = new Rig();
            DecisionFrame f = rig.NewFrame();

            Run(f.AcceptChangedPhaseA());
            Assert.That(rig.Take(), Is.EqualTo("knowledge > warm > recon > aggressionFacts > aggression > resolve > actors"));
            Run(f.PrepareAdmission());
            Assert.That(rig.Take(), Does.StartWith("knowledge"), "the first Phase A grants no credit by itself");

            Run(f.AcceptChangedReentry());
            Assert.That(rig.Take(), Is.EqualTo("knowledge > warm > recon > aggressionFacts > aggression > resolve > actors"));
            Run(f.PrepareAdmission());
            Assert.That(rig.Take(), Is.Empty);

            Run(f.AcceptChangedCold(default(WorldAnalysis.StepObservationStamp), All));
            string sequence = rig.Take();
            Assert.That(sequence, Does.StartWith("observe > warm > recon > aggressionFacts > aggression > resolve > actors > generate:"));
            Run(f.PrepareAdmission());
            Assert.That(rig.Take(), Is.Empty);
        }

        [Test]
        public void ARefreshedSnapshotIsWhatTheNextReaderSees()
        {
            var rig = new Rig();
            DecisionFrame f = rig.NewFrame();
            WorldSnapshot first = f.Snapshot;
            Run(f.PrepareAdmission());
            WorldSnapshot second = f.Snapshot;
            Assert.That(second, Is.Not.SameAs(first));
            f.ObserveSettled(default(WorldAnalysis.StepObservationStamp), null);
            Assert.That(f.Snapshot, Is.Not.SameAs(second), "a settled step replaces the snapshot");
            f.RefreshAfterFormation();
            Assert.That(f.Snapshot.TurnNumber, Is.GreaterThan(second.TurnNumber));
            f.PrepareTempoOwnership();
            WorldSnapshot afterTempo = f.Snapshot;
            f.AcceptHousekeeping();
            Assert.That(f.Snapshot, Is.Not.SameAs(afterTempo));
        }

        [Test]
        public void TheFormationAndTheTempoRoundUseTheirOwnPartialRecipes()
        {
            var rig = new Rig();
            DecisionFrame f = rig.NewFrame();
            f.RefreshAfterFormation();
            Assert.That(rig.Take(), Is.EqualTo("knowledge > recon > resolve > actors"),
                "no estimate warm-up, no Aggression refresh, no turn context");

            var intents = f.Intents;
            f.PrepareTempoOwnership();
            Assert.That(rig.Take(), Is.EqualTo("knowledge > recon > persistentActors"));
            Assert.That(f.Intents, Is.SameAs(intents), "a Phase B round does not resolve the intents again");
            Assert.That(f.PostCommitments, Is.Null, "the scripted persistent view");

            f.RefreshFinalOwnership();
            Assert.That(rig.Take(), Is.EqualTo("persistentActors"));
        }

        [Test]
        public void TheColdResidualPreparesLikeAnAdmissionWithoutTheCreditCheck()
        {
            var rig = new Rig();
            DecisionFrame f = rig.NewFrame();
            f.StartWithCredit(true);
            Run(f.PrepareColdResidual());
            Assert.That(rig.Take(), Is.EqualTo("knowledge > warm > recon > aggressionFacts > aggression > resolve > actors"),
                "the cold stage always refreshes, whatever credit is held");
        }

        [Test]
        public void ReplacingOneFamilyKeepsTheOtherFamiliesInTheirOrderBeforeTheNewOnes()
        {
            var rig = new Rig();
            DecisionFrame f = rig.NewFrame();
            var seed = new List<AxisDemand>
            {
                new AxisDemand { RequestingAxis = DesireAxis.Economy },
                new AxisDemand { RequestingAxis = DesireAxis.Development },
                new AxisDemand { RequestingAxis = DesireAxis.Economy },
                null,
                new AxisDemand { RequestingAxis = DesireAxis.Recon },
            };
            f.RebuildDemands(new HashSet<DesireAxis> { DesireAxis.Economy });          // Demands = [one Economy]
            // put the seeded list through the public API: replace the whole Economy family by the seed
            f.ReplaceDemandFamilies(new HashSet<DesireAxis> { DesireAxis.Economy }, seed);
            var again = new List<AxisDemand> { new AxisDemand { RequestingAxis = DesireAxis.Development } };
            f.ReplaceDemandFamilies(new HashSet<DesireAxis> { DesireAxis.Development }, again);
            Assert.That(f.Demands.Select(d => d.RequestingAxis).ToArray(), Is.EqualTo(new[]
            {
                DesireAxis.Economy, DesireAxis.Economy, DesireAxis.Recon,           // kept, null dropped, in order
                DesireAxis.Development,                                              // the replaced family last
            }));
        }

        // Data flow: every service receives the objects the previous step of the SAME recipe produced
        // (never a stale reference from an earlier refresh), and a reader gets the new objects at once.
        [Test]
        public void EveryStepOfTheRecipeConsumesWhatThePreviousStepProduced()
        {
            var violations = new List<string>();
            WorldSnapshot current = null;
            List<ReconObjective> lastRecon = null;
            List<RaidObjective> lastAggression = null;
            List<MissionIntent> lastIntents = null;
            ActorCommitments lastCommitments = null;
            var services = new FrameServices
            {
                RefreshKnowledge = s => { current = new WorldSnapshot { TurnNumber = s.TurnNumber + 1 }; return current; },
                ObserveSettled = (s, stamp, r) => { current = new WorldSnapshot { TurnNumber = s.TurnNumber + 1 }; return current; },
                WarmEstimates = s => Warm(s, () => current, violations),
                EnumerateRecon = s =>
                {
                    if (!ReferenceEquals(s, current)) violations.Add("recon enumerated a stale snapshot");
                    return lastRecon = new List<ReconObjective>();
                },
                RefreshAggressionFacts = s => { if (!ReferenceEquals(s, current)) violations.Add("aggression facts on a stale snapshot"); },
                EnumerateAggression = s =>
                {
                    if (!ReferenceEquals(s, current)) violations.Add("aggression enumerated a stale snapshot");
                    return lastAggression = new List<RaidObjective>();
                },
                ResolveActive = (s, recon, aggr, withCtx) =>
                {
                    if (!ReferenceEquals(s, current)) violations.Add("resolve on a stale snapshot");
                    if (!ReferenceEquals(recon, lastRecon)) violations.Add("resolve got stale recon");
                    if (!ReferenceEquals(aggr, lastAggression)) violations.Add("resolve got stale aggression");
                    return lastIntents = new List<MissionIntent>();
                },
                RefreshActors = (intents, s, recon) =>
                {
                    if (!ReferenceEquals(s, current)) violations.Add("actors on a stale snapshot");
                    if (!ReferenceEquals(intents, lastIntents)) violations.Add("actors got stale intents");
                    if (!ReferenceEquals(recon, lastRecon)) violations.Add("actors got stale recon");
                    return lastCommitments = null;
                },
                RefreshPersistentActors = (s, recon) =>
                {
                    if (!ReferenceEquals(s, current)) violations.Add("persistent actors on a stale snapshot");
                    if (!ReferenceEquals(recon, lastRecon)) violations.Add("persistent actors got stale recon");
                    return null;
                },
                GenerateDemands = (s, recon, aggr, intents, commitments, axes) =>
                {
                    if (!ReferenceEquals(s, current)) violations.Add("demands on a stale snapshot");
                    if (!ReferenceEquals(recon, lastRecon) || !ReferenceEquals(aggr, lastAggression)
                        || !ReferenceEquals(intents, lastIntents))
                        violations.Add("demands got stale objectives or intents");
                    return new List<AxisDemand>();
                },
            };
            current = new WorldSnapshot();
            DecisionFrame f = new DecisionFrame(current, services);
            f.EnumerateObjectives();
            f.ResolveInitialOwnership();
            f.RebuildDemands(All);
            Run(f.PrepareAdmission());
            f.RebuildDemands(All);
            Run(f.AcceptChangedReentry());
            f.RefreshAfterFormation();
            f.RebuildDemands(All);
            f.PrepareTempoOwnership();
            Run(f.PrepareColdResidual());
            f.GenerateDemands(All);
            Run(f.AcceptChangedCold(default(WorldAnalysis.StepObservationStamp), All));
            f.RefreshFinalOwnership();
            Assert.That(violations, Is.Empty, string.Join("; ", violations));
            Assert.That(f.Snapshot, Is.SameAs(current), "the frame holds the newest snapshot");
            Assert.That(f.Recon, Is.SameAs(lastRecon));
            Assert.That(f.Intents, Is.SameAs(lastIntents));
        }

        private static IEnumerator Warm(WorldSnapshot s, Func<WorldSnapshot> current, List<string> violations)
        {
            if (!ReferenceEquals(s, current())) violations.Add("warm-up of a stale snapshot");
            yield break;
        }

        // ---- the bank through the frame: the REAL ResolveActive / RefreshActors, session leases and ledger ----

        private static MissionIntent ReturnBuilder(int armyId, Game.HexGrid.HexCoord shelter)
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy,
                Status = IntentStatus.Active,
                PreferredMoverArmyId = armyId,
                Objective = new EconomyIntent
                {
                    Kind = EconomyTaskKind.ReturnBuilder, TargetHex = shelter, BuilderArmyId = armyId,
                },
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            return intent;
        }

        private static WorldSnapshot EmptyWorld(Game.Players.PlayerSetupData player, int turn) => new WorldSnapshot
        {
            Observer = player,
            TurnNumber = turn,
            Self = new SelfSnapshot
            {
                TotalPower = 1f,
                Stockpile = new ResourceBundle(),
                PerTurnIncome = new ResourceBundle(),
                BaseHexes = new List<Game.HexGrid.HexCoord> { new Game.HexGrid.HexCoord(0, 0) },
                Armies = new List<ArmySnapshot>(),
                Hand = new List<Game.Cards.CardData>(),
                Deck = new List<Game.Cards.CardDefinition>(),
            },
            Known = new KnownSnapshot
            {
                EnemySightings = new List<Game.Ai.AiMapMemory.KnownEnemySighting>(),
                NeutralSightings = new List<Game.Ai.AiMapMemory.KnownEnemySighting>(),
                Buildings = new List<Game.Ai.AiMapMemory.KnownBuilding>(),
                ResourceHexes = new List<Game.Ai.AiMapMemory.KnownResourceHex>(),
            },
            TrueWorld = new TrueWorldSnapshot { Opponents = new List<OpponentSnapshot>() },
            MapKnowledge = new MapKnowledgeSnapshot
            {
                Frontier = new List<FrontierHexSnapshot>(),
                AllHexes = new List<Game.HexGrid.HexCoord>(),
            },
            Threat = new ThreatModel
            {
                Contacts = new List<EnemyContactSnapshot>(),
                Threats = new List<AssetThreatSnapshot>(),
            },
            Development = new DevelopmentReadiness(),
            Economy = new EconomyStanding
            {
                PerType = new List<EconomyResourceStanding>(),
                HasActionableOpportunity = true,
                EconomicSecurity = 1f,
            },
        };

        // A stale intent (its actor is gone) is retired by the real ResolveActive at every
        // operational refresh, even when the world revision did not move; only ITS rows leave the
        // bank, a foreign operation's rows stay, and the end of the turn clears the rest.
        // useRealResolve: the real MissionContinuityLayer.ResolveActive (needs the engine: it compares a
        // UnityEngine.Object, so a managed run reports it inconclusive and Unity runs it); false: the
        // resolution policy is simulated (an intent whose actor is gone is removed) but the retire path
        // - MissionIntentState.Remove -> session leases -> ledger - and the frame are the real ones.
        [TestCase(false)]
        [TestCase(true)]
        public void TheOperationalRefreshRetiresAStaleIntentAndReleasesOnlyItsOwnRows(bool useRealResolve)
        {
            var player = new Game.Players.PlayerSetupData { Nickname = "FrameBank", ColorIndex = 7 };
            const int turn = 4;
            var trace = new AiDecouplingTrace("E5_frame_bank", player, 7);
            StrategicResourceReservationLedger.ClearAll();
            MissionIntentRegistry.Clear();
            try
            {
                var session = AiTurnSession.Begin(player, null, null, null, turn);
                MissionIntent staleA = ReturnBuilder(503, new Game.HexGrid.HexCoord(0, 0));
                MissionIntent staleC = ReturnBuilder(505, new Game.HexGrid.HexCoord(0, 0));
                var foreignKey = MissionIntentKey.ForEconomy(EconomyTaskKind.FoundBase, 0, new Game.HexGrid.HexCoord(6, 3));
                MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
                state.Put(staleA);
                session.Leases.For(staleA.IntentKey).Reserve(StrategicReservationReason.EconomyDeferredBuild,
                    StrategicReservedResource.Materials, 3);
                session.Leases.For(foreignKey).Reserve(StrategicReservationReason.EconomyDeferredBuild,
                    StrategicReservedResource.Energy, 2);
                trace.Record(turn, "Pass", "turn_start");
                Assert.That(StrategicResourceReservationLedger.Rows(player, turn).Count, Is.EqualTo(2));

                WorldSnapshot world = EmptyWorld(player, turn);
                var services = new FrameServices
                {
                    RefreshKnowledge = s => s,
                    ObserveSettled = (s, stamp, r) => s,
                    WarmEstimates = s => NoWarm(),
                    EnumerateRecon = s => new List<ReconObjective>(),
                    RefreshAggressionFacts = s => { },
                    EnumerateAggression = s => new List<RaidObjective>(),
                    // the real owners
                    ResolveActive = (s, recon, aggr, withCtx) => useRealResolve
                        ? MissionContinuityLayer.ResolveActive(player, s, recon, aggr)
                        : SimulatedResolve(state, s),
                    RefreshActors = (intents, s, recon) => session.RefreshActors(intents, s, recon),
                    RefreshPersistentActors = (s, recon) => session.RefreshActors(session.PersistentState.All, s, recon),
                    GenerateDemands = (s, recon, aggr, intents, commitments, axes) => new List<AxisDemand>(),
                };
                var frame = new DecisionFrame(world, services);

                // first refresh: A is stale and goes; the foreign operation's row stays
                try { Run(frame.RefreshOperationalDecision()); }
                catch (TypeInitializationException) when (useRealResolve)
                {
                    Assert.Inconclusive("the real ResolveActive needs the Unity engine (UnityEngine.Object)");
                }
                trace.Record(turn, "Reentry", "operational_refresh");
                Assert.That(state.TryGet(staleA.IntentKey, out _), Is.False, "the stale intent was retired");
                Assert.That(frame.Intents, Is.Empty);
                var rows = StrategicResourceReservationLedger.Rows(player, turn);
                Assert.That(rows.Select(r => r.Resource).ToArray(), Is.EqualTo(new[] { StrategicReservedResource.Energy }),
                    "only the retired operation's rows left the bank");
                Assert.That(rows.Single().Amount, Is.EqualTo(2f));

                // the SAME snapshot (same revision): a new stale intent is still resolved, never skipped
                state.Put(staleC);
                session.Leases.For(staleC.IntentKey).Reserve(StrategicReservationReason.EconomyDeferredBuild,
                    StrategicReservedResource.Human, 1);
                trace.Record(turn, "Reentry", "second_stale_intent");
                Assert.That(StrategicResourceReservationLedger.Rows(player, turn).Count, Is.EqualTo(2));
                Run(frame.RefreshOperationalDecision());
                trace.Record(turn, "Reentry", "operational_refresh");
                Assert.That(state.TryGet(staleC.IntentKey, out _), Is.False, "a refresh on an unchanged world still resolves");
                rows = StrategicResourceReservationLedger.Rows(player, turn);
                Assert.That(rows.Select(r => r.Resource).ToArray(), Is.EqualTo(new[] { StrategicReservedResource.Energy }));

                // the end of the turn clears what is left; the next turn starts empty
                session.CompleteReservations();
                trace.Record(turn, "Pass", "turn_end");
                Assert.That(StrategicResourceReservationLedger.Rows(player, turn), Is.Empty);
                session.Dispose();
                StrategicResourceReservationLedger.BeginTurn(player, turn + 1);
                trace.Record(turn + 1, "Pass", "turn_start");
                Assert.That(StrategicResourceReservationLedger.Rows(player, turn + 1), Is.Empty);
                trace.Flush();
            }
            finally
            {
                MissionIntentRegistry.Clear();
                StrategicResourceReservationLedger.ClearAll();
            }
        }

        private static IEnumerator NoWarm() { yield break; }

        // The simulated resolution: an intent whose builder is not among the snapshot's armies is dead.
        private static List<MissionIntent> SimulatedResolve(MissionIntentState state, WorldSnapshot snap)
        {
            var alive = new HashSet<int>(snap.Self.Armies.Select(a => a.ArmyId));
            foreach (MissionIntent intent in state.All.ToList())
                if (intent.PreferredMoverArmyId.HasValue && !alive.Contains(intent.PreferredMoverArmyId.Value))
                    state.Remove(intent.IntentKey);
            return state.All.ToList();
        }

        // ---- differential: the frame against the inline locals + flag it replaced ----

        // Scripted services whose products carry a serial number, so the objects a holder ends up
        // with can be compared by identity of production, not only by call order.
        private sealed class TaggedWorld
        {
            internal readonly List<string> Calls = new List<string>();
            private int _serial;
            internal readonly FrameServices Services;

            internal TaggedWorld()
            {
                Services = new FrameServices
                {
                    RefreshKnowledge = s => { Calls.Add("knowledge"); return new WorldSnapshot { TurnNumber = ++_serial }; },
                    ObserveSettled = (s, stamp, r) => { Calls.Add("observe"); return new WorldSnapshot { TurnNumber = ++_serial }; },
                    WarmEstimates = s => Warm(),
                    EnumerateRecon = s => { Calls.Add("recon@" + s.TurnNumber); return new List<ReconObjective> { new ReconObjective { BaseValue = ++_serial } }; },
                    RefreshAggressionFacts = s => Calls.Add("facts@" + s.TurnNumber),
                    EnumerateAggression = s => { Calls.Add("aggr@" + s.TurnNumber); return new List<RaidObjective> { new RaidObjective { BaseValue = ++_serial } }; },
                    ResolveActive = (s, recon, aggr, withCtx) =>
                    {
                        Calls.Add("resolve" + (withCtx ? "(ctx)" : "") + "@" + s.TurnNumber + "/" + recon[0].BaseValue + "/" + (aggr == null ? "-" : aggr[0].BaseValue.ToString()));
                        return new List<MissionIntent> { new MissionIntent { CreatedTurn = ++_serial } };
                    },
                    RefreshActors = (intents, s, recon) => { Calls.Add("actors@" + s.TurnNumber + "/" + intents[0].CreatedTurn); return null; },
                    RefreshPersistentActors = (s, recon) => { Calls.Add("persistent@" + s.TurnNumber + "/" + recon[0].BaseValue); return null; },
                    GenerateDemands = (s, recon, aggr, intents, commitments, axes) =>
                    {
                        Calls.Add("generate@" + s.TurnNumber + "/" + recon[0].BaseValue + "/" + intents[0].CreatedTurn + ":" + string.Join(",", axes.OrderBy(a => a)));
                        return new List<AxisDemand> { new AxisDemand { RequestingAxis = axes.OrderBy(a => a).First(), DesiredAmount = ++_serial } };
                    },
                };
            }

            private IEnumerator Warm() { Calls.Add("warm"); yield break; }
        }

        // The pre-E5 inline state of RunTurn: six locals and the freshness flag, with the recipes of
        // RefreshDecisionFrame and of each moment written exactly as they were inline.
        private sealed class InlineModel
        {
            private readonly FrameServices s;
            internal WorldSnapshot Snapshot;
            internal List<ReconObjective> Recon;
            internal List<RaidObjective> Aggression;
            internal List<MissionIntent> Intents;
            internal List<AxisDemand> Demands;
            internal bool Fresh;

            internal InlineModel(WorldSnapshot snapshot, FrameServices services) { Snapshot = snapshot; s = services; }

            private void RefreshDecisionFrame()
            {
                Run(s.WarmEstimates(Snapshot));
                var recon = s.EnumerateRecon(Snapshot);
                s.RefreshAggressionFacts(Snapshot);
                var aggr = s.EnumerateAggression(Snapshot);
                var intents = s.ResolveActive(Snapshot, recon, aggr, false);
                s.RefreshActors(intents, Snapshot, recon);
                Recon = recon; Aggression = aggr; Intents = intents;
            }

            internal void Start()
            {
                Recon = s.EnumerateRecon(Snapshot);
                Aggression = s.EnumerateAggression(Snapshot);
                Intents = s.ResolveActive(Snapshot, Recon, Aggression, true);
                s.RefreshActors(Intents, Snapshot, Recon);
                Demands = s.GenerateDemands(Snapshot, Recon, Aggression, Intents, null, All);
            }

            internal void PhaseAChanged() { Snapshot = s.RefreshKnowledge(Snapshot); RefreshDecisionFrame(); Demands = s.GenerateDemands(Snapshot, Recon, Aggression, Intents, null, All); }
            internal void Formation() { Snapshot = s.RefreshKnowledge(Snapshot); Recon = s.EnumerateRecon(Snapshot); Intents = s.ResolveActive(Snapshot, Recon, Aggression, false); s.RefreshActors(Intents, Snapshot, Recon); }
            internal void Admission() { if (!Fresh) { Snapshot = s.RefreshKnowledge(Snapshot); RefreshDecisionFrame(); } Fresh = false; }
            internal void Reentry(bool changed, ISet<DesireAxis> dirty)
            {
                RefreshDecisionFrame();
                var regenerated = s.GenerateDemands(Snapshot, Recon, Aggression, Intents, null, dirty);
                Demands = Demands.Where(d => d != null && !dirty.Contains(d.RequestingAxis)).Concat(regenerated).ToList();
                if (changed) { Snapshot = s.RefreshKnowledge(Snapshot); RefreshDecisionFrame(); Fresh = true; }
            }
            internal void Observe() { Snapshot = s.ObserveSettled(Snapshot, default(WorldAnalysis.StepObservationStamp), null); }
            internal void Tempo() { Snapshot = s.RefreshKnowledge(Snapshot); Recon = s.EnumerateRecon(Snapshot); s.RefreshPersistentActors(Snapshot, Recon); }
            internal void Cold(bool changed)
            {
                Snapshot = s.RefreshKnowledge(Snapshot); RefreshDecisionFrame();
                s.GenerateDemands(Snapshot, Recon, Aggression, Intents, null, All);
                if (changed)
                {
                    Snapshot = s.ObserveSettled(Snapshot, default(WorldAnalysis.StepObservationStamp), null);
                    RefreshDecisionFrame();
                    Demands = s.GenerateDemands(Snapshot, Recon, Aggression, Intents, null, All);
                    Fresh = true;
                }
            }
            internal void Final() { s.RefreshPersistentActors(Snapshot, Recon); }
            internal void Housekeeping() { Snapshot = s.RefreshKnowledge(Snapshot); }

            internal string Tags() => $"S{Snapshot.TurnNumber} R{Recon[0].BaseValue} A{Aggression[0].BaseValue} I{Intents[0].CreatedTurn} "
                + $"D[{string.Join(",", Demands.Select(d => d.RequestingAxis + ":" + d.DesiredAmount))}] F{Fresh}";
        }

        private static string FrameTags(DecisionFrame f, bool fresh) =>
            $"S{f.Snapshot.TurnNumber} R{f.Recon[0].BaseValue} A{f.Aggression[0].BaseValue} I{f.Intents[0].CreatedTurn} "
            + $"D[{string.Join(",", f.Demands.Select(d => d.RequestingAxis + ":" + d.DesiredAmount))}] F{fresh}";

        [Test]
        public void TheFrameFollowsTheInlineLocalsAndFlagOverRandomWalksOfTheTurn()
        {
            var rng = new Random(20261011);
            var dirtyChoices = new[]
            {
                new HashSet<DesireAxis> { DesireAxis.Development },
                new HashSet<DesireAxis> { DesireAxis.Economy, DesireAxis.Aggression },
                new HashSet<DesireAxis> { DesireAxis.Recon },
            };
            int reads = 0;
            for (int n = 0; n < 3000; n++)
            {
                var oldWorld = new TaggedWorld();
                var newWorld = new TaggedWorld();
                var inline = new InlineModel(new WorldSnapshot { TurnNumber = 0 }, oldWorld.Services);
                var frame = new DecisionFrame(new WorldSnapshot { TurnNumber = 0 }, newWorld.Services);

                inline.Start();
                frame.EnumerateObjectives();
                frame.ResolveInitialOwnership();
                frame.RebuildDemands(All);
                bool startsFresh = rng.Next(3) == 0;
                inline.Fresh = startsFresh;
                frame.StartWithCredit(startsFresh);
                Assert.That(newWorld.Calls, Is.EqualTo(oldWorld.Calls), "walk " + n + " start");

                var steps = new List<string>();
                int length = 5 + rng.Next(25);
                for (int i = 0; i < length; i++)
                {
                    int move = rng.Next(10);
                    HashSet<DesireAxis> dirty = dirtyChoices[rng.Next(dirtyChoices.Length)];
                    bool changed = rng.Next(2) == 0;
                    steps.Add(move + (move == 4 || move == 8 ? (changed ? "c" : "u") : ""));
                    switch (move)
                    {
                        case 0: inline.PhaseAChanged(); Run(frame.AcceptChangedPhaseA()); frame.RebuildDemands(All); break;
                        case 1: inline.Formation(); frame.RefreshAfterFormation(); break;
                        case 2: inline.Admission(); Run(frame.PrepareAdmission()); break;
                        case 3: inline.Observe(); frame.ObserveSettled(default(WorldAnalysis.StepObservationStamp), null); break;
                        case 4:
                            inline.Reentry(changed, dirty);
                            Run(frame.RefreshOperationalDecision());
                            frame.ReplaceDemandFamilies(dirty, frame.GenerateDemands(dirty));
                            if (changed) Run(frame.AcceptChangedReentry());
                            break;
                        case 5: inline.Tempo(); frame.PrepareTempoOwnership(); break;
                        case 6: inline.Final(); frame.RefreshFinalOwnership(); break;
                        case 7: inline.Housekeeping(); frame.AcceptHousekeeping(); break;
                        case 8:
                            inline.Cold(changed);
                            Run(frame.PrepareColdResidual());
                            frame.GenerateDemands(All);
                            if (changed) Run(frame.AcceptChangedCold(default(WorldAnalysis.StepObservationStamp), All));
                            break;
                        default: inline.Admission(); Run(frame.PrepareAdmission()); break;
                    }
                    // the credit is observable through whether the next admission refreshes
                    Assert.That(newWorld.Calls, Is.EqualTo(oldWorld.Calls), "walk " + n + " after " + string.Join(",", steps));
                    reads++;
                }
                // the references both sides hold are the products of the same calls
                // a probe admission on both sides exposes the credit state without extra bookkeeping
                inline.Admission(); Run(frame.PrepareAdmission());
                Assert.That(newWorld.Calls, Is.EqualTo(oldWorld.Calls), "walk " + n + " probe");
                Assert.That(FrameTags(frame, false), Is.EqualTo(inline.Tags()), "walk " + n + " [" + string.Join(",", steps) + "]");
            }
            Assert.That(reads, Is.GreaterThan(50000));
        }

        // The wiring of RunTurn needs the engine, so the points where each moment of the turn names
        // its frame operation are fixed at the source level: this is the order of the inline
        // refreshes / assignments of the baseline (file order), each now a named operation.
        [Test]
        public void RunTurnNamesEveryMomentOfTheFrameInTheBaselineOrder()
        {
            string root = AiTurnLoopTests.FindScriptsRoot();
            if (root == null) Assert.Ignore("Assets/Scripts not found from the working directory");
            string file = System.IO.Directory.GetFiles(root, "AiStrategyV2Pipeline.cs",
                System.IO.SearchOption.AllDirectories).Single();
            string code = string.Join(Environment.NewLine, System.IO.File.ReadLines(file)
                .Select(l => l.Split(new[] { "//" }, 2, StringSplitOptions.None)[0]));
            var calls = System.Text.RegularExpressions.Regex.Matches(code, @"frame\.(\w+)\(")
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value)
                .Where(n => n != "Snapshot").ToList();
            string[] expected =
            {
                "EnumerateObjectives", "ResolveInitialOwnership", "RebuildDemands",       // the start
                "AcceptChangedPhaseA", "RebuildDemands",                                   // the first Phase A changed
                "RefreshAfterFormation",                                                   // a wing was formed
                "StartWithCredit",                                                         // the loop block begins
                "RefreshOperationalDecision", "GenerateDemands", "ReplaceDemandFamilies",  // a re-admission
                "AcceptChangedReentry",                                                    //   ... that changed the world
                "ObserveSettled",                                                          // mandatory aviation step
                "PrepareAdmission", "ObserveSettled",                                      // an admission iteration
                "PrepareTempoOwnership", "ObserveSettled",                                 // a Phase B round
                "PrepareColdResidual", "GenerateDemands", "AcceptChangedCold",             // the cold residual
                "ObserveSettled",                                                          // air-support recall
                "RefreshFinalOwnership", "AcceptHousekeeping",                             // the end of the turn
            };
            Assert.That(calls.Where(n => n != "Aggression").ToArray(), Is.EqualTo(expected));
        }

        // The references of the frame are assigned only inside the frame, and the work bodies of
        // RunTurn no longer own the shared locals or the freshness flag.
        [Test]
        public void RunTurnHoldsNoSharedFrameLocalsAndAssignsNothingOfTheFrame()
        {
            string root = AiTurnLoopTests.FindScriptsRoot();
            if (root == null) Assert.Ignore("Assets/Scripts not found from the working directory");
            string file = System.IO.Directory.GetFiles(root, "AiStrategyV2Pipeline.cs",
                System.IO.SearchOption.AllDirectories).Single();
            var code = System.IO.File.ReadLines(file)
                .Select((l, i) => (n: i + 1, l: l.Split(new[] { "//" }, 2, StringSplitOptions.None)[0])).ToList();
            var locals = new System.Text.RegularExpressions.Regex(
                "reconObjectives|aggressionObjectives|activeIntents|actorCommitments|postCommitments"
                + "|ownershipFreshAfterPhaseA|RefreshDecisionFrame|RefreshOperationalFrame");
            var assigns = new System.Text.RegularExpressions.Regex(
                @"frame\.(Snapshot|Recon|Aggression|Intents|Commitments|Demands|PostCommitments)\s*(=[^=]|\+=)");
            var offenders = code.Where(x => locals.IsMatch(x.l) || assigns.IsMatch(x.l))
                .Select(x => file.Substring(file.LastIndexOfAny(new[] { '/', '\\' }) + 1) + ":" + x.n + ": " + x.l.Trim()).ToList();
            Assert.That(offenders, Is.Empty, string.Join(Environment.NewLine, offenders));
        }
    }
}
#endif
