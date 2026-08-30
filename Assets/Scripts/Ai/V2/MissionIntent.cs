using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  MISSION CONTINUITY  (Strategy V2 build-order step 7)
    // ===========================================================================================
    //  Closes the multi-turn loop. The pipeline recomputes everything from a fresh snapshot every
    //  turn; without this layer a half-walked recon chain re-plans from scratch on a 0.05 Radar
    //  wobble. Step 7 separates TWO things V1 kept fused (and kept corrupting):
    //
    //    INTENT      — "I still want to finish THIS objective / keep tracking THIS army." Durable.
    //                  Outlives any single MissionProposal. Every recon mission that starts and
    //                  does not finish leaves one; next turn MissionLayer re-materialises it from
    //                  the CURRENT snapshot (one place still owns proposal creation) and retarget
    //                  hysteresis keeps the scout pointed the same way through Radar noise.
    //    COMMITMENT  — a FUNDING POLICY on an Intent: "do not drop this from the budget over a
    //                  small Radar move." Soft for a far Surveil that has actually started moving;
    //                  Hard (raid) lands in step 9. A Commitment reaches the allocator as
    //                  already-funded, drawn first, allowed to drive an axis slice negative — but
    //                  NEVER to conjure AP that does not exist (Σ commitments <= real pool).
    //
    //  FOUR INVARIANTS (hold these into step 9):
    //    1. Intent != Proposal        — the proposal is this turn's attempt; the intent persists.
    //    2. Intent != Commitment      — Explore keeps an intent with NO funding protection.
    //    3. Progress != AP spent      — an intent earns continuation by MOVING (a step, or a
    //                                   stealth entry), never by having cost something. Sunk AP is
    //                                   telemetry, never continuation value.
    //    4. Post-execution observation != strategic policy — ReconcileAfterTurn reads NO live world
    //                                   state. It takes FACTS (MissionTurnOutcome, built by the
    //                                   ledger from ScoutObjectiveEvaluator + ExecutionResult) and
    //                                   only runs a state transition.
    // ===========================================================================================

    public enum CommitmentTier { None, Soft, Hard }
    public enum IntentStatus { Active, Suspended }

    // CapabilityUnavailable covers executor-side facts that are not properties of the target:
    // the capable actor disappeared, OR it exists but is temporarily claimed/contended this cycle.
    // Neither may age the objective into a structural target cooldown.
    public enum SuspendReason { None, Siege, PoolExhausted, CapabilityUnavailable }

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
            return new MissionIntentKey(m?.Kind ?? MissionKind.Scout, 0, 0, 0, 0);
        }

        public static MissionIntentKey ForScoutTarget(ScoutMissionTarget t) =>
            t.Kind == ScoutTargetKind.Surveil
                ? new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil, t.Contact?.Army?.ArmyId ?? 0, 0, 0)
                : new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Explore, 0, t.FocusHex.Q, t.FocusHex.R);

        public static MissionIntentKey For(MissionIntent intent)
        {
            RaidIntent ri = intent?.Raid;
            if (ri != null)
                return new MissionIntentKey(MissionKind.Raid, (int)AggressionObjectiveKind.Raid, ri.TargetArmyId, 0, 0);
            ScoutIntent s = intent?.Scout;
            if (s == null)
                return new MissionIntentKey(intent?.Kind ?? MissionKind.Scout, 0, 0, 0, 0);
            return s.Kind == ScoutTargetKind.Surveil
                ? new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil, s.TrackedArmyId ?? 0, 0, 0)
                : new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Explore, 0, s.FocusHex.Q, s.FocusHex.R);
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

        public override string ToString() =>
            Kind == MissionKind.Scout
                ? (SubKind == (int)ScoutTargetKind.Surveil
                    ? $"Intent(Surveil #{ObjectiveId})"
                    : $"Intent(Explore {Q},{R})")
                : Kind == MissionKind.Raid
                    ? $"Intent(Raid #{ObjectiveId})"
                    : $"Intent({Kind})";
    }

    public sealed class ScoutIntent
    {
        public ScoutTargetKind Kind;
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
        public int LastProgressTurn;
        public int StallTurns;
        public float CumulativeApSpent;
        public int StepsMovedTotal;
        public int? PreferredMoverArmyId;
        public ScoutIntent Scout => Objective as ScoutIntent;
        public RaidIntent Raid => Objective as RaidIntent;
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
        public bool StructuralFailure;
        public bool MadeProgress;
        public int StepsMoved;
        public float ApSpent;
        public int? MoverArmyId;
        public DeferReason? AllocationDeferReason;
        public ProvisionFailureKind? ProvisionFailureKindValue;
        public ScoutTargetKind ScoutKind;
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
                AiDebugLog.Write($"[AI][V2] ledger — WARN execution for unregistered mission {result.Key}, ignored");
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
                    satisfied = RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, pm.RaidTargetArmyId);
                else
                    satisfied = pm.ScoutKind == ScoutTargetKind.Surveil
                        ? ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex, pm.TrackedArmyId, pm.BaselineObservedTurn)
                        : ScoutObjectiveEvaluator.IsExploreSatisfiedLive(player, pm.FocusHex);
                if (satisfied)
                {
                    r.LiveSatisfiedOverride = true;
                    AiDebugLog.Write($"[AI][V2] ledger — {MissionIntentKey.For(r.Proposal)} objective met by "
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
                    else
                    {
                        o.HasScoutPayload = true;
                        o.ScoutKind = r.Provisioned.ScoutKind;
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
                    bool raidEngaged = o.MissionKind == MissionKind.Raid
                        && (e.StopReason == ExecutionStopReason.BattleStarted
                            || e.StopReason == ExecutionStopReason.HexEventStarted);
                    o.MadeProgress = e.StepsMoved > 0 || e.EnteredStealth || raidEngaged;
                    if (o.MissionKind == MissionKind.Raid && (e.StepsMoved > 0 || raidEngaged))
                        o.RaidOperationStarted = true;
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
                    o.Outcome = ExecutionOutcome.Completed;
                    o.ObjectiveSatisfied = true;
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
        public static List<MissionIntent> ResolveActive(PlayerSetupData player, WorldSnapshot snap)
        {
            var active = new List<MissionIntent>();
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            if (state.Count == 0)
                return active;

            bool underSiege = snap?.Threat?.UnderSiege == true;
            var dead = new List<MissionIntentKey>();

            foreach (MissionIntent intent in state.All)
            {
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
                    dead.Add(intent.IntentKey);
                    AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} retired at turn start (objective no longer valid)");
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

            active.Sort((x, y) =>
            {
                int c = y.Funding.CompareTo(x.Funding); if (c != 0) return c;
                c = x.CreatedTurn.CompareTo(y.CreatedTurn); if (c != 0) return c;
                return x.IntentKey.CompareTo(y.IntentKey);
            });

            if (state.Count > 0)
                AiDebugLog.Write($"[AI][V2] continuity — {state.Count} intent(s): "
                    + string.Join(" ", state.All.Select(i =>
                        $"{i.IntentKey}[{i.Funding}/{i.Status}{(i.Suspended != SuspendReason.None ? ":" + i.Suspended : "")} "
                        + $"t{i.TurnsActive} stall{i.StallTurns}{(i.PreferredMoverArmyId.HasValue ? " mv#" + i.PreferredMoverArmyId : "")}]")));
            return active;
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

        public static void ReconcileAfterTurn(PlayerSetupData player, int turn,
            IReadOnlyList<MissionTurnOutcome> outcomes)
        {
            MissionIntentState state = MissionIntentRegistry.GetOrCreate(player);
            AiAllocatorState allocState = AiAllocatorStateRegistry.GetOrCreate(player);
            var seen = new HashSet<MissionIntentKey>();

            foreach (MissionTurnOutcome o in outcomes ?? new List<MissionTurnOutcome>())
            {
                seen.Add(o.IntentKey);
                state.TryGet(o.IntentKey, out MissionIntent intent);

                if (o.Outcome == ExecutionOutcome.Completed && o.ObjectiveSatisfied)
                {
                    if (intent != null)
                    {
                        state.Remove(o.IntentKey);
                        AiDebugLog.Write($"[AI][V2] continuity — {o.IntentKey} COMPLETED, retired");
                    }
                    continue;
                }

                if (o.StructuralFailure)
                {
                    if (intent != null) state.Remove(o.IntentKey);
                    string reason = o.ProvisionFailureKindValue?.ToString() ?? "StructuralFailure";
                    StartPersistentCooldown(allocState, o.AttemptKey, o.MissionKind, turn, reason);
                    AiDebugLog.Write($"[AI][V2] continuity — {o.IntentKey} structural failure ({reason}), retired + cooldown");
                    continue;
                }

                if (o.Outcome == ExecutionOutcome.Failed)
                {
                    if (intent != null)
                    {
                        state.Remove(o.IntentKey);
                        AiDebugLog.Write($"[AI][V2] continuity — {o.IntentKey} failed ({Describe(o)}), retired");
                    }
                    continue;
                }

                if (intent != null)
                {
                    AdvanceIntent(intent, o, turn, state, allocState);
                }
                else if (o.MadeProgress && o.HasScoutPayload)
                {
                    CreateIntent(state, o, turn);
                }
                else if (o.HasRaidPayload && o.RaidOperationStarted)
                {
                    CreateRaidIntent(state, o, turn);
                }
            }

            foreach (MissionIntent intent in state.All.ToList())
            {
                if (seen.Contains(intent.IntentKey))
                    continue;
                if (intent.Status == IntentStatus.Suspended
                    && (intent.Suspended == SuspendReason.Siege
                        || intent.Suspended == SuspendReason.CapabilityUnavailable))
                    continue;

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

        private static void AdvanceIntent(MissionIntent intent, MissionTurnOutcome o, int turn,
            MissionIntentState state, AiAllocatorState allocState)
        {
            intent.TurnsActive++;
            intent.LastAttemptKey = o.AttemptKey;
            intent.CumulativeApSpent += o.ApSpent;
            intent.StepsMovedTotal += o.StepsMoved;
            if (o.MoverArmyId.HasValue)
                intent.PreferredMoverArmyId = o.MoverArmyId;

            if (o.HasScoutPayload && intent.Scout != null)
            {
                intent.Scout.FocusHex = o.FocusHex;
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

            bool poolExhausted = o.AllocationDeferReason == DeferReason.CommitmentPoolExhausted;
            bool capabilityUnavailable =
                o.ProvisionFailureKindValue == ProvisionFailureKind.NoMoverExists
                || o.ProvisionFailureKindValue == ProvisionFailureKind.MoverContended;

            if (o.MadeProgress)
            {
                intent.LastProgressTurn = turn;
                intent.StallTurns = 0;
            }
            else if (!poolExhausted && !capabilityUnavailable)
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

            // Missing or temporarily contended actors are external execution-capacity facts. They
            // cannot reap/poison a still-valid target, including through the absolute-age branch.
            if (!capabilityUnavailable && ShouldReap(intent))
            {
                state.Remove(intent.IntentKey);
                StartPersistentCooldown(allocState, intent.LastAttemptKey, intent.Kind, turn, "IntentReapedStall");
                AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} reaped (stall "
                    + $"{intent.StallTurns}/{AiConfigV2.commitmentStallTurns}, age "
                    + $"{intent.TurnsActive}/{AiConfigV2.commitmentMaxTurns})");
            }
            else
            {
                AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} advanced "
                    + $"({o.Outcome}, progress {(o.MadeProgress ? 1 : 0)}, t{intent.TurnsActive} stall{intent.StallTurns}"
                    + (capabilityUnavailable ? $", suspended CapabilityUnavailable:{o.ProvisionFailureKindValue}" : "") + ")");
            }
        }

        private static void CreateIntent(MissionIntentState state, MissionTurnOutcome o, int turn)
        {
            var si = new ScoutIntent
            {
                Kind = o.ScoutKind,
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
                LastProgressTurn = turn,
                StallTurns = 0,
                CumulativeApSpent = o.ApSpent,
                StepsMovedTotal = o.StepsMoved,
                PreferredMoverArmyId = o.MoverArmyId,
            };
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} created ({intent.Funding}, "
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
                LastProgressTurn = turn,
                StallTurns = 0,
                CumulativeApSpent = o.ApSpent,
                StepsMovedTotal = o.StepsMoved,
                PreferredMoverArmyId = o.MoverArmyId,
            };
            state.Put(intent);
            AiDebugLog.Write($"[AI][V2] continuity — {intent.IntentKey} created (Hard raid, mover #{o.MoverArmyId})");
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
            o.Proposal != null && o.Proposal.Target is ScoutMissionTarget t ? $"{t.Kind}" : "?";
    }
}
