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
    public enum SuspendReason { None, Siege, PoolExhausted, CapabilityUnavailable, EconomyLoan, ActiveDefencePreemption }

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

        // ATK §21/§72 — single owner of Attack key encoding. Identity is the target hex plus the
        // STABLE numeric id of the owner we expect to be holding it, so the same hex under a new
        // owner is a different objective, while a tactical detour, a phase change or a refined
        // Base/Citadel classification never disturbs the key.
        public static MissionIntentKey ForAttack(AttackTargetRef target) =>
            new MissionIntentKey(MissionKind.Attack, (int)AggressionObjectiveKind.Attack,
                target.ExpectedOwnerId, target.Hex.Q, target.Hex.R);

        public static MissionIntentKey ForActiveDefence(int enemyArmyId) =>
            new MissionIntentKey(MissionKind.ActiveDefence,
                (int)AggressionObjectiveKind.ActiveDefence, enemyArmyId, 0, 0);

        // The ONE Economy objective encoding, shared by MissionIntentKey, StableMissionKey (and
        // so every Economy reservation owner key): a recovery walk is identified by its actor, a
        // build or collection by its resource (+1, 0 for none), all by the target hex.
        public static int EconomyObjectiveId(EconomyTaskKind kind, int? builderArmyId,
            int? collectorArmyId, ResourceType? resourceType) =>
            kind == EconomyTaskKind.ReturnBuilder ? builderArmyId ?? 0
            : kind == EconomyTaskKind.ReturnCollector ? collectorArmyId ?? 0
            : resourceType.HasValue ? (int)resourceType.Value + 1 : 0;

        public static MissionIntentKey ForEconomy(EconomyTaskKind kind, int objectiveId, HexCoord hex) =>
            new MissionIntentKey(MissionKind.Economy, (int)kind, objectiveId, hex.Q, hex.R);

        public static MissionIntentKey For(MissionProposal m)
        {
            if (m != null && m.Kind == MissionKind.Scout && m.Target is ScoutMissionTarget t)
                return ForScoutTarget(t);
            if (m != null && m.Kind == MissionKind.Raid && m.Target is RaidMissionTarget rt)
                return ForRaid(rt.Target);
            if (m != null && m.Kind == MissionKind.ActiveDefence
                && m.Target is ActiveDefenceMissionTarget ad)
                return ForActiveDefence(ad.EnemyArmyId);
            if (m != null && m.Kind == MissionKind.Attack && m.Target is AttackMissionTarget at)
                return ForAttack(at.Target);
            if (m != null && m.Kind == MissionKind.Economy && m.Target is EconomyMissionTarget et)
                return ForEconomy(et.Kind, EconomyObjectiveId(et.Kind, et.BuilderArmyId,
                    et.CollectorArmyId, et.ResourceType), et.TargetHex);
            if (m != null && m.Kind == MissionKind.Development && m.Target is DevelopmentMissionTarget dt)
                return new MissionIntentKey(MissionKind.Development, (int)dt.Mode,
                    0, dt.FacilityHex.Q, dt.FacilityHex.R);
            return new MissionIntentKey(m?.Kind ?? MissionKind.Scout, 0, 0, 0, 0);
        }

        public static MissionIntentKey ForScoutTarget(ScoutMissionTarget t)
        {
            if (t.Kind == ScoutTargetKind.Surveil)
                return new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil,
                    t.Contact?.Army?.ArmyId ?? 0, 0, 0);
            // AirSweep is one durable operation whose anchor follows the enemy — hex-less identity.
            if (t.Kind == ScoutTargetKind.AirSweep)
                return new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.AirSweep, 0, 0, 0);
            return new MissionIntentKey(MissionKind.Scout, (int)t.Kind, 0, t.FocusHex.Q, t.FocusHex.R);
        }

        public static MissionIntentKey For(MissionIntent intent)
        {
            RaidIntent ri = intent?.Raid;
            if (ri != null)
                return ForRaid(ri.Target);
            AttackIntent ai = intent?.Attack;
            if (ai != null)
                return ForAttack(ai.Target);
            ActiveDefenceIntent ad = intent?.ActiveDefence;
            if (ad != null)
                return ForActiveDefence(ad.EnemyArmyId);
            EconomyIntent ei = intent?.Economy;
            if (ei != null)
                return ForEconomy(ei.Kind, EconomyObjectiveId(ei.Kind,
                    ei.BuilderArmyId ?? intent.PreferredMoverArmyId,
                    ei.CollectorArmyId ?? intent.PreferredMoverArmyId, ei.ResourceType), ei.TargetHex);
            DevelopmentIntent di = intent?.Development;
            if (di != null)
                return new MissionIntentKey(MissionKind.Development, (int)di.Mode,
                    0, di.FacilityHex.Q, di.FacilityHex.R);
            ScoutIntent s = intent?.Scout;
            if (s == null)
                return new MissionIntentKey(intent?.Kind ?? MissionKind.Scout, 0, 0, 0, 0);
            if (s.Kind == ScoutTargetKind.Surveil)
                return new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.Surveil,
                    s.TrackedArmyId ?? 0, 0, 0);
            if (s.Kind == ScoutTargetKind.AirSweep)
                return new MissionIntentKey(MissionKind.Scout, (int)ScoutTargetKind.AirSweep, 0, 0, 0);
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
                if (SubKind == (int)ScoutTargetKind.AirSweep)
                    return "Intent(AirSweep)";
                return $"Intent(Explore {Q},{R})";
            }
            if (Kind == MissionKind.Raid)
                return TargetKind == RaidTargetKind.EventGuard
                    ? $"Intent(Raid Guard@{Q},{R})"
                    : $"Intent(Raid Army#{ObjectiveId})";
            if (Kind == MissionKind.ActiveDefence)
                return $"Intent(ActiveDefence Army#{ObjectiveId})";
            if (Kind == MissionKind.Economy)
                return $"Intent(Economy {(EconomyTaskKind)SubKind} {Q},{R} res#{ObjectiveId})";
            if (Kind == MissionKind.Development)
                return $"Intent(Development {(ResearchProductionMode)SubKind} {Q},{R})";
            // ForAttack's identity: target hex + the owner expected to hold it.
            if (Kind == MissionKind.Attack)
                return $"Intent(Attack @{Q},{R}#P{ObjectiveId})";
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

    // S4 — the physical core every ground-combat operation (Raid, Attack, ActiveDefence) shares:
    // the one army the operation owns, and the separate support army it may be bringing in.
    // MissionIntent.PreferredMoverArmyId projects onto PrimaryArmyId through this interface, so the
    // primary exists exactly once per operation and generic code needs no per-lane knowledge.
    public interface IGroundCombatOperation
    {
        int? PrimaryArmyId { get; set; }
        int? SupportArmyId { get; }
    }

    public sealed class RaidIntent : IGroundCombatOperation
    {
        // THE target identity. Single source of truth for both physical neutral armies (ArmyId,
        // which may legitimately be 0) and event guards (stable hex, no ArmyId until spawned).
        // TargetArmyId/TargetHex below are read-only projections for existing non-Raid/logging
        // readers — never a second settable copy of the target.
        public RaidTargetRef Target;
        public HexCoord LastKnownHex;
        public bool TargetIsNeutral;
        public bool OperationStarted;
        public bool CompletedTargetAwaitingFreshDecision;

        public int TargetArmyId => Target.Kind == RaidTargetKind.NeutralArmy ? Target.ArmyId : 0;
        public HexCoord? TargetHex => Target.Kind == RaidTargetKind.EventGuard ? Target.Hex : (HexCoord?)null;

        // The execution phase of this one Raid operation (Assault ->
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
        public int? PrimaryArmyId { get; set; }

        public int? AirSupportArmyId;
        public HexCoord? AirSupportLandingHex;
        public int AirSupportAttemptedTurn = -1;
        public bool AirSupportStrikeSucceeded;

        // The separate mobile support army delivering reinforcement to the primary, and later the
        // one returning home after a full/full swap (RaidMissionPhase.SupportReturn). HasValue only
        // during Reinforcement/SupportReturn; released (without destroying the Raid) if lost.
        public int? SupportArmyId { get; set; }

        // The base the primary walks back to in RaidMissionPhase.Return. Fixed after the first
        // successful Return step; re-selected only if that base is lost or becomes unreachable.
        public HexCoord? ReturnHex;

        // The base the SUPPORT walks back to in RaidMissionPhase.SupportReturn, chosen the same way
        // (MissionContinuityLayer.SelectReturnBase) and fixed the same way as ReturnHex above — a
        // separate field because primary and support can be mid-transit to different homes at once
        // (primary already has ReturnHex set from a previous campaign leg).
        public HexCoord? SupportReturnHex;

        // RecoveryReturn is a continuation leg of this same durable Raid. The selected base
        // remains fixed until it is lost/unreachable; arrival retires the Raid outright (see
        // MissionContinuityLayer.CompleteRaidRecoveryReturn) rather than resuming it.
        public HexCoord? RecoveryBaseHex;

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
        public int? CollectorArmyId;
        public int? CollectorSourceArmyId;
        public int ExpectedMarginalYield;
        public HexCoord? SafeReturnHex;
        public int ArrivalTurn = -1;
        public int LastConfirmedIncomeTick = -1;
        public CardData BuildCard;
        public ResourceCost BuildResourceCost;
        public float BuildApCost;
        // Canonical world TaskScore.Value captured on a real scored proposal/handoff.
        // Site merit (BuildValue) is a distinct operational fact. A null value denotes an older
        // intent without score provenance; it must NOT be substituted with site-only merit.
        public float? IntrinsicValue;
        public float BuildValue;
        public float MinimumFollowupAp;
        public bool Loaned;
        public MissionIntentKey LoanSource;
    }

    public sealed class DevelopmentIntent
    {
        public HexCoord FacilityHex;
        public ResearchProductionMode Mode;
        public Game.Units.UnitData Hero;
        public string HeroKey;
        public float IntrinsicValue;
    }

    public sealed class ActiveDefenceIntent : IGroundCombatOperation
    {
        public ActiveDefencePhase Phase;
        public int EnemyArmyId;
        public HexCoord LastKnownHex;
        public int LastObservedTurn;
        public float Confidence;
        public HexCoord ProtectedAssetHex;
        public AssetKind ProtectedAssetKind;
        public float ProtectedAssetValue;
        public float ThreatSeverity;
        public int? PrimaryArmyId { get; set; }
        // The one support walking to the primary during Reinforcement; null otherwise.
        public int? SupportArmyId;
        int? IGroundCombatOperation.SupportArmyId => SupportArmyId;
        // ATK §49 — the offensive ground-combat intent this defence preempted for its actor, so
        // Continuity can resume exactly that one when the threat is gone. Deliberately NOT named
        // after a single lane: Raid and Attack are both offensive owners of the same armies, and a
        // second parallel SuspendedAttackIntentKey would split one ownership fact in two.
        public MissionIntentKey? SuspendedOffensiveIntentKey;
        public HexCoord? ReturnHex;
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
        public int EstimatedEta;
        // Set only by a completed intercept outcome. ResolveActive then either releases the actor
        // in place for Housekeeping to stabilize an under-garrisoned Base, or sends it through the
        // existing Return phase once local security is already sufficient.
        public bool ObjectiveCompleted;
    }

    // ATK §22 — the durable Attack operation. ONE intent is ONE target structure (§7): a hostile
    // Base/Citadel, which winning captures, or a hostile Facility, which winning destroys
    // (BuildingRegistry.CaptureOrDestroy). Either result changes the map's topology — a capture
    // brings a new home anchor, recovery point, garrison asset and distances — so an automatic
    // retarget inside the same intent would be planning the next war with the previous war's
    // world. Completion, a global replan, and then a FRESH objective is the only correct chain.
    public sealed class AttackGatherReturn
    {
        public int ArmyId;
        public HexCoord? BaseHex;
    }

    public sealed class AttackIntent : IGroundCombatOperation
    {
        // THE target identity. Hex + expected owner + kind live in one object (§21), never in
        // separate fields that can drift apart.
        public AttackTargetRef Target;
        public AttackMissionPhase Phase;
        // True once the operation has physically begun (a step taken, a battle fought). Until then
        // there is nothing to protect and the objective may be freely re-picked — same rule the
        // Raid lane uses for OperationStarted.
        public bool OperationStarted;
        public int? PrimaryArmyId { get; set; }
        public int? SupportArmyId { get; set; }
        // Gather phase only: supports still walking to (or about to hand off at) the primary.
        // A support leaves this list when its handoff is attempted or it stops existing; the
        // primary (the gather host) is never in it.
        public List<int> GatherSupportArmyIds = new List<int>();
        // Strike force step 5 — gather supports that handed over and walk home (the
        // AttackMissionPhase.GatherReturn legs). Continuity picks each one's base and drops it on
        // arrival or loss; the operation's own Phase never becomes GatherReturn.
        public List<AttackGatherReturn> GatherReturns = new List<AttackGatherReturn>();
        // Air support of the fist (AttackMissionPhase.AirSupport legs): the bound wing, where it
        // lands, the turn it was bound, whether its sortie has been seen flying, and the turn a
        // binding last ended (no second binding that turn). Continuity owns all five.
        public int? AirSupportArmyId;
        public HexCoord? AirSupportLandingHex;
        public int AirSupportBoundTurn = -1;
        public bool AirSupportSortieSeen;
        public int AirSupportAttemptedTurn = -1;
        public HexCoord? RecoveryBaseHex;
        public HexCoord? SupportReturnHex;
        public int ReinforcementRequestedTurn = -1;
        // ATK §17 — the game turn this operation last took an opportunistic side strike. At most
        // one per Attack per turn, so the operation can never degenerate into a hunt. A plain
        // turn-local marker on the intent is enough; no separate registry (§17).
        public int LastOpportunisticStrikeTurn = -1;
        public float ProjectedWinChance;
        public bool CoversAllDefenders;
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
        // Separate from LastReconciledTurn on purpose — that field's own == turn check makes
        // ReconcileAfterTurn's unseen sweep skip the WHOLE per-turn block, ShouldReap included, so
        // it cannot double as "don't age StallTurns" without also disabling the intent's absolute-
        // age reap cap. This one only ever suppresses that one turn's StallTurns++ (see
        // MissionContinuityLayer.MarkProtectedThisTurn); TurnsActive and ShouldReap still run.
        public int LastProtectedTurn = -1;
        public int StallTurns;
        // The canonical TaskScore value of the last admitted proposal of this operation that
        // carried one (lifecycle legs carry 0 and never overwrite it). What abandoning the
        // operation costs (GroundCombatDonorPolicy.BorrowableDonorApPrices). Continuity writes it.
        public float LastIntrinsicValue;
        public float CumulativeApSpent;
        public int StepsMovedTotal;
        private int? _preferredMoverArmyId;

        // The generic "actor this durable intent owns". For a Raid intent this is deliberately NOT
        // separate storage: it reads and writes RaidIntent.PrimaryArmyId, so "the primary raiding
        // army" exists exactly once in the model and generic code (ActorCommitments, Provisioning,
        // AdvanceIntent, telemetry) needs no Raid-specific knowledge.
        public int? PreferredMoverArmyId
        {
            // Pass-through to the ONE IGroundCombatOperation.PrimaryArmyId for Raid, Attack and
            // ActiveDefence: "the army this operation owns" exists once in the model.
            get => GroundCombat != null ? GroundCombat.PrimaryArmyId : _preferredMoverArmyId;
            set
            {
                IGroundCombatOperation g = GroundCombat;
                if (g != null) g.PrimaryArmyId = value;
                else _preferredMoverArmyId = value;
            }
        }

        public ScoutIntent Scout => Objective as ScoutIntent;
        public RaidIntent Raid => Objective as RaidIntent;
        public EconomyIntent Economy => Objective as EconomyIntent;
        public DevelopmentIntent Development => Objective as DevelopmentIntent;
        public ActiveDefenceIntent ActiveDefence => Objective as ActiveDefenceIntent;
        public AttackIntent Attack => Objective as AttackIntent;
        public IGroundCombatOperation GroundCombat => Objective as IGroundCombatOperation;
    }
}
