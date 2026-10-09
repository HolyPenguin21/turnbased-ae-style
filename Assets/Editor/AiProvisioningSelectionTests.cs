#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Stage E3 of the decoupling task: Provisioning owns the selection of the executable mission
    // (ProvisionNext) and the parking of a pass (PassParking). The world-dependent steps are driven
    // by scripts through the SelectionSteps seam; the AllocationSession, the exhaustion registry,
    // the ProvisioningSession and the call order are the real ones.
    public class AiProvisioningSelectionTests
    {
        private static readonly PlayerSetupData Us = new PlayerSetupData { Nickname = "Prov", ColorIndex = 2 };

        [SetUp]
        public void Reset() => CapabilityPoolExhaustionRegistry.Clear();

        [TearDown]
        public void Cleanup() => CapabilityPoolExhaustionRegistry.Clear();

        // A Hard commitment is funded by the allocator whatever its value: it gives deterministic
        // funded entries without a world.
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

        private sealed class Rig
        {
            internal WorldSnapshot Snap;
            internal List<MissionProposal> Jobs;
            internal AllocationSession Session;
            internal ProvisioningSession Provisioning;
            internal PassParking Parking = new PassParking();
            internal TentativeAllocation Initial;
        }

        private static Rig Build(int jobs, float ap, int turn = 3)
        {
            WorldSnapshot snap = AiReconAuditBugTests.Snapshot(Us, turn, new HexCoord(5, 1));
            snap.Self.ActionPoints = (int)ap;
            var list = Enumerable.Range(0, jobs).Select(i => Job(5 + i)).ToList();
            var commitments = list.Select(j => new Commitment { Mission = j, Tier = CommitmentTier.Hard }).ToList();
            AllocationSession session = ResourceAllocator.BeginTurn(snap, Radar.Even(),
                new List<MissionProposal>(), commitments, Us);
            CapabilityPoolExhaustionRegistry.BeginTurn(Us, turn);
            return new Rig
            {
                Snap = snap, Jobs = list, Session = session,
                Provisioning = new ProvisioningSession(snap),
                Initial = session.Pack(),
            };
        }

        private static ProvisioningManager.SelectionSteps Steps(
            System.Func<FundedEntry, ProvisioningResult> provision,
            System.Func<TentativeAllocation, IReadOnlyList<(FundedEntry, ProvisionFailure)>> scout = null) =>
            new ProvisioningManager.SelectionSteps(_ => { },
                scout ?? (_ => new List<(FundedEntry Funded, ProvisionFailure Failure)>()), provision);

        private static ProvisionedMission Prov(FundedEntry f) =>
            new ProvisionedMission { Mission = f.Mission, Key = StableMissionKey.For(f.Mission), ClaimedAp = f.Tentative.Ap };

        // ---- exploration of the harness: the allocator really funds the scripted jobs ----

        [Test]
        public void TheRigFundsEveryScriptedJob()
        {
            Rig r = Build(jobs: 3, ap: 9f);
            Assert.That(r.Initial.Funded.Count, Is.EqualTo(3), string.Join(",", r.Initial.Deferred.Select(d => d.Reason.ToString())));
        }

        [Test]
        public void TheFirstAttemptableMissionIsProvisionedAndRecorded()
        {
            Rig r = Build(3, 9f);
            var seen = new List<StableMissionKey>();
            ProvisioningSelectionOutcome o = ProvisioningManager.ProvisionNext(Us, r.Snap, r.Session,
                r.Provisioning, r.Parking, r.Initial, Steps(f =>
                {
                    seen.Add(StableMissionKey.For(f.Mission));
                    return ProvisioningResult.Ok(Prov(f));
                }));
            Assert.That(seen.Count, Is.EqualTo(1));
            Assert.That(o.Selected, Is.Not.Null);
            Assert.That(o.SelectedKey, Is.EqualTo(seen[0]));
            Assert.That(o.Events.Select(e => e.Kind), Is.EqualTo(new[] { ProvisionEventKind.Success }));
            Assert.That(o.AttemptedKeys, Is.EquivalentTo(seen));
            Assert.That(o.FundedKeysAcrossPacks, Is.Empty, "no re-pack happened");
            Assert.That(o.FinalAllocation, Is.SameAs(r.Initial));
            Assert.That(r.Provisioning.AlreadyProvisioned(seen[0]), Is.True);
        }

        [Test]
        public void ARetryNextTurnFailureIsParkedForThePassAndTheTurnAndTheNextMissionIsTried()
        {
            Rig r = Build(3, 9f);
            var order = new List<StableMissionKey>();
            ProvisioningSelectionOutcome o = ProvisioningManager.ProvisionNext(Us, r.Snap, r.Session,
                r.Provisioning, r.Parking, r.Initial, Steps(f =>
                {
                    order.Add(StableMissionKey.For(f.Mission));
                    return order.Count == 1
                        ? ProvisioningResult.Fail(ProvisionFailure.MoverContended("busy"))
                        : ProvisioningResult.Ok(Prov(f));
                }));
            Assert.That(order.Count, Is.EqualTo(2), "the allocator re-packed and the next mission was tried");
            Assert.That(o.Events.Select(e => e.Kind),
                Is.EqualTo(new[] { ProvisionEventKind.Failure, ProvisionEventKind.Success }));
            Assert.That(r.Parking.IsParked(order[0]), Is.True);
            Assert.That(o.AttemptedKeys, Is.EquivalentTo(order));
            Assert.That(o.FundedKeysAcrossPacks, Is.Not.Empty, "the re-pack is reported");
            Assert.That(o.FundedKeysAcrossPacks.Contains(order[0]), Is.False,
                "the rejected mission left the funded set");
            Assert.That(o.Selected, Is.Not.Null);
        }

        [Test]
        public void ARepricedMissionIsRetriedAtMostMaxReallocIterationsTimes()
        {
            Rig r = Build(1, 9f);
            int attempts = 0;
            ProvisioningSelectionOutcome o = ProvisioningManager.ProvisionNext(Us, r.Snap, r.Session,
                r.Provisioning, r.Parking, r.Initial, Steps(f =>
                {
                    attempts++;
                    return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(2f + attempts, "grow"));
                }));
            Assert.That(attempts, Is.EqualTo(AiConfigV2.maxReallocIterations),
                "`++reprice >= max` after a RepriceThisTurn failure: exactly max attempts, then stop");
            Assert.That(o.Selected, Is.Null);
            Assert.That(o.Events.All(e => e.Kind == ProvisionEventKind.Failure), Is.True);
            Assert.That(o.Events.Count, Is.EqualTo(attempts));
        }

        [Test]
        public void ANonRepricingFailureWithNoOtherMissionEndsTheSelection()
        {
            Rig r = Build(1, 9f);
            int attempts = 0;
            ProvisioningSelectionOutcome o = ProvisioningManager.ProvisionNext(Us, r.Snap, r.Session,
                r.Provisioning, r.Parking, r.Initial, Steps(f =>
                {
                    attempts++;
                    return ProvisioningResult.Fail(ProvisionFailure.TargetSatisfied("done"));
                }));
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(o.Selected, Is.Null);
            Assert.That(o.AttemptedKeys.Count, Is.EqualTo(1));
            Assert.That(o.FinalAllocation, Is.Not.Null);
        }

        // The Scout batch and the single-mission re-pack have INDEPENDENT budgets: a batch that
        // keeps failing must not consume the re-packs a RepriceThisTurn needs to see a corrected
        // envelope. Three batches spend the whole assignment budget (`< max`), yet the repriced
        // mission still gets its own `max` attempts (`++counter >= max`).
        [Test]
        public void TheScoutBatchBudgetDoesNotConsumeTheRepriceBudget()
        {
            Rig r = Build(5, 20f);
            int batchCalls = 0, provisions = 0;
            ProvisioningSelectionOutcome o = ProvisioningManager.ProvisionNext(Us, r.Snap, r.Session,
                r.Provisioning, r.Parking, r.Initial, Steps(
                    f =>
                    {
                        provisions++;
                        return ProvisioningResult.Fail(ProvisionFailure.EnvelopeTooSmall(3f + provisions, "grow"));
                    },
                    allocation =>
                    {
                        batchCalls++;
                        FundedEntry victim = allocation.Funded.FirstOrDefault();
                        return batchCalls <= AiConfigV2.maxReallocIterations && victim != null
                            ? new List<(FundedEntry, ProvisionFailure)> { (victim, ProvisionFailure.MoverContended("x")) }
                            : new List<(FundedEntry, ProvisionFailure)>();
                    }));
            Assert.That(o.Events.Count(e => e.Kind == ProvisionEventKind.ScoutBatchFailure),
                Is.EqualTo(AiConfigV2.maxReallocIterations), "the assignment budget was spent by batches");
            Assert.That(provisions, Is.EqualTo(AiConfigV2.maxReallocIterations),
                "the reprice budget is its own and was not consumed by the batches");
            Assert.That(o.Selected, Is.Null);
        }

        // The batch re-pack bound is `< max`: with a batch that always fails there are max re-packs,
        // the next batch is still reported and registered (max + 1 events) but not re-packed, and
        // the selection goes on to provision the first funded mission.
        [Test]
        public void AnEndlessScoutBatchIsRepackedExactlyMaxTimesThenTheSelectionContinues()
        {
            Rig r = Build(8, 30f);
            int provisions = 0;
            ProvisioningSelectionOutcome o = ProvisioningManager.ProvisionNext(Us, r.Snap, r.Session,
                r.Provisioning, r.Parking, r.Initial, Steps(
                    f => { provisions++; return ProvisioningResult.Ok(Prov(f)); },
                    allocation =>
                    {
                        FundedEntry victim = allocation.Funded.FirstOrDefault();
                        return victim == null
                            ? new List<(FundedEntry, ProvisionFailure)>()
                            : new List<(FundedEntry, ProvisionFailure)> { (victim, ProvisionFailure.MoverContended("x")) };
                    }));
            Assert.That(o.Events.Count(e => e.Kind == ProvisionEventKind.ScoutBatchFailure),
                Is.EqualTo(AiConfigV2.maxReallocIterations + 1));
            Assert.That(provisions, Is.EqualTo(1));
            Assert.That(o.Selected, Is.Not.Null);
        }

        // Acceptance of the stage: no executable code of the whole Orchestration folder names the
        // pool-exhaustion registry or runs the reallocation budgets any more (comments are ignored).
        [Test]
        public void OrchestrationDoesNotRunTheProvisioningProtocol()
        {
            string root = AiTurnLoopTests.FindScriptsRoot();
            if (root == null) Assert.Ignore("Assets/Scripts not found from the working directory");
            string dir = System.IO.Directory.GetDirectories(root, "Orchestration",
                System.IO.SearchOption.AllDirectories).First();
            var banned = new System.Text.RegularExpressions.Regex(
                "CapabilityPoolExhaustionRegistry|assignmentReallocPass|repriceReallocPass|maxReallocIterations"
                + "|PreparePass|ScoutAssignmentFailures|RegisterProvisionFailure|RegisterProvisionSuccess");
            var offenders = System.IO.Directory.GetFiles(dir, "*.cs", System.IO.SearchOption.AllDirectories)
                .SelectMany(f => System.IO.File.ReadLines(f).Select((l, i) => (f, n: i + 1, l)))
                .Where(x => banned.IsMatch(x.l.Split(new[] { "//" }, 2, System.StringSplitOptions.None)[0]))
                .Select(x => System.IO.Path.GetFileName(x.f) + ":" + x.n + ": " + x.l.Trim()).ToList();
            Assert.That(offenders, Is.Empty, string.Join(System.Environment.NewLine, offenders));
        }

        // The selection is a protocol over claims, not a spender: it must not touch the reservation
        // ledger, the lease book or the Economy stages, and it must not close the session (the
        // selected mission executes under its claims).
        [Test]
        public void TheSelectionProtocolSpendsNothingAndLeavesTheSessionOpen()
        {
            string root = AiTurnLoopTests.FindScriptsRoot();
            if (root == null) Assert.Ignore("Assets/Scripts not found from the working directory");
            string file = System.IO.Directory.GetFiles(root, "ProvisioningManager.Selection.cs",
                System.IO.SearchOption.AllDirectories).Single();
            string code = string.Join(System.Environment.NewLine, System.IO.File.ReadLines(file)
                .Select(l => l.Split(new[] { "//" }, 2, System.StringSplitOptions.None)[0]));
            foreach (string banned in new[] { "StrategicResourceReservationLedger", "MissionLeaseBook",
                "EconomyReservationLifecycle", "OperationContinuationWindow", "SpendAuthority", ".Dispose(" })
                Assert.That(code, Does.Not.Contain(banned), banned);
        }

        [Test]
        public void ASuccessfulSelectionKeepsItsClaimsOpenAndTheLedgerUntouched()
        {
            Rig r = Build(2, 9f);
            StrategicResourceReservationLedger.BeginTurn(Us, 3);
            int rowsBefore = StrategicResourceReservationLedger.Rows(Us, 3).Count;
            ProvisioningSelectionOutcome o = ProvisioningManager.ProvisionNext(Us, r.Snap, r.Session,
                r.Provisioning, r.Parking, r.Initial, Steps(f => ProvisioningResult.Ok(Prov(f))));
            Assert.That(StrategicResourceReservationLedger.Rows(Us, 3).Count, Is.EqualTo(rowsBefore));
            Assert.That(r.Provisioning.AlreadyProvisioned(o.SelectedKey), Is.True,
                "the claim of the selected mission is held for its execution");
            Assert.That(r.Session.Pack().LockedClaim.Ap, Is.EqualTo(o.Selected.ClaimedAp),
                "and the allocator treats it as a locked claim in the next pack");
            r.Provisioning.Dispose();
        }

        // ---- PassParking ----

        [Test]
        public void ParkingFiltersTheParkedMissionAndReportsADurableLegOnly()
        {
            WorldSnapshot snap = AiReconAuditBugTests.Snapshot(Us, 3, new HexCoord(5, 1));
            CapabilityPoolExhaustionRegistry.BeginTurn(Us, 3);
            MissionProposal parkedFresh = Job(5), parkedDurable = Job(6), other = Job(7);
            parkedDurable.FromDurableIntent = true;
            var parking = new PassParking();
            parking.Park(Us, parkedFresh);
            parking.Park(Us, parkedDurable);

            var input = new List<MissionProposal> { parkedFresh, parkedDurable, other, null };
            PassParkingResult result = parking.Filter(input, snap, Us);

            Assert.That(result.Retained, Is.EqualTo(new MissionProposal[] { other, null }),
                "a null entry is retained exactly as before");
            Assert.That(result.Deferrals.Count, Is.EqualTo(1));
            Assert.That(result.Deferrals[MissionIntentKey.For(parkedDurable)], Is.EqualTo(PassParking.RetryNextTurnReason));
        }

        [Test]
        public void ParkingReturnsTheSameListWhenThereIsNothingToFilter()
        {
            var empty = new List<MissionProposal>();
            PassParkingResult result = new PassParking().Filter(empty, null, Us);
            Assert.That(result.Retained, Is.SameAs(empty));
            Assert.That(result.Deferrals, Is.Empty);
        }

        [Test]
        public void ANewPassForgetsTheSetButTheRegistryKeepsTheVerdictWhileThePoolIsProvenEmpty()
        {
            WorldSnapshot snap = AiReconAuditBugTests.Snapshot(Us, 3, new HexCoord(5, 1));
            foreach (ArmySnapshot a in snap.Self.Armies)
                a.CurrentMovement = 0;
            CapabilityPoolExhaustionRegistry.BeginTurn(Us, 3);
            MissionProposal job = Job(5);
            var first = new PassParking();
            first.Park(Us, job);
            CapabilityPoolExhaustionRegistry.MarkExhausted(Us, CapabilityPoolKind.Scout, "test");

            var next = new PassParking();   // the next admission pass
            Assert.That(next.IsParked(StableMissionKey.For(job)), Is.False, "the pass set starts empty");
            Assert.That(next.Filter(new List<MissionProposal> { job }, snap, Us).Retained, Is.Empty,
                "but the turn-wide registry still skips the job while its pool is proven empty");
        }
    }
}
#endif
