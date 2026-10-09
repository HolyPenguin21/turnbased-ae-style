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

            internal DecisionFrame NewFrame() => new DecisionFrame(new WorldSnapshot(), null, null, null, null, Services);

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
            DecisionFrame f = new DecisionFrame(current, null, null, null, null, services);
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
