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

        // Owned airfield + departed wing: an intermediate airfield does not terminate an
        // outbound flight. Landing completion is an explicit Return-state fact, not an MP cap.
        [Test]
        public void OwnedAirfieldDuringOutbound_DoesNotCompleteSortie()
        {
            var wing = new ReconAirSortieState { Phase = ReconAirPhase.Outbound };
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(
                wing, atAirfield: true, hasDeparted: true), Is.False);
        }

        [Test]
        public void ReturnPhaseAtOwnedAirfield_CompletesSortie()
        {
            var wing = new ReconAirSortieState { Phase = ReconAirPhase.Return };
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, true, true), Is.True);
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, true, false), Is.False);
            Assert.That(ReconAirSortieLifecycle.CompletesAtAirfield(wing, false, true), Is.False);
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
