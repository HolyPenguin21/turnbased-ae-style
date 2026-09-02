using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;

namespace ReconOwnershipSim
{
    // Deterministic acceptance for the ownership / classification invariants from the
    // AiDebug(20260902-082556) review. See recon-ownership-sim.csproj for the scenarios that are
    // covered in-editor instead of here.
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            ScenarioA_OneDurableRolePerPhysicalScout();
            ScenarioB_StaleDuplicateCannotInflateActorCount();
            ScenarioC_EventInterruptionIsProductiveStopNotFailed();
            ScenarioD_AlreadyCombatLockedIsBlockedNotFailed();
            ScenarioF_LocalExploreOutranksEquallyInformativeDistant();
            ScenarioG_ReconOnlyIsolatesMissionsNotHandManagement();

            Console.WriteLine();
            Console.WriteLine($"recon-ownership-sim: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // §1/§10 — feeding fresh opportunistic scout outcomes for a mover that already owns a
        // durable Explore role re-focuses that ONE role; it never creates a second durable intent
        // for the same physical scout, and the accumulated state carries across.
        private static void ScenarioA_OneDurableRolePerPhysicalScout()
        {
            PlayerSetupData me = Fresh("A");

            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExploreOutcome(H(1, 0), mover: 10, steps: 4, ap: 1f) });
            MissionContinuityLayer.ReconcileAfterTurn(me, 2,
                new[] { ExploreOutcome(H(3, -1), mover: 11, steps: 3, ap: 1f) });

            var afterTwo = MissionIntentRegistry.GetOrCreate(me).All.ToList();
            Check("A1 two physical scouts -> exactly two durable intents", afterTwo.Count == 2);

            MissionIntent scout10 = afterTwo.Single(i => i.PreferredMoverArmyId == 10);
            int createdTurn10 = scout10.CreatedTurn;

            // A brand-new opportunistic Explore mission runs on mover #10 at a DIFFERENT focus hex.
            MissionContinuityLayer.ReconcileAfterTurn(me, 3,
                new[] { ExploreOutcome(H(3, 1), mover: 10, steps: 4, ap: 2f) });
            MissionContinuityLayer.ReconcileAfterTurn(me, 4,
                new[] { ExploreOutcome(H(4, -1), mover: 11, steps: 4, ap: 1f) });

            var intents = MissionIntentRegistry.GetOrCreate(me).All.ToList();
            Check("A2 still exactly two durable intents after both movers re-focus",
                intents.Count == 2);
            Check("A3 no physical scout owns more than one durable Recon role",
                intents.Where(i => i.Scout != null && i.Scout.Kind != ScoutTargetKind.Surveil
                        && i.PreferredMoverArmyId.HasValue)
                    .GroupBy(i => i.PreferredMoverArmyId.Value)
                    .All(g => g.Count() == 1));

            MissionIntent refocused10 = intents.Single(i => i.PreferredMoverArmyId == 10);
            Check("A4 #10's durable role was re-focused onto the new waypoint (3,1)",
                refocused10.Scout != null && refocused10.Scout.FocusHex.Equals(H(3, 1)));
            Check("A5 re-focus preserved the original CreatedTurn (accumulated identity kept)",
                refocused10.CreatedTurn == createdTurn10);
            Check("A6 re-focus accumulated AP/steps rather than resetting them",
                refocused10.CumulativeApSpent >= 3f && refocused10.StepsMovedTotal >= 8);
        }

        // §1/§10 — even a corrupted registry (a stale duplicate intent pointing at a live mover)
        // resolves to one durable role per actor, so the demand layer's distinct-actor count can
        // never see that scout as two executions.
        private static void ScenarioB_StaleDuplicateCannotInflateActorCount()
        {
            PlayerSetupData me = Fresh("B");

            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExploreOutcome(H(1, 0), mover: 10, steps: 3, ap: 1f) });
            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExploreOutcome(H(5, 5), mover: 10, steps: 3, ap: 1f) });
            MissionContinuityLayer.ReconcileAfterTurn(me, 2,
                new[] { ExploreOutcome(H(3, -1), mover: 11, steps: 3, ap: 1f) });

            var intents = MissionIntentRegistry.GetOrCreate(me).All.ToList();
            int distinctActors = intents
                .Where(i => i.PreferredMoverArmyId.HasValue)
                .Select(i => i.PreferredMoverArmyId.Value).Distinct().Count();

            Check("B1 two movers -> at most two durable intents (no duplicate for #10)",
                intents.Count == 2);
            Check("B2 distinct active scout actors == 2 (never 3/4)", distinctActors == 2);
        }

        // §2 — a scout that moved several hexes and then hit a hex event is a ProductiveStop and
        // keeps its durable role; it is NOT classified Failed and NOT retired.
        private static void ScenarioC_EventInterruptionIsProductiveStopNotFailed()
        {
            PlayerSetupData me = Fresh("C");

            MissionTurnOutcome ev = ScoutStop(H(2, 1), mover: 7, steps: 3, ap: 2f,
                ExecutionStopReason.HexEventStarted, blockedBeforeMovement: false);
            Check("C1 HexEventStarted after useful movement -> ProductiveStop",
                ev.Outcome == ExecutionOutcome.ProductiveStop);
            Check("C2 not a structural failure", !ev.StructuralFailure);
            Check("C3 progress is recorded", ev.MadeProgress);

            MissionTurnOutcome bt = ScoutStop(H(2, 1), mover: 7, steps: 2, ap: 1f,
                ExecutionStopReason.BattleStarted, blockedBeforeMovement: false);
            Check("C4 BattleStarted after useful movement -> ProductiveStop",
                bt.Outcome == ExecutionOutcome.ProductiveStop);

            // durable role survives the interruption
            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExploreOutcome(H(2, 1), mover: 7, steps: 2, ap: 1f) });
            Check("C5 durable intent created", MissionIntentRegistry.GetOrCreate(me).Count == 1);
            MissionContinuityLayer.ReconcileAfterTurn(me, 2,
                new[] { ScoutStop(H(2, 1), mover: 7, steps: 3, ap: 2f,
                    ExecutionStopReason.HexEventStarted, blockedBeforeMovement: false) });
            Check("C6 event interruption did NOT retire the durable Recon role",
                MissionIntentRegistry.GetOrCreate(me).Count == 1);
        }

        // §2 — a scout that could not take a single step because it was already combat-locked is a
        // recoverable Blocked, never a structural Failed, and its durable role is not destroyed.
        private static void ScenarioD_AlreadyCombatLockedIsBlockedNotFailed()
        {
            PlayerSetupData me = Fresh("D");

            MissionTurnOutcome locked = ScoutStop(H(4, -1), mover: 9, steps: 0, ap: 0f,
                ExecutionStopReason.BattleStarted, blockedBeforeMovement: true);
            Check("D1 pre-move combat lock -> Blocked (not Failed)",
                locked.Outcome == ExecutionOutcome.Blocked);
            Check("D2 not a structural failure", !locked.StructuralFailure);

            MissionContinuityLayer.ReconcileAfterTurn(me, 1,
                new[] { ExploreOutcome(H(4, -1), mover: 9, steps: 3, ap: 1f) });
            Check("D3 durable intent created", MissionIntentRegistry.GetOrCreate(me).Count == 1);
            MissionContinuityLayer.ReconcileAfterTurn(me, 2,
                new[] { ScoutStop(H(4, -1), mover: 9, steps: 0, ap: 0f,
                    ExecutionStopReason.BattleStarted, blockedBeforeMovement: true) });
            Check("D4 combat lock did NOT retire the durable Recon role on first occurrence",
                MissionIntentRegistry.GetOrCreate(me).Count == 1);
        }

        // §4 — a nearby frontier out-scores an equally informative distant one while meaningful
        // nearby unknown remains; distance from home (Citadel + bases) materially drives the value.
        private static void ScenarioF_LocalExploreOutranksEquallyInformativeDistant()
        {
            WorldSnapshot snap = Snap(turn: 3);

            // Same info content (freshNeighbors), different distance from home.
            ReconObjective near = ReconObjectiveEvaluator.BuildExplore(snap, H(2, 0), 4, 2, false, false);
            ReconObjective far = ReconObjectiveEvaluator.BuildExplore(snap, H(10, 0), 4, 10, false, false);
            Check("F1 equally informative: nearer frontier has the higher BaseValue",
                near.BaseValue > far.BaseValue + 0.5f);

            // A slightly LESS informative near frontier still beats a max-info distant corridor.
            ReconObjective nearWeak = ReconObjectiveEvaluator.BuildExplore(snap, H(2, 0), 2, 2, false, false);
            ReconObjective farStrong = ReconObjectiveEvaluator.BuildExplore(snap, H(11, 0), 6, 11, false, false);
            Check("F2 a weaker nearby frontier still out-scores a max-info distant corridor",
                nearWeak.BaseValue > farStrong.BaseValue);

            Check("F3 HomeDistance folds in the Citadel explicitly",
                ReconObjectiveEvaluator.HomeDistance(snap, H(3, 0), 99) == 3
                && ReconObjectiveEvaluator.HomeDistance(snap, H(0, 0), 99) == 0);

            // Soft, not a leash: the distant objective still carries a real, usable value.
            Check("F4 distant exploration still has a materially non-zero value",
                far.BaseValue >= AiConfigV2.scoutBaseValueMin + 5f);
        }

        // §5/§13 — ReconOnly isolates which operational MISSIONS execute, not hand/card management.
        private static void ScenarioG_ReconOnlyIsolatesMissionsNotHandManagement()
        {
            AiStrategyV2Mode saved = AiStrategyV2Scope.Mode;
            try
            {
                AiStrategyV2Scope.Mode = AiStrategyV2Mode.ReconOnly;
                Check("G1 ReconOnly is active", AiStrategyV2Scope.IsReconOnly);
                Check("G2 StrategicManager Phase B (UseSurplus) still runs in ReconOnly",
                    AiStrategyV2Scope.AllowSurplusPreparation);

                var demands = new List<AxisDemand>
                {
                    new AxisDemand { RequestingAxis = DesireAxis.Recon, Capability = CapabilityKind.ScoutCapability },
                    new AxisDemand { RequestingAxis = DesireAxis.Aggression, Capability = CapabilityKind.FieldCombatPower },
                    new AxisDemand { RequestingAxis = DesireAxis.Development, Capability = CapabilityKind.DevelopmentInfrastructure },
                };
                var scoped = AiStrategyV2Scope.ApplyDemandScope(demands);
                Check("G3 non-Recon strategic DEMANDS are still suppressed in ReconOnly",
                    scoped.Count == 1 && scoped[0].RequestingAxis == DesireAxis.Recon);
            }
            finally
            {
                AiStrategyV2Scope.Mode = saved;
            }
        }

        // ============================================================= builders =====

        private static PlayerSetupData Fresh(string name)
        {
            MissionIntentRegistry.Clear();
            AiAllocatorStateRegistry.Clear();
            return new PlayerSetupData { Nickname = name, IsNeutral = false, IsHuman = false };
        }

        private static HexCoord H(int q, int r) => new HexCoord(q, r);

        private static MissionProposal ExploreProp(HexCoord focus)
        {
            var t = new ScoutMissionTarget
            {
                FocusHex = focus,
                Kind = ScoutTargetKind.Explore,
                Stealth = StealthRequirement.None,
                DetectionRisk = 0f,
            };
            var p = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = t,
                BaseValue = 50f,
                LocalAdmissionScore = 50f,
                Requirements = new MissionRequirements
                {
                    MoverKnown = false, ApMinimum = 1f, ApDesired = 1f, ApMaximum = 1f, EtaTurns = 1,
                },
            };
            p.Axes.Value[DesireAxis.Recon] = 1f;
            return p;
        }

        // A productive scout turn: moved `steps` hexes, spent `ap`, stopped OutOfMovement.
        private static MissionTurnOutcome ExploreOutcome(HexCoord focus, int mover, int steps, float ap) =>
            ScoutStop(focus, mover, steps, ap, ExecutionStopReason.OutOfMovement, blockedBeforeMovement: false);

        private static MissionTurnOutcome ScoutStop(HexCoord focus, int mover, int steps, float ap,
            ExecutionStopReason stop, bool blockedBeforeMovement)
        {
            MissionProposal p = ExploreProp(focus);
            var led = new MissionOutcomeLedger();
            led.RegisterProposals(new[] { p });
            led.RecordProvisionSuccess(p, new ProvisionedMission
            {
                Mission = p,
                Key = StableMissionKey.For(p),
                Kind = MissionKind.Scout,
                ScoutKind = ScoutTargetKind.Explore,
                MoverArmyId = mover,
                FocusHex = focus,
                ExecutionHex = focus,
                BaselineObservedTurn = 0,
                ClaimedAp = ap,
            });
            led.RecordExecution(new ExecutionResult
            {
                Key = StableMissionKey.For(p),
                StepsMoved = steps,
                EnteredStealth = false,
                ReachedGoal = false,
                StopReason = stop,
                BlockedBeforeMovement = blockedBeforeMovement,
                ApSpent = ap,
            });
            return led.Finalize().Single();
        }

        private static WorldSnapshot Snap(int turn)
        {
            return new WorldSnapshot
            {
                TurnNumber = turn,
                Self = new SelfSnapshot
                {
                    Citadel = H(0, 0),
                    BaseHexes = new List<HexCoord> { H(0, 0) },
                    Armies = new List<ArmySnapshot>(),
                    ActionPoints = 10,
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
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    TotalHexes = 100,
                    VisitedHexes = 4,
                    VisibleHexes = 6,
                    UnknownFrac = 0.9f,
                    ExplorableUnknownFrac = 0.9f,
                    Frontier = Array.Empty<FrontierHexSnapshot>(),
                    AllHexes = new List<HexCoord>(),
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                    VisitedHexSet = new HashSet<HexCoord>(),
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
