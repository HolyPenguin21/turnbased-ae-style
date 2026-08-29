using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;

namespace CommitmentSim
{
    // Acceptance harness for Strategy V2 build-order step 7 (Mission Continuity). Scripts turns and
    // feeds MissionTurnOutcome FACTS (built through the real MissionOutcomeLedger) into
    // MissionContinuityLayer, exactly as Pipeline.RunTurn does — execution against live ArmyData
    // cannot run headless. Pins BEHAVIOUR (a started recon chain survives Radar noise as a funded
    // commitment; continuation is earned by moving, not by spending AP; siege suspends funding but
    // keeps the intent), not magnitudes.
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Scenario01_Canonical_SurvivesRadarCollapse();
            Scenario02_SurveilNoMovement_NoCommitment();
            Scenario03_SurveilEntersStealthThenBlocked_CommitmentEarned();
            Scenario04_TargetSatisfiedExternally_Retire();
            Scenario05_NoObservationVantage_RetireAndCooldown();
            Scenario06_ExploreProgress_HysteresisHolds();
            Scenario07_SoftCommitmentUnderSiege_Suspended();
            Scenario08_RepricePass1FailPass2Success_LedgerReportsFinalState();
            Scenario09_SurveilAlreadyReObserved_Retired();
            Scenario10_CommitmentOrderingIsDeterministic();
            Scenario11_PoolExhaustedIsDistinctFromAProvisioningBlock();

            Console.WriteLine();
            Console.WriteLine($"commitment-sim: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 01 canonical ----
        private static void Scenario01_Canonical_SurvivesRadarCollapse()
        {
            PlayerSetupData me = Fresh("S1");
            int tracked = 4242;

            // T1: a Surveil executes, walks 2 hexes, runs out of movement, unfinished.
            MissionProposal t1prop = SurveilProp(H(9, 3), tracked, baseValue: 40f, ap: 1f);
            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExecOutcome(t1prop, ExecutionStopReason.OutOfMovement, steps: 2, enteredStealth: false, wasCommitment: false, mover: 7) });

            MissionIntentState st = MissionIntentRegistry.GetOrCreate(me);
            bool created = st.Count == 1
                && st.All.Single().Funding == CommitmentTier.Soft
                && st.All.Single().Status == IntentStatus.Active
                && st.All.Single().PreferredMoverArmyId == 7;
            Check("01a Surveil that started walking -> Soft intent, mover remembered", created);

            MissionIntentKey key = st.All.Single().IntentKey;

            // T2: Radar collapses (RCN weight 0.05). The intent must still be re-materialised, bound
            //     as a commitment, and FUNDED — while a fresh mission at the same RCN slice is not.
            WorldSnapshot t2 = Snap(turn: 2, actionPoints: 6);
            t2.Threat.ReconContactByArmyId = new Dictionary<int, EnemyContactSnapshot>
            {
                [tracked] = SurveilContact(H(9, 4), tracked), // focus drifted one hex — same intent
            };
            t2.Threat.Contacts = new List<EnemyContactSnapshot> { t2.Threat.ReconContactByArmyId[tracked] };

            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(me, t2);
            Check("01b intent still active next turn (no siege)", active.Count == 1 && active[0].IntentKey.Equals(key));

            var breakdown = new DesireBreakdown { ReconExploration = 0.02f, ReconSurveillance = 0.02f };
            List<MissionProposal> missions = MissionLayer.Propose(t2, breakdown, active);
            missions.Add(FreshExploreProp(H(2, 0), baseValue: 45f, ap: 1f)); // a competing fresh decision

            bool surveilMaterialised = missions.Any(m => MissionIntentKey.For(m).Equals(key));
            Check("01c Surveil re-materialised despite ReconSurveillance ~0", surveilMaterialised);

            List<Commitment> commitments = MissionContinuityLayer.BindFunding(active, missions);
            Check("01d Soft intent bound as a commitment", commitments.Count == 1 && commitments[0].Tier == CommitmentTier.Soft);

            var radar = LowReconRadar();
            AllocationSession session = ResourceAllocator.BeginTurn(t2, radar, missions, commitments, me);
            TentativeAllocation alloc = session.Pack();

            StableMissionKey surveilKey = StableMissionKey.For(commitments[0].Mission);
            bool commitmentFunded = alloc.Funded.Any(f => f.IsCommitment && f.Mission != null
                && StableMissionKey.For(f.Mission).Equals(surveilKey));
            bool freshDeferred = alloc.Deferred.Any(d => d.Reason == DeferReason.InsufficientBudget);
            Check("01e commitment funded through the Radar collapse; fresh decision deferred on budget",
                commitmentFunded && freshDeferred);

            // T2 execution: another hex, still unfinished.
            MissionContinuityLayer.ReconcileAfterTurn(me, 2,
                new[] { ExecOutcome(commitments[0].Mission, ExecutionStopReason.OutOfMovement, steps: 1, enteredStealth: false, wasCommitment: true, mover: 7) });
            MissionIntent afterT2 = MissionIntentRegistry.GetOrCreate(me).All.SingleOrDefault();
            Check("01f intent advanced, still one, identity unchanged",
                afterT2 != null && afterT2.IntentKey.Equals(key) && afterT2.TurnsActive == 2 && afterT2.StallTurns == 0);

            // T3: Radar recovers, the scout re-observes the target -> objective met -> retire.
            MissionContinuityLayer.ReconcileAfterTurn(me, 3,
                new[] { ExecOutcome(commitments[0].Mission, ExecutionStopReason.ReachedGoal, steps: 1, enteredStealth: false, wasCommitment: true, mover: 7) });
            Check("01g objective met -> intent retired", MissionIntentRegistry.GetOrCreate(me).Count == 0);
        }

        // ---------------------------------------------------------------- 02 no movement ----
        private static void Scenario02_SurveilNoMovement_NoCommitment()
        {
            PlayerSetupData me = Fresh("S2");
            MissionProposal p = SurveilProp(H(5, 5), 111, 30f, 1f);
            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExecOutcome(p, ExecutionStopReason.NoSafeStep, steps: 0, enteredStealth: false, wasCommitment: false, mover: 3) });
            Check("02 Surveil made no move / no stealth entry -> no intent (continuation is earned by moving)",
                MissionIntentRegistry.GetOrCreate(me).Count == 0);
        }

        // ---------------------------------------------------------------- 03 stealth entry ----
        private static void Scenario03_SurveilEntersStealthThenBlocked_CommitmentEarned()
        {
            PlayerSetupData me = Fresh("S3");
            MissionProposal p = SurveilProp(H(6, 6), 222, 33f, 2f);
            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExecOutcome(p, ExecutionStopReason.NoSafeStep, steps: 0, enteredStealth: true, wasCommitment: false, mover: 5) });
            MissionIntentState st = MissionIntentRegistry.GetOrCreate(me);
            Check("03 entered required stealth then blocked -> Soft intent (a state change is progress)",
                st.Count == 1 && st.All.Single().Funding == CommitmentTier.Soft);
        }

        // ---------------------------------------------------------------- 04 target satisfied ----
        private static void Scenario04_TargetSatisfiedExternally_Retire()
        {
            PlayerSetupData me = Fresh("S4");
            MissionProposal p = SurveilProp(H(7, 1), 333, 28f, 1f);
            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExecOutcome(p, ExecutionStopReason.OutOfMovement, steps: 2, enteredStealth: false, wasCommitment: false, mover: 8) });
            Check("04a intent exists", MissionIntentRegistry.GetOrCreate(me).Count == 1);

            MissionTurnOutcome sat = FailOutcome(p, ProvisionFailure.TargetSatisfied("another scout re-observed it"));
            Check("04b TargetSatisfied classifies as Completed", sat.Outcome == ExecutionOutcome.Completed && sat.ObjectiveSatisfied);
            MissionContinuityLayer.ReconcileAfterTurn(me, 2, new[] { sat });
            Check("04c intent retired as complete", MissionIntentRegistry.GetOrCreate(me).Count == 0);
        }

        // ---------------------------------------------------------------- 05 structural ----
        private static void Scenario05_NoObservationVantage_RetireAndCooldown()
        {
            PlayerSetupData me = Fresh("S5");
            MissionProposal p = SurveilProp(H(8, 2), 444, 26f, 1f);
            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExecOutcome(p, ExecutionStopReason.OutOfMovement, steps: 3, enteredStealth: false, wasCommitment: false, mover: 9) });
            Check("05a intent exists", MissionIntentRegistry.GetOrCreate(me).Count == 1);

            MissionTurnOutcome fail = FailOutcome(p, ProvisionFailure.NoObservationVantage("no on-map vantage"));
            Check("05b NoObservationVantage is a structural failure", fail.StructuralFailure);
            MissionContinuityLayer.ReconcileAfterTurn(me, 2, new[] { fail });

            bool retired = MissionIntentRegistry.GetOrCreate(me).Count == 0;
            bool cooled = AiAllocatorStateRegistry.GetOrCreate(me).OnCooldown(fail.AttemptKey, 3);
            Check("05c intent retired AND attempt key on allocator cooldown", retired && cooled);
        }

        // ---------------------------------------------------------------- 06 hysteresis ----
        private static void Scenario06_ExploreProgress_HysteresisHolds()
        {
            // Step 7.1 — MissionLayer no longer trims to K, so the retarget hysteresis is asserted
            // on the ALLOCATOR's funded portfolio (K = maxConcurrentReconExecutions = 2), through
            // the same pipeline order Pipeline.RunTurn uses: ResolveActive -> Propose -> BindFunding
            // -> Pack. Radar is all-Recon and AP is ample, so the ONLY thing deciding the two
            // funded hexes is MissionAdmissionPolicy.AdmissionRank + the K cut.
            var bd = new DesireBreakdown { ReconExploration = 0.6f };

            // Case A — a marginally better fresh frontier hex must NOT flip the heading. The
            // in-flight scout is heading for H(0,9).
            PlayerSetupData a = Fresh("S6a");
            MissionProposal ex = FreshExploreProp(H(0, 9), 40f, 1f);
            MissionContinuityLayer.ReconcileAfterTurn(a, 1,
                new[] { ExecOutcome(ex, ExecutionStopReason.OutOfMovement, steps: 2, enteredStealth: false, wasCommitment: false, mover: 2) });
            MissionIntentState sa = MissionIntentRegistry.GetOrCreate(a);
            Check("06a Explore progress -> intent kept, Funding == None",
                sa.Count == 1 && sa.All.Single().Funding == CommitmentTier.None);

            // C near base (top rank), alt A better than the incumbent but within the +20% margin.
            WorldSnapshot snapA = ExploreSnap(turn: 2,
                incumbent: H(0, 9), incumbentFreshNeighbours: 4,
                frontier: new[] { (H(0, 2), 4, 2), (H(4, 0), 4, 7) });   // C near base ; A better-by-<20%
            List<HexCoord> fundedA = FundedFocusHexes(a, snapA, bd);
            Check("06b incumbent within the retarget margin keeps a K slot; the better-by-<20% alt does not",
                fundedA.Count == 2 && fundedA.Contains(H(0, 9)) && fundedA.Contains(H(0, 2)) && !fundedA.Contains(H(4, 0)));

            // Case B — the alt is now as good as C (well past the margin): both fresh hexes take
            // the K slots and the incumbent is dropped for this turn (ExecutionCapacity).
            PlayerSetupData b = Fresh("S6b");
            MissionProposal exB = FreshExploreProp(H(0, 9), 40f, 1f);
            MissionContinuityLayer.ReconcileAfterTurn(b, 1,
                new[] { ExecOutcome(exB, ExecutionStopReason.OutOfMovement, steps: 2, enteredStealth: false, wasCommitment: false, mover: 2) });
            WorldSnapshot snapB = ExploreSnap(turn: 2,
                incumbent: H(0, 9), incumbentFreshNeighbours: 4,
                frontier: new[] { (H(0, 2), 4, 2), (H(4, 0), 4, 2) });   // C and A both near base
            List<HexCoord> fundedB = FundedFocusHexes(b, snapB, bd);
            Check("06c two fresh hexes past the retarget margin fill K; the incumbent is dropped this turn",
                fundedB.Count == 2 && fundedB.Contains(H(0, 2)) && fundedB.Contains(H(4, 0)) && !fundedB.Contains(H(0, 9)));
        }

        // Full ResolveActive -> Propose -> BindFunding -> Pack, returning the focus hexes the
        // allocator actually funded. All-Recon radar + ample AP so K + AdmissionRank are the only
        // binding constraints.
        private static List<HexCoord> FundedFocusHexes(PlayerSetupData player, WorldSnapshot snap, DesireBreakdown bd)
        {
            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(player, snap);
            List<MissionProposal> proposals = MissionLayer.Propose(snap, bd, active);
            List<Commitment> commitments = MissionContinuityLayer.BindFunding(active, proposals);
            var radar = new Radar();
            radar.Weight[DesireAxis.Recon] = 1f;
            foreach (DesireAxis ax in DesireAxes.All)
                if (ax != DesireAxis.Recon) radar.Weight[ax] = 0f;
            AllocationSession session = ResourceAllocator.BeginTurn(snap, radar, proposals, commitments, player);
            TentativeAllocation alloc = session.Pack();
            return alloc.Funded
                .Where(f => f.Mission?.Target is ScoutMissionTarget)
                .Select(f => ((ScoutMissionTarget)f.Mission.Target).FocusHex)
                .ToList();
        }

        // ---------------------------------------------------------------- 07 siege suspend ----
        private static void Scenario07_SoftCommitmentUnderSiege_Suspended()
        {
            PlayerSetupData me = Fresh("S7");
            int tracked = 777;
            MissionProposal p = SurveilProp(H(9, 9), tracked, 35f, 1f);
            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExecOutcome(p, ExecutionStopReason.OutOfMovement, steps: 2, enteredStealth: false, wasCommitment: false, mover: 6) });

            WorldSnapshot siege = Snap(turn: 2, actionPoints: 6);
            siege.Threat.UnderSiege = true;
            siege.Threat.ReconContactByArmyId = new Dictionary<int, EnemyContactSnapshot> { [tracked] = SurveilContact(H(9, 9), tracked) };

            List<MissionIntent> underSiege = MissionContinuityLayer.ResolveActive(me, siege);
            MissionIntent it = MissionIntentRegistry.GetOrCreate(me).All.Single();
            Check("07a under siege -> Soft intent suspended, not in the active set, no funding bound",
                underSiege.Count == 0
                && it.Status == IntentStatus.Suspended && it.Suspended == SuspendReason.Siege
                && MissionContinuityLayer.BindFunding(underSiege, new List<MissionProposal>()).Count == 0);

            WorldSnapshot calm = Snap(turn: 3, actionPoints: 6);
            calm.Threat.ReconContactByArmyId = new Dictionary<int, EnemyContactSnapshot> { [tracked] = SurveilContact(H(9, 9), tracked) };
            List<MissionIntent> afterSiege = MissionContinuityLayer.ResolveActive(me, calm);
            Check("07b siege lifts -> same intent active again",
                afterSiege.Count == 1 && afterSiege[0].Status == IntentStatus.Active);
        }

        // ---------------------------------------------------------------- 08 reprice ----
        private static void Scenario08_RepricePass1FailPass2Success_LedgerReportsFinalState()
        {
            PlayerSetupData me = Fresh("S8");
            MissionProposal p = SurveilProp(H(3, 7), 888, 22f, 2f);
            var tt = (ScoutMissionTarget)p.Target;

            var led = new MissionOutcomeLedger();
            led.RegisterProposals(new[] { p });
            led.RecordProvisionFailure(p, ProvisionFailure.EnvelopeTooSmall(2f, "pass 1"));   // intermediate
            led.RecordProvisionSuccess(p, new ProvisionedMission
            {
                Mission = p, Key = StableMissionKey.For(p), Kind = MissionKind.Scout, ScoutKind = tt.Kind,
                MoverArmyId = 4, FocusHex = tt.FocusHex, ExecutionHex = tt.FocusHex,
                TrackedArmyId = tt.Contact.Army.ArmyId, BaselineObservedTurn = 0, ClaimedAp = 2f,
            });
            led.RecordExecution(new ExecutionResult
            {
                Key = StableMissionKey.For(p), StepsMoved = 2, EnteredStealth = false,
                ReachedGoal = false, StopReason = ExecutionStopReason.OutOfMovement, ApSpent = 2f,
            });
            List<MissionTurnOutcome> outs = led.Finalize();

            Check("08a ledger reports the FINAL state (ProductiveStop), not the stale EnvelopeTooSmall",
                outs.Count == 1 && outs[0].Outcome == ExecutionOutcome.ProductiveStop
                && outs[0].MadeProgress && !outs[0].StructuralFailure);

            MissionContinuityLayer.ReconcileAfterTurn(me, 1, outs);
            Check("08b -> Soft intent created (the reprice was not treated as a failure)",
                MissionIntentRegistry.GetOrCreate(me).Count == 1
                && MissionIntentRegistry.GetOrCreate(me).All.Single().Funding == CommitmentTier.Soft);
        }

        // ------------------------------------------------------- 09 already re-observed ----
        private static void Scenario09_SurveilAlreadyReObserved_Retired()
        {
            // A different scout / action honestly re-observed the tracked army AFTER the intent's
            // baseline. The old Surveil is already done — the snapshot knows via LastObservedTurn.
            PlayerSetupData done = Fresh("S9a");
            PutSurveilIntent(done, tracked: 42, focus: H(9, 3), tier: CommitmentTier.Soft, createdTurn: 5, baseline: 5);
            WorldSnapshot fresher = Snap(turn: 8, actionPoints: 6);
            fresher.Threat.ReconContactByArmyId = new Dictionary<int, EnemyContactSnapshot>
            {
                [42] = ContactObservedAt(H(9, 3), 42, lastObservedTurn: 7),
            };
            List<MissionIntent> a9 = MissionContinuityLayer.ResolveActive(done, fresher);
            Check("09a Surveil re-observed past its baseline -> intent retired at turn start",
                a9.Count == 0 && MissionIntentRegistry.GetOrCreate(done).Count == 0);

            // Contrast: the only fix we have is still no fresher than the baseline -> keep chasing.
            PlayerSetupData chasing = Fresh("S9b");
            PutSurveilIntent(chasing, tracked: 42, focus: H(9, 3), tier: CommitmentTier.Soft, createdTurn: 5, baseline: 5);
            WorldSnapshot stale = Snap(turn: 8, actionPoints: 6);
            stale.Threat.ReconContactByArmyId = new Dictionary<int, EnemyContactSnapshot>
            {
                [42] = ContactObservedAt(H(9, 3), 42, lastObservedTurn: 3),
            };
            Check("09b no fresher fix than the baseline -> intent still active",
                MissionContinuityLayer.ResolveActive(chasing, stale).Count == 1);
        }

        // ------------------------------------------------------- 10 deterministic order ----
        private static void Scenario10_CommitmentOrderingIsDeterministic()
        {
            PlayerSetupData me = Fresh("S10");
            PutSurveilIntent(me, tracked: 20, focus: H(2, 0), tier: CommitmentTier.Soft, createdTurn: 2, baseline: 0);
            PutSurveilIntent(me, tracked: 50, focus: H(5, 0), tier: CommitmentTier.Hard, createdTurn: 5, baseline: 0);
            PutSurveilIntent(me, tracked: 10, focus: H(1, 0), tier: CommitmentTier.Soft, createdTurn: 1, baseline: 0);

            WorldSnapshot s = Snap(turn: 9, actionPoints: 6);
            s.Threat.ReconContactByArmyId = new Dictionary<int, EnemyContactSnapshot>
            {
                [20] = ContactObservedAt(H(2, 0), 20, 0),
                [50] = ContactObservedAt(H(5, 0), 50, 0),
                [10] = ContactObservedAt(H(1, 0), 10, 0),
            };

            List<int> order = MissionContinuityLayer.ResolveActive(me, s)
                .Select(i => i.Scout.TrackedArmyId.Value).ToList();
            // Hard before Soft; within a tier, the older intent first.
            Check("10 commitments resolve in a fixed order (Tier desc, CreatedTurn asc)",
                order.SequenceEqual(new[] { 50, 10, 20 }));
        }

        // ------------------------------------------- 11 pool cap != provisioning block ----
        private static void Scenario11_PoolExhaustedIsDistinctFromAProvisioningBlock()
        {
            // Real pool cap -> Suspended(PoolExhausted), stall NOT ticked.
            PlayerSetupData pe = Fresh("S11a");
            MissionProposal p1 = SurveilProp(H(3, 3), 601, 30f, 2f);
            MissionContinuityLayer.ReconcileAfterTurn(pe, 1,
                new[] { ExecOutcome(p1, ExecutionStopReason.OutOfMovement, steps: 2, false, false, 4) });

            var led = new MissionOutcomeLedger();
            led.RegisterProposals(new[] { p1 });
            led.RegisterCommitments(new[] { new Commitment { IntentKey = MissionIntentKey.For(p1), Mission = p1, Tier = CommitmentTier.Soft, ContinuationValue = p1.BaseValue } });
            led.RecordDeferrals(new[] { new DeferredEntry { Mission = p1, Reason = DeferReason.CommitmentPoolExhausted } });
            MissionTurnOutcome poolOut = led.Finalize().Single();
            Check("11a CommitmentPoolExhausted classifies as Blocked with the defer reason kept",
                poolOut.Outcome == ExecutionOutcome.Blocked && poolOut.AllocationDeferReason == DeferReason.CommitmentPoolExhausted);
            MissionContinuityLayer.ReconcileAfterTurn(pe, 2, new[] { poolOut });
            MissionIntent afterPool = MissionIntentRegistry.GetOrCreate(pe).All.Single();
            Check("11b -> Suspended(PoolExhausted), stall not ticked",
                afterPool.Status == IntentStatus.Suspended && afterPool.Suspended == SuspendReason.PoolExhausted
                && afterPool.StallTurns == 0);

            // An ordinary provisioning block (NoExecutableStep) is ALSO Blocked, but must NOT be
            // read as a pool cap -> no suspension, stall DOES tick.
            PlayerSetupData nb = Fresh("S11b");
            MissionProposal p2 = SurveilProp(H(4, 4), 701, 30f, 1f);
            MissionContinuityLayer.ReconcileAfterTurn(nb, 1,
                new[] { ExecOutcome(p2, ExecutionStopReason.OutOfMovement, steps: 2, false, false, 4) });
            MissionTurnOutcome blockOut = FailOutcome(p2, ProvisionFailure.NoExecutableStep("no safe first step"));
            Check("11c NoExecutableStep is Blocked but carries no pool-defer reason",
                blockOut.Outcome == ExecutionOutcome.Blocked && blockOut.AllocationDeferReason == null
                && blockOut.ProvisionFailureKindValue == ProvisionFailureKind.NoExecutableStep);
            MissionContinuityLayer.ReconcileAfterTurn(nb, 2, new[] { blockOut });
            MissionIntent afterBlock = MissionIntentRegistry.GetOrCreate(nb).All.Single();
            Check("11d -> not suspended, stall ticked",
                afterBlock.Status == IntentStatus.Active && afterBlock.StallTurns == 1);
        }

        // ================================================================ builders ====

        private static void PutSurveilIntent(PlayerSetupData player, int tracked, HexCoord focus,
            CommitmentTier tier, int createdTurn, int baseline)
        {
            MissionIntentRegistry.GetOrCreate(player).Put(new MissionIntent
            {
                IntentKey = new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil, tracked, 0, 0),
                LastAttemptKey = default,
                Kind = MissionKind.Scout,
                Funding = tier,
                Status = IntentStatus.Active,
                Suspended = SuspendReason.None,
                Objective = new ScoutIntent
                {
                    Kind = ScoutTargetKind.Surveil, FocusHex = focus,
                    TrackedArmyId = tracked, BaselineObservedTurn = baseline,
                },
                CreatedTurn = createdTurn,
                TurnsActive = 1,
                LastProgressTurn = createdTurn,
            });
        }

        private static EnemyContactSnapshot ContactObservedAt(HexCoord focus, int armyId, int lastObservedTurn)
        {
            EnemyContactSnapshot c = SurveilContact(focus, armyId);
            c.LastObservedTurn = lastObservedTurn;
            return c;
        }

        private static HexCoord H(int q, int r) => new HexCoord(q, r);

        private static PlayerSetupData Fresh(string name)
        {
            var p = new PlayerSetupData { Nickname = name, IsNeutral = false, IsHuman = false };
            MissionIntentRegistry.Clear();
            AiAllocatorStateRegistry.Clear();
            return p;
        }

        private static EnemyContactSnapshot SurveilContact(HexCoord focus, int armyId) => new EnemyContactSnapshot
        {
            Army = new ArmySnapshot { ArmyId = armyId, Hex = focus, Members = new List<WorthIt.DefenderProfile>() },
            Source = ContactSource.Honest,
            Knowledge = ContactKnowledge.LastKnown,
            Position = focus,
            Confidence = AiConfigV2.threatConfidenceLastKnown,
            LastObservedTurn = 0,
        };

        private static MissionProposal SurveilProp(HexCoord focus, int trackedArmyId, float baseValue, float ap)
        {
            var t = new ScoutMissionTarget
            {
                FocusHex = focus,
                Kind = ScoutTargetKind.Surveil,
                Contact = SurveilContact(focus, trackedArmyId),
                Stealth = StealthRequirement.Required,
                DetectionRisk = 0f,
            };
            return WrapScout(t, baseValue, ap);
        }

        private static MissionProposal FreshExploreProp(HexCoord focus, float baseValue, float ap)
        {
            var t = new ScoutMissionTarget
            {
                FocusHex = focus,
                Kind = ScoutTargetKind.Explore,
                Contact = null,
                Stealth = StealthRequirement.None,
                DetectionRisk = 0f,
            };
            return WrapScout(t, baseValue, ap);
        }

        private static MissionProposal WrapScout(ScoutMissionTarget t, float baseValue, float ap)
        {
            var p = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = t,
                BaseValue = baseValue,
                LocalAdmissionScore = baseValue,
                Requirements = new MissionRequirements
                {
                    MoverKnown = true, ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                    EnergyMinimum = 0f, EnergyDesired = 0f, EnergyMaximum = 0f, EtaTurns = 1,
                },
            };
            p.Axes.Value[DesireAxis.Recon] = 1f;
            return p;
        }

        private static MissionTurnOutcome ExecOutcome(MissionProposal p, ExecutionStopReason stop, int steps,
            bool enteredStealth, bool wasCommitment, int mover)
        {
            var tt = (ScoutMissionTarget)p.Target;
            var led = new MissionOutcomeLedger();
            led.RegisterProposals(new[] { p });
            if (wasCommitment)
                led.RegisterCommitments(new[]
                {
                    new Commitment { IntentKey = MissionIntentKey.For(p), Mission = p, Tier = CommitmentTier.Soft, ContinuationValue = p.BaseValue },
                });
            led.RecordProvisionSuccess(p, new ProvisionedMission
            {
                Mission = p, Key = StableMissionKey.For(p), Kind = MissionKind.Scout, ScoutKind = tt.Kind,
                MoverArmyId = mover, FocusHex = tt.FocusHex, ExecutionHex = tt.FocusHex,
                TrackedArmyId = tt.Kind == ScoutTargetKind.Surveil ? tt.Contact.Army.ArmyId : (int?)null,
                BaselineObservedTurn = 0, ClaimedAp = 1f,
            });
            led.RecordExecution(new ExecutionResult
            {
                Key = StableMissionKey.For(p), StepsMoved = steps, EnteredStealth = enteredStealth,
                ReachedGoal = stop == ExecutionStopReason.ReachedGoal, StopReason = stop, ApSpent = 1f,
            });
            return led.Finalize().Single();
        }

        private static MissionTurnOutcome FailOutcome(MissionProposal p, ProvisionFailure f)
        {
            var led = new MissionOutcomeLedger();
            led.RegisterProposals(new[] { p });
            led.RecordProvisionFailure(p, f);
            return led.Finalize().Single();
        }

        private static Radar LowReconRadar()
        {
            var r = new Radar();
            r.Weight[DesireAxis.Recon] = 0.05f;
            r.Weight[DesireAxis.Aggression] = 0.35f;
            r.Weight[DesireAxis.Defence] = 0.30f;
            r.Weight[DesireAxis.Economy] = 0.20f;
            r.Weight[DesireAxis.Development] = 0.10f;
            return r;
        }

        private static WorldSnapshot Snap(int turn, int actionPoints)
        {
            return new WorldSnapshot
            {
                TurnNumber = turn,
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new List<HexCoord> { new HexCoord(0, 0) },
                    Armies = new List<ArmySnapshot>(),
                    ActionPoints = actionPoints,
                    Hand = new List<Game.Cards.CardData>(),
                    Deck = new List<Game.Cards.CardDefinition>(),
                },
                Known = new KnownSnapshot
                {
                    EnemySightings = new List<AiMapMemory.KnownEnemySighting>(),
                    NeutralSightings = new List<AiMapMemory.KnownEnemySighting>(),
                    Buildings = new List<AiMapMemory.KnownBuilding>(),
                    EventGuardHexes = new List<HexCoord>(),
                    ResourceHexes = new List<KeyValuePair<HexCoord, Game.Economy.ResourceType>>(),
                },
                TrueWorld = new TrueWorldSnapshot
                {
                    EnemyArmies = new List<ArmySnapshot>(),
                    NeutralArmies = new List<ArmySnapshot>(),
                    AllBuildings = new List<BuildingSnapshot>(),
                    Opponents = new List<OpponentSnapshot>(),
                },
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    TotalHexes = 200, VisitedHexes = 100, VisibleHexes = 40, UnknownFrac = 0.5f,
                    ExplorableUnknownFrac = 0.5f,
                    Frontier = Array.Empty<FrontierHexSnapshot>(),
                    AllHexes = new List<HexCoord>(),
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                    VisitedHexSet = new HashSet<HexCoord>(),
                },
                Economy = new EconomyStanding
                {
                    PerType = new List<EconomyResourceStanding>(),
                    DeckResourceNeed = new ResourceBundle(),
                    AbsFloor = 0.5f, EconomicSecurity = 0.5f,
                },
                Threat = new ThreatModel
                {
                    Contacts = new List<EnemyContactSnapshot>(),
                    Assets = new List<StrategicAssetSnapshot>(),
                    Threats = new List<AssetThreatSnapshot>(),
                    ReconContactByArmyId = new Dictionary<int, EnemyContactSnapshot>(),
                    UnderSiege = false,
                },
            };
        }

        // A snapshot with a real frontier list + an on-map set that makes `incumbent` a still-open
        // Explore hex with exactly `incumbentFreshNeighbours` openable neighbours.
        private static WorldSnapshot ExploreSnap(int turn, HexCoord incumbent, int incumbentFreshNeighbours,
            (HexCoord hex, int fresh, int distBase)[] frontier)
        {
            WorldSnapshot s = Snap(turn, actionPoints: 6);
            var onMap = (List<HexCoord>)s.MapKnowledge.AllHexes;
            void Add(HexCoord h) { if (!onMap.Contains(h)) onMap.Add(h); }

            Add(incumbent);
            HexCoord[] nb = HexGridMath.Neighbors(incumbent).ToArray();
            for (int i = 0; i < incumbentFreshNeighbours && i < nb.Length; i++)
                Add(nb[i]);

            var fl = new List<FrontierHexSnapshot>();
            foreach ((HexCoord hex, int fresh, int distBase) in frontier)
            {
                Add(hex);
                fl.Add(new FrontierHexSnapshot
                {
                    Hex = hex,
                    FreshNeighbors = fresh,
                    DistanceFromNearestBase = distBase,
                    EnemyExposure = false,
                    StealthDetectionRisk = false,
                });
            }
            s.MapKnowledge.Frontier = fl;
            return s;
        }

        // ================================================================ plumbing ====

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _passed++; else _failed++;
        }
    }
}
