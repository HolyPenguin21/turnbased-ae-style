using System;
using System.Collections.Generic;
using Game.Ai.V2;
using Game.HexGrid;

namespace ReconAirAssignmentSim
{
    // RECON-AIR-03 acceptance harness — the 3 tests the round-5 brief explicitly names, exercising
    // the REAL production solver (ReconAssignmentPlanner.AssignFromCandidates / BuildFeasibleAirPool
    // — thin internal entry points over the exact RecurseScout/ScoreScoutAssignment code AssignFunded
    // itself uses) against hand-built candidate lists, so no live Unity world/ArmyRegistry is needed.
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            Test_InvalidEarlyAirCandidate_DoesNotHideLaterValidCandidate();
            Test_TwoAirLaunches_CannotEachConsumeSameEnergyPool();
            Test_AirActorCap_AppliesAcrossWholeBatch();

            // Round 7 (Problem 3) — generic multi-resource reprice contract, exercised directly
            // against the real ResourceAllocator/AllocationSession (internal, visible via
            // V2InternalsVisibleTo.cs) with a hand-built minimal WorldSnapshot. No live Unity world
            // needed — Pack()/RegisterProvisionFailure()/RegisterProvisionSuccess() are pure model
            // operations over WorldSnapshot.Self.
            Test_ApEnvelopeTooSmall_RaisesApFloor();
            Test_EnergyEnvelopeTooSmall_RaisesEnergyFloor();
            Test_MultiResourceReprice_RaisesAllRequiredComponents();
            Test_RepriceUsesComponentWiseMaximum();
            Test_InsufficientRepricedEnergy_DefersMission();
            Test_SuccessfulReprice_DoesNotLoopProvisionFailure();
            Test_GroundApOnlyReprice_RemainsUnchanged();

            if (_failures == 0)
            {
                Console.WriteLine("ALL PASS");
                return 0;
            }
            Console.WriteLine($"{_failures} FAILURE(S)");
            return 1;
        }

        // ---------------------------------------------------------------------------------------
        //  Test 1 — filter-before-take. The OLD code did `.Take(remaining)` THEN filtered for
        //  feasibility, so an infeasible early candidate could consume the ONE slot a later, valid
        //  candidate needed. BuildFeasibleAirPool filters FIRST, so the later candidate survives.
        // ---------------------------------------------------------------------------------------
        private static void Test_InvalidEarlyAirCandidate_DoesNotHideLaterValidCandidate()
        {
            var ordered = new List<AirObservationSlot>
            {
                new AirObservationSlot(1, default, 1, 1), // infeasible — earlier in the ordered list
                new AirObservationSlot(2, default, 1, 1), // feasible — later
            };

            List<AirObservationSlot> pool = ReconAssignmentPlanner.BuildFeasibleAirPool(
                ordered, take: 1, feasible: s => s.ActorId == 2);

            Expect("InvalidEarlyAirCandidate_DoesNotHideLaterValidCandidate: pool has exactly 1 slot",
                pool.Count == 1);
            Expect("InvalidEarlyAirCandidate_DoesNotHideLaterValidCandidate: the SURVIVING slot is the later, feasible one (#2), not the earlier infeasible one (#1)",
                pool.Count == 1 && pool[0].ActorId == 2);
        }

        // ---------------------------------------------------------------------------------------
        //  Test 2 — two AirLaunch candidates (different airfields, so no ActorKey collision) each
        //  individually fit their OWN 6-Energy cost, but the batch's shared airEnergyBudget (10) can
        //  only support one of them at once. The solver must not choose both.
        // ---------------------------------------------------------------------------------------
        private static void Test_TwoAirLaunches_CannotEachConsumeSameEnergyPool()
        {
            MissionProposal m1 = MakeRefreshProposal(new HexCoord(10, 0), "M1");
            MissionProposal m2 = MakeRefreshProposal(new HexCoord(20, 0), "M2");
            var open = new List<FundedEntry>
            {
                new FundedEntry { Mission = m1, Priority = 0, Tentative = new ResourceVector(1f, 0f, 0f, 0f, 0f) },
                new FundedEntry { Mission = m2, Priority = 1, Tentative = new ResourceVector(1f, 0f, 0f, 0f, 0f) },
            };

            var cand1 = new ScoutExecutionCandidate(null, new HexCoord(10, 0), 1, 1, 0, 0f, 0, false, 1f,
                ScoutExecutorKind.AirLaunch, new HexCoord(1, 1), null, requiredEnergy: 6f);
            var cand2 = new ScoutExecutionCandidate(null, new HexCoord(20, 0), 1, 1, 0, 0f, 0, false, 1f,
                ScoutExecutorKind.AirLaunch, new HexCoord(2, 2), null, requiredEnergy: 6f);
            var cands = new List<List<ScoutExecutionCandidate>>
            {
                new List<ScoutExecutionCandidate> { cand1 },
                new List<ScoutExecutionCandidate> { cand2 },
            };

            ReconAssignmentResult result = ReconAssignmentPlanner.AssignFromCandidates(
                open, cands, airEnergyBudget: 10f, airActorCap: 10);

            Expect("TwoAirLaunches_CannotEachConsumeSameEnergyPool: at most ONE of the two 6-Energy launches is assigned against a 10-Energy budget",
                result.Assigned.Count == 1);

            float totalEnergyOfAssigned = 0f;
            foreach (KeyValuePair<StableMissionKey, ScoutExecutionCandidate> kv in result.Assigned)
                totalEnergyOfAssigned += kv.Value.RequiredEnergy;
            Expect("TwoAirLaunches_CannotEachConsumeSameEnergyPool: the assigned candidate's Energy fits the shared budget",
                totalEnergyOfAssigned <= 10f);
        }

        // ---------------------------------------------------------------------------------------
        //  Test 3 — three funded missions, each with its OWN distinct, individually-feasible
        //  AirExisting actor (three different ArmyIds, no actor-identity collision), but the batch's
        //  air-actor slot cap is 2. At most 2 may be assigned, never all 3.
        // ---------------------------------------------------------------------------------------
        private static void Test_AirActorCap_AppliesAcrossWholeBatch()
        {
            MissionProposal m1 = MakeRefreshProposal(new HexCoord(1, 0), "M1");
            MissionProposal m2 = MakeRefreshProposal(new HexCoord(2, 0), "M2");
            MissionProposal m3 = MakeRefreshProposal(new HexCoord(3, 0), "M3");
            var open = new List<FundedEntry>
            {
                new FundedEntry { Mission = m1, Priority = 0, Tentative = new ResourceVector(1f, 0f, 0f, 0f, 0f) },
                new FundedEntry { Mission = m2, Priority = 1, Tentative = new ResourceVector(1f, 0f, 0f, 0f, 0f) },
                new FundedEntry { Mission = m3, Priority = 2, Tentative = new ResourceVector(1f, 0f, 0f, 0f, 0f) },
            };

            var cands = new List<List<ScoutExecutionCandidate>>();
            for (int i = 0; i < 3; i++)
            {
                var army = new ArmySnapshot { ArmyId = 101 + i };
                var cand = new ScoutExecutionCandidate(army, new HexCoord(1 + i, 0), 1, 1, 0, 0f, 0, false, 1f,
                    ScoutExecutorKind.AirExisting, requiredEnergy: 0f);
                cands.Add(new List<ScoutExecutionCandidate> { cand });
            }

            ReconAssignmentResult result = ReconAssignmentPlanner.AssignFromCandidates(
                open, cands, airEnergyBudget: 999f, airActorCap: 2);

            Expect("AirActorCap_AppliesAcrossWholeBatch: at most airActorCap (2) air actors are assigned across all 3 funded missions",
                result.Assigned.Count <= 2);

            var distinctActors = new HashSet<int>();
            foreach (KeyValuePair<StableMissionKey, ScoutExecutionCandidate> kv in result.Assigned)
                distinctActors.Add(kv.Value.ActorKey);
            Expect("AirActorCap_AppliesAcrossWholeBatch: distinct air actors chosen never exceed the cap",
                distinctActors.Count <= 2);
        }

        // =========================================================================================
        //  ROUND 7 (Problem 3) — generic ProvisionRequirement / component-wise-max reprice contract.
        // =========================================================================================
        private static WorldSnapshot BuildSnapshot(int ap, float energy = 0f, float human = 0f,
            float materials = 0f, float tech = 0f) => new WorldSnapshot
        {
            TurnNumber = 1,
            Self = new SelfSnapshot
            {
                ActionPoints = ap,
                Stockpile = new ResourceBundle { Human = human, Energy = energy, Materials = materials, Tech = tech },
                Armies = new List<ArmySnapshot>(),
                BaseHexes = new List<HexCoord>(),
            },
        };

        private static MissionProposal MakeReconMission(string attemptId, float apMin, float apDesired)
        {
            var m = new MissionProposal
            {
                AttemptId = attemptId,
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget
                {
                    FocusHex = new HexCoord(0, 0),
                    Kind = ScoutTargetKind.Refresh,
                    Stealth = StealthRequirement.None,
                    DetectionRisk = 0f,
                },
                BaseValue = 10f,
                Requirements = new MissionRequirements { ApMinimum = apMin, ApDesired = apDesired, ApMaximum = apDesired },
            };
            m.Axes.Value[DesireAxis.Recon] = 1f;
            return m;
        }

        // Radar that hands the WHOLE pool to Recon — keeps the reprice-floor math in these tests
        // independent of the (unrelated) axis-slicing behaviour already covered elsewhere.
        private static Radar AllReconRadar()
        {
            var r = new Radar();
            foreach (DesireAxis a in new[] { DesireAxis.Recon, DesireAxis.Aggression, DesireAxis.Defence,
                         DesireAxis.Economy, DesireAxis.Development })
                r.Weight[a] = a == DesireAxis.Recon ? 1f : 0f;
            return r;
        }

        private static void Test_ApEnvelopeTooSmall_RaisesApFloor()
        {
            MissionProposal m = MakeReconMission("AP1", apMin: 1f, apDesired: 1f);
            WorldSnapshot snap = BuildSnapshot(ap: 10);
            AllocationSession session = ResourceAllocator.BeginTurn(snap, AllReconRadar(),
                new List<MissionProposal> { m }, new List<Commitment>(), null);

            TentativeAllocation a1 = session.Pack();
            FundedEntry fe1 = a1.Funded.Find(f => f.Mission == m);
            Expect("ApEnvelopeTooSmall_RaisesApFloor: mission funded pass 1", fe1 != null);

            // AP-only convenience overload — the pre-round-7 call shape every existing caller uses.
            session.RegisterProvisionFailure(fe1, ProvisionFailure.EnvelopeTooSmall(3f, "needs 3 AP"));

            TentativeAllocation a2 = session.Pack();
            FundedEntry fe2 = a2.Funded.Find(f => f.Mission == m);
            Expect("ApEnvelopeTooSmall_RaisesApFloor: mission re-funded at raised AP floor (>=3)",
                fe2 != null && fe2.Tentative.Ap >= 3f - 0.01f);
        }

        private static void Test_EnergyEnvelopeTooSmall_RaisesEnergyFloor()
        {
            MissionProposal m = MakeReconMission("EN1", apMin: 1f, apDesired: 1f);
            WorldSnapshot snap = BuildSnapshot(ap: 10, energy: 20f);
            AllocationSession session = ResourceAllocator.BeginTurn(snap, AllReconRadar(),
                new List<MissionProposal> { m }, new List<Commitment>(), null);

            TentativeAllocation a1 = session.Pack();
            FundedEntry fe1 = a1.Funded.Find(f => f.Mission == m);
            Expect("EnergyEnvelopeTooSmall_RaisesEnergyFloor: mission funded pass 1", fe1 != null);
            Expect("EnergyEnvelopeTooSmall_RaisesEnergyFloor: no Energy drawn pass 1 (none required yet)",
                fe1 != null && fe1.PhysicalDraw.Energy <= 0.01f);

            session.RegisterProvisionFailure(fe1, ProvisionFailure.EnvelopeTooSmall(
                new ProvisionRequirement(1f, new ResourceVector(0f, 0f, 5f, 0f, 0f)), "needs 5 Energy"));

            TentativeAllocation a2 = session.Pack();
            FundedEntry fe2 = a2.Funded.Find(f => f.Mission == m);
            Expect("EnergyEnvelopeTooSmall_RaisesEnergyFloor: mission re-funded with an Energy floor (>=5)",
                fe2 != null && fe2.PhysicalDraw.Energy >= 5f - 0.01f);
        }

        private static void Test_MultiResourceReprice_RaisesAllRequiredComponents()
        {
            MissionProposal m = MakeReconMission("MR1", apMin: 1f, apDesired: 1f);
            WorldSnapshot snap = BuildSnapshot(ap: 10, energy: 20f, materials: 20f);
            AllocationSession session = ResourceAllocator.BeginTurn(snap, AllReconRadar(),
                new List<MissionProposal> { m }, new List<Commitment>(), null);

            TentativeAllocation a1 = session.Pack();
            FundedEntry fe1 = a1.Funded.Find(f => f.Mission == m);
            session.RegisterProvisionFailure(fe1, ProvisionFailure.EnvelopeTooSmall(
                new ProvisionRequirement(2f, new ResourceVector(0f, 0f, 3f, 4f, 0f)), "needs 2 AP / 3 Energy / 4 Materials"));

            TentativeAllocation a2 = session.Pack();
            FundedEntry fe2 = a2.Funded.Find(f => f.Mission == m);
            Expect("MultiResourceReprice_RaisesAllRequiredComponents: AP floor raised", fe2 != null && fe2.Tentative.Ap >= 2f - 0.01f);
            Expect("MultiResourceReprice_RaisesAllRequiredComponents: Energy floor raised", fe2 != null && fe2.PhysicalDraw.Energy >= 3f - 0.01f);
            Expect("MultiResourceReprice_RaisesAllRequiredComponents: Materials floor raised", fe2 != null && fe2.PhysicalDraw.Materials >= 4f - 0.01f);
        }

        private static void Test_RepriceUsesComponentWiseMaximum()
        {
            MissionProposal m = MakeReconMission("CW1", apMin: 1f, apDesired: 1f);
            WorldSnapshot snap = BuildSnapshot(ap: 10, energy: 20f);
            AllocationSession session = ResourceAllocator.BeginTurn(snap, AllReconRadar(),
                new List<MissionProposal> { m }, new List<Commitment>(), null);

            TentativeAllocation a1 = session.Pack();
            FundedEntry fe1 = a1.Funded.Find(f => f.Mission == m);
            // First report: Ap=2, Energy=0. Second report: Ap=1, Energy=5. A correct MAX merge
            // yields Ap=2 (not 2+1=3) and Energy=5 (not 0+5 double-counted from a wrong sum path).
            session.RegisterProvisionFailure(fe1, ProvisionFailure.EnvelopeTooSmall(
                new ProvisionRequirement(2f, ResourceVector.Zero), "first report"));
            TentativeAllocation aMid = session.Pack();
            FundedEntry feMid = aMid.Funded.Find(f => f.Mission == m);
            session.RegisterProvisionFailure(feMid, ProvisionFailure.EnvelopeTooSmall(
                new ProvisionRequirement(1f, new ResourceVector(0f, 0f, 5f, 0f, 0f)), "second report"));

            TentativeAllocation a2 = session.Pack();
            FundedEntry fe2 = a2.Funded.Find(f => f.Mission == m);
            Expect("RepriceUsesComponentWiseMaximum: Ap floor is max(2,1)=2, never summed to 3",
                fe2 != null && fe2.Tentative.Ap >= 2f - 0.01f && fe2.Tentative.Ap < 3f - 0.01f);
            Expect("RepriceUsesComponentWiseMaximum: Energy floor is max(0,5)=5",
                fe2 != null && fe2.PhysicalDraw.Energy >= 5f - 0.01f && fe2.PhysicalDraw.Energy < 6f);
        }

        private static void Test_InsufficientRepricedEnergy_DefersMission()
        {
            MissionProposal m = MakeReconMission("DE1", apMin: 1f, apDesired: 1f);
            // Only 2 Energy ever exists — a 10-Energy floor can never be covered.
            WorldSnapshot snap = BuildSnapshot(ap: 10, energy: 2f);
            AllocationSession session = ResourceAllocator.BeginTurn(snap, AllReconRadar(),
                new List<MissionProposal> { m }, new List<Commitment>(), null);

            TentativeAllocation a1 = session.Pack();
            FundedEntry fe1 = a1.Funded.Find(f => f.Mission == m);
            session.RegisterProvisionFailure(fe1, ProvisionFailure.EnvelopeTooSmall(
                new ProvisionRequirement(1f, new ResourceVector(0f, 0f, 10f, 0f, 0f)), "needs 10 Energy, only 2 exist"));

            TentativeAllocation a2 = session.Pack();
            FundedEntry fe2 = a2.Funded.Find(f => f.Mission == m);
            DeferredEntry d2 = a2.Deferred.Find(d => d.Mission == m);
            Expect("InsufficientRepricedEnergy_DefersMission: mission is NOT funded when the physical pool can't cover the floor",
                fe2 == null);
            Expect("InsufficientRepricedEnergy_DefersMission: deferred with InsufficientPhysical",
                d2 != null && d2.Reason == DeferReason.InsufficientPhysical);
        }

        private static void Test_SuccessfulReprice_DoesNotLoopProvisionFailure()
        {
            MissionProposal m = MakeReconMission("SR1", apMin: 1f, apDesired: 1f);
            WorldSnapshot snap = BuildSnapshot(ap: 10, energy: 20f);
            AllocationSession session = ResourceAllocator.BeginTurn(snap, AllReconRadar(),
                new List<MissionProposal> { m }, new List<Commitment>(), null);

            TentativeAllocation a1 = session.Pack();
            FundedEntry fe1 = a1.Funded.Find(f => f.Mission == m);
            session.RegisterProvisionFailure(fe1, ProvisionFailure.EnvelopeTooSmall(
                new ProvisionRequirement(1f, new ResourceVector(0f, 0f, 4f, 0f, 0f)), "needs 4 Energy"));

            TentativeAllocation a2 = session.Pack();
            FundedEntry fe2 = a2.Funded.Find(f => f.Mission == m);
            Expect("SuccessfulReprice_DoesNotLoopProvisionFailure: re-funded at the raised floor", fe2 != null);
            // Simulate provisioning succeeding this time at the real cost.
            session.RegisterProvisionSuccess(fe2, claimedAp: 1f, claimedPhysical: new ResourceVector(0f, 0f, 4f, 0f, 0f));

            TentativeAllocation a3 = session.Pack();
            bool stillInFreshFunded = a3.Funded.Exists(f => f.Mission == m);
            Expect("SuccessfulReprice_DoesNotLoopProvisionFailure: a locked (already-provisioned) mission is not re-funded/re-failed again the same turn",
                !stillInFreshFunded);
            Expect("SuccessfulReprice_DoesNotLoopProvisionFailure: locked claim reflects the successful provision",
                a3.LockedClaim.Ap >= 1f - 0.01f);
        }

        private static void Test_GroundApOnlyReprice_RemainsUnchanged()
        {
            // Regression proof for Problem 3: an AP-only caller (Ground Scout / Raid / every other
            // axis's shape today) that never reports a physical shortfall must see EXACTLY the
            // pre-round-7 float-floor behaviour — Physical stays Zero throughout.
            MissionProposal m = MakeReconMission("GA1", apMin: 1f, apDesired: 1f);
            WorldSnapshot snap = BuildSnapshot(ap: 10);
            AllocationSession session = ResourceAllocator.BeginTurn(snap, AllReconRadar(),
                new List<MissionProposal> { m }, new List<Commitment>(), null);

            TentativeAllocation a1 = session.Pack();
            FundedEntry fe1 = a1.Funded.Find(f => f.Mission == m);
            session.RegisterProvisionFailure(fe1, ProvisionFailure.EnvelopeTooSmall(2.5f, "AP-only shortfall"));

            TentativeAllocation a2 = session.Pack();
            FundedEntry fe2 = a2.Funded.Find(f => f.Mission == m);
            Expect("GroundApOnlyReprice_RemainsUnchanged: AP floor raised to 2.5",
                fe2 != null && System.Math.Abs(fe2.Tentative.Ap - 2.5f) < 0.01f);
            Expect("GroundApOnlyReprice_RemainsUnchanged: Physical draw stays exactly Zero (no phantom physical floor)",
                fe2 != null && !fe2.PhysicalDraw.AnyPhysical);
        }

        private static MissionProposal MakeRefreshProposal(HexCoord focus, string attemptId) =>
            new MissionProposal
            {
                AttemptId = attemptId,
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget
                {
                    FocusHex = focus,
                    Kind = ScoutTargetKind.Refresh,
                    Stealth = StealthRequirement.None,
                    DetectionRisk = 0f,
                },
                BaseValue = 1f,
            };

        private static void Expect(string name, bool condition)
        {
            if (condition)
            {
                Console.WriteLine($"PASS  {name}");
                return;
            }
            _failures++;
            Console.WriteLine($"FAIL  {name}");
        }
    }
}
