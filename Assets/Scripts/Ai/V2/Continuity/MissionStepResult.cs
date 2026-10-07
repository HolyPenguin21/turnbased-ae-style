using System;
using System.Collections.Generic;
using Game.HexGrid;

namespace Game.Ai.V2
{
    public interface IMissionStepPayload { }

    public enum MissionStepDisposition { Progress, Completed, Waiting, Replan, Invalidated, PermanentFailure }

    // Common step lifecycle. A completed sub-leg is never itself authority to retire a campaign.
    // Domain facts are typed and stored once. Legacy outcomes project into the same payloads.
    public abstract class MissionStepResult
    {
        // Compatibility views share this exact record. No fact or payload is copied or mirrored.
        private sealed class Facts
        {
            internal MissionIntentKey IntentKey;
            internal MissionStepDisposition Disposition = MissionStepDisposition.Completed;
            internal float ApSpent;
            internal int? MoverArmyId;
            internal StableMissionKey AttemptKey;
            internal MissionProposal Proposal;
            internal bool WasCommitment;
            internal bool ObjectiveSatisfied;
            // Another action satisfied a waypoint; a durable Recon role may still continue.
            internal bool ObjectiveSatisfiedExternally;
            internal bool MadeProgress;
            internal int StepsMoved;
            internal DeferReason? AllocationDeferReason;
            internal ProvisionFailureKind? ProvisionFailureKindValue;
            // Execution fact retained for domain route-failure policy; null before Execution.
            internal ExecutionStopReason? StopReason;
            // Actual post-step location, including Economy arrival/holding evidence.
            internal HexCoord FinalHex;
            internal MissionKind MissionKind = MissionKind.Scout;
            internal IReadOnlyList<WorldDelta> WorldDeltas = Array.Empty<WorldDelta>();
            internal int StateVersionAfter = -1;
        }
        private readonly Facts _facts;
        private readonly Dictionary<Type, IMissionStepPayload> _payloads;
        protected MissionStepResult()
        {
            _facts = new Facts();
            _payloads = new Dictionary<Type, IMissionStepPayload>();
        }
        protected MissionStepResult(MissionStepResult source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            _facts = source._facts;
            _payloads = source._payloads;
        }
        public MissionIntentKey IntentKey { get => _facts.IntentKey; set => _facts.IntentKey = value; }
        public MissionStepDisposition Disposition { get => _facts.Disposition; set => _facts.Disposition = value; }
        public float ApSpent { get => _facts.ApSpent; set => _facts.ApSpent = value; }
        public int? MoverArmyId { get => _facts.MoverArmyId; set => _facts.MoverArmyId = value; }
        public StableMissionKey AttemptKey { get => _facts.AttemptKey; set => _facts.AttemptKey = value; }
        public MissionProposal Proposal { get => _facts.Proposal; set => _facts.Proposal = value; }
        public bool WasCommitment { get => _facts.WasCommitment; set => _facts.WasCommitment = value; }
        public bool ObjectiveSatisfied { get => _facts.ObjectiveSatisfied; set => _facts.ObjectiveSatisfied = value; }
        public bool ObjectiveSatisfiedExternally { get => _facts.ObjectiveSatisfiedExternally; set => _facts.ObjectiveSatisfiedExternally = value; }
        public bool MadeProgress { get => _facts.MadeProgress; set => _facts.MadeProgress = value; }
        public int StepsMoved { get => _facts.StepsMoved; set => _facts.StepsMoved = value; }
        public DeferReason? AllocationDeferReason { get => _facts.AllocationDeferReason; set => _facts.AllocationDeferReason = value; }
        public ProvisionFailureKind? ProvisionFailureKindValue { get => _facts.ProvisionFailureKindValue; set => _facts.ProvisionFailureKindValue = value; }
        public ExecutionStopReason? StopReason { get => _facts.StopReason; set => _facts.StopReason = value; }
        public HexCoord FinalHex { get => _facts.FinalHex; set => _facts.FinalHex = value; }
        public MissionKind MissionKind { get => _facts.MissionKind; set => _facts.MissionKind = value; }
        // Read-only receipts of already-published observations, not pending invalidations or a
        // replay instruction. Settlement must never apply them or advance the revision again.
        internal IReadOnlyList<WorldDelta> WorldDeltas { get => _facts.WorldDeltas; set => _facts.WorldDeltas = value; }
        public int StateVersionAfter { get => _facts.StateVersionAfter; set => _facts.StateVersionAfter = value; }

        // Read access never creates a payload or changes provisioned-presence evidence.
        public T GetPayload<T>() where T : class, IMissionStepPayload =>
            _payloads.TryGetValue(typeof(T), out var payload) ? (T)payload : null;
        public void SetPayload<T>(T payload) where T : class, IMissionStepPayload
        {
            if (payload == null) _payloads.Remove(typeof(T));
            else _payloads[typeof(T)] = payload;
        }
        internal T PayloadForWrite<T>() where T : class, IMissionStepPayload, new()
        {
            T payload = GetPayload<T>();
            if (payload == null) SetPayload(payload = new T());
            return payload;
        }
    }
    // New domains can return a typed payload without editing the common result schema.
    public sealed class MissionStepResult<TPayload> : MissionStepResult
        where TPayload : class, IMissionStepPayload
    {
        public TPayload Payload => GetPayload<TPayload>();
        public MissionStepResult(MissionIntentKey operation, MissionStepDisposition disposition, TPayload payload)
        { IntentKey = operation; MissionKind = operation.Kind; Disposition = disposition; SetPayload(payload); }
    }
}
