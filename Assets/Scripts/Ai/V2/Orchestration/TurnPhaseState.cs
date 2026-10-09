using System.Collections.Generic;

namespace Game.Ai.V2
{
    // The results of the two spending phases of the turn, shared by the components that spend or
    // account (the readmission runner, the Phase B round, the cold residual).
    internal sealed class PhaseResults
    {
        internal readonly StrategicPhaseResult PhaseA;
        internal readonly StrategicPhaseResult PhaseB;

        internal PhaseResults(StrategicPhaseResult phaseA, StrategicPhaseResult phaseB)
        {
            PhaseA = phaseA;
            PhaseB = phaseB;
        }

        // The reservation Phase A carries through the turn: Phase B's once a round produced one,
        // else Phase A's. Read at every use, never cached (both owners replace it).
        internal MaterializationReservation Carried => PhaseB.Reservation ?? PhaseA.Reservation;
    }

    // What the admission iterations account for the turn summary (turn activity). Written by the
    // admission component, read once by RunTurn when the loop is over.
    internal sealed class TurnTelemetry
    {
        internal List<MissionProposal> Missions = new List<MissionProposal>();
        internal TentativeAllocation Allocation = new TentativeAllocation();
        internal readonly HashSet<StableMissionKey> FundedKeys = new HashSet<StableMissionKey>();
        internal readonly List<ProvisionedMission> Provisioned = new List<ProvisionedMission>();
        internal readonly Dictionary<ProvisionFailureKind, int> ProvisioningFailures =
            new Dictionary<ProvisionFailureKind, int>();
        internal readonly List<ExecutionResult> AllExecuted = new List<ExecutionResult>();

        // Every mission any pack of this turn funded (turn activity: MissionsFunded), recorded right
        // after each pack; a key funded again is counted once.
        internal void RecordFunded(TentativeAllocation packed)
        {
            foreach (FundedEntry fe in packed.Funded)
                if (fe?.Mission != null)
                    FundedKeys.Add(StableMissionKey.For(fe.Mission));
        }
    }
}
