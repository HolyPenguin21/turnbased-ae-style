using System;
using System.Collections.Generic;
using System.Linq;
using Game.Players;

namespace Game.Ai.V2
{
    // Correlates facts only; result/domain interpretation belongs to the result boundary.
        internal sealed class MissionStepFacts
        {
            public MissionProposal Proposal;
            public bool WasCommitment;
            public ProvisionedMission Provisioned;
            public ProvisionFailure? PendingFailure;
            public ExecutionResult Execution;
            public DeferReason? Deferred;
            public bool LiveSatisfiedOverride;
        }


    public sealed class MissionOutcomeLedger
    {
        private readonly Dictionary<StableMissionKey, MissionStepFacts> _rows = new Dictionary<StableMissionKey, MissionStepFacts>();

        private MissionStepFacts MissionStepFactsFor(MissionProposal m)
        {
            StableMissionKey k = StableMissionKey.For(m);
            if (!_rows.TryGetValue(k, out MissionStepFacts r))
                _rows[k] = r = new MissionStepFacts();
            if (r.Proposal != null && !ReferenceEquals(r.Proposal, m)
                && !string.Equals(r.Proposal.AttemptId, m?.AttemptId, StringComparison.Ordinal))
                AiV2Trace.CheckError(m?.AttemptId, "DuplicateStableMissionKey",
                    $"key={k} existingAttempt={r.Proposal.AttemptId ?? "?"} incomingAttempt={m?.AttemptId ?? "?"}");
            if (r.Proposal == null) r.Proposal = m;
            return r;
        }

        public void RegisterProposals(IEnumerable<MissionProposal> missions)
        {
            if (missions == null) return;
            foreach (MissionProposal m in missions)
                if (m != null) MissionStepFactsFor(m);
        }

        public void RegisterCommitments(IEnumerable<Commitment> commitments)
        {
            if (commitments == null) return;
            foreach (Commitment c in commitments)
                if (c?.Mission != null) MissionStepFactsFor(c.Mission).WasCommitment = true;
        }

        public void RecordProvisionSuccess(MissionProposal m, ProvisionedMission pm)
        {
            MissionStepFacts r = MissionStepFactsFor(m);
            r.Provisioned = pm;
            r.PendingFailure = null;
        }

        public void RecordProvisionFailure(MissionProposal m, ProvisionFailure f)
        {
            MissionStepFacts r = MissionStepFactsFor(m);
            if (r.Provisioned == null)
                r.PendingFailure = f;
        }

        public void RecordExecution(ExecutionResult result)
        {
            if (result == null) return;
            if (!_rows.TryGetValue(result.Key, out MissionStepFacts r))
            {
                AiV2Trace.CheckError(result.Source?.Mission?.AttemptId, "ExecutionWithoutRegisteredProposal",
                    $"stableKey={result.Key} (execution result ignored — no ledger row)");
                return;
            }
            r.Execution = result;
        }

        public void RecordDeferrals(IEnumerable<DeferredEntry> deferred)
        {
            if (deferred == null) return;
            foreach (DeferredEntry d in deferred)
            {
                if (d?.Mission == null) continue;
                if (_rows.TryGetValue(StableMissionKey.For(d.Mission), out MissionStepFacts r) && r.Provisioned == null)
                    r.Deferred = d.Reason;
            }
        }

        public void RefreshObjectiveStatesLive(PlayerSetupData player) =>
            MissionStepResultPolicy.RefreshObjectiveStatesLive(_rows.Values, player);

        // Canonical operational ingress; compatibility Finalize exposes the same normalized objects.
        public IReadOnlyList<MissionStepResult> FinalizeSteps() => Finalize();

        public List<MissionTurnOutcome> Finalize()
        {
            var list = new List<MissionTurnOutcome>();
            foreach (KeyValuePair<StableMissionKey, MissionStepFacts> kv in _rows)
            {
                MissionStepFacts r = kv.Value;
                if (r.Proposal == null)
                    continue;

                MissionTurnOutcome o = MissionStepResultPolicy.Normalize(kv.Key, r);
                list.Add(o);
            }
            return list;
        }

    }
}
