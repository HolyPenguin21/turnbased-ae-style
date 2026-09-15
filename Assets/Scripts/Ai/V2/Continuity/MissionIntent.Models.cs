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
    // MissionIntent model types: CommitmentTier / IntentStatus / SuspendReason, MissionIntentKey, ScoutIntent, RaidIntent, EconomyIntent, MissionIntent.
    // File-split (mechanical, no behaviour change) from MissionIntent.cs — see
    // Docs/ai-v2-file-split-refactor-tasks.md Task 3. Independent standalone types,
    // not a partial class.
    public enum CommitmentTier { None, Soft, Hard }
    public enum IntentStatus { Active, Suspended }
    public enum SuspendReason { None, Siege, PoolExhausted, CapabilityUnavailable, EconomyLoan }

    public readonly struct MissionIntentKey : IEquatable<MissionIntentKey>, IComparable<MissionIntentKey>
    {
        public readonly MissionKind Kind;
        public readonly int SubKind;
        public readonly int ObjectiveId;
        public readonly int Q, R;
        // Default NeutralArmy so every non-Raid key (Economy, Scout) is unaffected by this field.
        public readonly RaidTargetKind TargetKind;

        public MissionIntentKey(MissionKind kind, int subKind, int objectiveId, int q, int r,
            RaidTargetKind targetKind = RaidTargetKind.NeutralArmy)
        {
            Kind = kind; SubKind = subKind; ObjectiveId = objectiveId; Q = q; R = r; TargetKind = targetKind;
        }

        // Single owner of Raid key encoding — no other class should hand-assemble a Raid
        // MissionIntentKey. Distinguishes a neutral army #0 from an event guard at (0,0) via
        // TargetKind, since both would otherwise collapse to the same numeric identity.
        public static MissionIntentKey ForRaid(RaidTargetRef target) =>
            target.Kind == RaidTargetKind.NeutralArmy
                ? new MissionIntentKey(MissionKind.Raid, (int)AggressionObjectiveKind.Raid, target.ArmyId, 0, 0, RaidTargetKind.NeutralArmy)
                : new MissionIntentKey(MissionKind.Raid, (int)AggressionObjectiveKind.Raid, 0, target.Hex.Q, target.Hex.R, RaidTargetKind.EventGuard);

        public static MissionIntentKey For(MissionProposal m)
        {
            if (m != null && m.Kind == MissionKind.Scout && m.Target is ScoutMissionTarget t)
                return ForScoutTarget(t);
            if (m != null && m.Kind == MissionKind.Raid && m.Target is RaidMissionTarget rt)
                return ForRaid(rt.Target);
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
                return ForRaid(ri.Target);
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
            Kind == o.Kind && SubKind == o.SubKind && ObjectiveId == o.ObjectiveId && Q == o.Q && R == o.R
            && TargetKind == o.TargetKind;
        public override bool Equals(object obj) => obj is MissionIntentKey o && Equals(o);
        public override int GetHashCode() => ((int)Kind, SubKind, ObjectiveId, Q, R, (int)TargetKind).GetHashCode();

        public int CompareTo(MissionIntentKey o)
        {
            int c = Kind.CompareTo(o.Kind); if (c != 0) return c;
            c = SubKind.CompareTo(o.SubKind); if (c != 0) return c;
            c = ObjectiveId.CompareTo(o.ObjectiveId); if (c != 0) return c;
            c = Q.CompareTo(o.Q); if (c != 0) return c;
            c = R.CompareTo(o.R); if (c != 0) return c;
            return ((int)TargetKind).CompareTo((int)o.TargetKind);
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
                return TargetKind == RaidTargetKind.EventGuard
                    ? $"Intent(Raid Guard@{Q},{R})"
                    : $"Intent(Raid Army#{ObjectiveId})";
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
        // THE target identity. Single source of truth for both physical neutral armies (ArmyId,
        // which may legitimately be 0) and event guards (stable hex, no ArmyId until spawned).
        // TargetArmyId/TargetHex below are read-only projections for existing non-Raid/logging
        // readers — never a second settable copy of the target.
        public RaidTargetRef Target;
        public HexCoord LastKnownHex;
        public bool TargetIsNeutral;
        public bool OperationStarted;

        public int TargetArmyId => Target.Kind == RaidTargetKind.NeutralArmy ? Target.ArmyId : 0;
        public HexCoord? TargetHex => Target.Kind == RaidTargetKind.EventGuard ? Target.Hex : (HexCoord?)null;

        // AGG-RAID §4/§5 — the execution phase of this one Raid operation (Assault ->
        // Reinforcement -> SupportReturn -> Assault -> ... -> Return). NOT an objective type.
        // ReinforcementRequestedTurn belongs to one reinforcement cycle, not to the durable Raid.
        // Any phase transition invalidates that age. A repeated Reinforcement assignment keeps it
        // only while the same support convoy is still bound; without support it starts a fresh
        // request cycle and must not inherit an old timeout.
        private RaidMissionPhase _phase = RaidMissionPhase.Assault;
        public RaidMissionPhase Phase
        {
            get => _phase;
            set
            {
                bool sameLiveReinforcementCycle = _phase == RaidMissionPhase.Reinforcement
                    && value == RaidMissionPhase.Reinforcement
                    && SupportArmyId.HasValue;
                if (!sameLiveReinforcementCycle)
                    ReinforcementRequestedTurn = -1;
                _phase = value;
            }
        }

        // THE primary raiding army. This is the SINGLE storage for that concept: MissionIntent
        // .PreferredMoverArmyId is a pass-through projection onto this field for a Raid intent
        // (see MissionIntent below), so generic continuity/commitment code keeps working and there
        // is never a second, divergent copy. null == no primary bound yet (a real army's Id may
        // legitimately be 0, so 0 is NOT used as "unbound" — see AiV2 raid-target-unification).
        public int? PrimaryArmyId;

        // The separate mobile support army delivering reinforcement to the primary, and later the
        // one returning home after a full/full swap (RaidMissionPhase.SupportReturn). HasValue only
        // during Reinforcement/SupportReturn; released (without destroying the Raid) if lost.
        public int? SupportArmyId;

        // The base the primary walks back to in RaidMissionPhase.Return. Fixed after the first
        // successful Return step; re-selected only if that base is lost or becomes unreachable.
        public HexCoord? ReturnHex;

        // The base the SUPPORT walks back to in RaidMissionPhase.SupportReturn, chosen the same way
        // (MissionContinuityLayer.SelectReturnBase) and fixed the same way as ReturnHex above — a
        // separate field because primary and support can be mid-transit to different homes at once
        // (primary already has ReturnHex set from a previous campaign leg).
        public HexCoord? SupportReturnHex;

        // Turn on which the reinforcement demand was raised, so exactly ONE support intent is
        // requested per weakened primary (no duplicate convoys).
        public int ReinforcementRequestedTurn = -1;
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
        public int ProjectedActivationApCost;
        public int ProjectedMaxMovement;
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
        private int? _preferredMoverArmyId;

        // The generic "actor this durable intent owns". For a Raid intent this is deliberately NOT
        // separate storage: it reads and writes RaidIntent.PrimaryArmyId, so "the primary raiding
        // army" exists exactly once in the model and generic code (ActorCommitments, Provisioning,
        // AdvanceIntent, telemetry) needs no Raid-specific knowledge.
        public int? PreferredMoverArmyId
        {
            get
            {
                RaidIntent r = Raid;
                if (r == null) return _preferredMoverArmyId;
                return r.PrimaryArmyId;
            }
            set
            {
                RaidIntent r = Raid;
                if (r != null) r.PrimaryArmyId = value;
                else _preferredMoverArmyId = value;
            }
        }

        public ScoutIntent Scout => Objective as ScoutIntent;
        public RaidIntent Raid => Objective as RaidIntent;
        public EconomyIntent Economy => Objective as EconomyIntent;
    }
}