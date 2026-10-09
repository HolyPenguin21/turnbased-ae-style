using System.Collections.Generic;

namespace Game.Ai.V2
{
    internal enum ProvisionEventKind
    {
        ScoutBatchFailure,   // an impossible Scout job reported by the batch assignment
        Failure,             // the picked mission could not be provisioned
        Success,             // the picked mission was provisioned
    }

    // One attempt of the selection, in the order it happened. A transient journal of ONE call, not
    // a lifecycle ledger: the caller replays it into the MissionOutcomeLedger (and counts it in the
    // turn telemetry) before the step executes. Nothing in the loop reads that ledger in between,
    // so replaying later is the same as recording at once; the logging of each attempt stays
    // synchronous inside the selection.
    internal readonly struct ProvisionEvent
    {
        internal readonly ProvisionEventKind Kind;
        internal readonly MissionProposal Proposal;
        internal readonly ProvisionFailure Failure;        // Kind != Success
        internal readonly ProvisionedMission Provisioned;  // Kind == Success

        internal ProvisionEvent(ProvisionEventKind kind, MissionProposal proposal,
            ProvisionFailure failure, ProvisionedMission provisioned)
        {
            Kind = kind;
            Proposal = proposal;
            Failure = failure;
            Provisioned = provisioned;
        }
    }

    // The result of ProvisioningManager.ProvisionNext: which funded mission became executable (or
    // none), the allocation the caller continues with, and everything the selection touched.
    internal sealed class ProvisioningSelectionOutcome
    {
        internal ProvisionedMission Selected;
        // Meaningful when Selected != null.
        internal StableMissionKey SelectedKey;
        internal bool SelectedIsCommitment;
        // The last pack of the selection (the caller's `allocation` from here on).
        internal TentativeAllocation FinalAllocation;
        // Every key the selection tried (successfully or not), for the step settlement.
        internal readonly HashSet<StableMissionKey> AttemptedKeys = new HashSet<StableMissionKey>();
        // Every attempt in order, including each failure of a Scout batch.
        internal readonly List<ProvisionEvent> Events = new List<ProvisionEvent>();
        // The missions funded by every pack the selection made after the initial one.
        internal readonly List<StableMissionKey> FundedKeysAcrossPacks = new List<StableMissionKey>();
    }
}
