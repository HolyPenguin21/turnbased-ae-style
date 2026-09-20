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

    public sealed class MissionTurnOutcome
    {
        public StableMissionKey AttemptKey;
        public MissionIntentKey IntentKey;
        public MissionProposal Proposal;
        public bool WasCommitment;
        public ExecutionOutcome Outcome;
        public bool ObjectiveSatisfied;
        // Review P1 #1/#2 — the objective was met by something OTHER than this actor's own
        // execution reaching its goal: another action opened the hex mid-turn (live pass), or
        // provisioning found it already satisfied (ProvisionFailureKind.TargetSatisfied). For a
        // durable Explore/Refresh ground scout that is a satisfied WAYPOINT, not a finished role,
        // so ReconcileAfterTurn keeps the intent and re-focuses it next turn — mirroring the
        // own-execution ExecutionResult.DurableRoleContinues path.
        public bool ObjectiveSatisfiedExternally;
        public bool StructuralFailure;
        public bool MadeProgress;
        public int StepsMoved;
        public float ApSpent;
        public int? MoverArmyId;
        public DeferReason? AllocationDeferReason;
        public ProvisionFailureKind? ProvisionFailureKindValue;
        public ScoutTargetKind ScoutKind;
        public bool ScoutRequiresStealth;   // AI-RECON-02 — provisioned Scout requirement was a stealth one
        public HexCoord FocusHex;
        // The mover's actual hex after this turn's execution step — set from
        // ExecutionResult.FinalHex. Continuity reads it to tell "arrived at target this turn"
        // apart from "still en route" (e.g. EconomyIntent.ArrivalTurn for MobileCollection).
        public HexCoord FinalHex;
        public int? TrackedArmyId;
        public int BaselineObservedTurn;
        public bool HasScoutPayload;
        public MissionKind MissionKind = MissionKind.Scout;
        public bool HasRaidPayload;
        // Single source of truth for the target of this outcome. RaidTargetArmyId below is a
        // read-only projection for existing non-Raid/logging readers — never a second settable copy.
        public RaidTargetRef RaidTarget;
        public int RaidTargetArmyId => RaidTarget.Kind == RaidTargetKind.NeutralArmy ? RaidTarget.ArmyId : 0;
        public HexCoord RaidLastKnownHex;
        public bool RaidTargetIsNeutral;
        public bool RaidOperationStarted;
        // Exact provisioned leg/actors plus the execution-time handoff boundary. Continuity uses
        // these immutable facts instead of inspecting RaidIntent.Phase after Execution may already
        // have advanced it.
        public RaidMissionPhase RaidPhase;
        public int? RaidPrimaryArmyId;
        public int? RaidSupportArmyId;
        public int? RaidAirSupportArmyId;
        public HexCoord? RaidAirSupportLandingHex;
        public bool RaidAirSupportStrikeSucceeded;
        public bool RaidReinforcementHandoffAttempted;
        public RaidRefitAction RaidRefitAction;
        public bool RaidRefitSucceeded;
        public ResourceVector RaidResourcesSpent;
        public bool HasEconomyPayload;
        public EconomyMissionTarget EconomyTarget;
        public bool HasDevelopmentPayload;
        public DevelopmentMissionTarget DevelopmentTarget;
        public bool EconomyBuildCompleted;
        public MissionIntentKey? EconomyLoanSource;
    }

    public sealed class MissionOutcomeLedger
    {
        private sealed class Row
        {
            public MissionProposal Proposal;
            public bool WasCommitment;
            public ProvisionedMission Provisioned;
            public ProvisionFailure? PendingFailure;
            public ExecutionResult Execution;
            public DeferReason? Deferred;
            public bool LiveSatisfiedOverride;
        }

        private readonly Dictionary<StableMissionKey, Row> _rows = new Dictionary<StableMissionKey, Row>();

        private Row RowFor(MissionProposal m)
        {
            StableMissionKey k = StableMissionKey.For(m);
            if (!_rows.TryGetValue(k, out Row r))
                _rows[k] = r = new Row();
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
                if (m != null) RowFor(m);
        }

        public void RegisterCommitments(IEnumerable<Commitment> commitments)
        {
            if (commitments == null) return;
            foreach (Commitment c in commitments)
                if (c?.Mission != null) RowFor(c.Mission).WasCommitment = true;
        }

        public void RecordProvisionSuccess(MissionProposal m, ProvisionedMission pm)
        {
            Row r = RowFor(m);
            r.Provisioned = pm;
            r.PendingFailure = null;
        }

        public void RecordProvisionFailure(MissionProposal m, ProvisionFailure f)
        {
            Row r = RowFor(m);
            if (r.Provisioned == null)
                r.PendingFailure = f;
        }

        public void RecordExecution(ExecutionResult result)
        {
            if (result == null) return;
            if (!_rows.TryGetValue(result.Key, out Row r))
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
                if (_rows.TryGetValue(StableMissionKey.For(d.Mission), out Row r) && r.Provisioned == null)
                    r.Deferred = d.Reason;
            }
        }

        public void RefreshObjectiveStatesLive(PlayerSetupData player)
        {
            foreach (Row r in _rows.Values)
            {
                if (r.Proposal == null || r.Provisioned == null)
                    continue;
                if (r.Execution != null && r.Execution.ReachedGoal)
                    continue;
                ProvisionedMission pm = r.Provisioned;
                bool satisfied;
                if (pm.Kind == MissionKind.Raid)
                {
                    // AGG-RAID §9/§10 — only the ASSAULT leg's objective is the target army. A
                    // Reinforcement convoy or a Return march must never be reported as "objective
                    // already met" just because the (by definition already dead) previous target no
                    // longer exists — that would retire the whole operation mid-leg.
                    satisfied = pm.RaidPhase == RaidMissionPhase.Assault
                        && RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, pm.RaidTarget);
                }
                else if (pm.Kind == MissionKind.Economy)
                {
                    satisfied = EconomyObjectiveSatisfied(player, pm.EconomyTarget);
                }
                else if (pm.Kind == MissionKind.Development)
                {
                    satisfied = ResearchProductionSystem.ActorStillQualifies(player,
                        pm.DevelopmentTarget.Hero, pm.DevelopmentTarget.FacilityHex,
                        pm.DevelopmentTarget.Mode)
                        && ResearchProductionSystem.IsEligible(player,
                            pm.DevelopmentTarget.FacilityHex, pm.DevelopmentTarget.Mode, out _);
                }
                else if (pm.ScoutKind == ScoutTargetKind.Surveil)
                {
                    satisfied = ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex,
                        pm.TrackedArmyId, pm.BaselineObservedTurn);
                }
                else if (ReconScoutKinds.IsRefresh(pm.ScoutKind))
                {
                    satisfied = ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, pm.FocusHex);
                }
                else
                {
                    satisfied = ScoutObjectiveEvaluator.IsExploreSatisfiedLive(player, pm.FocusHex);
                }

                if (satisfied)
                {
                    r.LiveSatisfiedOverride = true;
                    AiDebugLog.Write($"[AI][V2] ledger — [{r.Proposal.AttemptId}] {MissionIntentKey.For(r.Proposal)} objective met by "
                        + "another action this turn (post-execution live pass)");
                }
            }
        }

        public List<MissionTurnOutcome> Finalize()
        {
            var list = new List<MissionTurnOutcome>();
            foreach (KeyValuePair<StableMissionKey, Row> kv in _rows)
            {
                Row r = kv.Value;
                if (r.Proposal == null)
                    continue;

                var o = new MissionTurnOutcome
                {
                    AttemptKey = kv.Key,
                    IntentKey = MissionIntentKey.For(r.Proposal),
                    Proposal = r.Proposal,
                    WasCommitment = r.WasCommitment,
                };
                o.MissionKind = r.Proposal.Kind;

                if (r.Provisioned != null)
                {
                    o.MoverArmyId = r.Provisioned.MoverArmyId;
                    if (r.Provisioned.Kind == MissionKind.Raid)
                    {
                        o.HasRaidPayload = true;
                        o.RaidTarget = r.Provisioned.RaidTarget;
                        o.RaidLastKnownHex = r.Provisioned.RaidLastKnownHex;
                        o.RaidTargetIsNeutral = r.Provisioned.RaidTargetIsNeutral;
                        o.RaidPhase = r.Provisioned.RaidPhase;
                        o.RaidPrimaryArmyId = r.Provisioned.RaidPrimaryArmyId;
                        o.RaidSupportArmyId = r.Provisioned.RaidSupportArmyId;
                        o.RaidAirSupportArmyId = r.Provisioned.RaidAirSupportArmyId;
                        o.RaidAirSupportLandingHex = r.Provisioned.RaidAirSupportLandingHex;
                        o.RaidRefitAction = r.Provisioned.RaidRefitAction;
                    }
                    else if (r.Provisioned.Kind == MissionKind.Economy)
                    {
                        o.HasEconomyPayload = true;
                        o.EconomyTarget = r.Provisioned.EconomyTarget;
                        o.EconomyLoanSource = r.Provisioned.EconomyLoanSource;
                    }
                    else if (r.Provisioned.Kind == MissionKind.Development)
                    {
                        o.HasDevelopmentPayload = true;
                        o.DevelopmentTarget = r.Provisioned.DevelopmentTarget;
                    }
                    else
                    {
                        o.HasScoutPayload = true;
                        o.ScoutKind = r.Provisioned.ScoutKind;
                        o.ScoutRequiresStealth = r.Provisioned.RequiresStealth;
                        o.FocusHex = r.Provisioned.FocusHex;
                        o.TrackedArmyId = r.Provisioned.TrackedArmyId;
                        o.BaselineObservedTurn = r.Provisioned.BaselineObservedTurn;
                    }
                }

                if (r.Execution != null)
                {
                    ExecutionResult e = r.Execution;
                    o.StepsMoved = e.StepsMoved;
                    o.ApSpent = e.ApSpent;
                    o.FinalHex = e.FinalHex;
                    // RECON-AIR-06 — an AirLaunch mission was bound at Assignment time to a
                    // synthetic per-airfield actor id (no ArmyData existed yet); once execution
                    // actually launched the aircraft, ActualActorArmyId carries the REAL ArmyId, and
                    // that is what MissionContinuity must track from now on, not the synthetic key.
                    if (e.ActualActorArmyId.HasValue)
                        o.MoverArmyId = e.ActualActorArmyId;
                    bool raidEngaged = o.MissionKind == MissionKind.Raid
                        && (e.StopReason == ExecutionStopReason.BattleStarted
                            || e.StopReason == ExecutionStopReason.HexEventStarted);
                    // Extraction and direct-army preparation are distinct productive mutations:
                    // the former creates the actor, the latter changes its roster and/or donor intent.
                    o.MadeProgress = e.StepsMoved > 0 || e.EnteredStealth
                        || e.InfrastructureChanged || e.CombatChanged
                        || e.RaidOperationStarted || raidEngaged
                        || e.ActorMaterialized || e.EconomyPrepared;
                    if (o.MissionKind == MissionKind.Raid)
                    {
                        o.RaidOperationStarted = e.RaidOperationStarted
                            || e.StepsMoved > 0 || raidEngaged;
                        o.RaidReinforcementHandoffAttempted =
                            e.RaidReinforcementHandoffAttempted;
                        o.RaidAirSupportStrikeSucceeded =
                            e.RaidAirSupportStrikeSucceeded;
                        o.RaidRefitSucceeded = e.RaidRefitSucceeded;
                        o.RaidResourcesSpent = e.ResourcesSpent;
                    }
                    if (o.MissionKind == MissionKind.Economy)
                        o.EconomyBuildCompleted = e.InfrastructureChanged;
                    Classify(e, o);
                }
                else if (r.PendingFailure.HasValue)
                {
                    o.ProvisionFailureKindValue = r.PendingFailure.Value.Kind;
                    ClassifyProvisionFailure(r.PendingFailure.Value, o);
                }
                else
                {
                    o.AllocationDeferReason = r.Deferred;
                    o.Outcome = ExecutionOutcome.Blocked;
                }

                if (r.LiveSatisfiedOverride)
                {
                    o.Outcome = ExecutionOutcome.Completed;
                    o.ObjectiveSatisfied = true;
                    o.ObjectiveSatisfiedExternally = true;
                    o.StructuralFailure = false;
                }

                list.Add(o);
            }
            return list;
        }

        private static void Classify(ExecutionResult e, MissionTurnOutcome o)
        {
            if (e.ReachedGoal)
            {
                // Spec §1 (review P1 #1) — a satisfied WAYPOINT for an actor whose durable
                // Explore/Refresh role is still runnable is a ProductiveStop, not a Completed
                // objective: the MissionIntent is kept and re-focused next turn rather than retired.
                if (e.DurableRoleContinues)
                {
                    o.Outcome = ExecutionOutcome.ProductiveStop;
                    o.MadeProgress = true;
                    return;
                }
                o.Outcome = ExecutionOutcome.Completed;
                o.ObjectiveSatisfied = true;
                return;
            }

            if (o.MissionKind == MissionKind.Raid)
            {
                switch (e.StopReason)
                {
                    case ExecutionStopReason.BattleStarted:
                    case ExecutionStopReason.HexEventStarted:
                    case ExecutionStopReason.OutOfMovement:
                    case ExecutionStopReason.EnemyDiscovered:
                    case ExecutionStopReason.NeutralDiscovered:
                    case ExecutionStopReason.StepCompleted:
                        o.Outcome = ExecutionOutcome.ProductiveStop;
                        break;
                    case ExecutionStopReason.NoSafeStep:
                    case ExecutionStopReason.MoveRejected:
                        o.Outcome = ExecutionOutcome.Blocked;
                        break;
                    case ExecutionStopReason.MoverLost:
                    case ExecutionStopReason.TargetInvalidated:
                        // Reinforcement and SupportReturn are support-mover legs of a durable Raid.
                        // A missing/stale support must not retire the primary campaign; ResolveActive
                        // owns the canonical support cleanup/replacement on the next reaction pass.
                        // The same stop remains fatal for Assault/Return where the mover is primary.
                        o.Outcome = o.HasRaidPayload
                            && (o.RaidPhase == RaidMissionPhase.AirSupport
                                || o.RaidPhase == RaidMissionPhase.Reinforcement
                                || o.RaidPhase == RaidMissionPhase.SupportReturn
                                || o.RaidPhase == RaidMissionPhase.Refit)
                            ? ExecutionOutcome.Blocked
                            : ExecutionOutcome.Failed;
                        break;
                    default:
                        o.Outcome = ExecutionOutcome.Failed;
                        break;
                }
                return;
            }

            if (o.MissionKind == MissionKind.Economy)
            {
                // A committed roster mutation remains progress if its pinned tail became stale.
                if (e.EconomyPrepared && e.StopReason == ExecutionStopReason.TargetInvalidated)
                {
                    o.Outcome = ExecutionOutcome.ProductiveStop;
                    return;
                }
                switch (e.StopReason)
                {
                    case ExecutionStopReason.StepCompleted:
                    case ExecutionStopReason.OutOfMovement:
                        o.Outcome = ExecutionOutcome.ProductiveStop;
                        break;
                    case ExecutionStopReason.NoSafeStep:
                    case ExecutionStopReason.MoveRejected:
                        o.Outcome = ExecutionOutcome.Blocked;
                        break;
                    default:
                        o.Outcome = ExecutionOutcome.Failed;
                        break;
                }
                return;
            }

            if (o.MissionKind == MissionKind.Development)
            {
                switch (e.StopReason)
                {
                    case ExecutionStopReason.StepCompleted:
                    case ExecutionStopReason.OutOfMovement:
                        o.Outcome = ExecutionOutcome.ProductiveStop;
                        break;
                    case ExecutionStopReason.NoSafeStep:
                    case ExecutionStopReason.MoveRejected:
                    case ExecutionStopReason.BattleStarted:
                    case ExecutionStopReason.HexEventStarted:
                        o.Outcome = ExecutionOutcome.Blocked;
                        break;
                    default:
                        o.Outcome = ExecutionOutcome.Failed;
                        break;
                }
                return;
            }

            switch (e.StopReason)
            {
                case ExecutionStopReason.OutOfMovement:
                case ExecutionStopReason.EnemyDiscovered:
                case ExecutionStopReason.NeutralDiscovered:
                case ExecutionStopReason.StepCompleted:
                    o.Outcome = ExecutionOutcome.ProductiveStop;
                    break;
                case ExecutionStopReason.HexEventStarted:
                case ExecutionStopReason.BattleStarted:
                    // Spec §2 — an ordinary hex event or battle interruption is NOT a structural
                    // Recon failure. A scout that moved / entered stealth / made a discovery before
                    // the interruption made productive progress and keeps its durable role. Only a
                    // scout that was ALREADY combat-locked before it could take a single step
                    // (BlockedBeforeMovement, no progress) is a recoverable Blocked.
                    o.Outcome = (e.BlockedBeforeMovement && !o.MadeProgress)
                        ? ExecutionOutcome.Blocked
                        : ExecutionOutcome.ProductiveStop;
                    if (o.Outcome == ExecutionOutcome.ProductiveStop)
                        o.MadeProgress = true;
                    break;
                case ExecutionStopReason.NoSafeStep:
                case ExecutionStopReason.MoveRejected:
                case ExecutionStopReason.RequiredStealthUnavailable:
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
                default:
                    o.Outcome = ExecutionOutcome.Failed;
                    break;
            }
        }

        internal static bool EconomyObjectiveSatisfied(PlayerSetupData player, EconomyMissionTarget t)
        {
            if (t.Kind == EconomyTaskKind.MobileCollection)
                return false;
            if (t.Kind == EconomyTaskKind.ReturnCollector)
                return t.CollectorArmyId.HasValue && ArmyRegistry.AllForOwner(player).Any(a => a != null
                    && a.Id == t.CollectorArmyId.Value && a.Owner == player
                    && a.Hex.Equals(t.TargetHex));
            if (t.Kind == EconomyTaskKind.ReturnBuilder)
                return t.BuilderArmyId.HasValue && ArmyRegistry.AllForOwner(player).Any(a => a != null
                    && a.Id == t.BuilderArmyId.Value && a.Owner == player
                    && a.Hex.Equals(t.TargetHex));
            BuildingData b = BuildingRegistry.AllBuildings().FirstOrDefault(x => x != null
                && x.Owner == player && x.Hex.Equals(t.TargetHex));
            if (t.Kind == EconomyTaskKind.FoundBase)
                return b != null && b.IsBase;
            return b != null && t.ResourceType.HasValue
                && b.HasFacilityWithAbility(UnitAbilities.CollectAbilityFor(t.ResourceType.Value));
        }

        private static void ClassifyProvisionFailure(ProvisionFailure f, MissionTurnOutcome o)
        {
            switch (f.Kind)
            {
                case ProvisionFailureKind.NoMoverExists:
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
                case ProvisionFailureKind.NoObservationVantage:
                case ProvisionFailureKind.AssemblyInfeasible:
                    o.Outcome = ExecutionOutcome.Failed;
                    o.StructuralFailure = true;
                    break;
                case ProvisionFailureKind.TargetSatisfied:
                    // SupportReturn is a sub-leg of one durable Raid campaign. ProvisionReturn can
                    // discover that the support is already standing at its fixed home before an
                    // executor is ever created. Reporting that as Completed+ObjectiveSatisfied
                    // would make generic continuity retire the WHOLE Raid. Keep the campaign alive;
                    // ResolveActive owns the canonical CompleteRaidSupportReturn transition and will
                    // consume this already-home fact on the next reconciliation/reaction pass.
                    if (o.MissionKind == MissionKind.Raid
                        && o.Proposal?.Target is RaidMissionTarget raidTarget
                        && (raidTarget.Phase == RaidMissionPhase.SupportReturn
                            || raidTarget.Phase == RaidMissionPhase.RecoveryReturn))
                    {
                        o.Outcome = ExecutionOutcome.ProductiveStop;
                        o.MadeProgress = true;
                        break;
                    }
                    // Review P1 #2 — provisioning short-circuited because the focus hex was
                    // already visited/refreshed by an earlier action this turn. No mover was
                    // assigned; the durable actor lives on the existing MissionIntent, so mark
                    // this as an external satisfaction and let ReconcileAfterTurn keep the
                    // Explore/Refresh intent for re-focus instead of retiring it.
                    o.Outcome = ExecutionOutcome.Completed;
                    o.ObjectiveSatisfied = true;
                    o.ObjectiveSatisfiedExternally = true;
                    break;
                case ProvisionFailureKind.TargetInvalidated:
                    // ProvisionReturn uses TargetInvalidated when the SupportReturn mover vanished
                    // or stopped being a usable field army. That invalidates only the support leg,
                    // not the durable primary Raid. Return/Assault/Reinforcement target invalidation
                    // keeps its existing failure semantics (notably a lost primary in Reinforcement).
                    if (o.MissionKind == MissionKind.Raid
                        && o.Proposal?.Target is RaidMissionTarget invalidRaidTarget
                        && (invalidRaidTarget.Phase == RaidMissionPhase.SupportReturn
                            || invalidRaidTarget.Phase == RaidMissionPhase.Refit))
                    {
                        o.Outcome = ExecutionOutcome.Blocked;
                        break;
                    }
                    o.Outcome = ExecutionOutcome.Failed;
                    break;
                default:
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
            }
        }
    }
}
