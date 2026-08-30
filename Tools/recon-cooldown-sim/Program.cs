using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;

namespace ReconCooldownSim
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Scenario01_NoMoverIsTransientAndNeverStructural();
            Scenario02_InFlightIntentLosingMoverDoesNotPoisonTarget();
            Scenario03_StructuralReconFailureOwnsTwoTurnCooldown();
            Scenario04_RaidStructuralFailureUsesRaidDuration();
            Scenario05_AllocatorDoesNotWritePersistentCooldownEarly();
            Scenario06_NoMoverCanBeFundedAgainNextTurn();
            Scenario07_DemandIgnoresAllCooldownBlockedReconJobs();
            Scenario08_DemandSizesOnlyRunnableReconJobs();

            Console.WriteLine();
            Console.WriteLine($"recon-cooldown-sim: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // NoMoverExists is a capability fact, not a target fact.
        private static void Scenario01_NoMoverIsTransientAndNeverStructural()
        {
            PlayerSetupData me = Fresh("S1");
            MissionProposal p = ExploreProp(H(8, 4), 50f, 1f);
            ProvisionFailure f = ProvisionFailure.NoMoverExists("no solo Recce on the map");

            Check("01a NoMoverExists disposition is RetryNextTurn",
                f.Disposition == ProvisionDisposition.RetryNextTurn);

            MissionTurnOutcome o = FailOutcome(p, f);
            Check("01b ledger classifies NoMoverExists as Blocked, non-structural",
                o.Outcome == ExecutionOutcome.Blocked && !o.StructuralFailure
                && o.ProvisionFailureKindValue == ProvisionFailureKind.NoMoverExists);

            MissionContinuityLayer.ReconcileAfterTurn(me, 1, new[] { o });
            Check("01c fresh NoMover failure creates no target cooldown",
                !AiAllocatorStateRegistry.GetOrCreate(me).OnCooldown(o.AttemptKey, 2));
        }

        // If a started operation loses its mover, keep the objective alive without ageing it into a
        // structural cooldown. Demand can restore the missing capability next turn.
        private static void Scenario02_InFlightIntentLosingMoverDoesNotPoisonTarget()
        {
            PlayerSetupData me = Fresh("S2");
            int tracked = 42;
            MissionProposal p = SurveilProp(H(9, 3), tracked, 40f, 1f);
            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExecOutcome(p, ExecutionStopReason.OutOfMovement, steps: 2, mover: 7) });
            Check("02a started Surveil creates a durable intent",
                MissionIntentRegistry.GetOrCreate(me).Count == 1);

            MissionTurnOutcome lost = FailOutcome(p, ProvisionFailure.NoMoverExists("mover destroyed"));
            MissionContinuityLayer.ReconcileAfterTurn(me, 2, new[] { lost });
            MissionIntent i = MissionIntentRegistry.GetOrCreate(me).All.SingleOrDefault();
            Check("02b lost capability suspends intent without stall ageing",
                i != null && i.Status == IntentStatus.Suspended
                && i.Suspended == SuspendReason.CapabilityUnavailable && i.StallTurns == 0);
            Check("02c lost capability still creates no target cooldown",
                !AiAllocatorStateRegistry.GetOrCreate(me).OnCooldown(lost.AttemptKey, 3));

            WorldSnapshot next = Snap(turn: 3, actionPoints: 6);
            EnemyContactSnapshot c = SurveilContact(H(9, 3), tracked);
            next.Threat.Contacts = new List<EnemyContactSnapshot> { c };
            next.Threat.ReconContactByArmyId = new Dictionary<int, EnemyContactSnapshot> { [tracked] = c };
            List<MissionIntent> active = MissionContinuityLayer.ResolveActive(me, next);
            Check("02d transient capability suspension reactivates next turn",
                active.Count == 1 && active[0].Status == IntentStatus.Active
                && active[0].Suspended == SuspendReason.None);
        }

        // A genuine observation-geometry dead end is still structural and receives the configured
        // inclusive two-turn recon cooldown.
        private static void Scenario03_StructuralReconFailureOwnsTwoTurnCooldown()
        {
            PlayerSetupData me = Fresh("S3");
            MissionProposal p = SurveilProp(H(8, 2), 444, 30f, 1f);
            MissionTurnOutcome fail = FailOutcome(p,
                ProvisionFailure.NoObservationVantage("no on-map vantage"));
            Check("03a NoObservationVantage remains structural", fail.StructuralFailure);

            MissionContinuityLayer.ReconcileAfterTurn(me, 2, new[] { fail });
            AiAllocatorState state = AiAllocatorStateRegistry.GetOrCreate(me);
            bool hasInfo = state.TryGetCooldown(fail.AttemptKey, 3, out MissionCooldownInfo cd);
            Check("03b cooldown records reason/start/until metadata",
                hasInfo && cd.StartedTurn == 2 && cd.UntilTurn == 4
                && cd.Reason == ProvisionFailureKind.NoObservationVantage.ToString());
            Check("03c recon cooldown suppresses T+1 and T+2 only",
                state.OnCooldown(fail.AttemptKey, 3)
                && state.OnCooldown(fail.AttemptKey, 4)
                && !state.OnCooldown(fail.AttemptKey, 5));
        }

        // Same owner, mission-kind-specific duration: Raid uses raidRejectCooldownTurns (=3).
        private static void Scenario04_RaidStructuralFailureUsesRaidDuration()
        {
            PlayerSetupData me = Fresh("S4");
            MissionProposal p = RaidProp(targetArmyId: 77, H(5, 5), 55f, 1f);
            MissionTurnOutcome fail = FailOutcome(p,
                ProvisionFailure.AssemblyInfeasible("no legal raid force"));
            MissionContinuityLayer.ReconcileAfterTurn(me, 10, new[] { fail });

            AiAllocatorState state = AiAllocatorStateRegistry.GetOrCreate(me);
            bool hasInfo = state.TryGetCooldown(fail.AttemptKey, 11, out MissionCooldownInfo cd);
            Check("04a Raid structural cooldown uses raid duration",
                hasInfo && cd.StartedTurn == 10 && cd.UntilTurn == 13);
            Check("04b Raid cooldown suppresses exactly three following turns",
                state.OnCooldown(fail.AttemptKey, 11)
                && state.OnCooldown(fail.AttemptKey, 12)
                && state.OnCooldown(fail.AttemptKey, 13)
                && !state.OnCooldown(fail.AttemptKey, 14));
        }

        // AllocationSession owns same-turn re-pack only. It must not write history before the final
        // ledger has established whether the structural failure remained authoritative.
        private static void Scenario05_AllocatorDoesNotWritePersistentCooldownEarly()
        {
            PlayerSetupData me = Fresh("S5");
            WorldSnapshot snap = Snap(turn: 1, actionPoints: 3);
            MissionProposal p = ExploreProp(H(8, 4), 50f, 1f);
            AllocationSession session = ResourceAllocator.BeginTurn(
                snap, ReconRadar(), new List<MissionProposal> { p }, new List<Commitment>(), me);
            TentativeAllocation alloc = session.Pack();
            FundedEntry fe = alloc.Funded.Single();

            session.RegisterProvisionFailure(fe,
                ProvisionFailure.NoObservationVantage("structural, but final ledger not reconciled yet"));
            Check("05a allocator rejects structural failure only for this turn",
                !AiAllocatorStateRegistry.GetOrCreate(me).OnCooldown(StableMissionKey.For(p), 2));

            MissionTurnOutcome final = FailOutcome(p,
                ProvisionFailure.NoObservationVantage("authoritative final failure"));
            MissionContinuityLayer.ReconcileAfterTurn(me, 1, new[] { final });
            Check("05b continuity writes persistent cooldown after final facts",
                AiAllocatorStateRegistry.GetOrCreate(me).OnCooldown(StableMissionKey.For(p), 2));
        }

        // Direct regression for the observed startup deadlock: a NoMover attempt on T1 must not
        // poison the Explore key; the same mission is fundable again from a fresh T2 session.
        private static void Scenario06_NoMoverCanBeFundedAgainNextTurn()
        {
            PlayerSetupData me = Fresh("S6");
            MissionProposal p = ExploreProp(H(8, 4), 50f, 1f);

            AllocationSession t1 = ResourceAllocator.BeginTurn(
                Snap(1, 3), ReconRadar(), new List<MissionProposal> { p }, new List<Commitment>(), me);
            FundedEntry first = t1.Pack().Funded.Single();
            t1.RegisterProvisionFailure(first, ProvisionFailure.NoMoverExists("no scout yet"));
            MissionTurnOutcome noMover = FailOutcome(p, ProvisionFailure.NoMoverExists("no scout yet"));
            MissionContinuityLayer.ReconcileAfterTurn(me, 1, new[] { noMover });

            AllocationSession t2 = ResourceAllocator.BeginTurn(
                Snap(2, 3), ReconRadar(), new List<MissionProposal> { p }, new List<Commitment>(), me);
            TentativeAllocation second = t2.Pack();
            Check("06 same Explore key is fundable next turn after NoMover",
                second.Funded.Any(f => StableMissionKey.For(f.Mission).Equals(StableMissionKey.For(p)))
                && !second.Deferred.Any(d => d.Reason == DeferReason.OnCooldown));
        }

        // Structural cooldown blocks the work itself, so it must not generate replacement-scout
        // demand while every uncovered objective is currently inadmissible.
        private static void Scenario07_DemandIgnoresAllCooldownBlockedReconJobs()
        {
            PlayerSetupData me = Fresh("S7");
            WorldSnapshot snap = Snap(turn: 2, actionPoints: 6);
            ReconObjective o = ExploreObjective(H(8, 4), 60f);
            StableMissionKey key = ReconKey(o);
            AiAllocatorStateRegistry.GetOrCreate(me).StartCooldown(key, 1, 3, "NoObservationVantage");

            List<AxisDemand> demands = DemandLayer.Generate(snap, new DesireBreakdown(),
                new[] { o }, Array.Empty<AggressionObjective>(), Array.Empty<MissionIntent>(),
                new ActorCommitments(), me);
            Check("07 all cooldown-blocked recon jobs create no ScoutCapability demand",
                !demands.Any(d => d.RequestingAxis == DesireAxis.Recon
                    && d.Capability == CapabilityKind.ScoutCapability));
        }

        // Mixed portfolio: only one of two uncovered jobs is runnable, therefore no-scout supply
        // asks for exactly one scout rather than two.
        private static void Scenario08_DemandSizesOnlyRunnableReconJobs()
        {
            PlayerSetupData me = Fresh("S8");
            WorldSnapshot snap = Snap(turn: 2, actionPoints: 6);
            ReconObjective blocked = ExploreObjective(H(8, 4), 60f);
            ReconObjective runnable = ExploreObjective(H(10, 3), 55f);
            AiAllocatorStateRegistry.GetOrCreate(me).StartCooldown(
                ReconKey(blocked), 1, 3, "NoObservationVantage");

            List<AxisDemand> demands = DemandLayer.Generate(snap, new DesireBreakdown(),
                new[] { blocked, runnable }, Array.Empty<AggressionObjective>(), Array.Empty<MissionIntent>(),
                new ActorCommitments(), me);
            AxisDemand recon = demands.SingleOrDefault(d => d.RequestingAxis == DesireAxis.Recon
                && d.Capability == CapabilityKind.ScoutCapability);
            Check("08 demand counts only the one runnable job",
                recon != null && Math.Abs(recon.DesiredAmount - 1f) < 0.001f);
        }

        // ================================================================ builders ====

        private static PlayerSetupData Fresh(string name)
        {
            MissionIntentRegistry.Clear();
            AiAllocatorStateRegistry.Clear();
            return new PlayerSetupData { Nickname = name, IsNeutral = false, IsHuman = false };
        }

        private static HexCoord H(int q, int r) => new HexCoord(q, r);

        private static MissionProposal ExploreProp(HexCoord focus, float baseValue, float ap)
        {
            var t = new ScoutMissionTarget
            {
                FocusHex = focus,
                Kind = ScoutTargetKind.Explore,
                Stealth = StealthRequirement.None,
                DetectionRisk = 0f,
            };
            return WrapScout(t, baseValue, ap);
        }

        private static MissionProposal SurveilProp(HexCoord focus, int targetArmyId, float baseValue, float ap)
        {
            var t = new ScoutMissionTarget
            {
                FocusHex = focus,
                Kind = ScoutTargetKind.Surveil,
                Contact = SurveilContact(focus, targetArmyId),
                Stealth = StealthRequirement.Required,
                DetectionRisk = 0f,
            };
            return WrapScout(t, baseValue, ap);
        }

        private static MissionProposal WrapScout(ScoutMissionTarget target, float baseValue, float ap)
        {
            var p = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = target,
                BaseValue = baseValue,
                LocalAdmissionScore = baseValue,
                Requirements = new MissionRequirements
                {
                    MoverKnown = false,
                    ApMinimum = ap,
                    ApDesired = ap,
                    ApMaximum = ap,
                    EtaTurns = 1,
                },
            };
            p.Axes.Value[DesireAxis.Recon] = 1f;
            return p;
        }

        private static MissionProposal RaidProp(int targetArmyId, HexCoord hex, float baseValue, float ap)
        {
            var target = new RaidMissionTarget
            {
                TargetArmyId = targetArmyId,
                LastKnownHex = hex,
                Confidence = 1f,
                ReadyWinChance = 0f,
                AssemblableWinChance = 0f,
                CanCoverAllDefenders = false,
                DefenderCount = 1,
                TargetPower = 10f,
                EstimatedEta = 1,
            };
            var p = new MissionProposal
            {
                Kind = MissionKind.Raid,
                Target = target,
                BaseValue = baseValue,
                LocalAdmissionScore = baseValue,
                Requirements = new MissionRequirements
                {
                    MoverKnown = false,
                    ApMinimum = ap,
                    ApDesired = ap,
                    ApMaximum = ap,
                    EtaTurns = 1,
                },
            };
            p.Axes.Value[DesireAxis.Aggression] = 1f;
            return p;
        }

        private static ReconObjective ExploreObjective(HexCoord focus, float value) => new ReconObjective
        {
            Kind = ReconObjectiveKind.Explore,
            FocusHex = focus,
            BaseValue = value,
            DetectionRisk = 0f,
            Stealth = StealthRequirement.None,
            FreshNeighbors = 4,
            DistanceFromBase = 2,
        };

        private static StableMissionKey ReconKey(ReconObjective o) =>
            new StableMissionKey(MissionKind.Scout, (int)ScoutTargetKind.Explore, 0,
                o.FocusHex.Q, o.FocusHex.R);

        private static EnemyContactSnapshot SurveilContact(HexCoord focus, int armyId) => new EnemyContactSnapshot
        {
            Army = new ArmySnapshot
            {
                ArmyId = armyId,
                Hex = focus,
                Members = new List<WorthIt.DefenderProfile>(),
            },
            Source = ContactSource.Honest,
            Knowledge = ContactKnowledge.LastKnown,
            Position = focus,
            Confidence = AiConfigV2.threatConfidenceLastKnown,
            LastObservedTurn = 0,
        };

        private static MissionTurnOutcome ExecOutcome(MissionProposal p, ExecutionStopReason stop,
            int steps, int mover)
        {
            var tt = (ScoutMissionTarget)p.Target;
            var led = new MissionOutcomeLedger();
            led.RegisterProposals(new[] { p });
            led.RecordProvisionSuccess(p, new ProvisionedMission
            {
                Mission = p,
                Key = StableMissionKey.For(p),
                Kind = MissionKind.Scout,
                ScoutKind = tt.Kind,
                MoverArmyId = mover,
                FocusHex = tt.FocusHex,
                ExecutionHex = tt.FocusHex,
                TrackedArmyId = tt.Kind == ScoutTargetKind.Surveil ? tt.Contact.Army.ArmyId : (int?)null,
                BaselineObservedTurn = 0,
                ClaimedAp = 1f,
            });
            led.RecordExecution(new ExecutionResult
            {
                Key = StableMissionKey.For(p),
                StepsMoved = steps,
                EnteredStealth = false,
                ReachedGoal = false,
                StopReason = stop,
                ApSpent = 1f,
            });
            return led.Finalize().Single();
        }

        private static MissionTurnOutcome FailOutcome(MissionProposal p, ProvisionFailure failure)
        {
            var led = new MissionOutcomeLedger();
            led.RegisterProposals(new[] { p });
            led.RecordProvisionFailure(p, failure);
            return led.Finalize().Single();
        }

        private static Radar ReconRadar()
        {
            var r = new Radar();
            foreach (DesireAxis a in DesireAxes.All)
                r.Weight[a] = a == DesireAxis.Recon ? 1f : 0f;
            return r;
        }

        private static WorldSnapshot Snap(int turn, int actionPoints)
        {
            return new WorldSnapshot
            {
                TurnNumber = turn,
                Self = new SelfSnapshot
                {
                    Citadel = H(0, 0),
                    BaseHexes = new List<HexCoord> { H(0, 0) },
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
                    TotalHexes = 100,
                    VisitedHexes = 1,
                    VisibleHexes = 1,
                    UnknownFrac = 0.99f,
                    ExplorableUnknownFrac = 0.99f,
                    Frontier = Array.Empty<FrontierHexSnapshot>(),
                    AllHexes = new List<HexCoord>(),
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                    VisitedHexSet = new HashSet<HexCoord>(),
                },
                Economy = new EconomyStanding
                {
                    PerType = new List<EconomyResourceStanding>(),
                    DeckResourceNeed = new ResourceBundle(),
                    AbsFloor = 0.5f,
                    EconomicSecurity = 0.5f,
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

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _passed++; else _failed++;
        }
    }
}
