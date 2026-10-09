#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Stage E4 of the decoupling task: Missions owns the portfolio (MissionPortfolio.Build) and
    // Continuity owns the deferral of return legs (MissionContinuityLayer.DeferReturnsBeforeTempo).
    // The portfolio is compared with a transcription of the orchestrator method it replaced
    // (Pipeline.BuildMissionSet @ 4b4a4519, with the Aggression re-refresh path that no caller used
    // removed); the deferral is compared with the inline sequence of RunAdmissionIteration.
    public class AiMissionPortfolioTests
    {
        private static readonly HexCoord Site = new HexCoord(3, 3);

        [TearDown]
        public void Cleanup()
        {
            LifecycleReturnPolicy.ClearAll();
            CapabilityPoolExhaustionRegistry.Clear();
        }

        // ---- return deferral (Continuity) ----

        private static (MissionIntent intent, MissionProposal proposal) Return(PlayerSetupData player, int armyId = 7)
        {
            var target = new EconomyMissionTarget
            { Kind = EconomyTaskKind.ReturnBuilder, TargetHex = Site, BuilderArmyId = armyId };
            var proposal = new MissionProposal { Kind = MissionKind.Economy, Target = target };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy, Status = IntentStatus.Active,
                IntentKey = MissionIntentKey.For(proposal),
                Objective = new EconomyIntent { Kind = EconomyTaskKind.ReturnBuilder, TargetHex = Site, BuilderArmyId = armyId },
            };
            MissionIntentRegistry.GetOrCreate(player).Put(intent);
            return (intent, proposal);
        }

        private static MissionProposal Task(int armyId = 8) => new MissionProposal
        {
            Kind = MissionKind.Economy,
            Target = new EconomyMissionTarget { Kind = EconomyTaskKind.BuildExtraction, TargetHex = Site, BuilderArmyId = armyId },
        };

        private static WorldSnapshot Calm() => new WorldSnapshot { Threat = new ThreatModel() };

        [Test]
        public void AnOrdinaryReturnWaitsIsRecordedAndProtectedAndARealTaskStays()
        {
            var player = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            var (intent, ret) = Return(player);
            MissionProposal task = Task();
            var input = new List<MissionProposal> { ret, task };
            try
            {
                ReturnDeferral d = MissionContinuityLayer.DeferReturnsBeforeTempo(
                    Calm(), player, 5, input, new[] { intent });
                Assert.That(d.Waiting, Is.EqualTo(new[] { ret }));
                Assert.That(d.Retained, Is.EqualTo(new[] { task }));
                Assert.That(d.Deferrals[MissionIntentKey.For(ret)], Is.EqualTo(LifecycleReturnPolicy.DeferralReason));
                Assert.That(LifecycleReturnPolicy.MayWait(player, MissionIntentKey.For(ret), 5), Is.True,
                    "later passes of the same turn still wait");
                Assert.That(LifecycleReturnPolicy.MayWait(player, MissionIntentKey.For(ret), 6), Is.False,
                    "the wait is recorded: never two turns in a row");
                Assert.That(intent.LastProtectedTurn, Is.EqualTo(5), "a deliberate wait is not a stall");
                Assert.That(StrategicResourceReservationLedger.Rows(player, 5), Is.Empty,
                    "a wait creates no reservation");
            }
            finally { MissionIntentRegistry.GetOrCreate(player).Remove(intent.IntentKey); }
        }

        [Test]
        public void AHomeThreatLeavesEveryProposalAndRecordsNothing()
        {
            var player = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            var (intent, ret) = Return(player);
            var input = new List<MissionProposal> { ret };
            try
            {
                var threatened = new WorldSnapshot { Threat = new ThreatModel { UnderSiege = true } };
                ReturnDeferral d = MissionContinuityLayer.DeferReturnsBeforeTempo(
                    threatened, player, 5, input, new[] { intent });
                Assert.That(d.Waiting, Is.Empty);
                Assert.That(d.Retained, Is.SameAs(input), "the input list instance is returned unchanged");
                Assert.That(d.Deferrals, Is.Empty);
                Assert.That(LifecycleReturnPolicy.MayWait(player, MissionIntentKey.For(ret), 6), Is.True);
                Assert.That(intent.LastProtectedTurn, Is.Not.EqualTo(5));
            }
            finally { MissionIntentRegistry.GetOrCreate(player).Remove(intent.IntentKey); }
        }

        [Test]
        public void AReturnThatWaitedLastTurnGoesAtOnceAndAnActiveDefenceWithdrawalNeverWaits()
        {
            var player = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };
            var (intent, ret) = Return(player);
            var adProposal = new MissionProposal { Kind = MissionKind.ActiveDefence };
            var adIntent = new MissionIntent
            {
                Kind = MissionKind.ActiveDefence, Status = IntentStatus.Active,
                IntentKey = MissionIntentKey.For(adProposal),
                Objective = new ActiveDefenceIntent { Phase = ActiveDefencePhase.Return },
            };
            MissionIntentRegistry.GetOrCreate(player).Put(adIntent);
            try
            {
                MissionContinuityLayer.DeferReturnsBeforeTempo(Calm(), player, 5,
                    new List<MissionProposal> { ret }, new[] { intent });
                var input = new List<MissionProposal> { ret, adProposal };
                ReturnDeferral next = MissionContinuityLayer.DeferReturnsBeforeTempo(
                    Calm(), player, 6, input, new[] { intent, adIntent });
                Assert.That(next.Waiting, Is.Empty, "waited on turn 5: goes now; the withdrawal never waits");
                Assert.That(next.Retained, Is.SameAs(input));
                Assert.That(next.Deferrals, Is.Empty);
            }
            finally
            {
                MissionIntentRegistry.GetOrCreate(player).Remove(intent.IntentKey);
                MissionIntentRegistry.GetOrCreate(player).Remove(adIntent.IntentKey);
            }
        }

        // ---- differential: the inline rule of RunAdmissionIteration vs the Continuity method ----

        // The block the method replaced (Pipeline.RunAdmissionIteration @ 4b4a4519), transcribed.
        private static (List<MissionProposal> retained, bool deferred, Dictionary<MissionIntentKey, string> deferrals)
            OldInline(bool returnsMayWait, WorldSnapshot snapshot, PlayerSetupData player, int turn,
                List<MissionProposal> missions, IReadOnlyList<MissionIntent> activeIntents)
        {
            var missionDeferrals = new Dictionary<MissionIntentKey, string>();
            bool deferred = false;
            if (returnsMayWait && !LifecycleReturnPolicy.HomeThreatened(snapshot))
            {
                var waiting = LifecycleReturnPolicy.SelectWaiting(missions, activeIntents, player, turn);
                if (waiting.Count > 0)
                {
                    deferred = true;
                    foreach (MissionProposal m in waiting)
                    {
                        MissionIntentKey waitKey = MissionIntentKey.For(m);
                        missionDeferrals[waitKey] = LifecycleReturnPolicy.DeferralReason;
                        LifecycleReturnPolicy.RecordWait(player, waitKey, turn);
                        MissionContinuityLayer.MarkProtectedThisTurn(player, waitKey, turn);
                    }
                    missions = missions.Except(waiting).ToList();
                }
            }
            return (missions, deferred, missionDeferrals);
        }

        private sealed class DeferralRig
        {
            internal PlayerSetupData Player = new PlayerSetupData { Nickname = "Diff", ColorIndex = 5 };
            internal List<MissionProposal> Missions = new List<MissionProposal>();
            internal List<MissionIntent> Intents = new List<MissionIntent>();
        }

        private static DeferralRig MakeDeferralRig(Random rng, out bool threatened, out int turn, out bool mayWait)
        {
            var rig = new DeferralRig();
            threatened = rng.Next(4) == 0;
            turn = 4 + rng.Next(4);
            mayWait = rng.Next(5) != 0;
            int n = rng.Next(7);
            for (int i = 0; i < n; i++)
            {
                int kind = rng.Next(4);
                if (kind <= 1)
                {
                    var (intent, proposal) = Return(rig.Player, armyId: 10 + i);
                    if (rng.Next(3) == 0)   // it already waited on the previous turn
                        LifecycleReturnPolicy.RecordWait(rig.Player, MissionIntentKey.For(proposal), turn - 1);
                    rig.Intents.Add(intent);
                    rig.Missions.Add(proposal);
                }
                else if (kind == 2)
                    rig.Missions.Add(Task(armyId: 40 + i));
                else
                {
                    var ad = new MissionProposal
                    {
                        Kind = MissionKind.ActiveDefence,
                        Target = new EconomyMissionTarget { BuilderArmyId = 90 + i, TargetHex = Site },
                    };
                    var adIntent = new MissionIntent
                    {
                        Kind = MissionKind.ActiveDefence, Status = IntentStatus.Active,
                        IntentKey = MissionIntentKey.For(ad),
                        Objective = new ActiveDefenceIntent { Phase = ActiveDefencePhase.Return },
                    };
                    MissionIntentRegistry.GetOrCreate(rig.Player).Put(adIntent);
                    rig.Intents.Add(adIntent);
                    rig.Missions.Add(ad);
                }
            }
            return rig;
        }

        private static string KeysOf(IEnumerable<MissionProposal> ms) =>
            string.Join(",", ms.Select(m => m == null ? "null" : MissionIntentKey.For(m).ToString()));

        private static string StateDigest(DeferralRig rig, List<MissionProposal> retained, bool deferred,
            IReadOnlyDictionary<MissionIntentKey, string> deferrals, int turn)
        {
            string wait = string.Join(",", deferrals.OrderBy(k => k.Key.ToString()).Select(kv => kv.Key + "=" + kv.Value));
            string next = string.Join(",", rig.Missions.Select(m => LifecycleReturnPolicy.MayWait(rig.Player, MissionIntentKey.For(m), turn + 1)));
            string same = string.Join(",", rig.Missions.Select(m => LifecycleReturnPolicy.MayWait(rig.Player, MissionIntentKey.For(m), turn)));
            string prot = string.Join(",", rig.Intents.Select(i => i.LastProtectedTurn));
            return "retained=[" + KeysOf(retained) + "] deferred=" + deferred + " deferrals=[" + wait + "] next=[" + next
                + "] same=[" + same + "] prot=[" + prot + "]";
        }

        [Test]
        public void TheContinuityMethodReproducesTheInlineRuleOverManyScenarios()
        {
            var seeds = new Random(20261010);
            int waited = 0, urgentKept = 0, threatenedRuns = 0;
            for (int n = 0; n < 1500; n++)
            {
                int seed = seeds.Next();
                string[] digests = new string[2];
                for (int variant = 0; variant < 2; variant++)
                {
                    LifecycleReturnPolicy.ClearAll();
                    var rng = new Random(seed);
                    DeferralRig rig = MakeDeferralRig(rng, out bool threatened, out int turn, out bool mayWait);
                    var snap = new WorldSnapshot { Threat = new ThreatModel { UnderSiege = threatened } };
                    if (variant == 0)
                    {
                        var (retained, deferred, deferrals) = OldInline(mayWait, snap, rig.Player, turn, rig.Missions, rig.Intents);
                        digests[0] = StateDigest(rig, retained, deferred, deferrals, turn);
                    }
                    else
                    {
                        List<MissionProposal> retained = rig.Missions;
                        bool deferred = false;
                        IReadOnlyDictionary<MissionIntentKey, string> deferrals = new Dictionary<MissionIntentKey, string>();
                        if (mayWait)
                        {
                            ReturnDeferral d = MissionContinuityLayer.DeferReturnsBeforeTempo(snap, rig.Player, turn, rig.Missions, rig.Intents);
                            if (d.Waiting.Count > 0) { deferred = true; deferrals = d.Deferrals; retained = d.Retained; }
                        }
                        digests[1] = StateDigest(rig, retained, deferred, deferrals, turn);
                        if (deferred) waited++;
                        if (threatened) threatenedRuns++;
                        if (rig.Missions.Any(m => m.Kind == MissionKind.ActiveDefence)
                            && retained.Any(m => m.Kind == MissionKind.ActiveDefence)) urgentKept++;
                    }
                    foreach (MissionIntent i in rig.Intents.ToList())
                        MissionIntentRegistry.GetOrCreate(rig.Player).Remove(i.IntentKey);
                }
                Assert.That(digests[1], Is.EqualTo(digests[0]), "scenario #" + n + " seed=" + seed);
            }
            Assert.That(waited, Is.GreaterThan(200));
            Assert.That(urgentKept, Is.GreaterThan(100));
            Assert.That(threatenedRuns, Is.GreaterThan(100));
        }

        // ---- the bank: a waiting return holds no AP in the first pass and is funded after the tempo ----

        [Test]
        public void AWaitingReturnLeavesItsApToPhaseBAndIsFundedAfterTheFirstRound()
        {
            var player = new PlayerSetupData { Nickname = "Bank", ColorIndex = 6 };
            const int turn = 7;
            WorldSnapshot snap = AiReconAuditBugTests.Snapshot(player, turn, new HexCoord(5, 1));
            snap.Self.ActionPoints = 6;
            snap.Threat = new ThreatModel();
            StrategicResourceReservationLedger.BeginTurn(player, turn);
            var trace = new AiDecouplingTrace("S4_bank", player, 6);

            var (intent, ret) = Return(player);
            intent.Funding = CommitmentTier.Hard;
            ret.Requirements = new MissionRequirements { ApMinimum = 2, ApDesired = 2, ApMaximum = 2 };
            ret.Axes.Value[DesireAxis.Economy] = 1f;
            ret.FromDurableIntent = true;
            MissionProposal job = AiReconAuditBugTests.Scout(ScoutTargetKind.Explore, new HexCoord(5, 1));
            job.Requirements = new MissionRequirements { ApMinimum = 2, ApDesired = 2, ApMaximum = 2 };
            job.Axes.Value[DesireAxis.Recon] = 1f;
            var intents = new[] { intent };
            try
            {
                trace.Record(turn, "Pass", "turn_start", ap: 6f);

                // first pass: the return waits
                ReturnDeferral d = MissionContinuityLayer.DeferReturnsBeforeTempo(
                    snap, player, turn, new List<MissionProposal> { job, ret }, intents);
                Assert.That(d.Waiting, Is.EqualTo(new[] { ret }));
                List<Commitment> commitments = MissionContinuityLayer.BindFunding(
                    intents, d.Retained, snap, d.Deferrals);
                Assert.That(commitments, Is.Empty, "the waiting leg is not bound to funding");
                TentativeAllocation first = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                    d.Retained, commitments, player).Pack();
                float firstAp = first.Funded.Sum(f => f.Tentative.Ap);
                Assert.That(first.Funded.Select(f => f.Mission), Is.EqualTo(new[] { job }));
                Assert.That(firstAp, Is.EqualTo(2f), "only the real task holds AP");
                Assert.That(StrategicResourceReservationLedger.Rows(player, turn), Is.Empty);
                trace.Record(turn, "Pass", "tempo_round_start", ap: snap.Self.ActionPoints - firstAp);

                // after the first Phase B round the wait is over: the leg is bound and funded
                List<Commitment> later = MissionContinuityLayer.BindFunding(
                    intents, new List<MissionProposal> { job, ret }, snap, null);
                TentativeAllocation second = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                    new List<MissionProposal> { job }, later, player).Pack();
                float secondAp = second.Funded.Sum(f => f.Tentative.Ap);
                Assert.That(second.Funded.Select(f => f.Mission), Is.EquivalentTo(new[] { job, ret }));
                Assert.That(secondAp, Is.EqualTo(4f));
                Assert.That(StrategicResourceReservationLedger.Rows(player, turn), Is.Empty,
                    "funding is tentative: no reservation row is created by the wait or by the pack");
                trace.Record(turn, "Pass", "turn_end", ap: snap.Self.ActionPoints - secondAp);
                trace.Flush();
                Assert.That(intent.LastProtectedTurn, Is.EqualTo(turn));
            }
            finally { MissionIntentRegistry.GetOrCreate(player).Remove(intent.IntentKey); }
        }

        // The wiring RunTurn cannot be exercised without the engine; the one condition that matters
        // (returns wait only while the loop says the first Phase B round has not settled) is fixed
        // at the source level: the deferral is called only under `view.ReturnsMayWait`.
        [Test]
        public void TheReturnDeferralIsCalledOnlyWhileTheLoopAllowsTheWait()
        {
            string root = AiTurnLoopTests.FindScriptsRoot();
            if (root == null) Assert.Ignore("Assets/Scripts not found from the working directory");
            string file = System.IO.Directory.GetFiles(root, "AiStrategyV2Pipeline.cs",
                System.IO.SearchOption.AllDirectories).Single();
            string code = string.Join(Environment.NewLine, System.IO.File.ReadLines(file)
                .Select(l => l.Split(new[] { "//" }, 2, StringSplitOptions.None)[0]));
            int call = code.IndexOf("MissionContinuityLayer.DeferReturnsBeforeTempo(", StringComparison.Ordinal);
            Assert.That(call, Is.GreaterThan(-1));
            Assert.That(code.IndexOf("MissionContinuityLayer.DeferReturnsBeforeTempo(", call + 1, StringComparison.Ordinal),
                Is.EqualTo(-1), "exactly one call site");
            string before = code.Substring(Math.Max(0, call - 160), Math.Min(160, call));
            Assert.That(before, Does.Contain("if (view.ReturnsMayWait)"));
        }

        // ---- portfolio (Missions) ----

        private sealed class Fixture
        {
            internal PlayerSetupData Player;
            internal WorldSnapshot Snap;
            internal DesireBreakdown Breakdown = new DesireBreakdown();
            internal List<ReconObjective> Recon;
            // An uneven Radar, so the valuation is not the identity (Recon is weighted 0.1).
            internal Radar Radar = UnevenRadar();
            internal AiTurnContext Ctx = new AiTurnContext { TurnNumber = 3 };
        }

        private static Radar UnevenRadar()
        {
            Radar r = Radar.Even();
            r.Weight[DesireAxis.Recon] = 0.1f;
            return r;
        }

        private static Fixture MakeFixture(int jobs)
        {
            var player = new PlayerSetupData { Nickname = "Port", ColorIndex = 4 };
            return new Fixture
            {
                Player = player,
                Snap = AiReconAuditBugTests.Snapshot(player, 3, new HexCoord(5, 1)),
                Recon = Enumerable.Range(0, jobs).Select(i => new ReconObjective
                {
                    Kind = ReconObjectiveKind.Refresh, FocusHex = new HexCoord(5 + i, 1),
                    TaskScore = new TaskScore(infoGain: 20 + i), BaseValue = 20 + i,
                }).ToList(),
            };
        }

        // The orchestrator method the portfolio replaced, transcribed (Aggression pressures were
        // always already refreshed by the decision frame).
        private static List<MissionProposal> OldBuildMissionSet(Fixture f, V2TraceScope trace, out Dictionary<MissionIntentKey, string> deferred)
        {
            StrategyLayer.RefreshReconLanePressures(f.Snap, f.Breakdown);
            deferred = new Dictionary<MissionIntentKey, string>();
            List<MissionProposal> missions = ReconMissionPlanner.Propose(f.Snap, f.Breakdown,
                Array.Empty<MissionIntent>(), f.Recon, deferred, f.Ctx);
            missions.AddRange(AggressionMissionLayer.Propose(f.Snap, f.Breakdown,
                Array.Empty<MissionIntent>(), Array.Empty<RaidObjective>(), f.Ctx, deferred));
            missions.AddRange(EconomyMissionPlanner.Propose(f.Snap, f.Breakdown,
                Array.Empty<MissionIntent>(), Array.Empty<AxisDemand>(), deferred));
            missions.AddRange(DevelopmentMissionPlanner.Propose(f.Snap, Array.Empty<MissionIntent>(),
                Array.Empty<AxisDemand>()));
            foreach (MissionProposal m in missions)
                if (m != null && string.IsNullOrEmpty(m.AttemptId))
                    m.AttemptId = trace?.NextMissionAttemptId() ?? "?";
            foreach (MissionProposal m in missions)
                if (m != null)
                    m.EffectiveValue = m.BaseValue * RadarValueScale.For(f.Radar, m);
            AttackPreparationPriority.Apply(missions);
            AiV2Trace.CorrelateDemandsToMissions(Array.Empty<AxisDemand>(), missions);
            return missions;
        }

        private static string Digest(IEnumerable<MissionProposal> missions) => string.Join(";", missions.Select(m =>
            $"{m.Kind}|{StableMissionKey.For(m)}|{m.AttemptId}|{m.BaseValue}|{m.EffectiveValue}"));

        [Test]
        public void ThePortfolioMatchesTheReplacedOrchestratorMethod()
        {
            for (int jobs = 0; jobs <= 4; jobs++)
            {
                Fixture oldF = MakeFixture(jobs), newF = MakeFixture(jobs);
                // a real trace scope per side: the attempt-id counter is a write the portfolio owns
                var oldTrace = new V2TraceScope("T3-P1-M");
                var newTrace = new V2TraceScope("T3-P1-M");
                List<MissionProposal> old = OldBuildMissionSet(oldF, oldTrace, out Dictionary<MissionIntentKey, string> oldDeferred);
                MissionPortfolioResult built = MissionPortfolio.Build(newF.Snap, newF.Breakdown,
                    Array.Empty<MissionIntent>(), newF.Recon, Array.Empty<RaidObjective>(), newF.Radar,
                    Array.Empty<AxisDemand>(), newTrace, newF.Ctx);
                Assert.That(Digest(built.Missions), Is.EqualTo(Digest(old)), "jobs=" + jobs);
                // the counter continues across the admissions of one turn
                Fixture oldG = MakeFixture(jobs), newG = MakeFixture(jobs);
                List<MissionProposal> old2 = OldBuildMissionSet(oldG, oldTrace, out _);
                MissionPortfolioResult built2 = MissionPortfolio.Build(newG.Snap, newG.Breakdown,
                    Array.Empty<MissionIntent>(), newG.Recon, Array.Empty<RaidObjective>(), newG.Radar,
                    Array.Empty<AxisDemand>(), newTrace, newG.Ctx);
                Assert.That(Digest(built2.Missions), Is.EqualTo(Digest(old2)), "second admission, jobs=" + jobs);
                Assert.That(built.Deferrals.Select(kv => kv.Key + "=" + kv.Value),
                    Is.EqualTo(oldDeferred.Select(kv => kv.Key + "=" + kv.Value)), "jobs=" + jobs);
                if (jobs > 0)
                    Assert.That(built.Missions.Count, Is.GreaterThan(0), "the fixture really proposes missions");
                Assert.That(StrategicResourceReservationLedger.Rows(newF.Player, 3), Is.Empty,
                    "building a portfolio reserves nothing");
            }
        }

        [Test]
        public void EveryBuildProposesFreshInstances()
        {
            Fixture f = MakeFixture(2);
            MissionPortfolioResult first = MissionPortfolio.Build(f.Snap, f.Breakdown,
                Array.Empty<MissionIntent>(), f.Recon, Array.Empty<RaidObjective>(), f.Radar,
                Array.Empty<AxisDemand>(), null, f.Ctx);
            MissionPortfolioResult second = MissionPortfolio.Build(f.Snap, f.Breakdown,
                Array.Empty<MissionIntent>(), f.Recon, Array.Empty<RaidObjective>(), f.Radar,
                Array.Empty<AxisDemand>(), null, f.Ctx);
            Assert.That(first.Missions, Is.Not.Empty);
            Assert.That(second.Missions.Intersect(first.Missions), Is.Empty,
                "proposals and their attempt ids are never carried between admissions");
        }

        [Test]
        public void EveryMissionIsValuedByTheRadarAndGetsAnAttemptId()
        {
            Fixture f = MakeFixture(3);
            MissionPortfolioResult built = MissionPortfolio.Build(f.Snap, f.Breakdown,
                Array.Empty<MissionIntent>(), f.Recon, Array.Empty<RaidObjective>(), f.Radar,
                Array.Empty<AxisDemand>(), null, f.Ctx);
            Assert.That(built.Missions, Is.Not.Empty);
            foreach (MissionProposal m in built.Missions)
            {
                Assert.That(m.AttemptId, Is.Not.Null.And.Not.Empty);
                Assert.That(m.EffectiveValue, Is.EqualTo(m.BaseValue * RadarValueScale.For(f.Radar, m)));
                Assert.That(RadarValueScale.For(f.Radar, m), Is.Not.EqualTo(1f).Within(1e-4f),
                    "the fixture's Radar really scales");
            }
        }

        // The fixture has no Attack preparation proposal, so the priority pass is not observable in the
        // output; its place in the order is fixed here: pressures -> the four planners in order ->
        // attempt id -> Radar valuation -> preparation priority -> task-score log -> demand correlation.
        [Test]
        public void ThePortfolioKeepsTheOrderOfItsSteps()
        {
            string root = AiTurnLoopTests.FindScriptsRoot();
            if (root == null) Assert.Ignore("Assets/Scripts not found from the working directory");
            string file = System.IO.Directory.GetFiles(root, "MissionPortfolio.cs",
                System.IO.SearchOption.AllDirectories).Single();
            string code = string.Join(Environment.NewLine, System.IO.File.ReadLines(file)
                .Select(l => l.Split(new[] { "//" }, 2, StringSplitOptions.None)[0]));
            string[] steps =
            {
                "RefreshReconLanePressures(", "ReconMissionPlanner.Propose(", "AggressionMissionLayer.Propose(",
                "EconomyMissionPlanner.Propose(", "DevelopmentMissionPlanner.Propose(", "NextMissionAttemptId()",
                "RadarValueScale.For(", "AttackPreparationPriority.Apply(", "AiFrameLog.TaskScores(",
                "CorrelateDemandsToMissions(",
            };
            int last = -1;
            foreach (string step in steps)
            {
                int at = code.IndexOf(step, StringComparison.Ordinal);
                Assert.That(at, Is.GreaterThan(last), step);
                last = at;
            }
            Assert.That(code, Does.Not.Contain("RefreshAggressionOperationalFacts"),
                "the decision frame owns the Aggression facts refresh");
        }

        // Acceptance of the stage: the orchestrator no longer assembles or values the portfolio and
        // no longer runs the return rule.
        [Test]
        public void OrchestrationDoesNotAssembleThePortfolioOrRunTheReturnRule()
        {
            string root = AiTurnLoopTests.FindScriptsRoot();
            if (root == null) Assert.Ignore("Assets/Scripts not found from the working directory");
            string dir = System.IO.Directory.GetDirectories(root, "Orchestration",
                System.IO.SearchOption.AllDirectories).First();
            var banned = new System.Text.RegularExpressions.Regex(
                "ReconMissionPlanner|AggressionMissionLayer|EconomyMissionPlanner|DevelopmentMissionPlanner"
                + "|AttackPreparationPriority|LifecycleReturnPolicy|MarkProtectedThisTurn"
                + "|RefreshReconLanePressures|" + @"\.EffectiveValue\s*=[^=]");
            var offenders = System.IO.Directory.GetFiles(dir, "*.cs", System.IO.SearchOption.AllDirectories)
                .SelectMany(f => System.IO.File.ReadLines(f).Select((l, i) => (f, n: i + 1, l)))
                .Where(x => banned.IsMatch(x.l.Split(new[] { "//" }, 2, StringSplitOptions.None)[0]))
                .Select(x => System.IO.Path.GetFileName(x.f) + ":" + x.n + ": " + x.l.Trim()).ToList();
            Assert.That(offenders, Is.Empty, string.Join(Environment.NewLine, offenders));
        }
    }
}
#endif
