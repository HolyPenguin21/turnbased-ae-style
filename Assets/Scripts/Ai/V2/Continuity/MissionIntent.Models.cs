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
        public int? PreferredMoverArmyId;
        public ScoutIntent Scout => Objective as ScoutIntent;
        public RaidIntent Raid => Objective as RaidIntent;
        public EconomyIntent Economy => Objective as EconomyIntent;
    }
}
