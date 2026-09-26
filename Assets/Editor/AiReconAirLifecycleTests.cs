#if UNITY_INCLUDE_TESTS
using System.Linq;
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Recon S1/S5 — air-recon lifecycle facts that must reach Continuity honestly.
    public class AiReconAirLifecycleTests
    {
        // S5 — an AirLaunch that never formed aircraft carries no durable mover: its synthetic
        // per-airfield key is not an army and must never become an intent's PreferredMoverArmyId.
        [Test]
        public void UnlaunchedAirLaunch_ReportsNoMover_LaunchedOneReportsTheRealArmy()
        {
            Assert.That(Finalize(actualArmyId: null).MoverArmyId, Is.Null);
            Assert.That(Finalize(actualArmyId: 41).MoverArmyId, Is.EqualTo(41));
        }

        // S1 — the used-up outbound leg is one rule on the sortie state.
        [Test]
        public void OutboundCapReached_IsTheSpentVersusCapRule()
        {
            var sortie = new ReconAirSortieState { OutboundMovementCap = 1, OutboundMovementSpent = 0 };
            Assert.That(sortie.OutboundCapReached, Is.False);
            sortie.OutboundMovementSpent = 1;
            Assert.That(sortie.OutboundCapReached, Is.True, "a launch step can already spend a small cap");
        }

        // Owned airfield + departed wing: only a wing turning for home lands there.
        [Test]
        public void OwnedAirfieldDuringOutbound_DoesNotCompleteSortie()
        {
            var heli = new ReconAirSortieState
            {
                Phase = ReconAirPhase.Outbound, OutboundMovementCap = 6, OutboundMovementSpent = 2,
            };
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(heli, atAirfield: true,
                hasDeparted: true), Is.False, "intermediate airfield on the outbound route");
        }

        [Test]
        public void ReturnPhaseAtOwnedAirfield_CompletesSortie()
        {
            var wing = new ReconAirSortieState { Phase = ReconAirPhase.Return, OutboundMovementCap = 6 };
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, true, true), Is.True);
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, true, hasDeparted: false),
                Is.False, "a wing that never left its airfield has not flown a sortie");
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, atAirfield: false, true),
                Is.False);
        }

        [Test]
        public void OutboundCapUsedUpOnAnAirfield_CompletesSortie()
        {
            var wing = new ReconAirSortieState
            {
                Phase = ReconAirPhase.Outbound, OutboundMovementCap = 3, OutboundMovementSpent = 3,
            };
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, true, true), Is.True,
                "PlanStep does not turn a wing for home while it stands on an airfield");
        }

        [Test]
        public void OutboundCap_PlaneReservesHalf_HelicopterMayEndAloft()
        {
            Assert.That(ReconAirSortieState.OutboundCapFor(6, safeUnlandedEnds: 0), Is.EqualTo(3),
                "plane: half the movement out, the other half back the same turn");
            Assert.That(ReconAirSortieState.OutboundCapFor(6, safeUnlandedEnds: 1), Is.EqualTo(6),
                "helicopter: full movement out, may end the turn aloft and return next turn");
        }

        private static MissionTurnOutcome Finalize(int? actualArmyId)
        {
            var m = new MissionProposal
            {
                Kind = MissionKind.Scout,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.AirSweep, FocusHex = new HexCoord(9, 0) },
            };
            var pm = new ProvisionedMission
            {
                Mission = m, Key = StableMissionKey.For(m), Kind = MissionKind.Scout,
                ScoutKind = ScoutTargetKind.AirSweep, ExecutorKind = ScoutExecutorKind.AirLaunch,
                MoverArmyId = ScoutExecutionCandidate.SyntheticAirfieldActorId(new HexCoord(1, 1)),
                AirfieldHex = new HexCoord(1, 1),
            };
            var ledger = new MissionOutcomeLedger();
            ledger.RegisterProposals(new[] { m });
            ledger.RecordProvisionSuccess(m, pm);
            ledger.RecordExecution(new ExecutionResult
            {
                Key = pm.Key, Source = pm, ActualActorArmyId = actualArmyId,
                StepsMoved = actualArmyId.HasValue ? 1 : 0,
                StopReason = actualArmyId.HasValue ? ExecutionStopReason.StepCompleted
                    : ExecutionStopReason.NoSafeStep,
            });
            return ledger.Finalize().Single();
        }
    }
}
#endif
