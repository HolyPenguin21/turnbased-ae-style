using System;
using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ExecutionOutcome, MissionTurnOutcome, MissionOutcomeLedger.
    // File-split (mechanical, no behaviour change) from MissionIntent.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 3. Independent standalone types,
    // not a partial class.
    public enum ExecutionOutcome { Completed, ProductiveStop, Blocked, Failed }

    public sealed class MissionTurnOutcome : MissionStepResult
    {
        public StableMissionKey AttemptKey;
        public MissionProposal Proposal;
        public bool WasCommitment;
        public ExecutionOutcome Outcome
        {
            get => Disposition == MissionStepDisposition.Completed ? ExecutionOutcome.Completed
                : Disposition == MissionStepDisposition.Progress ? ExecutionOutcome.ProductiveStop
                : Disposition == MissionStepDisposition.Waiting || Disposition == MissionStepDisposition.Replan
                    ? ExecutionOutcome.Blocked : ExecutionOutcome.Failed;
            set => Disposition = value == ExecutionOutcome.Completed ? MissionStepDisposition.Completed
                : value == ExecutionOutcome.ProductiveStop ? MissionStepDisposition.Progress
                : value == ExecutionOutcome.Blocked ? MissionStepDisposition.Waiting
                : Disposition == MissionStepDisposition.PermanentFailure
                    ? MissionStepDisposition.PermanentFailure : MissionStepDisposition.Invalidated;
        }
        public bool ObjectiveSatisfied;
        // Review P1 #1/#2 — the objective was met by something OTHER than this actor's own
        // execution reaching its goal: another action opened the hex mid-turn (live pass), or
        // provisioning found it already satisfied (ProvisionFailureKind.TargetSatisfied). For a
        // durable Explore/Refresh ground scout that is a satisfied WAYPOINT, not a finished role,
        // so ReconcileAfterTurn keeps the intent and re-focuses it next turn — mirroring the
        // own-execution ExecutionResult.DurableRoleContinues path.
        public bool ObjectiveSatisfiedExternally;
        public bool StructuralFailure
        {
            get => Disposition == MissionStepDisposition.PermanentFailure;
            set
            {
                if (value) Disposition = MissionStepDisposition.PermanentFailure;
                else if (Disposition == MissionStepDisposition.PermanentFailure)
                    Disposition = MissionStepDisposition.Invalidated;
            }
        }
        public bool MadeProgress;
        public int StepsMoved;
        public DeferReason? AllocationDeferReason;
        public ProvisionFailureKind? ProvisionFailureKindValue;
        // The executor's stop reason when the mission reached Execution (null otherwise).
        // Continuity reads it to tell a proven route failure from a transient stop.
        public ExecutionStopReason? StopReason;
        public ScoutTargetKind ScoutKind;
        public bool ScoutRequiresStealth;   // AI-RECON-02 — provisioned Scout requirement was a stealth one
        public HexCoord FocusHex;
        // The mover's actual hex after this turn's execution step — set from
        // ExecutionResult.FinalHex. Continuity reads it to tell "arrived at target this turn"
        // apart from "still en route" (e.g. EconomyIntent.ArrivalTurn for MobileCollection).
        public HexCoord FinalHex;
        public bool HasScoutPayload;
        public MissionKind MissionKind = MissionKind.Scout;
        public bool HasRaidPayload;
        // Single source of truth for the target of this outcome. RaidTargetArmyId below is a
        // read-only projection for existing non-Raid/logging readers — never a second settable copy.
        public RaidTargetRef RaidTarget;
        public int RaidTargetArmyId => RaidTarget.Kind == RaidTargetKind.NeutralArmy ? RaidTarget.ArmyId : 0;
        public HexCoord RaidLastKnownHex;
        public bool RaidTargetIsNeutral;
        public bool OperationStarted;
        // Exact provisioned leg/actors plus the execution-time handoff boundary. Continuity uses
        // these immutable facts instead of inspecting RaidIntent.Phase after Execution may already
        // have advanced it.
        public RaidMissionPhase RaidPhase;
        public int? RaidPrimaryArmyId;
        public int? RaidSupportArmyId;
        public int? RaidAirSupportArmyId;
        public HexCoord? RaidAirSupportLandingHex;
        public bool RaidAirSupportStrikeSucceeded;
        public bool ReinforcementHandoffAttempted;
        public RaidRefitAction RaidRefitAction;
        public bool RaidRefitSucceeded;
        public ResourceVector RaidResourcesSpent;
        public bool HasAttackPayload;
        // ATK §69 — the whole provisioned Attack leg, carried as ONE frozen object so Continuity
        // reads immutable execution facts instead of inspecting an intent Execution may already
        // have advanced.
        public AttackMissionTarget AttackTarget;
        // ATK §17 — this Attack actually spent its one opportunistic side strike this step.
        public bool AttackOpportunisticStrike;
        public bool AttackIntermediateCaptured;
        public bool AttackCaptureHadBattle;
        public bool HasActiveDefencePayload;
        public ActiveDefenceMissionTarget ActiveDefenceTarget;
        public bool HasEconomyPayload;
        public EconomyMissionTarget EconomyTarget;
        public bool HasDevelopmentPayload;
        public DevelopmentMissionTarget DevelopmentTarget;
        public bool EconomyBuildCompleted;
        public MissionIntentKey? EconomyLoanSource;
    }

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

        // Compatibility delegate: domain objective interpretation is outside telemetry storage.
        internal static bool EconomyObjectiveSatisfied(PlayerSetupData player, EconomyMissionTarget target) =>
            MissionStepResultPolicy.EconomyObjectiveSatisfied(player, target);

    }
}
