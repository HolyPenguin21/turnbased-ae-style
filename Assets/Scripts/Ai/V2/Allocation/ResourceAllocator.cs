using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    public readonly struct ResourceVector
    {
        public readonly float Ap;
        public readonly float Human;
        public readonly float Energy;
        public readonly float Materials;
        public readonly float Tech;

        public ResourceVector(float ap) : this(ap, 0f, 0f, 0f, 0f) { }
        public ResourceVector(float ap, float human, float energy, float materials, float tech)
        {
            Ap = ap; Human = human; Energy = energy; Materials = materials; Tech = tech;
        }

        public static readonly ResourceVector Zero = new ResourceVector(0f, 0f, 0f, 0f, 0f);
        public bool IsPositive => Ap > AiConfigV2.allocatorSliceEpsilon;
        public bool AnyPhysical => Human > AiConfigV2.allocatorSliceEpsilon || Energy > AiConfigV2.allocatorSliceEpsilon
            || Materials > AiConfigV2.allocatorSliceEpsilon || Tech > AiConfigV2.allocatorSliceEpsilon;

        public static ResourceVector operator +(ResourceVector a, ResourceVector b) =>
            new ResourceVector(a.Ap + b.Ap, a.Human + b.Human, a.Energy + b.Energy, a.Materials + b.Materials, a.Tech + b.Tech);
        public static ResourceVector operator -(ResourceVector a, ResourceVector b) =>
            new ResourceVector(a.Ap - b.Ap, a.Human - b.Human, a.Energy - b.Energy, a.Materials - b.Materials, a.Tech - b.Tech);
        public static ResourceVector operator *(ResourceVector a, float k) =>
            new ResourceVector(a.Ap * k, a.Human * k, a.Energy * k, a.Materials * k, a.Tech * k);

        public ResourceVector ClampLow0() => new ResourceVector(
            Mathf.Max(0f, Ap), Mathf.Max(0f, Human), Mathf.Max(0f, Energy), Mathf.Max(0f, Materials), Mathf.Max(0f, Tech));
        public float Magnitude => Ap;
        public bool CoversPhysical(ResourceVector need, float eps) =>
            Human + eps >= need.Human && Energy + eps >= need.Energy
            && Materials + eps >= need.Materials && Tech + eps >= need.Tech;
        public string Fmt() => Ap.ToString("0.00", CultureInfo.InvariantCulture);
        public string FmtPhysical() =>
            $"H{Human.ToString("0.#", CultureInfo.InvariantCulture)} "
            + $"E{Energy.ToString("0.#", CultureInfo.InvariantCulture)} "
            + $"M{Materials.ToString("0.#", CultureInfo.InvariantCulture)} "
            + $"T{Tech.ToString("0.#", CultureInfo.InvariantCulture)}";
    }

    public readonly struct ProvisionRequirement
    {
        public readonly float Ap;
        public readonly ResourceVector Physical;

        public ProvisionRequirement(float ap, ResourceVector physical)
        {
            Ap = Mathf.Max(0f, ap);
            Physical = physical.ClampLow0();
        }

        public static readonly ProvisionRequirement Zero = new ProvisionRequirement(0f, ResourceVector.Zero);
        public static ProvisionRequirement ApOnly(float ap) => new ProvisionRequirement(ap, ResourceVector.Zero);
        public static ProvisionRequirement Max(ProvisionRequirement a, ProvisionRequirement b) =>
            new ProvisionRequirement(
                Mathf.Max(a.Ap, b.Ap),
                new ResourceVector(0f,
                    Mathf.Max(a.Physical.Human, b.Physical.Human),
                    Mathf.Max(a.Physical.Energy, b.Physical.Energy),
                    Mathf.Max(a.Physical.Materials, b.Physical.Materials),
                    Mathf.Max(a.Physical.Tech, b.Physical.Tech)));
        public string Fmt() => Physical.AnyPhysical
            ? $"{Ap.ToString("0.##", CultureInfo.InvariantCulture)}AP [{Physical.FmtPhysical()}]"
            : $"{Ap.ToString("0.##", CultureInfo.InvariantCulture)}AP";
    }

    public enum ProvisionFailureKind
    {
        None,
        MoverContended,
        NoMoverExists,
        EnvelopeTooSmall,
        NoExecutableStep,
        DestinationUnreachable,
        TargetSatisfied,
        TargetInvalidated,
        NoObservationVantage,
        AssemblyInfeasible,
        SortieNotWorthwhile,
    }

    public enum ProvisionDisposition
    {
        RetryNextTurn,
        DropThisTurn,
        RepriceThisTurn,
        RejectWithCooldown,
    }

    public readonly struct StableMissionKey : IEquatable<StableMissionKey>, IComparable<StableMissionKey>
    {
        public readonly MissionKind Kind;
        public readonly int SubKind;
        public readonly int TargetId;
        public readonly int Q;
        public readonly int R;
        public readonly RaidTargetKind TargetKind;
        public readonly int ActorId;
        public readonly int DetailId;

        public StableMissionKey(MissionKind kind, int subKind, int targetId, int q, int r,
            RaidTargetKind targetKind = RaidTargetKind.NeutralArmy, int actorId = 0,
            int detailId = 0)
        {
            Kind = kind; SubKind = subKind; TargetId = targetId; Q = q; R = r;
            TargetKind = targetKind; ActorId = actorId; DetailId = detailId;
        }

        public static StableMissionKey ForRaidAssault(RaidTargetRef target) =>
            target.Kind == RaidTargetKind.NeutralArmy
                ? new StableMissionKey(MissionKind.Raid, (int)RaidMissionPhase.Assault, target.ArmyId, 0, 0, RaidTargetKind.NeutralArmy)
                : new StableMissionKey(MissionKind.Raid, (int)RaidMissionPhase.Assault, 0, target.Hex.Q, target.Hex.R, RaidTargetKind.EventGuard);

        public static StableMissionKey ForRaid(RaidMissionTarget rt)
        {
            if (rt.Phase == RaidMissionPhase.AirSupport)
                return new StableMissionKey(MissionKind.Raid, (int)RaidMissionPhase.AirSupport,
                    rt.AirSupportArmyId ?? 0, rt.DestinationHex.Q, rt.DestinationHex.R,
                    rt.Target.Kind);
            if (rt.Phase == RaidMissionPhase.Assault)
                return ForRaidAssault(rt.Target);
            if (rt.Phase == RaidMissionPhase.SupportReturn)
                return new StableMissionKey(MissionKind.Raid, (int)RaidMissionPhase.SupportReturn,
                    rt.SupportArmyId ?? 0, rt.DestinationHex.Q, rt.DestinationHex.R);
            return new StableMissionKey(MissionKind.Raid, (int)rt.Phase, rt.PrimaryArmyId ?? 0,
                rt.DestinationHex.Q, rt.DestinationHex.R);
        }

        // ATK §22/§44 — the Attack lane's own key shape, in the SAME place and the same encoding
        // every other lane's lives. Identity is (phase, owner-of-the-target, hex) for the assault —
        // exactly the AttackTargetRef identity MissionIntentKey.ForAttack uses, so the durable
        // intent and the per-cycle provisioning key can never disagree about which operation this
        // is — and (phase, leg actor, destination) for the three lifecycle legs, mirroring ForRaid.
        // Without this, every Attack proposal collapsed onto the fallback key below: two objectives
        // shared one batch-assignment slot, one AlreadyProvisioned/rejected/cooldown entry spoke
        // for the whole lane, and ExcludedForGroundCombat read another operation's actor as its own.
        public static StableMissionKey ForAttack(AttackMissionTarget at)
        {
            switch (at.Phase)
            {
                case AttackMissionPhase.Reinforcement:
                    return new StableMissionKey(MissionKind.Attack, (int)AttackMissionPhase.Reinforcement,
                        at.PrimaryArmyId ?? 0, at.DestinationHex.Q, at.DestinationHex.R);
                case AttackMissionPhase.SupportReturn:
                    return new StableMissionKey(MissionKind.Attack, (int)AttackMissionPhase.SupportReturn,
                        at.SupportArmyId ?? 0, at.DestinationHex.Q, at.DestinationHex.R);
                case AttackMissionPhase.RecoveryReturn:
                    return new StableMissionKey(MissionKind.Attack, (int)AttackMissionPhase.RecoveryReturn,
                        at.PrimaryArmyId ?? 0, at.DestinationHex.Q, at.DestinationHex.R);
                default:
                    return new StableMissionKey(MissionKind.Attack, (int)AttackMissionPhase.Assault,
                        at.Target.ExpectedOwnerId, at.Target.Hex.Q, at.Target.Hex.R);
            }
        }

        public static StableMissionKey For(MissionProposal m)
        {
            if (m != null && m.Kind == MissionKind.Scout && m.Target is ScoutMissionTarget t)
            {
                int targetId = t.Kind == ScoutTargetKind.Surveil ? (t.Contact?.Army?.ArmyId ?? 0) : 0;
                return new StableMissionKey(MissionKind.Scout, (int)t.Kind, targetId, t.FocusHex.Q, t.FocusHex.R);
            }
            if (m != null && m.Kind == MissionKind.Raid && m.Target is RaidMissionTarget rt)
                return ForRaid(rt);
            if (m != null && m.Kind == MissionKind.Attack && m.Target is AttackMissionTarget at)
                return ForAttack(at);
            if (m != null && m.Kind == MissionKind.ActiveDefence
                && m.Target is ActiveDefenceMissionTarget ad)
                return new StableMissionKey(MissionKind.ActiveDefence, (int)ad.Phase,
                    ad.EnemyArmyId, 0, 0);
            if (m != null && m.Kind == MissionKind.Economy && m.Target is EconomyMissionTarget et)
                return new StableMissionKey(MissionKind.Economy, (int)et.Kind,
                    et.Kind == EconomyTaskKind.ReturnBuilder
                        ? et.BuilderArmyId ?? 0
                        : et.Kind == EconomyTaskKind.ReturnCollector
                            ? et.CollectorArmyId ?? 0
                        : et.ResourceType.HasValue ? (int)et.ResourceType.Value + 1 : 0,
                    et.TargetHex.Q, et.TargetHex.R);
            if (m != null && m.Kind == MissionKind.Development && m.Target is DevelopmentMissionTarget dt)
                return new StableMissionKey(MissionKind.Development, (int)dt.Mode,
                    0, dt.FacilityHex.Q, dt.FacilityHex.R);
            return new StableMissionKey(m?.Kind ?? MissionKind.Scout, 0, 0, 0, 0);
        }

        public bool Equals(StableMissionKey o) =>
            Kind == o.Kind && SubKind == o.SubKind && TargetId == o.TargetId && Q == o.Q && R == o.R
            && TargetKind == o.TargetKind && ActorId == o.ActorId && DetailId == o.DetailId;
        public override bool Equals(object obj) => obj is StableMissionKey o && Equals(o);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Kind;
                hash = hash * 397 ^ SubKind;
                hash = hash * 397 ^ TargetId;
                hash = hash * 397 ^ Q;
                hash = hash * 397 ^ R;
                hash = hash * 397 ^ (int)TargetKind;
                hash = hash * 397 ^ ActorId;
                return hash * 397 ^ DetailId;
            }
        }
        public override string ToString() =>
            Kind == MissionKind.Scout
                ? (TargetId != 0
                    ? $"{Kind}({(ScoutTargetKind)SubKind} #{TargetId} {Q},{R})"
                    : $"{Kind}({(ScoutTargetKind)SubKind} {Q},{R})")
                : Kind == MissionKind.Raid
                    ? (SubKind == (int)RaidMissionPhase.Assault
                        ? (TargetKind == RaidTargetKind.EventGuard ? $"Raid(Guard@{Q},{R})" : $"Raid(#{TargetId})")
                        : $"Raid({(RaidMissionPhase)SubKind} #{TargetId} {Q},{R})")
                    : Kind == MissionKind.Attack
                        ? (SubKind == (int)AttackMissionPhase.Assault
                            ? $"Attack(Base@{Q},{R}#P{TargetId})"
                            : $"Attack({(AttackMissionPhase)SubKind} #{TargetId} {Q},{R})")
                    : Kind == MissionKind.ActiveDefence
                        ? $"ActiveDefence({(ActiveDefencePhase)SubKind} #{TargetId})"
                    : Kind == MissionKind.Economy
                        ? $"Economy({(EconomyTaskKind)SubKind} {Q},{R} res#{TargetId})"
                        : Kind == MissionKind.Development
                            ? $"Development({(Game.Cards.ResearchProductionMode)SubKind} {Q},{R})"
                        : $"{Kind}";

        public int CompareTo(StableMissionKey o)
        {
            int c = Kind.CompareTo(o.Kind); if (c != 0) return c;
            c = SubKind.CompareTo(o.SubKind); if (c != 0) return c;
            c = TargetId.CompareTo(o.TargetId); if (c != 0) return c;
            c = ((int)TargetKind).CompareTo((int)o.TargetKind); if (c != 0) return c;
            c = Q.CompareTo(o.Q); if (c != 0) return c;
            c = R.CompareTo(o.R); if (c != 0) return c;
            c = ActorId.CompareTo(o.ActorId); if (c != 0) return c;
            return DetailId.CompareTo(o.DetailId);
        }
    }

    public enum FundingStage { Strict, Remainder }

    public sealed class FundedEntry
    {
        public MissionProposal Mission;
        public int Priority;
        public ResourceVector Tentative;
        public float StrictAp;
        public ResourceVector RemainderTopUp;
        public ResourceVector PhysicalDraw;
        public bool IsCommitment;
        public FundingStage Stage;
    }

    public enum DeferReason
    {
        InsufficientBudget,
        InsufficientPhysical,
        InvalidContribution,
        RejectedThisTurn,
        OnCooldown,
        CommitmentPoolExhausted,
        ExecutionCapacity,
        // Pairwise conflict with any already funded/locked mission in the whole portfolio. The
        // category-specific policy still owns what constitutes a conflict; the allocator merely
        // applies that policy before committing resources.
        MissionConflict,
    }

    public sealed class DeferredEntry
    {
        public MissionProposal Mission;
        public DeferReason Reason;
        public DesireAxis? BottleneckAxis;
        public ResourceVector Required;
        public ResourceVector Available;
        public ResourceVector Missing;
        public int CooldownStartedTurn;
        public int CooldownUntilTurn;
        public string CooldownReason;
    }

    public sealed class TentativeAllocation
    {
        public readonly List<FundedEntry> Funded = new List<FundedEntry>();
        public readonly List<DeferredEntry> Deferred = new List<DeferredEntry>();
        public ResourceVector InitialPool;
        public ResourceVector ManagerReserve;
        public ResourceVector CommitmentDraw;
        public ResourceVector StrictFunded;
        public ResourceVector RemainderGenerated;
        public ResourceVector RemainderSpent;
        public ResourceVector Unused;
        public ResourceVector LockedClaim;
        public ResourceVector PhysicalPool;
        public ResourceVector PhysicalLocked;
        public ResourceVector PhysicalFunded;
        public ResourceVector AxisOverdraft;
        public ResourceVector GlobalOverdraft;
        public int PassNumber;
        public bool CommitmentsStarveFreshDecisions;
    }

    public readonly struct MissionCooldownInfo
    {
        public readonly int StartedTurn;
        public readonly int UntilTurn;
        public readonly string Reason;
        public MissionCooldownInfo(int startedTurn, int untilTurn, string reason)
        {
            StartedTurn = startedTurn;
            UntilTurn = untilTurn;
            Reason = string.IsNullOrEmpty(reason) ? "StructuralFailure" : reason;
        }
        public int RemainingAt(int turn) => Mathf.Max(0, UntilTurn - turn + 1);
    }

    public sealed class AiAllocatorState
    {
        private readonly Dictionary<StableMissionKey, MissionCooldownInfo> _cooldowns =
            new Dictionary<StableMissionKey, MissionCooldownInfo>();
        public bool OnCooldown(StableMissionKey k, int turn) => TryGetCooldown(k, turn, out _);
        public bool TryGetCooldown(StableMissionKey k, int turn, out MissionCooldownInfo info)
        {
            if (_cooldowns.TryGetValue(k, out info) && turn <= info.UntilTurn)
                return true;
            info = default;
            return false;
        }
        public void StartCooldown(StableMissionKey k, int startedTurn, int untilTurn, string reason)
        {
            var next = new MissionCooldownInfo(startedTurn, untilTurn, reason);
            if (!_cooldowns.TryGetValue(k, out MissionCooldownInfo cur) || untilTurn > cur.UntilTurn)
                _cooldowns[k] = next;
        }
        public void StartCooldown(StableMissionKey k, int untilTurn) =>
            StartCooldown(k, Mathf.Max(0, untilTurn - AiConfigV2.allocatorRejectCooldownTurns), untilTurn, "LegacySeed");
        public void PurgeExpired(int turn)
        {
            var dead = _cooldowns.Where(kv => kv.Value.UntilTurn < turn).Select(kv => kv.Key).ToList();
            foreach (StableMissionKey k in dead)
                _cooldowns.Remove(k);
        }
    }

    public static class AiAllocatorStateRegistry
    {
        private static readonly Dictionary<PlayerSetupData, AiAllocatorState> ByPlayer =
            new Dictionary<PlayerSetupData, AiAllocatorState>();
        public static AiAllocatorState GetOrCreate(PlayerSetupData player)
        {
            if (player == null) return new AiAllocatorState();
            if (!ByPlayer.TryGetValue(player, out AiAllocatorState s))
                ByPlayer[player] = s = new AiAllocatorState();
            return s;
        }
        public static AiAllocatorState Peek(PlayerSetupData player) =>
            player != null && ByPlayer.TryGetValue(player, out AiAllocatorState s) ? s : null;
        public static void Clear() => ByPlayer.Clear();
    }

    internal static class ResourceAllocator
    {
        internal static bool ActiveDefencePreemptsRaid(float activeEffectiveValue,
            float raidEffectiveValue, float switchingCost, float epsilon) =>
            activeEffectiveValue > raidEffectiveValue + switchingCost + epsilon;

        public static AllocationSession BeginTurn(WorldSnapshot snapshot, Radar radar,
            List<MissionProposal> missions, List<Commitment> commitments, PlayerSetupData player,
            AxisBudgetLedger ledger = null, float protectedPhysicalEnergy = 0f, float protectedAp = 0f)
        {
            AiAllocatorState state = AiAllocatorStateRegistry.GetOrCreate(player);
            state.PurgeExpired(snapshot?.TurnNumber ?? 0);
            return new AllocationSession(snapshot, radar ?? Radar.Even(),
                missions ?? new List<MissionProposal>(), commitments ?? new List<Commitment>(), state, ledger,
                protectedPhysicalEnergy, protectedAp);
        }
    }

    public sealed class AllocationSession
    {
        private readonly WorldSnapshot _snap;
        private readonly List<MissionProposal> _missions;
        private readonly List<Commitment> _commitments;
        private readonly AiAllocatorState _state;
        private readonly AxisBudgetLedger _ledger;
        private readonly float _protectedAp;
        private readonly float _protectedPhysicalEnergy;
        private readonly HashSet<StableMissionKey> _rejectedThisTurn = new HashSet<StableMissionKey>();
        private readonly Dictionary<StableMissionKey, ProvisionRequirement> _repricedFloors =
            new Dictionary<StableMissionKey, ProvisionRequirement>();
        private readonly Dictionary<StableMissionKey, LockedAllocation> _lockedClaims =
            new Dictionary<StableMissionKey, LockedAllocation>();
        private string _lastFingerprint;

        private readonly struct LockedAllocation
        {
            public readonly float StrictAp;
            public readonly float RemainderAp;
            public readonly float GrantedAp;
            public readonly float ClaimedAp;
            public readonly ResourceVector PhysicalClaim;
            public readonly MissionProposal Mission;
            public LockedAllocation(MissionProposal mission, float strictAp, float remainderAp,
                float grantedAp, float claimedAp, ResourceVector physicalClaim)
            {
                Mission = mission;
                StrictAp = Mathf.Max(0f, strictAp);
                RemainderAp = remainderAp;
                GrantedAp = grantedAp;
                ClaimedAp = claimedAp;
                PhysicalClaim = physicalClaim;
            }
            public void Resolve(float eps, out float strictScale, out float remainderConsumed, out bool overclaim)
            {
                float claimed = Mathf.Max(0f, ClaimedAp);
                overclaim = claimed > GrantedAp + eps;
                if (overclaim) claimed = GrantedAp;
                if (claimed + eps >= StrictAp)
                {
                    strictScale = 1f;
                    remainderConsumed = Mathf.Max(0f, claimed - StrictAp);
                }
                else
                {
                    strictScale = StrictAp > 1e-6f ? claimed / StrictAp : 0f;
                    remainderConsumed = 0f;
                }
            }
        }

        public int PassCount { get; private set; }
        public bool HasNewFailures { get; private set; }
        public bool Converged { get; private set; }

        internal AllocationSession(WorldSnapshot snap, Radar radar, List<MissionProposal> missions,
            List<Commitment> commitments, AiAllocatorState state, AxisBudgetLedger ledger = null,
            float protectedPhysicalEnergy = 0f, float protectedAp = 0f)
        {
            _snap = snap;
            _missions = missions;
            _commitments = commitments;
            _state = state;
            _ledger = ledger;
            _protectedPhysicalEnergy = Mathf.Max(0f, protectedPhysicalEnergy);
            _protectedAp = Mathf.Max(0f, protectedAp);
        }

        public void RegisterProvisionFailure(FundedEntry funded, ProvisionFailure failure)
        {
            if (funded?.Mission == null) return;
            StableMissionKey key = StableMissionKey.For(funded.Mission);
            HasNewFailures = true;
            _lastFingerprint = null;
            switch (failure.Disposition)
            {
                case ProvisionDisposition.RepriceThisTurn:
                {
                    ProvisionRequirement cur = _repricedFloors.TryGetValue(key, out ProvisionRequirement f)
                        ? f : ProvisionRequirement.Zero;
                    _repricedFloors[key] = ProvisionRequirement.Max(cur, failure.Requirement);
                    break;
                }
                case ProvisionDisposition.RejectWithCooldown:
                    _rejectedThisTurn.Add(key);
                    break;
                default:
                    _rejectedThisTurn.Add(key);
                    break;
            }
        }

        public void RegisterProvisionSuccess(FundedEntry funded, float claimedAp, ResourceVector? claimedPhysical = null)
        {
            if (funded?.Mission == null) return;
            _lockedClaims[StableMissionKey.For(funded.Mission)] =
                new LockedAllocation(funded.Mission, funded.StrictAp, funded.RemainderTopUp.Ap,
                    funded.Tentative.Ap, claimedAp, claimedPhysical ?? funded.PhysicalDraw);
        }

        public TentativeAllocation Pack()
        {
            HasNewFailures = false;
            PassCount++;
            int turn = _snap?.TurnNumber ?? 0;
            float eps = AiConfigV2.allocatorSliceEpsilon;
            var alloc = new TentativeAllocation { PassNumber = PassCount };

            float rawAp = _snap?.Self?.ActionPoints ?? 0;
            float reserve = Mathf.Max(0f, AiConfigV2.housekeepingApReserve);
            var pool = new ResourceVector(Mathf.Max(0f, rawAp - reserve - _protectedAp));
            alloc.InitialPool = pool;
            alloc.ManagerReserve = new ResourceVector(reserve + _protectedAp);

            float lockedStrict = 0f;
            float lockedRemainderConsumed = 0f;
            float lockedTotal = 0f;
            foreach (KeyValuePair<StableMissionKey, LockedAllocation> lc in _lockedClaims)
            {
                lc.Value.Resolve(eps, out float strictScale, out float remainderConsumed, out bool overclaim);
                if (overclaim)
                {
                    AiV2Trace.CheckError(lc.Value.Mission?.AttemptId, "ProvisionClaimExceedsEnvelope",
                        $"claimed={LogNum(lc.Value.ClaimedAp)} granted={LogNum(lc.Value.GrantedAp)} key={lc.Key}");
                    AiDebugLog.Write($"[AI][V2] allocator — WARN locked claim {LogNum(lc.Value.ClaimedAp)} "
                        + $"exceeds granted {LogNum(lc.Value.GrantedAp)} for {lc.Key} — clamped");
                }
                lockedTotal += Mathf.Min(lc.Value.ClaimedAp, lc.Value.GrantedAp);
                lockedRemainderConsumed += remainderConsumed;
                lockedStrict += lc.Value.StrictAp * strictScale;
            }
            alloc.LockedClaim = new ResourceVector(lockedTotal);

            float entitlement = _ledger != null ? Mathf.Min(_ledger.Balance(), pool.Ap) : pool.Ap;
            float budget = Mathf.Max(0f, entitlement - lockedStrict);

            ResourceBundle stock = _snap?.Self?.Stockpile ?? default;
            float poolEnergy = Mathf.Max(0f, stock.Energy - _protectedPhysicalEnergy);
            var physicalPool = new ResourceVector(0f, stock.Human, poolEnergy, stock.Materials, stock.Tech);
            var lockedPhysical = ResourceVector.Zero;
            foreach (LockedAllocation lc in _lockedClaims.Values)
                lockedPhysical += lc.PhysicalClaim;
            ResourceVector physicalRemaining = (physicalPool - lockedPhysical).ClampLow0();
            alloc.PhysicalPool = physicalPool;
            alloc.PhysicalLocked = lockedPhysical;

            int priority = 0;
            var laneUsed = new Dictionary<ExecutionLane, int>();
            foreach (LockedAllocation lc in _lockedClaims.Values)
            {
                ExecutionLane lane = MissionAdmissionPolicy.LaneFor(lc.Mission);
                if (lane == ExecutionLane.None) continue;
                laneUsed.TryGetValue(lane, out int u);
                laneUsed[lane] = u + 1;
            }
            bool AtCapacity(ExecutionLane lane) =>
                lane != ExecutionLane.None
                && (laneUsed.TryGetValue(lane, out int u) ? u : 0) >= MissionAdmissionPolicy.Capacity(lane);
            void ConsumeSlot(ExecutionLane lane)
            {
                if (lane == ExecutionLane.None) return;
                laneUsed.TryGetValue(lane, out int u);
                laneUsed[lane] = u + 1;
            }
            // ResourceAllocator remains portfolio owner only: it does not assign actors. It asks
            // MissionAdmissionPolicy whether the already actor-priced proposals can coexist.
            bool ConflictsCurrentPortfolio(MissionProposal candidate)
            {
                if (candidate == null) return false;
                return alloc.Funded.Any(fe => fe?.Mission != null
                        && MissionAdmissionPolicy.Conflicts(fe.Mission, candidate))
                    || _lockedClaims.Values.Any(lc => lc.Mission != null
                        && MissionAdmissionPolicy.Conflicts(lc.Mission, candidate));
            }

            float committedApSoFar = 0f;
            foreach (Commitment c in _commitments)
            {
                MissionProposal m = c?.Mission;
                if (m == null) continue;
                // Active Defence may interrupt only the exact Raid commitment that owns the same
                // physical primary. The comparison uses the global EffectiveValue scale plus the
                // Raid's real switching cost; no family-local bonus leaks into this decision.
                MissionProposal defencePreemptor = null;
                if (m.Kind == MissionKind.Raid && m.Target is RaidMissionTarget raid
                    && raid.PrimaryArmyId.HasValue)
                {
                    defencePreemptor = _missions.FirstOrDefault(candidate =>
                        candidate?.Kind == MissionKind.ActiveDefence
                        && candidate.Target is ActiveDefenceMissionTarget defence
                        && defence.PrimaryArmyId == raid.PrimaryArmyId
                        && !_rejectedThisTurn.Contains(StableMissionKey.For(candidate))
                        && !_state.OnCooldown(StableMissionKey.For(candidate), turn)
                        && ResourceAllocator.ActiveDefencePreemptsRaid(candidate.EffectiveValue,
                            m.EffectiveValue, c.SwitchingCost, eps));
                }
                if (defencePreemptor != null)
                {
                    alloc.Deferred.Add(new DeferredEntry
                    {
                        Mission = m, Reason = DeferReason.MissionConflict,
                    });
                    AiDebugLog.Write($"[AI][V2][ActiveDefence][Preemption] decision=PREEMPT "
                        + $"raid={StableMissionKey.For(m)} active={StableMissionKey.For(defencePreemptor)} "
                        + $"activeValue={defencePreemptor.EffectiveValue:0.00} "
                        + $"raidThreshold={(m.EffectiveValue + c.SwitchingCost):0.00}");
                    continue;
                }
                StableMissionKey ckey = StableMissionKey.For(m);
                if (_lockedClaims.ContainsKey(ckey) || _rejectedThisTurn.Contains(ckey) || _state.OnCooldown(ckey, turn))
                    continue;
                if (!HasValidContribution(m))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.InvalidContribution });
                    continue;
                }

                // Commitments are protected from fresh work, not from physical impossibility. If two
                // active commitments advertise the same concrete actor, deterministic commitment
                // order keeps the first and defers the second rather than double-booking the actor.
                if (ConflictsCurrentPortfolio(m))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.MissionConflict });
                    alloc.CommitmentsStarveFreshDecisions = true;
                    continue;
                }

                ExecutionLane clane = MissionAdmissionPolicy.LaneFor(m);
                if (AtCapacity(clane))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.ExecutionCapacity });
                    alloc.CommitmentsStarveFreshDecisions = true;
                    continue;
                }

                float askAp = ApDesired(m);
                if (lockedTotal + committedApSoFar + askAp > pool.Ap + eps)
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.CommitmentPoolExhausted });
                    alloc.CommitmentsStarveFreshDecisions = true;
                    continue;
                }

                ResourceVector cPhys = PhysicalDesired(m);
                if (cPhys.AnyPhysical && !physicalRemaining.CoversPhysical(cPhys, eps))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.CommitmentPoolExhausted });
                    alloc.CommitmentsStarveFreshDecisions = true;
                    continue;
                }

                committedApSoFar += askAp;
                physicalRemaining = (physicalRemaining - cPhys).ClampLow0();
                ConsumeSlot(clane);
                var ask = new ResourceVector(askAp);
                var fe = new FundedEntry
                {
                    Mission = m, Priority = priority++, Tentative = ask, StrictAp = askAp,
                    IsCommitment = true, Stage = FundingStage.Strict, PhysicalDraw = cPhys,
                };
                budget -= askAp;
                alloc.Funded.Add(fe);
                alloc.CommitmentDraw += ask;
                alloc.PhysicalFunded += cPhys;
            }

            var commitmentKeys = new HashSet<StableMissionKey>(_commitments
                .Where(c => c?.Mission != null).Select(c => StableMissionKey.For(c.Mission)));
            List<MissionProposal> freshPool = _missions
                .Where(m => m != null
                    && !_lockedClaims.ContainsKey(StableMissionKey.For(m))
                    && !commitmentKeys.Contains(StableMissionKey.For(m)))
                .ToList();

            // Radar model #2 / Task C — ONE global admission order across every lane, not a
            // per-lane queue merged by peeking only the head of each lane. The old per-lane-queue
            // merge could hide a globally more valuable proposal behind a locally-preferred one in
            // the SAME lane: if a local planner preference differs from cross-lane value, the old
            // merge would still evaluate the local favourite first every
            // round, potentially spending the whole budget before the globally stronger candidate
            // is ever looked at. EffectiveValue (RankValue) is therefore the ONE primary key here;
            // AdmissionRank (which may fold in durable-retarget lifecycle hysteresis)
            // is only a tie-break when two proposals are equally valuable cross-lane, or an
            // explicit admission gate elsewhere (cooldown/conflict/capacity checks below) — never a
            // way to jump the global queue.
            List<MissionProposal> freshOrder = freshPool
                .OrderByDescending(RankValue)
                .ThenByDescending(m => MissionAdmissionPolicy.AdmissionRank(m))
                .ThenBy(m => StableMissionKey.For(m), MissionKeyComparer.Instance)
                .ToList();

            foreach (MissionProposal m in freshOrder)
            {
                ExecutionLane lane = MissionAdmissionPolicy.LaneFor(m);
                StableMissionKey key = StableMissionKey.For(m);
                if (_rejectedThisTurn.Contains(key))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.RejectedThisTurn });
                    continue;
                }
                if (_state.TryGetCooldown(key, turn, out MissionCooldownInfo cooldown))
                {
                    alloc.Deferred.Add(new DeferredEntry
                    {
                        Mission = m, Reason = DeferReason.OnCooldown,
                        CooldownStartedTurn = cooldown.StartedTurn,
                        CooldownUntilTurn = cooldown.UntilTurn,
                        CooldownReason = cooldown.Reason,
                    });
                    continue;
                }
                if (!HasValidContribution(m))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.InvalidContribution });
                    continue;
                }
                if (ConflictsCurrentPortfolio(m))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.MissionConflict });
                    continue;
                }
                if (AtCapacity(lane))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.ExecutionCapacity });
                    continue;
                }

                float affordable = Mathf.Max(0f, budget);
                float min = ApMinimum(m);
                if (affordable + eps < min)
                {
                    AddBudgetDeferred(alloc, m, min, affordable);
                    continue;
                }

                ResourceVector physMin = PhysicalMinimum(m);
                ResourceVector physDraw = PhysicalDesired(m);
                if ((physMin.AnyPhysical || physDraw.AnyPhysical) && !physicalRemaining.CoversPhysical(physMin, eps))
                {
                    alloc.Deferred.Add(new DeferredEntry
                    {
                        Mission = m, Reason = DeferReason.InsufficientPhysical,
                        Required = physMin, Available = physicalRemaining,
                        Missing = (physMin - physicalRemaining).ClampLow0(),
                    });
                    continue;
                }
                if (!physicalRemaining.CoversPhysical(physDraw, eps))
                    physDraw = physMin;

                float fundAp = Mathf.Min(ApDesired(m), Mathf.Max(min, affordable));
                if (budget + eps < fundAp)
                {
                    AddBudgetDeferred(alloc, m, min, Mathf.Max(0f, budget));
                    continue;
                }
                var v = new ResourceVector(fundAp);
                var funded = new FundedEntry
                {
                    Mission = m, Priority = priority++, Tentative = v, StrictAp = fundAp,
                    IsCommitment = false, Stage = FundingStage.Strict, PhysicalDraw = physDraw,
                };
                budget -= fundAp;
                physicalRemaining = (physicalRemaining - physDraw).ClampLow0();
                alloc.Funded.Add(funded);
                alloc.StrictFunded += v;
                alloc.PhysicalFunded += physDraw;
                ConsumeSlot(lane);
            }

            if (!alloc.CommitmentsStarveFreshDecisions && alloc.Funded.Any(f => f.IsCommitment))
            {
                float minCommitVal = alloc.Funded.Where(f => f.IsCommitment).Min(f => RankValue(f.Mission));
                bool starvedOnBudget = alloc.Deferred.Any(d => d.Reason == DeferReason.InsufficientBudget
                    && d.Mission != null && RankValue(d.Mission) > minCommitVal);
                bool starvedOnCapacity = alloc.Deferred.Any(d => d.Reason == DeferReason.ExecutionCapacity)
                    && alloc.Funded.Any(f => f.IsCommitment
                        && MissionAdmissionPolicy.LaneFor(f.Mission) != ExecutionLane.None);
                bool starvedOnConflict = alloc.Deferred.Any(d => d.Reason == DeferReason.MissionConflict)
                    && alloc.Funded.Any(f => f.IsCommitment);
                if (starvedOnBudget || starvedOnCapacity || starvedOnConflict)
                    alloc.CommitmentsStarveFreshDecisions = true;
            }

            float remainder = Mathf.Max(0f, budget - lockedRemainderConsumed);
            alloc.RemainderGenerated = new ResourceVector(remainder);
            List<DeferredEntry> spillover = alloc.Deferred
                .Where(d => d != null && d.Reason == DeferReason.InsufficientBudget && d.Mission != null)
                .ToList();
            foreach (DeferredEntry deferred in spillover)
            {
                if (remainder <= eps) break;
                MissionProposal m = deferred.Mission;
                float min = ApMinimum(m);
                if (min <= eps || remainder + eps < min) continue;
                ExecutionLane lane = MissionAdmissionPolicy.LaneFor(m);
                if (ConflictsCurrentPortfolio(m) || AtCapacity(lane)) continue;
                ResourceVector physMin = PhysicalMinimum(m);
                if (physMin.AnyPhysical && !physicalRemaining.CoversPhysical(physMin, eps)) continue;

                var v = new ResourceVector(min);
                var funded = new FundedEntry
                {
                    Mission = m, Priority = priority++, Tentative = v, IsCommitment = false,
                    Stage = FundingStage.Remainder, RemainderTopUp = v, PhysicalDraw = physMin,
                };
                alloc.Funded.Add(funded);
                alloc.Deferred.Remove(deferred);
                remainder -= min;
                alloc.RemainderSpent += v;
                physicalRemaining = (physicalRemaining - physMin).ClampLow0();
                alloc.PhysicalFunded += physMin;
                ConsumeSlot(lane);
                AiDebugLog.Write($"[AI][V2] allocator spillover — FUND {StableMissionKey.For(m)} "
                    + $"ap={LogNum(min)} from common remainder; left={LogNum(remainder)}");
            }

            if (alloc.CommitmentsStarveFreshDecisions && alloc.Funded.Any(f => f.IsCommitment))
            {
                float minCommitVal = alloc.Funded.Where(f => f.IsCommitment).Min(f => RankValue(f.Mission));
                bool stillStarvedOnBudget = alloc.Deferred.Any(d => d.Reason == DeferReason.InsufficientBudget
                    && d.Mission != null && RankValue(d.Mission) > minCommitVal);
                bool stillStarvedOnCapacity = alloc.Deferred.Any(d => d.Reason == DeferReason.ExecutionCapacity)
                    && alloc.Funded.Any(f => f.IsCommitment
                        && MissionAdmissionPolicy.LaneFor(f.Mission) != ExecutionLane.None);
                bool stillStarvedOnConflict = alloc.Deferred.Any(d => d.Reason == DeferReason.MissionConflict)
                    && alloc.Funded.Any(f => f.IsCommitment);
                alloc.CommitmentsStarveFreshDecisions = stillStarvedOnBudget || stillStarvedOnCapacity || stillStarvedOnConflict;
            }

            List<FundedEntry> topUpOrder = alloc.Funded
                .Where(fe => !fe.IsCommitment)
                .OrderByDescending(fe => RankValue(fe.Mission))
                .ThenBy(fe => StableMissionKey.For(fe.Mission), MissionKeyComparer.Instance)
                .ToList();
            foreach (Func<MissionProposal, float> target in new Func<MissionProposal, float>[] { ApDesired, ApMaximum })
            {
                foreach (FundedEntry fe in topUpOrder)
                {
                    if (remainder <= eps) break;
                    float want = target(fe.Mission) - fe.Tentative.Ap;
                    if (want <= eps) continue;
                    float give = Mathf.Min(want, remainder);
                    var topUp = new ResourceVector(give);
                    fe.Tentative += topUp;
                    fe.RemainderTopUp += topUp;
                    fe.Stage = FundingStage.Remainder;
                    remainder -= give;
                    alloc.RemainderSpent += topUp;
                }
            }
            alloc.Unused = new ResourceVector(Mathf.Max(0f, remainder));
            alloc.AxisOverdraft = ResourceVector.Zero;
            float committed = alloc.Funded.Sum(fe => fe.Tentative.Ap) + lockedTotal;
            alloc.GlobalOverdraft = new ResourceVector(Mathf.Max(0f, committed - pool.Ap));
            for (int i = 0; i < alloc.Funded.Count; i++)
                alloc.Funded[i].Priority = i;

            string fp = Fingerprint(alloc);
            Converged = fp == _lastFingerprint;
            _lastFingerprint = fp;
            LogDump(alloc, turn);
            return alloc;
        }

        private static bool HasValidContribution(MissionProposal m)
        {
            if (m?.Requirements == null || m.Axes?.Value == null) return false;
            foreach (DesireAxis a in DesireAxes.All)
                if (m.Axes.Value.TryGetValue(a, out float v) && v > 0f)
                    return true;
            return false;
        }

        private static void AddBudgetDeferred(TentativeAllocation alloc, MissionProposal m, float min, float available)
        {
            var required = new ResourceVector(Mathf.Max(0f, min));
            var have = new ResourceVector(Mathf.Max(0f, available));
            alloc.Deferred.Add(new DeferredEntry
            {
                Mission = m, Reason = DeferReason.InsufficientBudget,
                Required = required, Available = have, Missing = (required - have).ClampLow0(),
            });
        }

        private float ApMinimum(MissionProposal m)
        {
            float baseMin = Mathf.Max(0f, m.Requirements?.ApMinimum ?? 0f);
            return _repricedFloors.TryGetValue(StableMissionKey.For(m), out ProvisionRequirement floor)
                ? Mathf.Max(baseMin, floor.Ap) : baseMin;
        }
        private float ApDesired(MissionProposal m) =>
            Mathf.Max(ApMinimum(m), m.Requirements?.ApDesired ?? m.Requirements?.ApMinimum ?? 0f);
        private float ApMaximum(MissionProposal m) =>
            Mathf.Max(ApDesired(m), m.Requirements?.ApMaximum ?? m.Requirements?.ApDesired ?? 0f);
        // Radar model #2 — EffectiveValue is always populated (once, right after BuildMissionSet)
        // before any proposal reaches the allocator, INCLUDING a legitimate zero (a cold axis at
        // radar weight 0). There is no "not computed yet" case left to distinguish from "computed
        // as zero", so this must never fall back to the radar-blind BaseValue: that fallback used
        // to silently re-inflate a zero-priority proposal back to full intrinsic merit, defeating
        // the zero-weight contract. A zero-ranked proposal still competes for leftover AP nobody
        // else wants (see the remainder/spillover passes below) — it is simply never preferred
        // over a positively-ranked alternative.
        private static float RankValue(MissionProposal m) => m?.EffectiveValue ?? 0f;

        private ResourceVector PhysicalMinimum(MissionProposal m)
        {
            MissionRequirements r = m?.Requirements;
            ResourceVector baseMin = r == null ? ResourceVector.Zero
                : new ResourceVector(0f, Mathf.Max(0f, r.HumanMinimum), Mathf.Max(0f, r.EnergyMinimum),
                    Mathf.Max(0f, r.MaterialsMinimum), Mathf.Max(0f, r.TechMinimum));
            if (!_repricedFloors.TryGetValue(StableMissionKey.For(m), out ProvisionRequirement floor)
                || !floor.Physical.AnyPhysical)
                return baseMin;
            return new ResourceVector(0f,
                Mathf.Max(baseMin.Human, floor.Physical.Human),
                Mathf.Max(baseMin.Energy, floor.Physical.Energy),
                Mathf.Max(baseMin.Materials, floor.Physical.Materials),
                Mathf.Max(baseMin.Tech, floor.Physical.Tech));
        }

        private ResourceVector PhysicalDesired(MissionProposal m)
        {
            MissionRequirements r = m?.Requirements;
            ResourceVector min = PhysicalMinimum(m);
            if (r == null) return min;
            return new ResourceVector(0f,
                Mathf.Max(min.Human, r.HumanDesired),
                Mathf.Max(min.Energy, r.EnergyDesired),
                Mathf.Max(min.Materials, r.MaterialsDesired),
                Mathf.Max(min.Tech, r.TechDesired)).ClampLow0();
        }

        private sealed class MissionKeyComparer : IComparer<StableMissionKey>
        {
            public static readonly MissionKeyComparer Instance = new MissionKeyComparer();
            public int Compare(StableMissionKey a, StableMissionKey b) => a.CompareTo(b);
        }

        private string Fingerprint(TentativeAllocation a)
        {
            string funded = string.Join(",", a.Funded
                .Select(fe => $"{StableMissionKey.For(fe.Mission)}={fe.Tentative.Ap.ToString("0.00", CultureInfo.InvariantCulture)}"));
            string deferred = string.Join(",", a.Deferred
                .Select(d => $"{StableMissionKey.For(d.Mission)}:{d.Reason}")
                .OrderBy(x => x, StringComparer.Ordinal));
            string repriced = string.Join(",", _repricedFloors
                .OrderBy(kv => kv.Key, MissionKeyComparer.Instance)
                .Select(kv => $"{kv.Key}:{kv.Value.Fmt()}"));
            return funded + "|" + deferred
                + "|unused=" + a.Unused.Ap.ToString("0.00", CultureInfo.InvariantCulture)
                + "|repriced=" + repriced;
        }

        private static string LogNum(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        private static void LogDump(TentativeAllocation a, int turn)
        {
            AiDebugLog.WriteDeduped($"p{a.PassNumber}",
                $"[AI][V2] allocator p{a.PassNumber} — pool {LogNum(a.InitialPool.Ap)}, "
                + $"locked {LogNum(a.LockedClaim.Ap)}, funded {a.Funded.Count} ({a.Funded.Count(f => f.IsCommitment)} commit), "
                + $"deferred {a.Deferred.Count}, unused {LogNum(a.Unused.Ap)}, "
                + $"overdraft axis/global {LogNum(a.AxisOverdraft.Ap)}/{LogNum(a.GlobalOverdraft.Ap)}"
                + (a.CommitmentsStarveFreshDecisions ? " [commitments starve fresh]" : ""));
        }
    }
}
