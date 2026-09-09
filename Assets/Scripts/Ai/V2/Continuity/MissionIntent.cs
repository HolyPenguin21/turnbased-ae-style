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
    // ===========================================================================================
    //  MISSION CONTINUITY  (Strategy V2 build-order step 7)
    // ===========================================================================================
    //  Intent identity is separate from a turn's proposal. Recon sub-kind is part of that identity:
    //  Explore(hex), Refresh(hex), and Surveil(army) must never collapse onto the same ledger row.
    // ===========================================================================================

    public enum CommitmentTier { None, Soft, Hard }
    public enum IntentStatus { Active, Suspended }
    public enum SuspendReason { None, Siege, PoolExhausted, CapabilityUnavailable, EconomyLoan }

    public readonly struct MissionIntentKey : IEquatable<MissionIntentKey>, IComparable<MissionIntentKey>
    {
        public readonly MissionKind Kind;
        public readonly int SubKind;
        public readonly int ObjectiveId;
        public readonly int Q, R;

        public MissionIntentKey(MissionKind kind, int subKind, int objectiveId, int q, int r)
        {
            Kind = kind; SubKind = subKind; ObjectiveId = objectiveId; Q = q; R = r;
        }

        public static MissionIntentKey For(MissionProposal m)
        {
            if (m != null && m.Kind == MissionKind.Scout && m.Target is ScoutMissionTarget t)
                return ForScoutTarget(t);
            if (m != null && m.Kind == MissionKind.Raid && m.Target is RaidMissionTarget rt)
                return new MissionIntentKey(MissionKind.Raid, (int)AggressionObjectiveKind.Raid, rt.TargetArmyId, 0, 0);
            if (m != null && m.Kind == MissionKind.Economy && m.Target is EconomyMissionTarget et)
                return new MissionIntentKey(MissionKind.Economy, (int)et.Kind,
                    et.Kind == EconomyTaskKind.ReturnBuilder
                        ? et.BuilderArmyId ?? 0
                        : et.ResourceType.HasValue ? (int)et.ResourceType.Value + 1 : 0,
                    et.TargetHex.Q, et.TargetHex.R);
            return new MissionIntentKey(m?.Kind ?? MissionKind.Scout, 0, 0, 0, 0);
        }

        public static MissionIntentKey ForScoutTarget(ScoutMissionTarget t)
        {
            if (t.Kind == ScoutTargetKind.Surveil)
                return new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil,
                    t.Contact?.Army?.ArmyId ?? 0, 0, 0);
            return new MissionIntentKey(MissionKind.Scout, (int)t.Kind, 0, t.FocusHex.Q, t.FocusHex.R);
        }

        public static MissionIntentKey For(MissionIntent intent)
        {
            RaidIntent ri = intent?.Raid;
            if (ri != null)
                return new MissionIntentKey(MissionKind.Raid, (int)AggressionObjectiveKind.Raid, ri.TargetArmyId, 0, 0);
            EconomyIntent ei = intent?.Economy;
            if (ei != null)
                return new MissionIntentKey(MissionKind.Economy, (int)ei.Kind,
                    ei.Kind == EconomyTaskKind.ReturnBuilder
                        ? ei.BuilderArmyId ?? intent?.PreferredMoverArmyId ?? 0
                        : ei.ResourceType.HasValue ? (int)ei.ResourceType.Value + 1 : 0,
                    ei.TargetHex.Q, ei.TargetHex.R);
            ScoutIntent s = intent?.Scout;
            if (s == null)
                return new MissionIntentKey(intent?.Kind ?? MissionKind.Scout, 0, 0, 0, 0);
            if (s.Kind == ScoutTargetKind.Surveil)
                return new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil,
                    s.TrackedArmyId ?? 0, 0, 0);
            return new MissionIntentKey(MissionKind.Scout, (int)s.Kind, 0, s.FocusHex.Q, s.FocusHex.R);
        }

        public bool Equals(MissionIntentKey o) =>
            Kind == o.Kind && SubKind == o.SubKind && ObjectiveId == o.ObjectiveId && Q == o.Q && R == o.R;
        public override bool Equals(object obj) => obj is MissionIntentKey o && Equals(o);
        public override int GetHashCode() => ((int)Kind, SubKind, ObjectiveId, Q, R).GetHashCode();

        public int CompareTo(MissionIntentKey o)
        {
            int c = Kind.CompareTo(o.Kind); if (c != 0) return c;
            c = SubKind.CompareTo(o.SubKind); if (c != 0) return c;
            c = ObjectiveId.CompareTo(o.ObjectiveId); if (c != 0) return c;
            c = Q.CompareTo(o.Q); if (c != 0) return c;
            return R.CompareTo(o.R);
        }

        public override string ToString()
        {
            if (Kind == MissionKind.Scout)
            {
                if (SubKind == (int)ScoutTargetKind.Surveil)
                    return $"Intent(Surveil #{ObjectiveId})";
                if (SubKind == (int)ReconScoutKinds.Refresh)
                    return $"Intent(Refresh {Q},{R})";
                return $"Intent(Explore {Q},{R})";
            }
            if (Kind == MissionKind.Raid)
                return $"Intent(Raid #{ObjectiveId})";
            if (Kind == MissionKind.Economy)
                return $"Intent(Economy {(EconomyTaskKind)SubKind} {Q},{R} res#{ObjectiveId})";
            return $"Intent({Kind})";
        }
    }

    public sealed class ScoutIntent
    {
        public ScoutTargetKind Kind;
        // AI-RECON-02 — this durable lane's requirement is a stealthy one. Lets ReconCapacitySnapshot
        // exclude an active stealth lane from GENERIC capacity: aviation and an ordinary scout can't
        // serve it, so counting it as generic supply would mask a real generic deficit.
        public bool RequiresStealth;
        public HexCoord FocusHex;
        public int? TrackedArmyId;
        public int BaselineObservedTurn;
    }

    public sealed class RaidIntent
    {
        public int TargetArmyId;
        public HexCoord LastKnownHex;
        public bool TargetIsNeutral;
        public bool OperationStarted;
    }

    public sealed class EconomyIntent
    {
        public EconomyTaskKind Kind;
        public HexCoord TargetHex;
        public ResourceType? ResourceType;
        public int? BuilderArmyId;
        public CardData BuildCard;
        public ResourceCost BuildResourceCost;
        public float BuildApCost;
        public float BuildValue;
        public float MinimumFollowupAp;
        public bool Loaned;
        public MissionIntentKey LoanSource;
    }

    public sealed class MissionIntent
    {
        public MissionIntentKey IntentKey;
        public StableMissionKey LastAttemptKey;
        public MissionKind Kind;
        public CommitmentTier Funding;
        public IntentStatus Status;
        public SuspendReason Suspended;
        public object Objective;
        public int CreatedTurn;
        public int TurnsActive;
        // Reconciliation can run in the main pass and in up to two reaction rounds during one
        // game turn. Age/stall clocks are turn-based and advance at most once for that turn.
        public int LastReconciledTurn = -1;
        public int LastProgressTurn;
        public int StallTurns;
        public float CumulativeApSpent;
        public int StepsMovedTotal;
        public int? PreferredMoverArmyId;
        public ScoutIntent Scout => Objective as ScoutIntent;
        public RaidIntent Raid => Objective as RaidIntent;
        public EconomyIntent Economy => Objective as EconomyIntent;
    }

    public sealed class MissionIntentState
    {
        private readonly Dictionary<MissionIntentKey, MissionIntent> _intents =
            new Dictionary<MissionIntentKey, MissionIntent>();

        public IReadOnlyCollection<MissionIntent> All => _intents.Values;
        public int Count => _intents.Count;
        public bool TryGet(MissionIntentKey k, out MissionIntent i) => _intents.TryGetValue(k, out i);
        public void Put(MissionIntent i) => _intents[i.IntentKey] = i;
        public void Remove(MissionIntentKey k) => _intents.Remove(k);
    }

    public static class MissionIntentRegistry
    {
        private static readonly Dictionary<PlayerSetupData, MissionIntentState> ByPlayer =
            new Dictionary<PlayerSetupData, MissionIntentState>();

        public static MissionIntentState GetOrCreate(PlayerSetupData player)
        {
            if (player == null)
                return new MissionIntentState();
            if (!ByPlayer.TryGetValue(player, out MissionIntentState s))
                ByPlayer[player] = s = new MissionIntentState();
            return s;
        }

        public static void Clear() => ByPlayer.Clear();
    }

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
        public int? TrackedArmyId;
        public int BaselineObservedTurn;
        public bool HasScoutPayload;
        public MissionKind MissionKind = MissionKind.Scout;
        public bool HasRaidPayload;
        public int RaidTargetArmyId;
        public HexCoord RaidLastKnownHex;
        public bool RaidTargetIsNeutral;
        public bool RaidOperationStarted;
        public bool HasEconomyPayload;
        public EconomyMissionTarget EconomyTarget;
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
                    satisfied = RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, pm.RaidTargetArmyId);
                }
                else if (pm.Kind == MissionKind.Economy)
                {
                    satisfied = EconomyObjectiveSatisfied(player, pm.EconomyTarget);
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
                        o.RaidTargetArmyId = r.Provisioned.RaidTargetArmyId;
                        o.RaidLastKnownHex = r.Provisioned.RaidLastKnownHex;
                        o.RaidTargetIsNeutral = r.Provisioned.RaidTargetIsNeutral;
                    }
                    else if (r.Provisioned.Kind == MissionKind.Economy)
                    {
                        o.HasEconomyPayload = true;
                        o.EconomyTarget = r.Provisioned.EconomyTarget;
                        o.EconomyLoanSource = r.Provisioned.EconomyLoanSource;
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
                    // RECON-AIR-06 — an AirLaunch mission was bound at Assignment time to a
                    // synthetic per-airfield actor id (no ArmyData existed yet); once execution
                    // actually launched the aircraft, ActualActorArmyId carries the REAL ArmyId, and
                    // that is what MissionContinuity must track from now on, not the synthetic key.
                    if (e.ActualActorArmyId.HasValue)
                        o.MoverArmyId = e.ActualActorArmyId;
                    bool raidEngaged = o.MissionKind == MissionKind.Raid
                        && (e.StopReason == ExecutionStopReason.BattleStarted
                            || e.StopReason == ExecutionStopReason.HexEventStarted);
                    o.MadeProgress = e.StepsMoved > 0 || e.EnteredStealth
                        || e.InfrastructureChanged || e.CombatChanged
                        || e.RaidOperationStarted || raidEngaged;
                    if (o.MissionKind == MissionKind.Raid)
                        o.RaidOperationStarted = e.RaidOperationStarted
                            || e.StepsMoved > 0 || raidEngaged;
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
                    default:
                        o.Outcome = ExecutionOutcome.Failed;
                        break;
                }
                return;
            }

            if (o.MissionKind == MissionKind.Economy)
            {
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
                    o.Outcome = ExecutionOutcome.Failed;
                    break;
                default:
                    o.Outcome = ExecutionOutcome.Blocked;
                    break;
            }
        }
    }

    internal static class MissionContinuityLayer
    {
        internal static HexCoord? SelectEconomyRecoveryTarget(WorldSnapshot snap,
            PlayerSetupData player, ArmySnapshot actor, bool avoidCurrentHex = false)
        {
            if (snap?.Known?.Buildings == null || actor == null || player == null)
                return null;
            IEnumerable<Game.Ai.AiMapMemory.KnownBuilding> candidates =
                snap.Known.Buildings.Where(b => b.Owner == player
                    && (b.IsBase || b.IsStartingCitadel));
            if (avoidCurrentHex && candidates.Any(b => !b.Hex.Equals(actor.Hex)))
                candidates = candidates.Where(b => !b.Hex.Equals(actor.Hex));
            List<Game.Ai.AiMapMemory.KnownBuilding> ordered = candidates
                .OrderBy(b => HexGridMath.Distance(actor.Hex, b.Hex))
                .ThenBy(b => DemandLayer.EconomyRecoveryThreatExposure(snap, b.Hex))
                .ThenByDescending(b => b.IsStartingCitadel)
                .ThenBy(b => b.Hex.Q).ThenBy(b => b.Hex.R).ToList();
            return ordered.Count > 0 ? ordered[0].Hex : (HexCoord?)null;
        }

        internal static bool RequiresEconomyBuilderRecovery(EconomyTaskKind completedKind,
            MissionIntent lender, bool underImmediateThreat, bool alreadyProtected,
            bool hasRecoveryTarget)
        {
            if (!hasRecoveryTarget) return false;
            if (alreadyProtected && !underImmediateThreat) return false;
            if (lender?.Kind == MissionKind.Scout && lender.Scout != null
                && lender.Scout.Kind != ScoutTargetKind.Surveil && !underImmediateThreat)
                return false;
            return true;
        }

        internal static void BeginEconomyBuilderRecovery(PlayerSetupData player,
            WorldSnapshot snap, AxisDemand completedDemand, int builderArmyId, int turn)
        {
            if (player == null || snap == null || completedDemand?.TargetHex == null)
                return;
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            MissionIntent economy = state.All.FirstOrDefault(i => i != null
                && i.Kind == MissionKind.Economy && i.PreferredMoverArmyId == builderArmyId);
            MissionIntent lender = null;
            if (economy?.Economy?.Loaned == true)
                state.TryGet(economy.Economy.LoanSource, out lender);
            if (lender == null)
                lender = state.All.FirstOrDefault(i => i != null && i.Kind != MissionKind.Economy
                    && i.PreferredMoverArmyId == builderArmyId
                    && DemandLayer.EconomyDonorStructurallyEligible(i));

            ArmySnapshot actor = snap.Self?.Armies?.FirstOrDefault(a => a != null
                && a.ArmyId == builderArmyId && a.HasHero && !a.IsPrison && !a.IsAir);
            if (actor == null)
            {
                ResumeEconomyLender(lender);
                if (economy != null) state.Remove(economy.IntentKey);
                return;
            }

            EconomyTaskKind completedKind =
                completedDemand.Capability == CapabilityKind.EconomicExpansionBase
                    ? EconomyTaskKind.FoundBase : EconomyTaskKind.BuildExtraction;
            bool threatened = DemandLayer.EconomyBuilderUnderImmediateThreat(snap, actor.Hex);
            bool alreadyProtected = (completedKind == EconomyTaskKind.FoundBase && !threatened)
                || IsProtectedEconomyHex(snap, player, actor.Hex);
            HexCoord? target = SelectEconomyRecoveryTarget(
                snap, player, actor, avoidCurrentHex: threatened);
            if (!RequiresEconomyBuilderRecovery(completedKind, lender, threatened,
                    alreadyProtected, target.HasValue))
            {
                ResumeEconomyLender(lender);
                if (economy != null) state.Remove(economy.IntentKey);
                AiDebugLog.Write($"[AI][V2][Economy][Recovery] actor=#{builderArmyId} "
                    + "released at safe build hex / resumed scout");
                return;
            }

            MissionIntentKey oldKey = economy?.IntentKey ?? default;
            MissionIntent recovery = economy ?? new MissionIntent
            {
                Kind = MissionKind.Economy, CreatedTurn = turn, TurnsActive = 1,
                LastReconciledTurn = turn, PreferredMoverArmyId = builderArmyId,
            };
            recovery.Kind = MissionKind.Economy;
            recovery.Funding = CommitmentTier.Hard;
            recovery.Status = IntentStatus.Active;
            recovery.Suspended = SuspendReason.None;
            recovery.PreferredMoverArmyId = builderArmyId;
            recovery.LastProgressTurn = turn;
            recovery.StallTurns = 0;
            recovery.Objective = new EconomyIntent
            {
                Kind = EconomyTaskKind.ReturnBuilder, TargetHex = target.Value,
                BuilderArmyId = builderArmyId,
                BuildValue = completedDemand.EconomySiteValue > 0f
                    ? completedDemand.EconomySiteValue : completedDemand.Value,
                Loaned = lender != null, LoanSource = lender?.IntentKey ?? default,
            };
            recovery.IntentKey = MissionIntentKey.For(recovery);
            recovery.LastAttemptKey = new StableMissionKey(MissionKind.Economy,
                (int)EconomyTaskKind.ReturnBuilder, builderArmyId,
                target.Value.Q, target.Value.R);
            if (economy != null && !oldKey.Equals(recovery.IntentKey))
                state.Remove(oldKey);
            if (lender != null)
            {
                lender.Status = IntentStatus.Suspended;
                lender.Suspended = SuspendReason.EconomyLoan;
            }
            state.Put(recovery);
            AiDebugLog.Write($"[AI][V2][Economy][Recovery] actor=#{builderArmyId} -> "
                + $"({target.Value.Q},{target.Value.R})"
                + (lender != null ? $" lender={lender.IntentKey}" : ""));
        }

        private static bool IsProtectedEconomyHex(WorldSnapshot snap,
            PlayerSetupData player, HexCoord hex) =>
            snap?.Known?.Buildings != null && snap.Known.Buildings.Any(b => b.Owner == player
                && b.Hex.Equals(hex) && (b.IsBase || b.IsStartingCitadel));

        private static void ResumeEconomyLender(MissionIntent lender)
        {
            if (lender != null && lender.Status == IntentStatus.Suspended
                && lender.Suspended == SuspendReason.EconomyLoan)
            {
                lender.Status = IntentStatus.Active;
                lender.Suspended = SuspendReason.None;
            }
        }

        public static List<MissionIntent> ResolveActive(PlayerSetupData player, WorldSnapshot snap,
            IReadOnlyList<ReconObjective> reconObjectives = null)
        {
            var active = new List<MissionIntent>();
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            if (state.Count == 0)
                return active;

            bool underSiege = snap?.Threat?.UnderSiege == true;
            var dead = new List<MissionIntentKey>();
            var rekeys = new List<(MissionIntentKey Old, MissionIntent Intent)>();
            var liveLoanSources = new HashSet<MissionIntentKey>(state.All
                .Where(i => i?.Kind == MissionKind.Economy && i.Economy?.Loaned == true)
                .Select(i => i.Economy.LoanSource));
            foreach (MissionIntent orphanedDonor in state.All.Where(i => i != null
                && i.Status == IntentStatus.Suspended && i.Suspended == SuspendReason.EconomyLoan
                && !liveLoanSources.Contains(i.IntentKey)))
            {
                orphanedDonor.Status = IntentStatus.Active;
                orphanedDonor.Suspended = SuspendReason.None;
                AiDebugLog.Write($"[AI][V2][Economy][Loan] orphan repair donor={orphanedDonor.IntentKey}");
            }

            // Spec §1 — foci currently owned by ground scout intents, so a re-focus never lands two
            // durable intents on the same waypoint. Mutated as intents are re-pointed below.
            var scoutFoci = new HashSet<HexCoord>();
            foreach (MissionIntent i in state.All)
                if (i.Scout != null && i.Scout.Kind != ScoutTargetKind.Surveil)
                    scoutFoci.Add(i.Scout.FocusHex);

            foreach (MissionIntent intent in state.All)
            {
                if (intent.Kind == MissionKind.Economy)
                {
                    EconomyIntent ei = intent.Economy;
                    ArmySnapshot actor = snap?.Self?.Armies?.FirstOrDefault(a => a != null
                        && a.ArmyId == intent.PreferredMoverArmyId && a.HasHero && !a.IsPrison && !a.IsAir);
                    if (ei?.Kind == EconomyTaskKind.ReturnBuilder)
                    {
                        bool completed = actor != null && actor.Hex.Equals(ei.TargetHex);
                        bool targetValid = IsProtectedEconomyHex(snap, player, ei.TargetHex);
                        if (!targetValid && actor != null)
                        {
                            HexCoord? retarget = SelectEconomyRecoveryTarget(snap, player, actor);
                            if (retarget.HasValue)
                            {
                                MissionIntentKey oldKey = intent.IntentKey;
                                ei.TargetHex = retarget.Value;
                                completed = actor.Hex.Equals(ei.TargetHex);
                                intent.IntentKey = completed ? oldKey : MissionIntentKey.For(intent);
                                if (!completed && !oldKey.Equals(intent.IntentKey))
                                    rekeys.Add((oldKey, intent));
                                targetValid = true;
                            }
                        }
                        if (completed || actor == null || !targetValid)
                        {
                            MissionIntent lender = null;
                            if (ei?.Loaned == true) state.TryGet(ei.LoanSource, out lender);
                            ResumeEconomyLender(lender);
                            dead.Add(intent.IntentKey);
                            AiDebugLog.Write($"[AI][V2][Economy][Recovery] retire {intent.IntentKey} "
                                + $"arrived={(completed ? 1 : 0)} actor={(actor != null ? 1 : 0)} "
                                + $"target={(targetValid ? 1 : 0)}");
                            continue;
                        }
                        if (intent.Status == IntentStatus.Suspended)
                        {
                            intent.Status = IntentStatus.Active;
                            intent.Suspended = SuspendReason.None;
                        }
                        active.Add(intent);
                        continue;
                    }

                    bool completedBuild = ei == null || MissionOutcomeLedger.EconomyObjectiveSatisfied(player,
                        new EconomyMissionTarget { Kind = ei.Kind, TargetHex = ei.TargetHex,
                            ResourceType = ei.ResourceType, BuilderArmyId = ei.BuilderArmyId });
                    bool targetValidBuild = ei != null && (ei.Kind == EconomyTaskKind.FoundBase
                        ? snap?.Self?.Hand?.Contains(ei.BuildCard) == true
                            && snap?.Economy?.BaseOpportunities?.Any(
                                site => site.Hex.Equals(ei.TargetHex)) == true
                        : ei.ResourceType.HasValue && snap?.Economy?.IsExtractionActionable(
                            ei.TargetHex, ei.ResourceType.Value) == true);
                    if (completedBuild || actor == null || !targetValidBuild)
                    {
                        MissionIntent lender = null;
                        if (ei?.Loaned == true) state.TryGet(ei.LoanSource, out lender);
                        ResumeEconomyLender(lender);
                        StrategicResourceReservationLedger.ReleaseByOwner(player,
                            snap?.TurnNumber ?? 0, EconomyMissionPlanner.OwnerKey(intent.LastAttemptKey));
                        dead.Add(intent.IntentKey);
                        AiDebugLog.Write($"[AI][V2][Economy] retire {intent.IntentKey} "
                            + $"completed={(completedBuild ? 1 : 0)} actor={(actor != null ? 1 : 0)} "
                            + $"target={(targetValidBuild ? 1 : 0)}");
                        continue;
                    }
                    if (intent.Status == IntentStatus.Suspended
                        && (intent.Suspended == SuspendReason.PoolExhausted
                            || intent.Suspended == SuspendReason.CapabilityUnavailable))
                    {
                        intent.Status = IntentStatus.Active;
                        intent.Suspended = SuspendReason.None;
                    }
                    if (intent.Status == IntentStatus.Active) active.Add(intent);
                    continue;
                }
                if (intent.Kind == MissionKind.Raid)
                {
                    RaidIntent ri = intent.Raid;
                    if (ri == null || !RaidObjectiveEvaluator.IsIntentStillValid(snap, ri))
                    {
                        dead.Add(intent.IntentKey);
                        AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} retired at turn start (raid target no longer valid)");
                        continue;
                    }
                    if (intent.Status == IntentStatus.Suspended
                        && (intent.Suspended == SuspendReason.PoolExhausted
                            || intent.Suspended == SuspendReason.CapabilityUnavailable))
                    {
                        intent.Status = IntentStatus.Active;
                        intent.Suspended = SuspendReason.None;
                    }
                    if (intent.Status == IntentStatus.Active)
                        active.Add(intent);
                    continue;
                }

                ScoutIntent s = intent.Scout;
                if (s == null) { dead.Add(intent.IntentKey); continue; }

                if (!ScoutObjectiveEvaluator.IsIntentStillValid(snap, s))
                {
                    // Spec §1/§7/§50-52 — the focus hex is a live waypoint, not the durable
                    // identity. Re-point it at the nearest still-runnable Explore frontier / stale
                    // Refresh hex not already owned by another scout intent, re-key the ledger row
                    // in place, and keep the intent (with its CreatedTurn / PreferredMoverArmyId /
                    // accumulated progress). Only genuine exhaustion retires it.
                    MissionIntentKey oldKey = intent.IntentKey;
                    if (TryRefocusScoutIntent(snap, s, scoutFoci))
                    {
                        intent.IntentKey = MissionIntentKey.For(intent);
                        intent.LastProgressTurn = snap?.TurnNumber ?? intent.LastProgressTurn;
                        intent.StallTurns = 0;
                        if (!intent.IntentKey.Equals(oldKey))
                            rekeys.Add((oldKey, intent));
                        AiDebugLog.Write($"[AI][V2] continuity — {oldKey} waypoint done; re-focused to "
                            + $"{intent.IntentKey} — durable identity kept");
                        if (intent.Status == IntentStatus.Active)
                            active.Add(intent);
                        continue;
                    }
                    dead.Add(oldKey);
                    AiDebugLog.Write($"[AI][V2] continuity — {oldKey} retired at turn start (no runnable re-focus)");
                    continue;
                }

                if (intent.Status == IntentStatus.Suspended
                    && (intent.Suspended == SuspendReason.PoolExhausted
                        || intent.Suspended == SuspendReason.CapabilityUnavailable))
                {
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                }

                if (intent.Funding == CommitmentTier.Soft && underSiege)
                {
                    intent.Status = IntentStatus.Suspended;
                    intent.Suspended = SuspendReason.Siege;
                    continue;
                }
                if (intent.Status == IntentStatus.Suspended && intent.Suspended == SuspendReason.Siege && !underSiege)
                {
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                }

                if (intent.Status == IntentStatus.Active)
                    active.Add(intent);
            }

            foreach (MissionIntentKey k in dead)
                state.Remove(k);

            // Apply the in-place re-keys after the enumeration so the live dictionary is never
            // mutated mid-iteration. The intent object (and all its accumulated state) is kept;
            // only its dictionary slot moves to the new focus-hex key.
            foreach ((MissionIntentKey oldKey, MissionIntent it) in rekeys)
            {
                state.Remove(oldKey);
                state.Put(it);
            }

            active.Sort((x, y) =>
            {
                int c = y.Funding.CompareTo(x.Funding); if (c != 0) return c;
                c = x.CreatedTurn.CompareTo(y.CreatedTurn); if (c != 0) return c;
                return x.IntentKey.CompareTo(y.IntentKey);
            });

            if (state.Count > 0)
                AiDebugLog.WriteVerbose($"[AI][V2] continuity — {state.Count} intent(s): "
                    + string.Join(" ", state.All.Select(i =>
                        $"{i.IntentKey}[{i.Funding}/{i.Status}{(i.Suspended != SuspendReason.None ? ":" + i.Suspended : "")} "
                        + $"t{i.TurnsActive} stall{i.StallTurns}{(i.PreferredMoverArmyId.HasValue ? " mv#" + i.PreferredMoverArmyId : "")}]")));

            // §P1 — if desired concurrency has fallen below the number of active durable Scout
            // lanes (map mostly explored, fewer reachable regions), retire the surplus lanes
            // instead of carrying them forever. A "not create more" cap alone leaves earlier
            // lanes alive; this actively sheds them.
            if (reconObjectives != null)
                TrimSurplusReconLanes(player, active, state, snap, reconObjectives);

            // Spec §1/§10 invariant — one physical Recon actor owns at most one active durable
            // role. Prevention lives in ReconAssignmentPlanner, but persisted saves/log replays may
            // already contain a collision. Repair it here at the continuity boundary: keep the role
            // most recently reconciled/progressed by the physical actor and unbind the rest. The
            // objectives remain alive and may acquire another actor; no mission is silently deleted.
            foreach (IGrouping<int, MissionIntent> g in active
                .Where(i => i.Kind == MissionKind.Scout && i.Scout != null && i.PreferredMoverArmyId.HasValue)
                .GroupBy(i => i.PreferredMoverArmyId.Value))
            {
                List<MissionIntent> claims = g
                    .OrderByDescending(i => i.LastReconciledTurn)
                    .ThenByDescending(i => i.LastProgressTurn)
                    .ThenByDescending(i => i.Funding)
                    .ThenBy(i => i.CreatedTurn)
                    .ThenBy(i => i.IntentKey)
                    .ToList();
                if (claims.Count <= 1) continue;

                MissionIntent owner = claims[0];
                foreach (MissionIntent duplicate in claims.Skip(1))
                {
                    duplicate.PreferredMoverArmyId = null;
                    AiDebugLog.Write($"[AI][V2] continuity — repaired duplicate Recon actor #{g.Key}: "
                        + $"kept {owner.IntentKey}, unbound {duplicate.IntentKey}");
                }
            }
            return active;
        }

        // §P1 — GRADUAL contraction of durable Scout lanes toward desired concurrency: at most
        // maxReconLaneTrimPerTurn shed per turn, only Soft/None-funded lanes, and the target floor
        // already accounts for any Hard-funded lanes that are being kept regardless.
        private static void TrimSurplusReconLanes(PlayerSetupData player, List<MissionIntent> active,
            MissionIntentState state, WorldSnapshot snap, IReadOnlyList<ReconObjective> reconObjectives)
        {
            var airActorIds = new HashSet<int>((snap?.Self?.Armies ?? System.Array.Empty<ArmySnapshot>())
                .Where(a => a != null && a.IsAir).Select(a => a.ArmyId));
            // DesiredTotal/HardCap govern physical ground scout lanes. Air intents use the
            // independent aviation capacity policy and must survive this contraction pass.
            var scoutLanes = active.Where(i => i.Kind == MissionKind.Scout && i.Scout != null
                && (!i.PreferredMoverArmyId.HasValue || !airActorIds.Contains(i.PreferredMoverArmyId.Value)))
                .ToList();
            if (scoutLanes.Count <= 1)
                return;

            var runnable = reconObjectives
                .Where(o => o != null && o.BaseValue > 0f)
                .OrderByDescending(o => o.BaseValue)
                .ThenBy(o => o.IntentKey)
                .ToList();
            int desired = System.Math.Max(1, ReconConcurrencyPolicy.DesiredTotal(snap, runnable));

            var shedable = scoutLanes
                .Where(i => i.Funding < CommitmentTier.Hard)
                .OrderBy(i => (int)i.Funding)
                .ThenByDescending(i => i.CreatedTurn)
                .ThenByDescending(i => i.StallTurns)
                .ThenByDescending(i => i.IntentKey)
                .ToList();
            int hardKept = scoutLanes.Count - shedable.Count;
            // How many shedable lanes exceed the room left under `desired` after the Hard lanes.
            int surplus = shedable.Count - System.Math.Max(0, desired - hardKept);
            if (surplus <= 0)
                return;

            int dropped = 0;
            foreach (MissionIntent v in shedable)
            {
                if (dropped >= surplus || dropped >= AiConfigV2.maxReconLaneTrimPerTurn)
                    break;
                state.Remove(v.IntentKey);
                active.Remove(v);
                if (v.PreferredMoverArmyId.HasValue)
                    ReconPatrolStateRegistry.Retire(player, v.PreferredMoverArmyId.Value, "recon lane surplus trim");
                dropped++;
                AiDebugLog.Write($"[AI][V2] continuity — {v.IntentKey} retired: recon lane surplus "
                    + $"(active {scoutLanes.Count}, hard {hardKept}, desired {desired}, "
                    + $"shed 1/{surplus} this turn)");
            }
        }

        // Spec §1 — re-point a stale ground scout intent's live waypoint at the nearest still-
        // runnable hex of its own kind, avoiding hexes already owned by another scout intent.
        // Mutates s.FocusHex and the shared ownedFoci set. Returns false only when nothing runnable
        // remains, in which case the caller retires the intent.
        private static bool TryRefocusScoutIntent(WorldSnapshot snap, ScoutIntent s, HashSet<HexCoord> ownedFoci)
        {
            if (snap?.MapKnowledge == null || s == null || s.Kind == ScoutTargetKind.Surveil)
                return false;

            HexCoord old = s.FocusHex;
            HexCoord? pick = null;
            int bestDist = int.MaxValue;

            if (ReconScoutKinds.IsRefresh(s.Kind))
            {
                foreach (KeyValuePair<HexCoord, int> kv in ReconIntelSnapshotRegistry.LastObservedFor(snap))
                {
                    if (kv.Key.Equals(old) || ownedFoci.Contains(kv.Key))
                        continue;
                    int age = System.Math.Max(0, snap.TurnNumber - kv.Value);
                    if (age < AiConfigV2.scoutSurveilStaleTurnsLo)
                        continue;
                    if (!ScoutObjectiveEvaluator.IsRefreshFocusRunnable(snap, kv.Key))
                        continue;
                    int d = HexGridMath.Distance(old, kv.Key);
                    if (d < bestDist) { bestDist = d; pick = kv.Key; }
                }
            }
            else
            {
                if (snap.MapKnowledge.Frontier == null)
                    return false;
                foreach (FrontierHexSnapshot f in snap.MapKnowledge.Frontier)
                {
                    if (f.Hex.Equals(old) || ownedFoci.Contains(f.Hex))
                        continue;
                    if (!ScoutObjectiveEvaluator.IsExploreFocusRunnable(snap, f.Hex))
                        continue;
                    int d = HexGridMath.Distance(old, f.Hex);
                    if (d < bestDist) { bestDist = d; pick = f.Hex; }
                }
            }

            if (pick == null)
                return false;
            ownedFoci.Remove(old);
            ownedFoci.Add(pick.Value);
            s.FocusHex = pick.Value;
            return true;
        }

        public static List<Commitment> BindFunding(IReadOnlyList<MissionIntent> activeIntents,
            IReadOnlyList<MissionProposal> proposals)
        {
            var commitments = new List<Commitment>();
            if (activeIntents == null || proposals == null)
                return commitments;

            var byKey = new Dictionary<MissionIntentKey, MissionProposal>();
            foreach (MissionProposal p in proposals)
                if (p != null)
                    byKey[MissionIntentKey.For(p)] = p;

            foreach (MissionIntent intent in activeIntents)
            {
                if (intent.Funding == CommitmentTier.None)
                    continue;
                if (!byKey.TryGetValue(intent.IntentKey, out MissionProposal p))
                {
                    AiDebugLog.Write($"[AI][V2] continuity — WARN {intent.IntentKey} ({intent.Funding}) "
                        + "not materialised this turn; no funding bound");
                    continue;
                }
                commitments.Add(new Commitment
                {
                    IntentKey = intent.IntentKey,
                    Mission = p,
                    Tier = intent.Funding,
                    ContinuationValue = p.BaseValue,
                    SwitchingCost = 0f,
                });
            }
            return commitments;
        }

        // Mid-turn variant: apply exactly one settled outcome without aging, stalling or
        // reaping unrelated intents. ReconcileAfterTurn remains the sole end-of-turn sweep owner.
        public static void ReconcileStep(PlayerSetupData player, int turn,
            MissionTurnOutcome outcome)
        {
            if (player == null || outcome == null)
                return;
            ReconcileOutcome(MissionIntentRegistry.GetOrCreate(player),
                AiAllocatorStateRegistry.GetOrCreate(player), outcome, turn);
        }

        public static void ReconcileAfterTurn(PlayerSetupData player, int turn,
            IReadOnlyList<MissionTurnOutcome> outcomes)
        {
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            AiAllocatorState allocState = AiAllocatorStateRegistry.GetOrCreate(player);
            var seen = new HashSet<MissionIntentKey>();

            foreach (MissionTurnOutcome o in outcomes ?? new List<MissionTurnOutcome>())
            {
                seen.Add(o.IntentKey);
                ReconcileOutcome(state, allocState, o, turn);
            }

            foreach (MissionIntent intent in state.All.ToList())
            {
                if (seen.Contains(intent.IntentKey))
                    continue;
                if (intent.Status == IntentStatus.Suspended
                    && (intent.Suspended == SuspendReason.Siege
                        || intent.Suspended == SuspendReason.CapabilityUnavailable
                        || intent.Suspended == SuspendReason.EconomyLoan))
                    continue;

                if (intent.LastReconciledTurn == turn)
                    continue;

                intent.LastReconciledTurn = turn;
                intent.TurnsActive++;
                if (intent.Suspended != SuspendReason.PoolExhausted)
                    intent.StallTurns++;

                if (ShouldReap(intent))
                {
                    state.Remove(intent.IntentKey);
                    StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn, "IntentReapedIdle");
                    AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} reaped (idle: "
                        + $"stall {intent.StallTurns}/{AiConfigV2.commitmentStallTurns}, "
                        + $"age {intent.TurnsActive}/{AiConfigV2.commitmentMaxTurns})");
                }
            }
        }

        private static void ReconcileOutcome(MissionIntentState state,
            AiAllocatorState allocState, MissionTurnOutcome o, int turn)
        {
            state.TryGet(o.IntentKey, out MissionIntent intent);

            string aid = o.Proposal?.AttemptId;
            bool returnBuilderOutcome = o.MissionKind == MissionKind.Economy
                && (o.EconomyTarget.Kind == EconomyTaskKind.ReturnBuilder
                    || intent?.Economy?.Kind == EconomyTaskKind.ReturnBuilder);
            AiDebugLog.Write($"[AI][V2] [{aid}] outcome {o.Outcome}"
                + (o.ObjectiveSatisfied ? " satisfied" : "")
                + (o.StructuralFailure ? " structural" : "")
                + $" {o.IntentKey}");

            if (o.Outcome == ExecutionOutcome.Completed && o.ObjectiveSatisfied)
            {
                RepayEconomyLoan(state, intent, o);
                // Review P1 #1/#2 (+ follow-up) — an Explore/Refresh focus hex met by something
                // OTHER than this actor's own execution reaching goal (another scout opened it
                // mid-turn, or provisioning found it already live-satisfied) is a satisfied
                // WAYPOINT, not a finished role. KEEP — or, for a fresh mission that really
                // began executing this turn, CREATE — the durable ground-scout intent so
                // ActorCommitments retains the scout and ResolveActive re-focuses it next turn
                // (its hex now fails IsIntentStillValid). Mirrors the own-execution
                // ExecutionResult.DurableRoleContinues ProductiveStop path. Surveil and genuine
                // own-execution completions still retire.
                if (o.ObjectiveSatisfiedExternally)
                {
                    bool existingScoutRole = intent != null
                        && intent.Scout != null && intent.Scout.Kind != ScoutTargetKind.Surveil;
                    // Fresh role: the mission was provisioned AND executed at least one step
                    // this turn (so ReconPatrolState already exists). A provisioning-only
                    // TargetSatisfied for a never-executed fresh mission has HasScoutPayload ==
                    // false / MadeProgress == false and is correctly NOT made durable.
                    bool freshScoutRole = intent == null && o.HasScoutPayload && o.MadeProgress
                        && o.ScoutKind != ScoutTargetKind.Surveil;

                    if (existingScoutRole)
                    {
                        // Count the AP / steps the scout actually spent before the waypoint
                        // was taken (accumulated-state preservation), same as any other
                        // productive turn — AdvanceIntent owns that accounting.
                        o.MadeProgress = true;
                        AdvanceIntent(intent, o, turn, state, allocState);
                        AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} waypoint satisfied "
                            + "externally; durable scout role kept for next-turn re-focus");
                        return;
                    }
                    if (freshScoutRole)
                    {
                        if (TryAbsorbIntoExistingActorRole(state, o, turn, allocState))
                            return;
                        CreateIntent(state, o, turn);
                        AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} fresh scout began "
                            + "this turn; waypoint satisfied externally, durable intent created for re-focus");
                        return;
                    }
                }
                if (intent != null)
                {
                    state.Remove(o.IntentKey);
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} COMPLETED, retired");
                }
                return;
            }

            if (o.StructuralFailure)
            {
                if (returnBuilderOutcome && intent != null)
                {
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                    intent.LastReconciledTurn = turn;
                    return;
                }
                RepayEconomyLoan(state, intent, o);
                if (intent != null) state.Remove(o.IntentKey);
                string reason = o.ProvisionFailureKindValue?.ToString() ?? "StructuralFailure";
                StartPersistentCooldown(allocState, o.AttemptKey, o.MissionKind, turn, reason);
                AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} structural failure ({reason}), retired + cooldown");
                return;
            }

            if (o.Outcome == ExecutionOutcome.Failed)
            {
                if (returnBuilderOutcome && intent != null)
                {
                    intent.Status = IntentStatus.Active;
                    intent.Suspended = SuspendReason.None;
                    intent.LastReconciledTurn = turn;
                    return;
                }
                RepayEconomyLoan(state, intent, o);
                if (intent != null)
                {
                    state.Remove(o.IntentKey);
                    AiDebugLog.Write($"[AI][V2] continuity — [{aid}] {o.IntentKey} failed ({Describe(o)}), retired");
                }
                return;
            }

            if (o.MissionKind == MissionKind.Economy && !o.MadeProgress)
            {
                if (returnBuilderOutcome && intent != null)
                {
                    intent.LastReconciledTurn = turn;
                    intent.StallTurns = 0;
                    return;
                }
                RepayEconomyLoan(state, intent, o);
                if (intent != null) state.Remove(intent.IntentKey);
                return;
            }

            if (intent != null)
            {
                AdvanceIntent(intent, o, turn, state, allocState);
            }
            else if (o.MadeProgress && o.HasScoutPayload)
            {
                if (!TryAbsorbIntoExistingActorRole(state, o, turn, allocState))
                    CreateIntent(state, o, turn);
            }
            else if (o.HasRaidPayload && o.RaidOperationStarted)
            {
                CreateRaidIntent(state, o, turn);
            }
            else if (o.HasEconomyPayload && o.MadeProgress)
            {
                CreateEconomyIntent(state, o, turn);
            }

        }

        private static void AdvanceIntent(MissionIntent intent, MissionTurnOutcome o, int turn,
            MissionIntentState state, AiAllocatorState allocState)
        {
            bool firstReconcileThisTurn = intent.LastReconciledTurn != turn;
            if (firstReconcileThisTurn)
            {
                intent.LastReconciledTurn = turn;
                intent.TurnsActive++;
            }
            intent.LastAttemptKey = o.AttemptKey;
            intent.CumulativeApSpent += o.ApSpent;
            intent.StepsMovedTotal += o.StepsMoved;
            if (o.MoverArmyId.HasValue)
            {
                ReleaseOtherReconActorClaims(state, intent, o.MoverArmyId.Value);
                intent.PreferredMoverArmyId = o.MoverArmyId;
            }

            if (o.HasScoutPayload && intent.Scout != null)
            {
                intent.Scout.FocusHex = o.FocusHex;
                intent.Scout.Kind = o.ScoutKind;
                intent.Scout.RequiresStealth = o.ScoutRequiresStealth;
                if (o.TrackedArmyId.HasValue)
                    intent.Scout.TrackedArmyId = o.TrackedArmyId;
            }

            if (o.HasRaidPayload && intent.Raid != null)
            {
                intent.Raid.LastKnownHex = o.RaidLastKnownHex;
                if (o.RaidOperationStarted)
                {
                    intent.Raid.OperationStarted = true;
                    if (intent.Funding != CommitmentTier.Hard)
                    {
                        intent.Funding = CommitmentTier.Hard;
                        AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} promoted to Hard commitment (operation started)");
                    }
                }
            }

            if (o.HasEconomyPayload && intent.Economy != null)
            {
                intent.Economy.TargetHex = o.EconomyTarget.TargetHex;
                intent.Economy.BuilderArmyId = o.EconomyTarget.BuilderArmyId;
                if (o.EconomyBuildCompleted) intent.Funding = CommitmentTier.Hard;
            }

            bool poolExhausted = o.AllocationDeferReason == DeferReason.CommitmentPoolExhausted;
            bool capabilityUnavailable =
                o.ProvisionFailureKindValue == ProvisionFailureKind.NoMoverExists
                || o.ProvisionFailureKindValue == ProvisionFailureKind.MoverContended;

            if (o.MadeProgress)
            {
                intent.LastProgressTurn = turn;
                intent.StallTurns = 0;
            }
            else if (firstReconcileThisTurn && !poolExhausted && !capabilityUnavailable)
            {
                intent.StallTurns++;
            }

            if (poolExhausted)
            {
                intent.Status = IntentStatus.Suspended;
                intent.Suspended = SuspendReason.PoolExhausted;
            }
            else if (capabilityUnavailable)
            {
                intent.Status = IntentStatus.Suspended;
                intent.Suspended = SuspendReason.CapabilityUnavailable;
            }

            if (!capabilityUnavailable && ShouldReap(intent))
            {
                state.Remove(intent.IntentKey);
                StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn, "IntentReapedStall");
                AiDebugLog.Write($"[AI][V2] continuity — [{o.Proposal?.AttemptId}] {intent.IntentKey} reaped (stall "
                    + $"{intent.StallTurns}/{AiConfigV2.commitmentStallTurns}, age "
                    + $"{intent.TurnsActive}/{AiConfigV2.commitmentMaxTurns})");
            }
            else
            {
                AiDebugLog.Write($"[AI][V2] continuity — [{o.Proposal?.AttemptId}] {intent.IntentKey} advanced "
                    + $"({o.Outcome}, progress {(o.MadeProgress ? 1 : 0)}, t{intent.TurnsActive} stall{intent.StallTurns}"
                    + (capabilityUnavailable ? $", suspended CapabilityUnavailable:{o.ProvisionFailureKindValue}" : "") + ")");
            }
        }

        private static void ReleaseOtherReconActorClaims(MissionIntentState state,
            MissionIntent owner, int moverArmyId)
        {
            if (state == null || owner == null || owner.Kind != MissionKind.Scout
                || moverArmyId == 0)
                return;

            foreach (MissionIntent other in state.All)
            {
                if (other == null || object.ReferenceEquals(other, owner)
                    || other.Kind != MissionKind.Scout || other.Scout == null
                    || other.PreferredMoverArmyId != moverArmyId)
                    continue;
                other.PreferredMoverArmyId = null;
                AiDebugLog.Write($"[AI][V2] continuity — actor #{moverArmyId} moved to "
                    + $"{owner.IntentKey}; unbound prior role {other.IntentKey}");
            }
        }

        // Spec §1/§10 — the physical scout that produced this fresh scout outcome already owns a
        // durable Recon role (Explore / Refresh / Surveil) under a different key: a new
        // opportunistic mission ran on a mover continuity already tracks. Re-point that existing
        // role at the new objective and re-key its registry slot, preserving CreatedTurn /
        // TurnsActive / CumulativeApSpent / StepsMovedTotal / PreferredMoverArmyId, instead of
        // creating a second durable intent for the same physical actor. Ownership is actor-
        // exclusive across all three Recon sub-kinds. Returns true when it absorbed the outcome.
        private static bool TryAbsorbIntoExistingActorRole(MissionIntentState state,
            MissionTurnOutcome o, int turn, AiAllocatorState allocState)
        {
            if (!o.HasScoutPayload || o.MoverArmyId == null || o.MoverArmyId.Value == 0)
                return false;

            MissionIntent owner = null;
            foreach (MissionIntent it in state.All)
            {
                if (it.Kind != MissionKind.Scout || it.Scout == null)
                    continue;
                if (it.PreferredMoverArmyId == o.MoverArmyId && !it.IntentKey.Equals(o.IntentKey))
                {
                    owner = it;
                    break;
                }
            }
            if (owner == null)
                return false;

            MissionIntentKey oldKey = owner.IntentKey;
            owner.Scout.FocusHex = o.FocusHex;
            owner.Scout.Kind = o.ScoutKind;
            owner.Scout.RequiresStealth = o.ScoutRequiresStealth;
            owner.Scout.TrackedArmyId = o.ScoutKind == ScoutTargetKind.Surveil ? o.TrackedArmyId : null;
            // A durable Surveil role keeps the Soft funding that marks it as a bound surveillance
            // commitment; switching to Explore/Refresh drops back to an unfunded frontier role.
            owner.Funding = o.ScoutKind == ScoutTargetKind.Surveil
                ? (owner.Funding == CommitmentTier.Hard ? CommitmentTier.Hard : CommitmentTier.Soft)
                : (owner.Funding == CommitmentTier.Hard ? CommitmentTier.Hard : CommitmentTier.None);
            owner.IntentKey = MissionIntentKey.For(owner);
            state.Remove(oldKey);
            state.Put(owner);

            o.MadeProgress = true;
            AdvanceIntent(owner, o, turn, state, allocState);
            AiDebugLog.Write($"[AI][V2] continuity — [{o.Proposal?.AttemptId}] actor #{o.MoverArmyId} already "
                + $"owns {oldKey}; absorbed fresh {o.IntentKey} into that durable role (no duplicate intent)");
            return true;
        }

        private static void CreateIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            var si = new ScoutIntent
            {
                Kind = o.ScoutKind,
                RequiresStealth = o.ScoutRequiresStealth,
                FocusHex = o.FocusHex,
                TrackedArmyId = o.TrackedArmyId,
                BaselineObservedTurn = o.BaselineObservedTurn,
            };
            var intent = new MissionIntent
            {
                IntentKey = o.IntentKey,
                LastAttemptKey = o.AttemptKey,
                Kind = MissionKind.Scout,
                Funding = o.ScoutKind == ScoutTargetKind.Surveil ? CommitmentTier.Soft : CommitmentTier.None,
                Status = IntentStatus.Active,
                Suspended = SuspendReason.None,
                Objective = si,
                CreatedTurn = turn,
                TurnsActive = 1,
                LastReconciledTurn = turn,
                LastProgressTurn = turn,
                StallTurns = 0,
                CumulativeApSpent = o.ApSpent,
                StepsMovedTotal = o.StepsMoved,
                PreferredMoverArmyId = o.MoverArmyId,
            };
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2] continuity — [{o.Proposal?.AttemptId}] {intent.IntentKey} created ({intent.Funding}, "
                + $"mover #{o.MoverArmyId}, {o.StepsMoved} step(s))");
        }

        private static void CreateRaidIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            var ri = new RaidIntent
            {
                TargetArmyId = o.RaidTargetArmyId,
                LastKnownHex = o.RaidLastKnownHex,
                TargetIsNeutral = o.RaidTargetIsNeutral,
                OperationStarted = true,
            };
            var intent = new MissionIntent
            {
                IntentKey = o.IntentKey,
                LastAttemptKey = o.AttemptKey,
                Kind = MissionKind.Raid,
                Funding = CommitmentTier.Hard,
                Status = IntentStatus.Active,
                Suspended = SuspendReason.None,
                Objective = ri,
                CreatedTurn = turn,
                TurnsActive = 1,
                LastReconciledTurn = turn,
                LastProgressTurn = turn,
                StallTurns = 0,
                CumulativeApSpent = o.ApSpent,
                StepsMovedTotal = o.StepsMoved,
                PreferredMoverArmyId = o.MoverArmyId,
            };
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2] continuity — [{o.Proposal?.AttemptId}] {intent.IntentKey} created (Hard raid, mover #{o.MoverArmyId})");
        }

        private static void CreateEconomyIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            EconomyMissionTarget t = o.EconomyTarget;
            var intent = new MissionIntent
            {
                IntentKey = o.IntentKey, LastAttemptKey = o.AttemptKey, Kind = MissionKind.Economy,
                Funding = o.EconomyBuildCompleted ? CommitmentTier.Hard : CommitmentTier.Soft,
                Status = IntentStatus.Active, Suspended = SuspendReason.None,
                Objective = new EconomyIntent
                {
                    Kind = t.Kind, TargetHex = t.TargetHex, ResourceType = t.ResourceType,
                    BuilderArmyId = t.BuilderArmyId,
                    BuildCard = t.BuildCard, BuildResourceCost = t.BuildResourceCost,
                    BuildApCost = t.BuildApCost, BuildValue = t.BuildValue,
                    MinimumFollowupAp = t.MinimumFollowupAp,
                    Loaned = o.EconomyLoanSource.HasValue,
                    LoanSource = o.EconomyLoanSource ?? default,
                },
                CreatedTurn = turn, TurnsActive = 1, LastReconciledTurn = turn,
                LastProgressTurn = turn, CumulativeApSpent = o.ApSpent,
                StepsMovedTotal = o.StepsMoved, PreferredMoverArmyId = o.MoverArmyId,
            };
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2][Economy] continuity create {intent.IntentKey} mover=#{o.MoverArmyId}");
        }

        private static void RepayEconomyLoan(MissionIntentState state, MissionIntent economy,
            MissionTurnOutcome outcome)
        {
            MissionIntentKey? source = outcome.EconomyLoanSource;
            if (!source.HasValue && economy?.Economy?.Loaned == true)
                source = economy.Economy.LoanSource;
            if (!source.HasValue || !state.TryGet(source.Value, out MissionIntent lender))
                return;
            if (lender.Status == IntentStatus.Suspended && lender.Suspended == SuspendReason.EconomyLoan)
            {
                lender.Status = IntentStatus.Active;
                lender.Suspended = SuspendReason.None;
                AiDebugLog.Write($"[AI][V2][Economy][Loan] repay actor=#{lender.PreferredMoverArmyId} to={lender.IntentKey}");
            }
        }

        private static bool ShouldReap(MissionIntent i)
        {
            if (i.Kind == MissionKind.Raid)
                return i.StallTurns >= AiConfigV2.raidIntentStallTurns
                    || i.TurnsActive >= AiConfigV2.raidIntentMaxTurns;
            return i.StallTurns >= AiConfigV2.commitmentStallTurns
                || i.TurnsActive >= AiConfigV2.commitmentMaxTurns;
        }

        private static void StartPersistentCooldown(AiAllocatorState state, StableMissionKey key,
            MissionKind kind, int turn, string reason)
        {
            int duration = kind == MissionKind.Raid
                ? AiConfigV2.raidRejectCooldownTurns
                : AiConfigV2.allocatorRejectCooldownTurns;
            int until = turn + duration;
            state.StartCooldown(key, turn, until, reason);
            AiDebugLog.Write($"[AI][V2] cooldown — {key} reason={reason} start=t{turn} until=t{until} duration={duration}");
        }

        private static string Describe(MissionTurnOutcome o) =>
            o.Proposal != null && o.Proposal.Target is ScoutMissionTarget t
                ? ReconScoutKinds.Name(t.Kind)
                : o.Proposal != null && o.Proposal.Target is EconomyMissionTarget e
                    ? $"{e.Kind}@{e.TargetHex.Q},{e.TargetHex.R}"
                    : "?";
    }
}
