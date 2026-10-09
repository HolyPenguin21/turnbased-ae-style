#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Stage E3 parity: ProvisioningManager.ProvisionNext against an INDEPENDENT transcription of the
    // loop it replaced (Pipeline.RunAdmissionIteration @ 19c80a86: the `while (!provisioningSettled)`
    // with the inline ledger / telemetry / retry-set writes). Both run the same scripted world
    // steps on identical real AllocationSessions and registries over many random scenarios; the
    // sequence of world-step calls, the ledger, the telemetry, the parking, the registry reads and
    // the allocator state afterwards must be identical.
    public class AiProvisioningSelectionParityTests
    {
        // ---- scripted world (deterministic per seed, consumed in call order) ----

        private sealed class Script
        {
            private readonly Random _rng;
            internal readonly List<string> Calls = new List<string>();

            internal Script(int seed) { _rng = new Random(seed); }

            private static string Keys(TentativeAllocation a) =>
                string.Join(",", a.Funded.Select(f => StableMissionKey.For(f.Mission).ToString()));

            internal void Prepare(TentativeAllocation a) => Calls.Add("prepare:" + Keys(a));

            private ProvisionFailure RandomFailure()
            {
                switch (_rng.Next(6))
                {
                    case 0: return ProvisionFailure.MoverContended("c");
                    case 1: return ProvisionFailure.NoMoverExists("n");
                    case 2: return ProvisionFailure.NoExecutableStep("x");
                    case 3: return ProvisionFailure.TargetSatisfied("t");
                    case 4: return ProvisionFailure.NoObservationVantage("v");
                    default: return ProvisionFailure.EnvelopeTooSmall(2f + _rng.Next(4), "e");
                }
            }

            internal IReadOnlyList<(FundedEntry Funded, ProvisionFailure Failure)> Scout(TentativeAllocation a)
            {
                Calls.Add("scout:" + Keys(a));
                var list = new List<(FundedEntry Funded, ProvisionFailure Failure)>();
                if (a.Funded.Count > 0 && _rng.Next(3) == 0)
                {
                    int n = 1 + _rng.Next(Math.Min(2, a.Funded.Count));
                    for (int i = 0; i < n; i++)
                        list.Add((a.Funded[_rng.Next(a.Funded.Count)], RandomFailure()));
                    list = list.GroupBy(x => StableMissionKey.For(x.Funded.Mission)).Select(g => g.First()).ToList();
                }
                return list;
            }

            internal ProvisioningResult Provision(FundedEntry f)
            {
                Calls.Add("provision:" + StableMissionKey.For(f.Mission));
                if (_rng.Next(3) == 0)
                    return ProvisioningResult.Ok(new ProvisionedMission
                    {
                        Mission = f.Mission, Key = StableMissionKey.For(f.Mission), ClaimedAp = f.Tentative.Ap,
                    });
                return ProvisioningResult.Fail(RandomFailure());
            }
        }

        private sealed class Rig
        {
            internal PlayerSetupData Player;
            internal WorldSnapshot Snap;
            internal List<MissionProposal> Jobs;
            internal List<Commitment> Commitments;
            internal AllocationSession Session;
            internal ProvisioningSession Provisioning;
            internal TentativeAllocation Initial;
        }

        private static MissionProposal Job(int q)
        {
            var job = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = new HexCoord(q, 1) },
                BaseValue = 10f,
                Requirements = new MissionRequirements { ApMinimum = 1, ApDesired = 1, ApMaximum = 1 },
            };
            job.Axes.Value[DesireAxis.Recon] = 1f;
            return job;
        }

        private static Rig Build(int jobs, int ap, int hardJobs, bool stuck)
        {
            var player = new PlayerSetupData { Nickname = "Parity", ColorIndex = 3 };
            WorldSnapshot snap = AiReconAuditBugTests.Snapshot(player, 4, new HexCoord(5, 1));
            snap.Self.ActionPoints = ap;
            if (stuck)
                foreach (ArmySnapshot a in snap.Self.Armies)
                    a.CurrentMovement = 0;
            var all = Enumerable.Range(0, jobs).Select(i => Job(5 + i)).ToList();
            var commitments = all.Take(hardJobs).Select(j => new Commitment { Mission = j, Tier = CommitmentTier.Hard }).ToList();
            var fresh = all.Skip(hardJobs).ToList();
            CapabilityPoolExhaustionRegistry.BeginTurn(player, 4);
            AllocationSession session = ResourceAllocator.BeginTurn(snap, Radar.Even(), fresh, commitments, player);
            return new Rig
            {
                Player = player, Snap = snap, Jobs = all, Commitments = commitments, Session = session,
                Provisioning = new ProvisioningSession(snap), Initial = session.Pack(),
            };
        }

        // ---- everything observable after a run ----

        private sealed class Result
        {
            internal string Selected, SelectedKey, SelectedIsCommitment, Attempted, FinalFunded, Funded;
            internal string Telemetry, Provisioned, Ledger, Parked, Registry, Allocator;
        }

        private static string KeyList(IEnumerable<StableMissionKey> keys) => string.Join(",", keys.Select(k => k.ToString()));

        private static Result Observe(Rig rig, TentativeAllocation finalAllocation, ProvisionedMission selected,
            StableMissionKey selectedKey, bool selectedIsCommitment, IEnumerable<StableMissionKey> attempted,
            IEnumerable<StableMissionKey> fundedKeys, MissionOutcomeLedger ledger,
            Dictionary<ProvisionFailureKind, int> telemetry, List<ProvisionedMission> provisioned,
            Func<StableMissionKey, bool> parked)
        {
            var r = new Result
            {
                Selected = selected == null ? "-" : StableMissionKey.For(selected.Mission).ToString(),
                SelectedKey = selected == null ? "-" : selectedKey.ToString(),
                SelectedIsCommitment = selected == null ? "-" : selectedIsCommitment.ToString(),
                Attempted = KeyList(attempted.OrderBy(k => k.ToString())),
                FinalFunded = KeyList(finalAllocation.Funded.Select(f => StableMissionKey.For(f.Mission))),
                Funded = KeyList(fundedKeys.Distinct().OrderBy(k => k.ToString())),
                Telemetry = string.Join(",", telemetry.Select(kv => kv.Key + "=" + kv.Value)),
                Provisioned = KeyList(provisioned.Select(p => StableMissionKey.For(p.Mission))),
                Parked = string.Join(",", rig.Jobs.Select(j => parked(StableMissionKey.For(j)) ? "1" : "0")),
            };
            ledger.RegisterProposals(rig.Jobs);
            var sb = new StringBuilder();
            foreach (MissionStepResult o in ledger.FinalizeSteps().OrderBy(x => x.AttemptKey.ToString()))
                sb.Append(o.AttemptKey).Append('|').Append(o.Disposition).Append('|')
                    .Append(o.ProvisionFailureKindValue?.ToString() ?? "-").Append('|')
                    .Append(o.MoverArmyId?.ToString() ?? "-").Append(';');
            r.Ledger = sb.ToString();
            r.Registry = string.Join(",", rig.Jobs.Select(j =>
                (CapabilityPoolExhaustionRegistry.CanAttempt(rig.Player, j, rig.Snap) ? "A" : "a")
                + (CapabilityPoolExhaustionRegistry.ShouldSkipRetried(rig.Player, j, rig.Snap) ? "S" : "s")))
                + (CapabilityPoolExhaustionRegistry.IsExhausted(rig.Player, CapabilityPoolKind.Scout) ? "E" : "e");
            r.Allocator = $"fail={rig.Session.HasNewFailures} conv={rig.Session.Converged} pass={finalAllocation.PassNumber}";
            // The reservation-relevant allocator state, read through the next pack: repriced floors,
            // rejected jobs and the locked claim of a provisioned mission all show in what it funds.
            TentativeAllocation next = rig.Session.Pack();
            r.Allocator += $" next=[{string.Join(",", next.Funded.Select(f => StableMissionKey.For(f.Mission) + "@" + f.Tentative.Ap))}]"
                + $" locked={next.LockedClaim.Ap} deferred={next.Deferred.Count}"
                + $" claimed=[{string.Join(",", rig.Jobs.Select(j => rig.Provisioning.AlreadyProvisioned(StableMissionKey.For(j)) ? "1" : "0"))}]"
                + $" apClaimed={rig.Provisioning.ApClaimed}";
            return r;
        }

        // ---- the new code ----

        private static (Result, List<string>) RunNew(int seed, int jobs, int ap, int hard, bool stuck)
        {
            Rig rig = Build(jobs, ap, hard, stuck);
            var script = new Script(seed);
            var parking = new PassParking();
            ProvisioningSelectionOutcome o = ProvisioningManager.ProvisionNext(rig.Player, rig.Snap, rig.Session,
                rig.Provisioning, parking, rig.Initial,
                new ProvisioningManager.SelectionSteps(script.Prepare, script.Scout, script.Provision));

            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(rig.Jobs);
            ledger.RegisterCommitments(rig.Commitments);
            var telemetry = new Dictionary<ProvisionFailureKind, int>();
            var provisioned = new List<ProvisionedMission>();
            // exactly the replay the orchestrator performs
            foreach (ProvisionEvent attempt in o.Events)
            {
                ledger.RecordProvisionAttempt(attempt);
                if (attempt.Kind == ProvisionEventKind.Success) { provisioned.Add(attempt.Provisioned); continue; }
                telemetry.TryGetValue(attempt.Failure.Kind, out int n);
                telemetry[attempt.Failure.Kind] = n + 1;
            }
            var funded = rig.Initial.Funded.Where(f => f?.Mission != null).Select(f => StableMissionKey.For(f.Mission))
                .Concat(o.FundedKeysAcrossPacks);
            return (Observe(rig, o.FinalAllocation, o.Selected, o.SelectedKey, o.SelectedIsCommitment,
                o.AttemptedKeys, funded, ledger, telemetry, provisioned, parking.IsParked), script.Calls);
        }

        // ---- the transcription of the replaced loop (Pipeline.RunAdmissionIteration @ 19c80a86) ----

        private static (Result, List<string>) RunBaseline(int seed, int jobs, int ap, int hard, bool stuck)
        {
            Rig rig = Build(jobs, ap, hard, stuck);
            var script = new Script(seed);
            var player = rig.Player; var snapshot = rig.Snap; var cycleSession = rig.Session;
            var cycleProvisioning = rig.Provisioning;
            TentativeAllocation allocation = rig.Initial;
            var fundedKeysThisTurn = new HashSet<StableMissionKey>();
            foreach (FundedEntry fe in allocation.Funded)
                if (fe?.Mission != null) fundedKeysThisTurn.Add(StableMissionKey.For(fe.Mission));
            void RecordFunded(TentativeAllocation packed)
            {
                foreach (FundedEntry fe in packed.Funded)
                    if (fe?.Mission != null)
                        fundedKeysThisTurn.Add(StableMissionKey.For(fe.Mission));
            }
            var cycleLedger = new MissionOutcomeLedger();
            cycleLedger.RegisterProposals(rig.Jobs);
            cycleLedger.RegisterCommitments(rig.Commitments);
            var provisioned = new List<ProvisionedMission>();
            var provisioningFailures = new Dictionary<ProvisionFailureKind, int>();
            var retryNextTurnThisPass = new HashSet<StableMissionKey>();

            ProvisionedMission selected = null;
            bool selectedIsCommitment = false;
            StableMissionKey selectedKey = default;
            var attemptedKeys = new HashSet<StableMissionKey>();
            int assignmentReallocPass = 0;
            int repriceReallocPass = 0;
            bool provisioningSettled = false;
            while (!provisioningSettled)
            {
                script.Prepare(allocation);
                IReadOnlyList<(FundedEntry Funded, ProvisionFailure Failure)> scoutFailures = script.Scout(allocation);
                if (scoutFailures.Count > 0)
                {
                    foreach ((FundedEntry failedFunding, ProvisionFailure failure) in scoutFailures)
                    {
                        StableMissionKey failedKey = StableMissionKey.For(failedFunding.Mission);
                        attemptedKeys.Add(failedKey);
                        provisioningFailures.TryGetValue(failure.Kind, out int scoutFailureCount);
                        provisioningFailures[failure.Kind] = scoutFailureCount + 1;
                        CapabilityPoolExhaustionRegistry.DeferNoExecutableStep(
                            player, failedFunding.Mission, failure);
                        cycleSession.RegisterProvisionFailure(failedFunding, failure);
                        cycleLedger.RecordProvisionFailure(failedFunding.Mission, failure);
                        if (failure.Disposition == ProvisionDisposition.RetryNextTurn)
                        {
                            retryNextTurnThisPass.Add(failedKey);
                            CapabilityPoolExhaustionRegistry.CarryRetryNextTurn(
                                player, failedFunding.Mission);
                        }
                    }

                    List<FundedEntry> openScouts = allocation.Funded.Where(fe =>
                        fe?.Mission?.Kind == MissionKind.Scout
                        && !cycleProvisioning.AlreadyProvisioned(
                            StableMissionKey.For(fe.Mission))).ToList();
                    Dictionary<StableMissionKey, ProvisionFailure> scoutFailureByKey =
                        scoutFailures.ToDictionary(
                            f => StableMissionKey.For(f.Funded.Mission), f => f.Failure);
                    CapabilityPoolExhaustionRegistry.SettleScoutBatch(snapshot, player,
                        openScouts.Select(fe => fe.Mission), scoutFailureByKey,
                        cycleProvisioning.Successful.Values.Select(m => m?.Mission));

                    if (cycleSession.HasNewFailures && !cycleSession.Converged
                        && assignmentReallocPass < AiConfigV2.maxReallocIterations)
                    {
                        assignmentReallocPass++;
                        allocation = cycleSession.Pack();
                        RecordFunded(allocation);
                        continue;
                    }
                }
                FundedEntry selectedFunding = allocation.Funded.FirstOrDefault(fe =>
                    fe?.Mission != null
                    && CapabilityPoolExhaustionRegistry.CanAttempt(
                        player, fe.Mission, snapshot));
                if (selectedFunding == null)
                    break;

                selectedKey = StableMissionKey.For(selectedFunding.Mission);
                attemptedKeys.Add(selectedKey);
                ProvisioningResult provisionResult = script.Provision(selectedFunding);
                if (provisionResult.Success)
                {
                    selected = provisionResult.Provisioned;
                    selectedIsCommitment = selectedFunding.IsCommitment;
                    cycleProvisioning.RegisterSuccess(selectedKey, selected);
                    cycleSession.RegisterProvisionSuccess(selectedFunding,
                        selected.ClaimedAp, selected.ClaimedPhysical);
                    cycleLedger.RecordProvisionSuccess(selectedFunding.Mission, selected);
                    provisioned.Add(selected);
                    provisioningSettled = true;
                    break;
                }

                provisioningFailures.TryGetValue(provisionResult.Failure.Kind,
                    out int failureCount);
                provisioningFailures[provisionResult.Failure.Kind] = failureCount + 1;
                CapabilityPoolExhaustionRegistry.RecordProvisionFailure(snapshot, player,
                    selectedFunding.Mission, provisionResult.Failure);
                cycleSession.RegisterProvisionFailure(selectedFunding, provisionResult.Failure);
                cycleLedger.RecordProvisionFailure(selectedFunding.Mission,
                    provisionResult.Failure);
                if (provisionResult.Failure.Disposition == ProvisionDisposition.RetryNextTurn)
                {
                    retryNextTurnThisPass.Add(selectedKey);
                    CapabilityPoolExhaustionRegistry.CarryRetryNextTurn(
                        player, selectedFunding.Mission);
                }

                if (!cycleSession.HasNewFailures || cycleSession.Converged
                    || (provisionResult.Failure.Disposition == ProvisionDisposition.RepriceThisTurn
                        && ++repriceReallocPass >= AiConfigV2.maxReallocIterations))
                {
                    provisioningSettled = true;
                    break;
                }
                allocation = cycleSession.Pack();
                RecordFunded(allocation);
            }
            return (Observe(rig, allocation, selected, selectedKey, selectedIsCommitment, attemptedKeys,
                fundedKeysThisTurn, cycleLedger, provisioningFailures, provisioned,
                retryNextTurnThisPass.Contains), script.Calls);
        }

        private static string Diff(string name, string a, string b) => a == b ? null : $"{name}: baseline [{a}] vs new [{b}]";

        [TearDown]
        public void Cleanup() => CapabilityPoolExhaustionRegistry.Clear();

        [Test]
        public void ProvisionNextReproducesTheReplacedLoopOverManyScriptedScenarios()
        {
            var rng = new Random(20261009);
            int selectedCount = 0, parkedCount = 0, repacked = 0, noSelection = 0, exhausted = 0;
            for (int n = 0; n < 1500; n++)
            {
                int seed = rng.Next();
                int jobs = 1 + rng.Next(6);
                int ap = 2 + rng.Next(14);
                int hard = rng.Next(jobs + 1);
                bool stuck = rng.Next(3) == 0;
                (Result b, List<string> bCalls) = RunBaseline(seed, jobs, ap, hard, stuck);
                CapabilityPoolExhaustionRegistry.Clear();
                (Result a, List<string> aCalls) = RunNew(seed, jobs, ap, hard, stuck);
                CapabilityPoolExhaustionRegistry.Clear();

                var diffs = new List<string>
                {
                    Diff("calls", string.Join(" / ", bCalls), string.Join(" / ", aCalls)),
                    Diff("selected", b.Selected, a.Selected), Diff("selectedKey", b.SelectedKey, a.SelectedKey),
                    Diff("isCommitment", b.SelectedIsCommitment, a.SelectedIsCommitment),
                    Diff("attempted", b.Attempted, a.Attempted), Diff("finalFunded", b.FinalFunded, a.FinalFunded),
                    Diff("fundedKeys", b.Funded, a.Funded), Diff("telemetry", b.Telemetry, a.Telemetry),
                    Diff("provisioned", b.Provisioned, a.Provisioned), Diff("ledger", b.Ledger, a.Ledger),
                    Diff("parked", b.Parked, a.Parked), Diff("registry", b.Registry, a.Registry),
                    Diff("allocator", b.Allocator, a.Allocator),
                }.Where(d => d != null).ToList();
                Assert.That(diffs, Is.Empty,
                    $"scenario #{n} seed={seed} jobs={jobs} ap={ap} hard={hard} stuck={stuck}:\n" + string.Join("\n", diffs));

                if (b.Selected != "-") selectedCount++; else noSelection++;
                if (b.Parked.Contains("1")) parkedCount++;
                if (b.Registry.EndsWith("E")) exhausted++;
                if (b.Allocator.Contains("pass=") && !b.Allocator.EndsWith("pass=1")) repacked++;
            }
            // the generator really reaches the interesting branches
            Assert.That(selectedCount, Is.GreaterThan(100));
            Assert.That(noSelection, Is.GreaterThan(100));
            Assert.That(parkedCount, Is.GreaterThan(100));
            Assert.That(repacked, Is.GreaterThan(100));
            Assert.That(exhausted, Is.GreaterThan(50), "the pool-exhaustion proof branch is reached");
        }
    }
}
#endif
