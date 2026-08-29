using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;

namespace MissionSelectionSim
{
    // Acceptance harness for Strategy V2 build-order step 7.1 — Mission Candidate Beam / Execute
    // Capacity. Separates N (how many sensible alternatives MissionLayer hands downstream) from K
    // (how many Recon operations may actually execute per AI turn). Drives MissionLayer.Propose +
    // ResourceAllocator's bounded pack -> provision -> re-pack loop with scripted provisioning
    // outcomes (execution against live ArmyData cannot run headless), exactly as Pipeline.RunTurn
    // does. Pins BEHAVIOUR, not magnitudes.
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Scenario00_ConfigInvariant();
            Scenario01_NGreaterThanK();
            Scenario02_BackupAfterProvisioningFailure();
            Scenario03_ConflictBackup();
            Scenario04_ExecutionCapacity();
            Scenario05_CommitmentConsumesSlot();
            Scenario06_TwoCommitments();
            Scenario07_LockedClaimSurvivesRepack();
            Scenario08_BudgetFallback();
            Scenario09_IncumbentDuplicate();
            Scenario10_Deterministic();
            Scenario11_NoCooldown();
            Scenario12_ExistingBehaviourBaseline();

            Console.WriteLine();
            Console.WriteLine($"mission-selection-sim: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------- 00 config invariant ----
        private static void Scenario00_ConfigInvariant()
        {
            Check("00 scoutCandidateBeamWidth >= maxConcurrentReconExecutions (N >= K)",
                AiConfigV2.scoutCandidateBeamWidth >= AiConfigV2.maxConcurrentReconExecutions);
            Check("00 K is still 2 (unchanged gameplay value)", AiConfigV2.maxConcurrentReconExecutions == 2);
        }

        // ------------------------------------------------------------------------ 01 N > K ----
        private static void Scenario01_NGreaterThanK()
        {
            PlayerSetupData me = Fresh("S1");
            // Six well-spread frontier hexes, all viable Explore candidates.
            WorldSnapshot snap = FrontierSnap(turn: 3, actionPoints: 20, frontier: new[]
            {
                (H(0, 6), 4, 3), (H(6, 0), 4, 3), (H(-6, 0), 4, 3),
                (H(0, -6), 4, 3), (H(6, -6), 4, 3), (H(-6, 6), 4, 3),
            });
            var bd = new DesireBreakdown { ReconExploration = 0.7f };

            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(me, snap);
            List<MissionProposal> proposals = MissionLayer.Propose(snap, bd, active);
            Check("01a MissionLayer emits a BEAM (> K proposals)",
                proposals.Count > AiConfigV2.maxConcurrentReconExecutions && proposals.Count <= AiConfigV2.scoutCandidateBeamWidth);

            var (passes, provisioned) = RunLoop(me, ReconRadar(1f), proposals, NoCommitments(), AllSucceed(2f));
            int fundedRecon = passes[0].Funded.Count(f => Lane(f.Mission) == ExecutionLane.Recon);
            Check("01b allocator funds <= K Recon", fundedRecon <= AiConfigV2.maxConcurrentReconExecutions && fundedRecon == 2);
            Check("01c provisioning sees <= K funded Recon", provisioned.Count <= AiConfigV2.maxConcurrentReconExecutions);
            Check("01d the surplus candidates are deferred ExecutionCapacity, not failed",
                passes[0].Deferred.Count(d => d.Reason == DeferReason.ExecutionCapacity) == proposals.Count - 2
                && passes[0].Deferred.All(d => d.Reason == DeferReason.ExecutionCapacity));
        }

        // --------------------------------------------------- 02 backup after prov failure ----
        private static void Scenario02_BackupAfterProvisioningFailure()
        {
            PlayerSetupData me = Fresh("S2");
            MissionProposal a = ExploreProp(H(0, 8), localScore: 90f, ap: 1f);
            MissionProposal b = ExploreProp(H(8, 0), localScore: 60f, ap: 1f);
            MissionProposal c = ExploreProp(H(-8, 0), localScore: 30f, ap: 1f);
            var missions = new List<MissionProposal> { a, b, c };

            // Pass 1: A fails provisioning (retry-next-turn, no cooldown). Everything else succeeds.
            var (passes, provisioned) = RunLoop(me, ReconRadar(1f), missions, NoCommitments(),
                FailOnce(Key(a), ProvisionFailure.NoExecutableStep("scripted"), claimAp: 1f));

            Check("02a pass 1 funds the top two by LocalAdmissionScore (A + B)",
                FundedKeys(passes[0]).SetEquals(new[] { Key(a), Key(b) }));
            Check("02b C is deferred ExecutionCapacity on pass 1 (not lost), and NOT funded there",
                passes[0].Deferred.Any(d => SameKey(d.Mission, c) && d.Reason == DeferReason.ExecutionCapacity)
                && !FundedKeys(passes[0]).Contains(Key(c)));
            Check("02c the backup C is funded on a LATER pass — same turn — once A is rejected",
                passes.Skip(1).Any(p => FundedKeys(p).Contains(Key(c))));
            Check("02d B and C end up provisioned this turn; A does not",
                provisioned.SetEquals(new[] { Key(b), Key(c) }));
        }

        // --------------------------------------------------------------- 03 conflict backup ----
        private static void Scenario03_ConflictBackup()
        {
            PlayerSetupData me = Fresh("S3");
            MissionProposal a = ExploreProp(H(5, 5), localScore: 90f, ap: 1f);
            MissionProposal b = ExploreProp(H(6, 5), localScore: 80f, ap: 1f);   // adjacent to A -> conflicts
            MissionProposal c = ExploreProp(H(-9, 0), localScore: 40f, ap: 1f);  // independent
            var missions = new List<MissionProposal> { a, b, c };

            var (passes, provisioned) = RunLoop(me, ReconRadar(1f), missions, NoCommitments(),
                FailOnce(Key(a), ProvisionFailure.NoExecutableStep("scripted"), claimAp: 1f));

            Check("03a pass 1: A + C funded, B deferred MissionConflict (not ExecutionCapacity)",
                FundedKeys(passes[0]).SetEquals(new[] { Key(a), Key(c) })
                && passes[0].Deferred.Any(d => SameKey(d.Mission, b) && d.Reason == DeferReason.MissionConflict));
            Check("03b after A is rejected this turn the conflict clears — B is funded on a later pass",
                passes.Skip(1).Any(p => FundedKeys(p).Contains(Key(b))));
            Check("03c B + C provisioned this turn, A not", provisioned.SetEquals(new[] { Key(b), Key(c) }));
        }

        // ----------------------------------------------------------- 04 execution capacity ----
        private static void Scenario04_ExecutionCapacity()
        {
            PlayerSetupData me = Fresh("S4");
            MissionProposal a = ExploreProp(H(0, 9), 90f, 1f);
            MissionProposal b = ExploreProp(H(9, 0), 70f, 1f);
            MissionProposal c = ExploreProp(H(-9, 0), 50f, 1f);
            MissionProposal d = ExploreProp(H(0, -9), 30f, 1f);
            var missions = new List<MissionProposal> { a, b, c, d };

            var (passes, _) = RunLoop(me, ReconRadar(1f), missions, NoCommitments(), AllSucceed(1f));
            TentativeAllocation alloc = passes[0];

            Check("04a exactly K funded", alloc.Funded.Count == 2 && FundedKeys(alloc).SetEquals(new[] { Key(a), Key(b) }));
            Check("04b the rest -> ExecutionCapacity (not a provisioning failure, not budget)",
                alloc.Deferred.Count == 2
                && alloc.Deferred.All(x => x.Reason == DeferReason.ExecutionCapacity));
        }

        // ---------------------------------------------------- 05 commitment consumes slot ----
        private static void Scenario05_CommitmentConsumesSlot()
        {
            PlayerSetupData me = Fresh("S5");
            MissionProposal soft = SurveilProp(H(9, 3), trackedArmyId: 4242, localScore: 20f, ap: 1f,
                fromIntent: true, tier: CommitmentTier.Soft);
            MissionProposal f1 = ExploreProp(H(0, 9), 90f, 1f);
            MissionProposal f2 = ExploreProp(H(9, 0), 70f, 1f);
            MissionProposal f3 = ExploreProp(H(-9, 0), 50f, 1f);
            var missions = new List<MissionProposal> { soft, f1, f2, f3 };
            var commitments = new List<Commitment> { Commit(soft, CommitmentTier.Soft) };

            var (passes, _) = RunLoop(me, ReconRadar(1f), missions, commitments, AllSucceed(1f));
            TentativeAllocation alloc = passes[0];

            Check("05a the commitment is funded first", alloc.Funded.Count > 0 && alloc.Funded[0].IsCommitment
                && SameKey(alloc.Funded[0].Mission, soft));
            Check("05b only ONE fresh Recon is funded (commitment ate the other K slot)",
                alloc.Funded.Count(f => !f.IsCommitment) == 1);
            Check("05c the two other fresh -> ExecutionCapacity",
                alloc.Deferred.Count(d => d.Reason == DeferReason.ExecutionCapacity) == 2);
        }

        // ------------------------------------------------------------- 06 two commitments ----
        private static void Scenario06_TwoCommitments()
        {
            PlayerSetupData me = Fresh("S6");
            MissionProposal sa = SurveilProp(H(9, 3), 4242, 25f, 1f, fromIntent: true, tier: CommitmentTier.Soft);
            MissionProposal sb = SurveilProp(H(3, 9), 5353, 22f, 1f, fromIntent: true, tier: CommitmentTier.Soft);
            MissionProposal fc = ExploreProp(H(0, 9), 90f, 1f);
            var missions = new List<MissionProposal> { sa, sb, fc };
            var commitments = new List<Commitment> { Commit(sa, CommitmentTier.Soft), Commit(sb, CommitmentTier.Soft) };

            var (passes, _) = RunLoop(me, ReconRadar(1f), missions, commitments, AllSucceed(1f));
            TentativeAllocation alloc = passes[0];

            Check("06a both commitments funded", alloc.Funded.Count(f => f.IsCommitment) == 2
                && FundedKeys(alloc).SetEquals(new[] { Key(sa), Key(sb) }));
            Check("06b fresh C -> ExecutionCapacity (commitments get no magic extra slot)",
                alloc.Deferred.Any(d => SameKey(d.Mission, fc) && d.Reason == DeferReason.ExecutionCapacity));
        }

        // ------------------------------------------------ 07 locked claim survives re-pack ----
        private static void Scenario07_LockedClaimSurvivesRepack()
        {
            PlayerSetupData me = Fresh("S7");
            MissionProposal a = ExploreProp(H(0, 9), 90f, 1f);
            MissionProposal b = ExploreProp(H(9, 0), 70f, 1f);
            MissionProposal c = ExploreProp(H(-9, 0), 50f, 1f);
            MissionProposal d = ExploreProp(H(0, -9), 30f, 1f);
            var missions = new List<MissionProposal> { a, b, c, d };

            // Pass 1: A locks (success), B fails -> re-pack with A already consuming 1/K.
            var (passes, provisioned) = RunLoop(me, ReconRadar(1f), missions, NoCommitments(),
                fe =>
                {
                    if (SameKey(fe.Mission, a)) return Ok(1f);
                    if (SameKey(fe.Mission, b)) return Fail(ProvisionFailure.NoExecutableStep("scripted"));
                    return Ok(1f);
                });

            Check("07a A locked, then only ONE more Recon fundable on re-pack (A holds 1/K)",
                passes.Count >= 2 && passes[^1].Funded.Count(f => Lane(f.Mission) == ExecutionLane.Recon) <= 1);
            Check("07b total provisioned this turn never exceeds K",
                provisioned.Count <= AiConfigV2.maxConcurrentReconExecutions);
            Check("07c D is held out by ExecutionCapacity on the re-pack, not funded",
                passes[^1].Deferred.Any(x => SameKey(x.Mission, d) && x.Reason == DeferReason.ExecutionCapacity)
                && !FundedKeys(passes[^1]).Contains(Key(d)));
        }

        // --------------------------------------------------------------- 08 budget fallback ----
        private static void Scenario08_BudgetFallback()
        {
            PlayerSetupData me = Fresh("S8");
            MissionProposal a = ExploreProp(H(0, 9), localScore: 99f, ap: 100f);  // top rank, unaffordable
            MissionProposal b = ExploreProp(H(9, 0), localScore: 60f, ap: 1f);
            MissionProposal c = ExploreProp(H(-9, 0), localScore: 40f, ap: 1f);
            var missions = new List<MissionProposal> { a, b, c };

            var (passes, _) = RunLoop(me, ReconRadar(1f, poolAp: 6), missions, NoCommitments(), AllSucceed(1f));
            TentativeAllocation alloc = passes[0];

            Check("08a the unaffordable top pick is deferred InsufficientBudget",
                alloc.Deferred.Any(d => SameKey(d.Mission, a) && d.Reason == DeferReason.InsufficientBudget));
            Check("08b the allocator falls through to B + C instead of leaving a slot empty",
                FundedKeys(alloc).SetEquals(new[] { Key(b), Key(c) }));
        }

        // ------------------------------------------------------------ 09 incumbent duplicate ----
        private static void Scenario09_IncumbentDuplicate()
        {
            PlayerSetupData me = Fresh("S9");
            PutExploreIntent(me, focus: H(0, 6), preferredMover: 55, createdTurn: 4);

            WorldSnapshot snap = ExploreSnap(turn: 5, incumbent: H(0, 6), incumbentFreshNeighbours: 4,
                frontier: new[] { (H(0, 6), 4, 3), (H(6, 0), 4, 4) });   // fresh gen ALSO proposes H(0,6)
            var bd = new DesireBreakdown { ReconExploration = 0.6f };

            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(me, snap);
            List<MissionProposal> proposals = MissionLayer.Propose(snap, bd, active);

            List<MissionProposal> atFocus = proposals.Where(m => Focus(m).Equals(H(0, 6))).ToList();
            Check("09a exactly ONE proposal for the shared objective", atFocus.Count == 1);
            Check("09b it is the incumbent version (FromDurableIntent + PreferredMover carried)",
                atFocus.Count == 1 && atFocus[0].FromDurableIntent
                && atFocus[0].DurableFundingTier == CommitmentTier.None
                && atFocus[0].PreferredMoverArmyId == 55);
        }

        // ---------------------------------------------------------------- 10 deterministic ----
        private static void Scenario10_Deterministic()
        {
            MissionProposal[] Build()
            {
                return new[]
                {
                    ExploreProp(H(0, 9), 80f, 1f),
                    ExploreProp(H(9, 0), 65f, 1f),
                    ExploreProp(H(-9, 0), 50f, 1f),
                    ExploreProp(H(0, -9), 35f, 1f),
                };
            }

            PlayerSetupData a = Fresh("S10a");
            var forward = Build().ToList();
            var (pa, _) = RunLoop(a, ReconRadar(1f), forward, NoCommitments(), AllSucceed(1f));

            PlayerSetupData b = Fresh("S10b");
            var reversed = Build().ToList();
            reversed.Reverse();
            var (pb, _) = RunLoop(b, ReconRadar(1f), reversed, NoCommitments(), AllSucceed(1f));

            Check("10 funded portfolio is identical regardless of candidate input order",
                pa[0].Funded.Select(f => Key(f.Mission).ToString())
                    .SequenceEqual(pb[0].Funded.Select(f => Key(f.Mission).ToString())));
        }

        // ------------------------------------------------------------------- 11 no cooldown ----
        private static void Scenario11_NoCooldown()
        {
            PlayerSetupData me = Fresh("S11");
            MissionProposal a = ExploreProp(H(5, 5), 90f, 1f);
            MissionProposal b = ExploreProp(H(6, 5), 40f, 1f);   // conflicts A
            MissionProposal c = ExploreProp(H(-9, 0), 70f, 1f);
            MissionProposal d = ExploreProp(H(0, -9), 50f, 1f);

            var (passes, _) = RunLoop(me, ReconRadar(1f), new List<MissionProposal> { a, b, c, d },
                NoCommitments(), AllSucceed(1f));
            TentativeAllocation alloc = passes[0];
            Check("11a B deferred MissionConflict, D deferred ExecutionCapacity in the same pass",
                alloc.Deferred.Any(x => SameKey(x.Mission, b) && x.Reason == DeferReason.MissionConflict)
                && alloc.Deferred.Any(x => SameKey(x.Mission, d) && x.Reason == DeferReason.ExecutionCapacity));

            AiAllocatorState st = AiAllocatorStateRegistry.GetOrCreate(me);
            Check("11b neither key is put on an allocator cooldown",
                !st.OnCooldown(Key(b), 100) && !st.OnCooldown(Key(d), 100));

            // Next turn, with the conflicting / capacity-holding picks gone, B and D fund freely.
            var (npasses, _) = RunLoop2(me, ReconRadar(1f), new List<MissionProposal> { b, d }, NoCommitments(),
                AllSucceed(1f), turn: 4);
            Check("11c B and D are funded the next turn (no lingering rejection / cooldown)",
                FundedKeys(npasses[0]).SetEquals(new[] { Key(b), Key(d) }));
        }

        // -------------------------------------------------- 12 existing behaviour baseline ----
        private static void Scenario12_ExistingBehaviourBaseline()
        {
            PlayerSetupData me = Fresh("S12");
            MissionProposal a = ExploreProp(H(0, 9), 80f, 1f);
            MissionProposal b = ExploreProp(H(9, 0), 60f, 1f);   // N == K == 2, independent, affordable

            var (passes, provisioned) = RunLoop(me, ReconRadar(1f), new List<MissionProposal> { a, b },
                NoCommitments(), AllSucceed(1f));
            TentativeAllocation alloc = passes[0];
            Check("12 N == K, feasible, no conflicts, ample AP -> both funded, nothing deferred, one pass",
                passes.Count == 1 && FundedKeys(alloc).SetEquals(new[] { Key(a), Key(b) })
                && alloc.Deferred.Count == 0 && provisioned.SetEquals(new[] { Key(a), Key(b) }));
        }

        // ================================================================ allocator loop ====

        private sealed class ProvStep
        {
            public bool Success;
            public float ClaimAp;
            public ProvisionFailure Failure;
        }

        private static ProvStep Ok(float ap) => new ProvStep { Success = true, ClaimAp = ap };
        private static ProvStep Fail(ProvisionFailure f) => new ProvStep { Success = false, Failure = f };

        private static Func<FundedEntry, ProvStep> AllSucceed(float ap) => _ => Ok(ap);

        private static Func<FundedEntry, ProvStep> FailOnce(StableMissionKey key, ProvisionFailure f, float claimAp)
        {
            var failed = new HashSet<StableMissionKey>();
            return fe =>
            {
                StableMissionKey k = StableMissionKey.For(fe.Mission);
                if (k.Equals(key) && failed.Add(k))
                    return Fail(f);
                return Ok(claimAp);
            };
        }

        // Mirrors Pipeline.RunTurn's bounded pack -> provision -> re-pack loop.
        private static (List<TentativeAllocation> passes, HashSet<StableMissionKey> provisioned) RunLoop(
            PlayerSetupData player, Radar radar, List<MissionProposal> missions, List<Commitment> commitments,
            Func<FundedEntry, ProvStep> provFn)
            => RunLoop2(player, radar, missions, commitments, provFn, turn: 3);

        private static (List<TentativeAllocation> passes, HashSet<StableMissionKey> provisioned) RunLoop2(
            PlayerSetupData player, Radar radar, List<MissionProposal> missions, List<Commitment> commitments,
            Func<FundedEntry, ProvStep> provFn, int turn)
        {
            WorldSnapshot snap = Snap(turn, RadarPoolAp(radar));
            AllocationSession session = ResourceAllocator.BeginTurn(snap, radar, missions, commitments, player);
            var passes = new List<TentativeAllocation>();
            var provisioned = new HashSet<StableMissionKey>();

            TentativeAllocation alloc = session.Pack();
            passes.Add(alloc);
            int reallocPass = 0;
            while (true)
            {
                foreach (FundedEntry fe in alloc.Funded)
                {
                    if (fe?.Mission == null) continue;
                    StableMissionKey key = StableMissionKey.For(fe.Mission);
                    if (provisioned.Contains(key)) continue;
                    ProvStep step = provFn(fe);
                    if (step == null) continue;
                    if (step.Success)
                    {
                        provisioned.Add(key);
                        session.RegisterProvisionSuccess(fe, step.ClaimAp);
                    }
                    else
                    {
                        session.RegisterProvisionFailure(fe, step.Failure);
                    }
                }

                if (!session.HasNewFailures || session.Converged || ++reallocPass >= AiConfigV2.maxReallocIterations)
                    break;
                alloc = session.Pack();
                passes.Add(alloc);
            }
            return (passes, provisioned);
        }

        // ================================================================ builders ====

        private static ExecutionLane Lane(MissionProposal m) => MissionAdmissionPolicy.LaneFor(m);
        private static StableMissionKey Key(MissionProposal m) => StableMissionKey.For(m);
        private static bool SameKey(MissionProposal a, MissionProposal b) => Key(a).Equals(Key(b));
        private static HashSet<StableMissionKey> FundedKeys(TentativeAllocation a) =>
            new HashSet<StableMissionKey>(a.Funded.Select(f => StableMissionKey.For(f.Mission)));
        private static HexCoord Focus(MissionProposal m) => ((ScoutMissionTarget)m.Target).FocusHex;

        private static List<Commitment> NoCommitments() => new List<Commitment>();

        private static Commitment Commit(MissionProposal m, CommitmentTier tier) => new Commitment
        {
            IntentKey = MissionIntentKey.For(m),
            Mission = m,
            Tier = tier,
            ContinuationValue = m.BaseValue,
        };

        private static HexCoord H(int q, int r) => new HexCoord(q, r);

        private static PlayerSetupData Fresh(string name)
        {
            var p = new PlayerSetupData { Nickname = name, IsNeutral = false, IsHuman = false };
            MissionIntentRegistry.Clear();
            AiAllocatorStateRegistry.Clear();
            return p;
        }

        private static Radar ReconRadar(float reconWeight, int poolAp = 20)
        {
            var r = new Radar();
            foreach (DesireAxis ax in DesireAxes.All)
                r.Weight[ax] = ax == DesireAxis.Recon ? reconWeight : 0f;
            _radarPoolAp[r] = poolAp;
            return r;
        }

        private static readonly Dictionary<Radar, int> _radarPoolAp = new Dictionary<Radar, int>();
        private static int RadarPoolAp(Radar r) => _radarPoolAp.TryGetValue(r, out int ap) ? ap : 20;

        private static MissionProposal ExploreProp(HexCoord focus, float localScore, float ap)
        {
            var t = new ScoutMissionTarget
            {
                FocusHex = focus, Kind = ScoutTargetKind.Explore, Contact = null,
                Stealth = StealthRequirement.None, DetectionRisk = 0f,
            };
            return Wrap(t, localScore, ap, fromIntent: false, tier: CommitmentTier.None, preferredMover: null);
        }

        private static MissionProposal SurveilProp(HexCoord focus, int trackedArmyId, float localScore, float ap,
            bool fromIntent = false, CommitmentTier tier = CommitmentTier.None)
        {
            var t = new ScoutMissionTarget
            {
                FocusHex = focus, Kind = ScoutTargetKind.Surveil,
                Contact = SurveilContact(focus, trackedArmyId),
                Stealth = StealthRequirement.Required, DetectionRisk = 0f,
            };
            return Wrap(t, localScore, ap, fromIntent, tier, preferredMover: fromIntent ? 7 : (int?)null);
        }

        private static MissionProposal Wrap(ScoutMissionTarget t, float localScore, float ap,
            bool fromIntent, CommitmentTier tier, int? preferredMover)
        {
            var p = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = t,
                BaseValue = localScore,
                LocalAdmissionScore = localScore,
                FromDurableIntent = fromIntent,
                DurableFundingTier = tier,
                PreferredMoverArmyId = preferredMover,
                Requirements = new MissionRequirements
                {
                    MoverKnown = true, ApMinimum = ap, ApDesired = ap, ApMaximum = ap,
                    EnergyMinimum = 0f, EnergyDesired = 0f, EnergyMaximum = 0f, EtaTurns = 1,
                },
            };
            p.Axes.Value[DesireAxis.Recon] = 1f;
            return p;
        }

        private static void PutExploreIntent(PlayerSetupData player, HexCoord focus, int preferredMover, int createdTurn)
        {
            MissionIntentRegistry.GetOrCreate(player).Put(new MissionIntent
            {
                IntentKey = new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Explore, 0, focus.Q, focus.R),
                LastAttemptKey = default,
                Kind = MissionKind.Scout,
                Funding = CommitmentTier.None,
                Status = IntentStatus.Active,
                Suspended = SuspendReason.None,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore, FocusHex = focus },
                CreatedTurn = createdTurn,
                TurnsActive = 1,
                LastProgressTurn = createdTurn,
                PreferredMoverArmyId = preferredMover,
            });
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

        private static WorldSnapshot FrontierSnap(int turn, int actionPoints,
            (HexCoord hex, int fresh, int distBase)[] frontier)
        {
            WorldSnapshot s = Snap(turn, actionPoints);
            var fl = new List<FrontierHexSnapshot>();
            foreach ((HexCoord hex, int fresh, int distBase) in frontier)
                fl.Add(new FrontierHexSnapshot
                {
                    Hex = hex, FreshNeighbors = fresh, DistanceFromNearestBase = distBase,
                    EnemyExposure = false, StealthDetectionRisk = false,
                });
            s.MapKnowledge.Frontier = fl;
            return s;
        }

        private static WorldSnapshot ExploreSnap(int turn, HexCoord incumbent, int incumbentFreshNeighbours,
            (HexCoord hex, int fresh, int distBase)[] frontier)
        {
            WorldSnapshot s = FrontierSnap(turn, actionPoints: 20, frontier);
            var onMap = (List<HexCoord>)s.MapKnowledge.AllHexes;
            void Add(HexCoord h) { if (!onMap.Contains(h)) onMap.Add(h); }

            Add(incumbent);
            HexCoord[] nb = HexGridMath.Neighbors(incumbent).ToArray();
            for (int i = 0; i < incumbentFreshNeighbours && i < nb.Length; i++)
                Add(nb[i]);
            foreach ((HexCoord hex, int _, int __) in frontier)
                Add(hex);
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
