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
    // Recon audit (2026-09-25) — one regression test per confirmed Recon-branch defect. Every test
    // drives the real production owner. The portable subset uses frozen snapshots; assignment
    // tests that touch Unity object comparisons also require native EditMode.
    public class AiReconAuditBugTests
    {
        [TearDown]
        public void ClearState()
        {
            MissionIntentRegistry.Clear();
            ReconPatrolStateRegistry.ClearAll();
            AiReconMemory.Clear();
        }

        [Test]
        public void OrdinaryRediscovery_UpdatesHonestReconHistory()
        {
            var player = new PlayerSetupData { Nickname = "Recon" };
            var enemy = new PlayerSetupData { Nickname = "Enemy" };
            var oldHex = new HexCoord(10, 8);
            var newHex = new HexCoord(12, 9);
            AiReconMemory.Observe(player, 5, 1, new[]
            {
                new AiMapMemory.KnownEnemySighting(oldHex, enemy, "army", 5, 20f, 42f,
                    null, seenTurn: 5, armyId: 17),
            });
            Assert.That(AiReconMemory.Historical(player, new HashSet<int>()).Single().LastObservedHex,
                Is.EqualTo(oldHex));

            AiReconMemory.Observe(player, 10, 2, new[]
            {
                new AiMapMemory.KnownEnemySighting(newHex, enemy, "army", 4, 18f, 38f,
                    null, seenTurn: 10, armyId: 17),
            });
            ReconObservation refreshed = AiReconMemory.Historical(player, new HashSet<int>()).Single();
            Assert.That(refreshed.LastObservedHex, Is.EqualTo(newHex));
            Assert.That(refreshed.LastObservedTurn, Is.EqualTo(10));
            Assert.That(refreshed.MemberCount, Is.EqualTo(4));
            Assert.That(AiReconMemory.Historical(player, new HashSet<int> { 17 }), Is.Empty);
        }

        [Test]
        public void StaleEnemyContact_RemainsThreatData_WithoutCreatingAReconJob()
        {
            var player = new PlayerSetupData { Nickname = "Recon" };
            var focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, 10, focus);
            snap.Known = new KnownSnapshot { EnemySightings =
                new List<AiMapMemory.KnownEnemySighting>() };
            var contact = new EnemyContactSnapshot
            {
                Army = new ArmySnapshot { ArmyId = 17, MemberCount = 5 },
                Knowledge = ContactKnowledge.LastKnown,
                Position = focus,
                LastObservedTurn = 5,
                Confidence = 0.5f,
            };
            snap.Threat = new ThreatModel { Contacts = new List<EnemyContactSnapshot> { contact } };

            List<ReconObjective> objectives = ReconObjectiveEvaluator.Enumerate(snap);

            Assert.That(snap.Threat.Contacts.Single(), Is.SameAs(contact));
            Assert.That(contact.Position, Is.EqualTo(focus));
            Assert.That(contact.LastObservedTurn, Is.EqualTo(5));
            Assert.That(objectives.All(o => o.Kind == ReconObjectiveKind.Explore
                || o.Kind == ReconObjectiveKind.Refresh || o.Kind == ReconObjectiveKind.AirSweep), Is.True);
            Assert.That(objectives.All(o => o.IntentKey.SubKind != 1), Is.True);
        }

        [Test]
        public void ScoutNumericIdentities_LeaveTheRemovedSubtypeUnused()
        {
            Assert.That((int)ScoutTargetKind.Explore, Is.Zero);
            Assert.That((int)ScoutTargetKind.Refresh, Is.EqualTo(2));
            Assert.That((int)ScoutTargetKind.AirSweep, Is.EqualTo(3));
            Assert.That(System.Enum.IsDefined(typeof(ScoutTargetKind), 1), Is.False);
            foreach (ScoutTargetKind kind in System.Enum.GetValues(typeof(ScoutTargetKind)))
                Assert.That(MissionIntentKey.ForScoutTarget((ScoutMissionTarget)Scout(kind, new HexCoord(4, 3))
                    .Target).SubKind, Is.Not.EqualTo(1));
        }

        [Test]
        public void ObservationCapacity_CountsOnlyRefreshLanes_ExploreStaysGround()
        {
            var player = new PlayerSetupData { Nickname = "Recon" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, 11, focus);
            MissionIntent explore = Incumbent(focus, preferredMover: 10);
            MissionIntent refresh = Incumbent(new HexCoord(3, 4), preferredMover: 20);
            ((ScoutIntent)refresh.Objective).Kind = ScoutTargetKind.Refresh;
            refresh.IntentKey = MissionIntentKey.For(refresh);
            var intents = new List<MissionIntent> { explore, refresh };
            ActorCommitments commitments = ActorCommitments.FromIntents(intents, snap, new List<ReconObjective>
            {
                new ReconObjective { Kind = ReconObjectiveKind.Explore, FocusHex = focus, BaseValue = 10f },
                new ReconObjective { Kind = ReconObjectiveKind.Refresh, FocusHex = new HexCoord(3, 4), BaseValue = 10f },
            });

            ReconCapacitySnapshot cap = ReconCapacitySnapshot.Build(snap,
                new List<ReconObjective>(), new List<ReconObjective>(), intents, commitments, player);

            Assert.That(cap.GenericGroundLaneActors, Is.EquivalentTo(new[] { 10 }));
            Assert.That(cap.GenericObservationLaneActors, Is.EquivalentTo(new[] { 20 }));
        }

        // B3 — an AirSweep with no air candidate must not borrow a GROUND capability diagnosis:
        // MoverContended / NoMoverExists suspend the intent under CapabilityUnavailable, which never
        // ages or reaps an AirSweep (it is exempt from the moverless rule), so it lived forever.
        [Test]
        public void AirSweep_WithoutAirCandidate_IsNoExecutableStep_NotAGroundCapabilityShortage()
        {
            var player = new PlayerSetupData { Nickname = "Recon audit" };
            WorldSnapshot snap = Snapshot(player, 11, new HexCoord(4, 3));
            MissionProposal sweep = Scout(ScoutTargetKind.AirSweep, new HexCoord(8, 8));
            var open = new List<FundedEntry> { new FundedEntry { Mission = sweep, Priority = 1 } };

            ReconAssignmentResult result = ReconAssignmentPlanner.AssignFunded(snap, null, player,
                open, new HashSet<int>(), durableClaimedArmyIds: new HashSet<int> { 10, 20 });

            Assert.That(result.Assigned, Is.Empty);
            Assert.That(result.Rejected[StableMissionKey.For(sweep)],
                Is.EqualTo(ScoutAssignmentFailureReason.NoExecutableStep),
                "ground scouts are never AirSweep capacity, busy or not");
        }

        [Test]
        public void FreshAirSweepWithoutCandidate_IsNotProposed_WhileObjectiveSurvives()
        {
            var player = new PlayerSetupData();
            WorldSnapshot snap = Snapshot(player, 11, new HexCoord(4, 3));
            var objective = new ReconObjective { Kind = ReconObjectiveKind.AirSweep,
                FocusHex = new HexCoord(4, 3), TaskScore = new TaskScore(infoGain: 30f), BaseValue = 30f };
            var objectives = new[] { objective };
            Assert.That(ReconMissionPlanner.Propose(snap, new DesireBreakdown(),
                new MissionIntent[0], objectives), Is.Empty);
            Assert.That(objectives.Single(), Is.SameAs(objective));
            Assert.That(objective.TaskScore.Value, Is.EqualTo(30f));
            Assert.That(MissionIntentRegistry.GetOrCreate(player).All, Is.Empty);
        }

        [Test]
        public void GroundBootstrapWithoutScout_KeepsProposalAndCapabilityDemand()
        {
            var player = new PlayerSetupData();
            WorldSnapshot snap = Snapshot(player, 11, new HexCoord(4, 3));
            snap.Observer = player;
            snap.Self.Armies = new ArmySnapshot[0];
            var objectives = new[] { new ReconObjective { Kind = ReconObjectiveKind.Explore,
                FocusHex = new HexCoord(4, 3), FreshNeighbors = 6,
                TaskScore = new TaskScore(infoGain: 30f), BaseValue = 30f } };
            var intents = new MissionIntent[0];
            var bd = new DesireBreakdown { ReconExplorePressure = 1f };
            MissionProposal proposal = ReconMissionPlanner.Propose(snap, bd, intents, objectives).Single();
            Assert.That(proposal.Requirements.MoverKnown, Is.False);
            var demands = DemandLayer.Generate(snap, bd, objectives, new RaidObjective[0], intents,
                ActorCommitments.FromIntents(intents, snap, objectives), player,
                dirtyAxes: new HashSet<DesireAxis> { DesireAxis.Recon });
            Assert.That(demands.Any(d => d.Capability == CapabilityKind.ScoutCapability
                && d.DesiredAmount > 0 && !d.IsPersistenceDeferred), Is.True);
        }

        [Test]
        public void BlockedAirIncumbent_KeepsProposalAndUsesOrdinaryStallLifecycle()
        {
            var player = new PlayerSetupData();
            var focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, 11, focus);
            snap.TrueWorld = new TrueWorldSnapshot { EnemyArmies = new[] {
                new ArmySnapshot { ArmyId = 99, Hex = focus, EffectiveArmyPower = 20f } } };
            var intent = Incumbent(focus, 31);
            intent.Scout.Kind = ScoutTargetKind.AirSweep;
            intent.IntentKey = MissionIntentKey.For(intent);
            var state = MissionIntentRegistry.GetOrCreate(player);
            state.Put(intent);
            MissionProposal proposal = ReconMissionPlanner.Propose(snap, new DesireBreakdown(),
                new[] { intent }, new ReconObjective[0]).Single();
            Assert.That(proposal.FromDurableIntent, Is.True);
            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(new[] { proposal });
            ledger.RecordProvisionFailure(proposal, ProvisionFailure.NoExecutableStep("blocked air"));
            MissionStepResult result = ledger.FinalizeSteps().Single();
            Assert.That(result.IsBlocked, Is.True);
            MissionContinuityLayer.ReconcileStep(player, 11, result);
            Assert.That(state.TryGet(intent.IntentKey, out var kept), Is.True);
            Assert.That(kept.PreferredMoverArmyId, Is.EqualTo(31));
            Assert.That(kept.StallTurns, Is.EqualTo(1));
            Assert.That(kept.Suspended, Is.Not.EqualTo(SuspendReason.CapabilityUnavailable));
        }

        [TestCase(ScoutTargetKind.AirSweep)]
        [TestCase(ScoutTargetKind.Explore)]
        public void NoExecutableStepDiagnostic_DoesNotAssertAnEligibleActorExists(ScoutTargetKind kind)
        {
            var player = new PlayerSetupData();
            var snap = Snapshot(player, 11, new HexCoord(4, 3));
            foreach (ArmySnapshot army in snap.Self.Armies) army.CurrentMovement = 0;
            MissionProposal mission = Scout(kind, new HexCoord(4, 3));
            var funded = new FundedEntry { Mission = mission, Priority = 1 };
            // Test the translator with the canonical rejection vocabulary; eligibility belongs
            // to Assignment and is covered by its own tests, not re-derived in Provisioning.
            var assignment = new ReconAssignmentResult();
            assignment.Rejected[StableMissionKey.For(mission)] = ScoutAssignmentFailureReason.NoExecutableStep;
            using var session = new ProvisioningSession(snap);
            session.SetAssignment(assignment);
            var allocation = new TentativeAllocation();
            allocation.Funded.Add(funded);
            ProvisionFailure failure = ProvisioningManager.ScoutAssignmentFailures(session, allocation).Single().Failure;
            Assert.That(failure.Kind, Is.EqualTo(ProvisionFailureKind.NoExecutableStep));
            Assert.That(failure.Disposition, Is.EqualTo(ProvisionDisposition.RetryNextTurn));
            Assert.That(failure.Detail, Does.Contain(kind.ToString()));
            Assert.That(failure.Detail, Does.Contain("no executable"));
            Assert.That(failure.Detail, Does.Not.Contain("eligible scout(s)/vantage exist"));
            Assert.That(session.ClaimedArmyIds, Is.Empty);
            Assert.That(session.ApClaimed, Is.Zero);
        }

        [Test]
        public void SpentGroundPreference_CanUseAFreeAlternativeAfterFunding()
        {
            var player = new PlayerSetupData();
            var snap = Snapshot(player, 11, new HexCoord(4, 3));
            snap.Self.Armies[0].CurrentMovement = 0;
            MissionProposal mission = Scout(ScoutTargetKind.Explore, new HexCoord(4, 3));
            mission.PreferredMoverArmyId = 10;
            var assignment = ReconAssignmentPlanner.AssignFunded(snap, null, player,
                new List<FundedEntry> { new FundedEntry { Mission = mission, Priority = 1 } }, new HashSet<int>());
            Assert.That(assignment.Assigned.Single().Value.ActorKey, Is.EqualTo(20));
        }

        // B5 — a tactical TargetInvalidated (opportunistic attack/sabotage target gone, stale vantage,
        // stale plan) is not a failed Recon objective. Continuity re-validates the objective itself;
        // the ledger must not turn it into Failed, which retired the whole durable role mid-turn.
        [Test]
        public void ScoutExecutionTargetInvalidated_IsBlocked_AndTheDurableRoleSurvives()
        {
            var player = new PlayerSetupData { Nickname = "Recon audit" };
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            HexCoord focus = new HexCoord(4, 3);
            state.Put(Incumbent(focus, preferredMover: 10));

            MissionProposal m = Scout(ScoutTargetKind.Explore, focus);
            m.FromDurableIntent = true;
            var pm = new ProvisionedMission
            {
                Mission = m, Key = StableMissionKey.For(m), Kind = MissionKind.Scout,
                ScoutKind = ScoutTargetKind.Explore, MoverArmyId = 10, FocusHex = focus,
                ExecutionHex = focus,
            };
            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(new[] { m });
            ledger.RecordProvisionSuccess(m, pm);
            ledger.RecordExecution(new ExecutionResult
            {
                Key = pm.Key, Source = pm, StepsMoved = 1,
                StopReason = ExecutionStopReason.TargetInvalidated,
            });
            MissionStepResult outcome = ledger.FinalizeSteps().Single();

            Assert.That(outcome.IsBlocked, Is.True);
            MissionContinuityLayer.ReconcileStep(player, 8, outcome);
            Assert.That(state.TryGet(outcome.IntentKey, out MissionIntent kept), Is.True);
            Assert.That(kept.PreferredMoverArmyId, Is.EqualTo(10));
        }

        [Test]
        public void ScoutProvisioningTargetInvalidated_IsBlocked()
        {
            MissionProposal m = Scout(ScoutTargetKind.Refresh, new HexCoord(4, 3));
            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(new[] { m });
            ledger.RecordProvisionFailure(m, ProvisionFailure.TargetInvalidated("focus holds a known army"));

            Assert.That(ledger.FinalizeSteps().Single().IsBlocked, Is.True);
        }

        // B6 — aviation serves only AirSweep, so a mover-less ground Refresh must not ask the
        // allocator for the notional air-launch Energy it can never spend.
        [Test]
        public void NotionalRefresh_RequestsNoAirEnergy()
        {
            var player = new PlayerSetupData { Nickname = "Recon audit" };
            WorldSnapshot snap = Snapshot(player, 11, new HexCoord(4, 3));
            ((List<ArmySnapshot>)snap.Self.Armies).Clear();

            ScoutCostEstimate refresh = ScoutCostModel.Estimate(snap, new ScoutMissionTarget
                { Kind = ScoutTargetKind.Refresh, FocusHex = new HexCoord(4, 3) });
            ScoutCostEstimate sweep = ScoutCostModel.Estimate(snap, new ScoutMissionTarget
                { Kind = ScoutTargetKind.AirSweep, FocusHex = new HexCoord(4, 3) });

            Assert.That(refresh.MoverKnown, Is.False);
            Assert.That(refresh.EnergyDesired, Is.Zero);
            Assert.That(refresh.EnergyMaximum, Is.Zero);
            Assert.That(sweep.EnergyDesired, Is.GreaterThan(0f), "AirSweep keeps its notional launch");
        }

        // B7 — an air mover of a durable intent is priced through the air witness alone; counting
        // it again as an ordinary committed mover double-reserved its activation in Phase A.
        [Test]
        public void PhaseACommittedAp_DoesNotCountAnAirMoverTwice()
        {
            var player = new PlayerSetupData { Nickname = "Recon audit" };
            WorldSnapshot snap = Snapshot(player, 11, new HexCoord(4, 3));
            ((List<ArmySnapshot>)snap.Self.Armies).Add(new ArmySnapshot
            {
                ArmyId = 31, Owner = player, Hex = new HexCoord(2, 2), IsAir = true,
                MemberCount = 1, CurrentMovement = 4, MaxMovement = 4, ActivationApCost = 2,
            });
            var sweep = new MissionIntent
            {
                Kind = MissionKind.Scout, Status = IntentStatus.Active,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.AirSweep },
                PreferredMoverArmyId = 31,
            };
            sweep.IntentKey = MissionIntentKey.For(sweep);
            System.Reflection.MethodInfo method = typeof(StrategicPhaseA).GetMethod(
                "CommittedNonCardApDemand",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            // No root => the air witness is empty; the wing must not be priced by the ground path.
            float ap = (float)method.Invoke(null, new object[]
                { snap, player, null, null, new List<MissionIntent> { sweep },
                  new List<ReconObjective>() });
            Assert.That(ap, Is.Zero);
        }

        // B8 — a patrol state whose army no longer exists is not a live coverage claim.
        [Test]
        public void PatrolStateOfAMissingArmy_IsNotACoverageClaim()
        {
            var player = new PlayerSetupData { Nickname = "Recon audit" };
            HexCoord from = new HexCoord(0, 0), anchor = new HexCoord(6, 0);
            ReconPatrolStateRegistry.GetOrCreate(player, 5, from, anchor, ReconMode.Explore, 1);
            ReconSector sector = ReconDirectionModel.Sector(from, anchor);

            Assert.That(ReconPatrolStateRegistry.OtherSectorClaims(player, 99, sector), Is.Zero);
            Assert.That(ReconPatrolStateRegistry.OtherNearbyAnchorClaims(player, 99, anchor, 2), Is.Zero);
        }

        // B10 — a planning witness never relaxes ANOTHER intent's durable claim; only the
        // incumbent's own actor may be re-bound (the rule ground combat already applies).
        [Test]
        public void PlanningWitness_CannotReleaseAnotherIntentsDurableActor()
        {
            var player = new PlayerSetupData { Nickname = "Recon audit" };
            WorldSnapshot snap = Snapshot(player, 11, new HexCoord(4, 3));
            MissionIntentRegistry.GetOrCreate(player).Put(Incumbent(new HexCoord(9, 9), preferredMover: 20));

            MissionProposal fresh = Scout(ScoutTargetKind.Explore, new HexCoord(4, 3));
            fresh.PreferredMoverArmyId = 20;   // the cheapest actor the estimate priced against
            var open = new List<FundedEntry> { new FundedEntry { Mission = fresh, Priority = 1 } };

            ReconAssignmentResult result = ReconAssignmentPlanner.AssignFunded(snap, null, player,
                open, new HashSet<int>(), durableClaimedArmyIds: new HashSet<int> { 20 });

            Assert.That(result.Assigned, Has.Count.EqualTo(1));
            Assert.That(result.Assigned.Single().Value.ActorKey, Is.EqualTo(10));
        }

        [Test]
        public void DurableIncumbent_StillRecoversItsOwnActor()
        {
            var player = new PlayerSetupData { Nickname = "Recon audit" };
            WorldSnapshot snap = Snapshot(player, 11, new HexCoord(4, 3));
            snap.Intents = MissionIntentRegistry.GetOrCreate(player);
            MissionIntentRegistry.GetOrCreate(player).Put(Incumbent(new HexCoord(4, 3), preferredMover: 20));

            MissionProposal incumbent = Scout(ScoutTargetKind.Explore, new HexCoord(4, 3));
            incumbent.FromDurableIntent = true;
            incumbent.PreferredMoverArmyId = 20;
            var open = new List<FundedEntry> { new FundedEntry { Mission = incumbent, Priority = 1 } };

            ReconAssignmentResult result = ReconAssignmentPlanner.AssignFunded(snap, null, player,
                open, new HashSet<int>(), durableClaimedArmyIds: new HashSet<int> { 20 });

            Assert.That(result.Assigned.Single().Value.ActorKey, Is.EqualTo(20));
        }

        // B11 — a bound actor that still exists but can no longer serve the role (not a solo Recce
        // any more) is unbound like a missing one, so the role ages instead of hiding behind the
        // CapabilityUnavailable exemption forever.
        [Test]
        public void BoundActorThatIsNoLongerAScout_IsUnbound()
        {
            var player = new PlayerSetupData { Nickname = "Recon audit" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, 11, focus);
            ((List<ArmySnapshot>)snap.Self.Armies)[0].IsSoloRecce = false;
            ((List<ArmySnapshot>)snap.Self.Armies)[0].MemberCount = 3;
            MissionIntent intent = Incumbent(focus, preferredMover: 10);
            MissionIntentRegistry.GetOrCreate(player).Put(intent);

            MissionContinuityLayer.ResolveActive(player, snap);

            Assert.That(intent.PreferredMoverArmyId, Is.Null);
        }

        [Test]
        public void BoundScout_StaysBound()
        {
            var player = new PlayerSetupData { Nickname = "Recon audit" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, 11, focus);
            MissionIntent intent = Incumbent(focus, preferredMover: 10);
            MissionIntentRegistry.GetOrCreate(player).Put(intent);

            MissionContinuityLayer.ResolveActive(player, snap);

            Assert.That(intent.PreferredMoverArmyId, Is.EqualTo(10));
        }

        // B1 — a stalled mandatory air obligation is skipped for the rest of THIS turn only.
        [Test]
        public void StalledAirObligation_IsSkippedForTheTurnOnly()
        {
            var player = new PlayerSetupData { Nickname = "Recon audit" };
            AviationObligationStallRegistry.Clear();
            AviationObligationStallRegistry.MarkStalled(player, 7, 31);

            Assert.That(AviationObligationStallRegistry.IsStalled(player, 7, 31), Is.True);
            Assert.That(AviationObligationStallRegistry.IsStalled(player, 7, 32), Is.False);
            Assert.That(AviationObligationStallRegistry.IsStalled(player, 8, 31), Is.False,
                "the next turn re-tries the obligation from scratch");
            AviationObligationStallRegistry.MarkStalled(player, 8, 32);
            Assert.That(AviationObligationStallRegistry.IsStalled(player, 7, 31), Is.False);
            AviationObligationStallRegistry.Clear();
        }

        // ---- Hunt (2026-10-22) ------------------------------------------------------------------

        [Test]
        public void Hunt_IsAOneShotLocalGroundStrike()
        {
            Assert.That((int)ScoutTargetKind.Hunt, Is.EqualTo(5), "persisted identity");
            Assert.That(ReconScoutKinds.IsLocalStrike(ScoutTargetKind.Hunt), Is.True);
            Assert.That(ReconScoutKinds.IsLocalStrike(ScoutTargetKind.CaptureStructure), Is.True);
            Assert.That(ReconScoutKinds.IsLocalStrike(ScoutTargetKind.Refresh), Is.False);
            Assert.That(ReconScoutKinds.IsGround(ScoutTargetKind.Hunt), Is.True);
            Assert.That(ReconScoutKinds.Name(ScoutTargetKind.Hunt), Is.EqualTo("Hunt"));
        }

        // A hunt exists only for a hunter Analysis proved reaches and beats the force this turn;
        // only that hunter is eligible, and it is never a Recon lane nor a scout to buy.
        [Test]
        public void HuntObjective_ExistsOnlyForAProvenHunter()
        {
            var player = new PlayerSetupData();
            var focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, 11, focus);
            snap.Observer = player;
            Assert.That(ReconObjectiveEvaluator.HuntAt(snap, focus), Is.Null);

            snap.Self.ReconHuntOpportunities = new[] { (10, focus) };
            ReconObjective hunt = ReconObjectiveEvaluator.HuntAt(snap, focus);

            Assert.That(hunt, Is.Not.Null);
            Assert.That(hunt.ToTarget().Kind, Is.EqualTo(ScoutTargetKind.Hunt));
            Assert.That(hunt.IsLocalStrike, Is.True);
            Assert.That(hunt.BaseValue, Is.GreaterThan(0f));
            Assert.That(ScoutMoverSelector.Eligible(snap, hunt.ToTarget(), null).Select(a => a.ArmyId),
                Is.EqualTo(new[] { 10 }), "the scout without a proven hunt is not a hunter");
            MissionProposal proposal = ReconMissionPlanner.Propose(snap, new DesireBreakdown(),
                new MissionIntent[0], new[] { hunt }).Single();
            Assert.That(((ScoutMissionTarget)proposal.Target).Kind, Is.EqualTo(ScoutTargetKind.Hunt));
            Assert.That(proposal.PreferredMoverArmyId, Is.EqualTo(10));
        }

        // A structure lost since the enemy's last turn: its saboteur stands revealed on the ruin.
        // While the ruin is out of sight the air sweep looks there first; an old loss does not.
        [TestCase(7, true)]
        [TestCase(9, false)]
        public void RecentStructureLoss_AnchorsTheAirSweepOnTheUnseenRuin(int turnNow, bool anchored)
        {
            var player = new PlayerSetupData();
            var ruin = new HexCoord(2, -1);
            AiMapMemory.Clear();
            AiMapMemory.EnsureSubscribed();
            try
            {
                AiMapMemory.OnTurnStarted(player, 7);
                Game.Map.BuildingRegistry.RaiseStructureLost(ruin, player);
                WorldSnapshot snap = Snapshot(player, turnNow, new HexCoord(4, 3));
                snap.Observer = player;
                snap.TrueWorld = new TrueWorldSnapshot { EnemyArmies = new ArmySnapshot[0] };

                bool found = WorldAnalysis.TryAirSweepAnchor(snap, out HexCoord anchor, out bool enemyThere);

                Assert.That(found && anchor.Equals(ruin), Is.EqualTo(anchored));
                if (anchored)
                    Assert.That(enemyThere, Is.True, "priced as an enemy presence");
            }
            finally
            {
                AiMapMemory.Clear();
            }
        }

        internal static MissionProposal Scout(ScoutTargetKind kind, HexCoord focus) =>
            new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = kind, FocusHex = focus },
                BaseValue = 10f,
            };

        internal static MissionIntent Incumbent(HexCoord focus, int preferredMover)
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.Scout,
                Funding = CommitmentTier.None,
                Status = IntentStatus.Active,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore, FocusHex = focus },
                PreferredMoverArmyId = preferredMover,
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            return intent;
        }

        internal static WorldSnapshot Snapshot(PlayerSetupData player, int turn, HexCoord focus)
        {
            var hexes = new HashSet<HexCoord>(HexGridMath.Neighbors(focus))
            {
                focus,
                new HexCoord(0, 0),
                new HexCoord(9, 9),
            };
            return new WorldSnapshot
            {
                TurnNumber = turn,
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new List<HexCoord> { new HexCoord(0, 0) },
                    Armies = new List<ArmySnapshot>
                    {
                        new ArmySnapshot
                        {
                            ArmyId = 10, Owner = player, Hex = new HexCoord(3, 3),
                            IsSoloRecce = true, MemberCount = 1, CurrentMovement = 3,
                            MaxMovement = 3, ActivationApCost = 1,
                        },
                        new ArmySnapshot
                        {
                            ArmyId = 20, Owner = player, Hex = new HexCoord(3, 2),
                            IsSoloRecce = true, MemberCount = 1, CurrentMovement = 3,
                            MaxMovement = 3, ActivationApCost = 1,
                        },
                    },
                },
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    AllHexes = hexes.ToList(),
                    VisitedHexSet = new HashSet<HexCoord>(),
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                },
            };
        }
    }
}
#endif
