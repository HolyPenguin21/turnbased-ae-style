#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Recon audit (2026-09-25) — one regression test per confirmed Recon-branch defect. Every test
    // drives the real production owner with a frozen snapshot only (no live ArmyData), so it runs
    // in the Tools/ai-verify harness as well as in Unity.
    public class AiReconAuditBugTests
    {
        [TearDown]
        public void ClearState()
        {
            MissionIntentRegistry.Clear();
            ReconPatrolStateRegistry.ClearAll();
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

        // B4 — a fresh Surveil absorbed into the actor's existing durable role must carry the
        // provisioned baseline, or IsIntentStillValid retires the role on the very next pass.
        [Test]
        public void AbsorbIntoSurveil_KeepsTheProvisionedBaseline()
        {
            var player = new PlayerSetupData { Nickname = "Recon audit" };
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntent explore = Incumbent(new HexCoord(4, 3), preferredMover: 10);
            state.Put(explore);

            var target = new ScoutMissionTarget
            {
                Kind = ScoutTargetKind.Surveil,
                FocusHex = new HexCoord(6, 6),
                Stealth = StealthRequirement.Required,
            };
            var proposal = new MissionProposal { Kind = MissionKind.Scout, Target = target };
            var outcome = new MissionTurnOutcome
            {
                AttemptKey = StableMissionKey.For(proposal),
                IntentKey = new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil, 99, 0, 0),
                Proposal = proposal,
                MissionKind = MissionKind.Scout,
                Outcome = ExecutionOutcome.ProductiveStop,
                MadeProgress = true,
                StepsMoved = 1,
                HasScoutPayload = true,
                MoverArmyId = 10,
                ScoutKind = ScoutTargetKind.Surveil,
                ScoutRequiresStealth = true,
                FocusHex = new HexCoord(6, 6),
                TrackedArmyId = 99,
                BaselineObservedTurn = 7,
            };

            MissionContinuityLayer.ReconcileStep(player, 8, outcome);

            Assert.That(state.TryGet(outcome.IntentKey, out MissionIntent absorbed), Is.True);
            Assert.That(absorbed, Is.SameAs(explore), "the actor's durable role is re-pointed, not duplicated");
            Assert.That(absorbed.Scout.BaselineObservedTurn, Is.EqualTo(7));
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
            MissionTurnOutcome outcome = ledger.Finalize().Single();

            Assert.That(outcome.Outcome, Is.EqualTo(ExecutionOutcome.Blocked));
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

            Assert.That(ledger.Finalize().Single().Outcome, Is.EqualTo(ExecutionOutcome.Blocked));
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
                  new List<ReconObjective>(), null });
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
