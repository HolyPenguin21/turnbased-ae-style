using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  RESOURCE ALLOCATOR  (Strategy V2 build-order step 5)  — implements the ResourceAllocator seam
    // ===========================================================================================
    //  ONE AP pool (AxisBudgetLedger.Balance(), already net of Phase-A spend) -> value-ordered
    //  packing -> an ORDERED TentativeAllocation the ProvisioningManager consumes front-to-back.
    //
    //  RADAR MODEL #1a
    //  --------------------------------------------------------------------------------------------
    //  The radar NO LONGER slices AP per axis. There is one shared pool; a mission draws its AP
    //  straight from it. The radar scales OBJECTIVE VALUE (EffectiveValue) upstream instead — a
    //  golden low-axis opportunity survives on its BaseValue, routine low-axis work is scaled
    //  down. AxisContribution is kept only as a >0 validity check + a telemetry tag; it no longer
    //  sizes anything.
    //
    //  FOUR HARD RULES
    //  --------------------------------------------------------------------------------------------
    //   1. Cross-lane ordering is by MissionProposal.EffectiveValue (BaseValue x radar-weight
    //      scale, stamped once by the orchestrator). WITHIN one lane it is
    //      MissionAdmissionPolicy.AdmissionRank (planner-local LocalAdmissionScore + step-7
    //      retarget hysteresis) so the Recon Explore-vs-Surveil balance survives the N>K beam.
    //      No double-count: EffectiveValue scales by the AXIS-level radar weight; LocalAdmissionScore
    //      mixes in the finer TYPE-level recon sub-desires (Explore vs Surveil vs Refresh), a
    //      separate signal that only orders inside the lane.
    //   2. A mission still names >=1 axis it serves (AxisContribution > 0) or it is invalid; the
    //      axis no longer draws a slice.
    //   3. Whatever is left of the pool after strict funding is a fungible REMAINDER. Missions
    //      deferred ONLY for InsufficientBudget get one second admission pass from it (all
    //      non-budget gates rechecked); what remains then tops up funded missions toward
    //      Desired/Max.
    //   4. The allocator NEVER assigns a concrete army / mover. MoverKnown is ignored here.
    //
    //  RE-ALLOCATE ON FAIL — BOUNDED (risk 2)
    //  --------------------------------------------------------------------------------------------
    //  AllocationSession owns same-turn retry state/policy but never calls ProvisioningManager.
    //  Step 6 wires real provisioning failures through RegisterProvisionFailure -> Pack, bounded by
    //  maxReallocIterations + RejectedThisTurn + cross-turn structural cooldown + fingerprint
    //  convergence. Persistent cooldown is written ONLY by MissionContinuityLayer after the final
    //  MissionOutcomeLedger result is known; an intermediate provisioning failure can never poison
    //  next turn before a later re-pack has a chance to succeed.
    //
    //  RESOURCE DIMENSIONS  (step 9 closure — spec §19.1)
    //  --------------------------------------------------------------------------------------------
    //  AP + Human + Energy + Materials + Tech. AP is one shared pool (AxisBudgetLedger).
    //  Human/Energy/Materials/Tech are ONE global physical pool — never axis-sliced (spec §18 /
    //  §19.3): a mission is funded only if its AP draw AND the whole global physical draw succeed
    //  together, atomically (spec §19.4 / AC #17). The physical
    //  pool the allocator sees is already post-Initiative + post-Phase-A (spec §41 / §16 / AC
    //  #19/#20) — the real remaining stockpile, never a re-reservation of what is already spent.
    // ===========================================================================================

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

        // AP-side only — the fresh/remainder pack logic is AP-driven; physical is a separate gate.
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

        // True iff every physical dimension of `need` fits within this vector (AP ignored — the AP
        // check is the axis-slice path). Atomic multi-resource admission (spec §19.4).
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

    // Round 7 (Problem 3) — the GENERIC multi-resource envelope a provisioner reports as "what this
    // mission's real, actor-specific cost actually needs" when the funded envelope was too small.
    // Replaces the old bare-float RequiredAp: any provisioner (Recon ground/air today; Aggression /
    // Defence / Economy / Development / equipment / abilities tomorrow) can report BOTH an AP floor
    // and a physical (Human/Energy/Materials/Tech) floor through the SAME struct, and the allocator
    // merges every report for one mission key as a component-wise MAXIMUM (a minimum envelope for
    // that one mission, never an accumulator — see ResourceAllocator._repricedFloors).
    public readonly struct ProvisionRequirement
    {
        public readonly float Ap;
        public readonly ResourceVector Physical; // .Ap component is always 0 here — AP lives in Ap above

        public ProvisionRequirement(float ap, ResourceVector physical)
        {
            Ap = Mathf.Max(0f, ap);
            Physical = physical.ClampLow0();
        }

        public static readonly ProvisionRequirement Zero = new ProvisionRequirement(0f, ResourceVector.Zero);

        public static ProvisionRequirement ApOnly(float ap) => new ProvisionRequirement(ap, ResourceVector.Zero);

        // Component-wise MAXIMUM merge — a minimum envelope for one mission's outstanding repricing,
        // NOT a sum. When both sides' Physical is all-zero (every existing AP-only axis — Aggression/
        // Defence/Economy/Development, and Ground Scout/Raid) this reduces exactly to
        // Mathf.Max(old.Ap, new.Ap) — today's pre-round-7 AP-only reprice-floor behaviour.
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

    // Declared in step 5; ProvisioningManager fills it in step 6. The concrete REASON a mission
    // could not be provisioned. Retry policy is a SEPARATE axis (ProvisionDisposition) so the
    // allocator branches on "what do I do about it" without a special-case per reason, and
    // telemetry keeps the "why" at full resolution — e.g. TargetSatisfied ("another scout already
    // opened that hex, the work is simply no longer needed") must never read back as a failure
    // that wants retrying.
    public enum ProvisionFailureKind
    {
        None,
        MoverContended,      // a capable mover exists, but this allocation cycle handed every one to a higher-priority mission
        NoMoverExists,       // transient capability shortage: no executor exists yet (or none with required stealth)
        EnvelopeTooSmall,    // the funded AP envelope cannot cover the real mover's cost — carries RequiredAp for repricing
        NoExecutableStep,    // mover + budget are fine, but no safe first step toward the target exists right now
        TargetSatisfied,     // the objective is already met (Explore focus hex already visited) — drop, not fail
        TargetInvalidated,   // the world changed under the mission (focus hex now holds a known army)
        NoObservationVantage,// Surveil: a capable scout exists, but NO on-map hex within any scout's vision can observe the focus
        AssemblyInfeasible,  // structural: the mission cannot be made executable by any assemblable means
        SortieNotWorthwhile, // air recon: resources are technically sufficient, but the staged reservation
                             // decision (Energy runway -> hand/deck Energy pressure -> recon value) says this
                             // sortie is not worth reserving Energy/AP for THIS turn. Recomputed every turn.
    }

    // The retry semantics the allocator applies to a ProvisionFailure, kept orthogonal to Kind.
    public enum ProvisionDisposition
    {
        RetryNextTurn,       // out of the running THIS turn; a fresh snapshot re-proposes it next turn. No cooldown.
        DropThisTurn,        // same mechanics as RetryNextTurn, but semantically "no longer wanted" (telemetry only).
        RepriceThisTurn,     // re-fund THIS turn at a raised AP floor (ProvisionFailure.RequiredAp), still <= funded.Tentative.
        RejectWithCooldown,  // structural dead end: reject this pack; continuity writes cross-turn cooldown from final facts.
    }

    // Stable across turns so ordering/reject/cooldown/fingerprint all address the same mission.
    // TargetId carries the mission's typed strategic identity beyond its hex: for a Surveil it is
    // the tracked ArmyData.Id, so Surveil(#42 @ H) and Surveil(#77 @ H) are DIFFERENT missions and
    // a NoObservationVantage cooldown on one never lands on the other. Explore / other kinds: 0.
    public readonly struct StableMissionKey : IEquatable<StableMissionKey>
    {
        public readonly MissionKind Kind;
        public readonly int SubKind;
        public readonly int TargetId;
        public readonly int Q;
        public readonly int R;

        public StableMissionKey(MissionKind kind, int subKind, int targetId, int q, int r)
        {
            Kind = kind;
            SubKind = subKind;
            TargetId = targetId;
            Q = q;
            R = r;
        }

        public static StableMissionKey For(MissionProposal m)
        {
            if (m != null && m.Kind == MissionKind.Scout && m.Target is ScoutMissionTarget t)
            {
                int targetId = t.Kind == ScoutTargetKind.Surveil ? (t.Contact?.Army?.ArmyId ?? 0) : 0;
                return new StableMissionKey(MissionKind.Scout, (int)t.Kind, targetId, t.FocusHex.Q, t.FocusHex.R);
            }
            // Step 9 — Raid identity is the tracked target army (spec §25). Hex is telemetry /
            // tie-break only, so it stays out of the key: a moving target is the same mission.
            if (m != null && m.Kind == MissionKind.Raid && m.Target is RaidMissionTarget rt)
                return new StableMissionKey(MissionKind.Raid, (int)AggressionObjectiveKind.Raid, rt.TargetArmyId, 0, 0);
            return new StableMissionKey(m?.Kind ?? MissionKind.Scout, 0, 0, 0, 0);
        }

        public bool Equals(StableMissionKey o) =>
            Kind == o.Kind && SubKind == o.SubKind && TargetId == o.TargetId && Q == o.Q && R == o.R;
        public override bool Equals(object obj) => obj is StableMissionKey o && Equals(o);
        public override int GetHashCode() => ((int)Kind, SubKind, TargetId, Q, R).GetHashCode();
        public override string ToString() =>
            Kind == MissionKind.Scout
                ? (TargetId != 0
                    ? $"{Kind}({(ScoutTargetKind)SubKind} #{TargetId} {Q},{R})"
                    : $"{Kind}({(ScoutTargetKind)SubKind} {Q},{R})")
                : Kind == MissionKind.Raid
                    ? $"Raid(#{TargetId})"
                    : $"{Kind}";

        public int CompareTo(StableMissionKey o)
        {
            int c = Kind.CompareTo(o.Kind); if (c != 0) return c;
            c = SubKind.CompareTo(o.SubKind); if (c != 0) return c;
            c = TargetId.CompareTo(o.TargetId); if (c != 0) return c;
            c = Q.CompareTo(o.Q); if (c != 0) return c;
            return R.CompareTo(o.R);
        }
    }

    public enum FundingStage { Strict, Remainder }

    public sealed class FundedEntry
    {
        public MissionProposal Mission;
        public int Priority;
        public ResourceVector Tentative;

        // Radar model #1a — one AP pool, no per-axis attribution. StrictAp is the admission draw
        // (Tentative minus the fungible remainder top-up).
        public float StrictAp;
        public ResourceVector RemainderTopUp;

        // Step 9 — the global physical draw (Human/Energy/Materials/Tech) this mission was funded
        // for. NOT axis-attributed (spec §18) — one global pool. Locked verbatim on provision
        // success so a re-pack cannot hand the same physical resources out twice (spec §19.5).
        public ResourceVector PhysicalDraw;

        public bool IsCommitment;
        public FundingStage Stage;
    }

    public enum DeferReason
    {
        InsufficientBudget,
        InsufficientPhysical,      // step 9 — global Human/Energy/Materials/Tech pool cannot cover this mission
        InvalidContribution,
        RejectedThisTurn,
        OnCooldown,
        CommitmentPoolExhausted,   // a commitment whose funding would push Σ commitments past the real AP pool
        // Step 7.1 — NOT a failure, NOT a cooldown, NOT a structural / provisioning problem. A good
        // candidate existed but did not fit THIS turn's funded portfolio. Both are recomputed from
        // scratch on every Pack(): a re-pack after a provisioning failure re-evaluates them and can
        // fund a backup the same turn.
        ExecutionCapacity,         // the mission's execution lane is already at K (locked + commitments + funded)
        MissionConflict,           // pairwise-conflicts a currently funded / locked mission in the same lane
    }

    public sealed class DeferredEntry
    {
        public MissionProposal Mission;
        public DeferReason Reason;
        // Retained for wire compatibility with older log/telemetry readers; never set under the
        // single-pool model.
        public DesireAxis? BottleneckAxis;
        public ResourceVector Required;
        public ResourceVector Available;
        public ResourceVector Missing;

        // OnCooldown telemetry — populated only for DeferReason.OnCooldown. Kept on the row so the
        // log can explain the historical cause instead of printing a context-free boolean.
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
        public ResourceVector LockedClaim;   // Σ actual claims of missions provisioned in an earlier pass this turn

        // Step 9 — global physical pool telemetry (Human/Energy/Materials/Tech, spec §19).
        public ResourceVector PhysicalPool;      // real post-Phase-A stockpile
        public ResourceVector PhysicalLocked;    // Σ physical claims of missions provisioned earlier this turn
        public ResourceVector PhysicalFunded;    // Σ physical draw of this pack's funded set

        // Two DISTINCT overdraft measures:
        //  AxisOverdraft  — Σ of the amounts individual slices were driven negative (a commitment /
        //                   locked claim outrunning its OWN axis budget). Expected under many-to-many.
        //  GlobalOverdraft— max(0, total AP actually committed − the whole sliceable pool). The real
        //                   "spent more than we have" alarm; ~0 unless commitments exceed the pool.
        public ResourceVector AxisOverdraft;
        public ResourceVector GlobalOverdraft;
        public int PassNumber;

        // Telemetry breadcrumb for the future pre-emption pass (step 9+): a fresh mission worth
        // MORE (BaseValue) than some funded commitment was deferred purely because commitments ate
        // the pool, OR a commitment itself could not be funded within the real AP pool. Nothing
        // acts on it yet — commitments are still honoured to completion.
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

    // Cross-turn state only. RejectedThisTurn/pass/fingerprint live in AllocationSession. The
    // state stores the WHY/start/until triple as well as the deadline so Demand/mission telemetry
    // can reason about blocked work without inventing a second cooldown registry.
    public sealed class AiAllocatorState
    {
        private readonly Dictionary<StableMissionKey, MissionCooldownInfo> _cooldowns =
            new Dictionary<StableMissionKey, MissionCooldownInfo>();

        // Inclusive `until`: a failure on turn T with cooldown=2 suppresses T+1 and T+2.
        public bool OnCooldown(StableMissionKey k, int turn) => TryGetCooldown(k, turn, out _);

        public bool TryGetCooldown(StableMissionKey k, int turn, out MissionCooldownInfo info)
        {
            if (_cooldowns.TryGetValue(k, out info) && turn <= info.UntilTurn)
                return true;
            info = default;
            return false;
        }

        // Canonical cross-turn write. MissionContinuityLayer is the runtime owner; the public API
        // stays here because tests/sims seed historical state directly.
        public void StartCooldown(StableMissionKey k, int startedTurn, int untilTurn, string reason)
        {
            var next = new MissionCooldownInfo(startedTurn, untilTurn, reason);
            if (!_cooldowns.TryGetValue(k, out MissionCooldownInfo cur) || untilTurn > cur.UntilTurn)
                _cooldowns[k] = next;
        }

        // Compatibility helper for older harnesses that seed a deadline only.
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
            if (player == null)
                return new AiAllocatorState();
            if (!ByPlayer.TryGetValue(player, out AiAllocatorState s))
                ByPlayer[player] = s = new AiAllocatorState();
            return s;
        }

        // Non-creating read — for a pure/deterministic caller that must not register a new state
        // entry as a side effect (AggressionDemandEvaluator). null == no state yet == no cooldowns.
        public static AiAllocatorState Peek(PlayerSetupData player) =>
            player != null && ByPlayer.TryGetValue(player, out AiAllocatorState s) ? s : null;

        public static void Clear() => ByPlayer.Clear();
    }

    internal static class ResourceAllocator
    {
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

        // Strategy V2 Strategic Manager — the shared per-turn AP entitlement split. When present,
        // per-axis slice size comes from this (already net of Phase-A demand-fulfilment spend)
        // instead of re-splitting current AP by the radar (NO second radar split). Null in a bare
        // unit test / sim -> fall back to radar * pool as before.
        private readonly AxisBudgetLedger _ledger;

        // AI-RECON-01 — AP / Energy the Recon Air Reservation Prepass has set aside for a
        // planned-but-unlaunched recon sortie. Both are netted out of the allocator's own global
        // pools (AP off the top of Pack's pool; Energy off the physical pool) so a raid /
        // materialisation chain the allocator funds cannot spend them — parity with the ledger AP
        // debit the pipeline applies before Phase A.
        private readonly float _protectedAp;
        private readonly float _protectedPhysicalEnergy;

        private readonly HashSet<StableMissionKey> _rejectedThisTurn = new HashSet<StableMissionKey>();
        // Step 6 repricing feedback (risk 2). A mission that failed provisioning with
        // EnvelopeTooSmall(requiredAp) is NOT rejected — instead its AP minimum is raised to
        // requiredAp for every later Pack() this turn, so the next pass either funds it at the
        // real cost or defers it honestly on budget. Never lowers a floor; cleared each turn with
        // the session. Provisioning still refuses to claim above funded.Tentative, so the raised
        // floor can only ever move a mission from "funded too low to execute" to "funded at cost"
        // or "deferred — the axis slice genuinely can't afford this mover".
        // Round 7 (Problem 3) — GENERIC multi-resource floor, one entry per mission key, merged via
        // ProvisionRequirement.Max on every RepriceThisTurn report (component-wise maximum — a
        // minimum envelope for THIS mission, never an accumulator). Ap-only callers (every axis
        // other than air Recon today) simply never populate Physical, so this degrades exactly to
        // the old float-floor behaviour for them.
        private readonly Dictionary<StableMissionKey, ProvisionRequirement> _repricedFloors =
            new Dictionary<StableMissionKey, ProvisionRequirement>();
        // Missions physically provisioned in an earlier pass THIS turn, with their FUNDING
        // PROVENANCE (strict AP part + the fungible remainder part), scaled to what provisioning
        // actually claimed. A re-pack takes each locked mission's strict AP off the top of the one
        // pool and removes its remainder part from the fungible pool; the mission itself is dropped
        // from re-funding.
        private readonly Dictionary<StableMissionKey, LockedAllocation> _lockedClaims =
            new Dictionary<StableMissionKey, LockedAllocation>();
        private string _lastFingerprint;

        private readonly struct LockedAllocation
        {
            public readonly float StrictAp;                           // strict admission draw, as granted
            public readonly float RemainderAp;                        // fungible top-up, as granted
            public readonly float GrantedAp;                          // StrictAp + RemainderAp (== FundedEntry.Tentative)
            public readonly float ClaimedAp;                          // what provisioning actually took
            // Step 9 — the global physical resources (H/E/M/T) this locked mission consumed. A
            // re-pack subtracts this from the global physical pool so the same units can't be
            // handed out again (spec §19.5 / AC #18).
            public readonly ResourceVector PhysicalClaim;
            // Step 7.1 — the mission provisioned in an earlier pass this turn. Kept so a re-pack
            // still charges its execution-lane slot (fundedReconCount <= K counts locked successes)
            // and so a fresh candidate can be conflict-tested against work already under way.
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

            // What provisioning REALLY consumed, resolved as a WATERFALL (not a flat scale): the
            // remainder top-up — a nice-to-have that improved an already-accepted mission — is the
            // first thing to disappear when the authoritative (often integer) claim lands below the
            // granted (float) envelope. Strict funding only shrinks once the claim drops under the
            // strict level itself. Claiming ABOVE the granted envelope is an invariant violation
            // (provisioning must stay within Tentative); it is clamped and logged.
            public void Resolve(float eps, out float strictScale, out float remainderConsumed, out bool overclaim)
            {
                float claimed = Mathf.Max(0f, ClaimedAp);
                overclaim = claimed > GrantedAp + eps;
                if (overclaim)
                    claimed = GrantedAp;

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

        // `radar` is accepted for signature stability (radar model #1a stopped slicing AP; a
        // future radar-aware cross-lane tie-break could use it again) but no longer read.
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

        // Step 6 calls this after a real atomic provisioning failure. Takes the whole FundedEntry
        // (not just the mission) so the allocator sees the envelope it granted, and the typed
        // ProvisionFailure so it branches on Disposition alone — never a switch on Kind.
        public void RegisterProvisionFailure(FundedEntry funded, ProvisionFailure failure)
        {
            if (funded?.Mission == null)
                return;

            StableMissionKey key = StableMissionKey.For(funded.Mission);
            HasNewFailures = true;
            // Any provisioning failure means the NEXT Pack() is solving a genuinely different
            // allocation problem (a mission dropped, or a floor raised) — force it to be treated
            // as new even if the funded/deferred/slice fingerprint would otherwise match.
            _lastFingerprint = null;

            switch (failure.Disposition)
            {
                case ProvisionDisposition.RepriceThisTurn:
                {
                    ProvisionRequirement cur = _repricedFloors.TryGetValue(key, out ProvisionRequirement f)
                        ? f : ProvisionRequirement.Zero;
                    _repricedFloors[key] = ProvisionRequirement.Max(cur, failure.Requirement);
                    // Deliberately NOT added to _rejectedThisTurn — it must return next pass at
                    // the raised floor.
                    break;
                }
                case ProvisionDisposition.RejectWithCooldown:
                    // SAME-TURN owner only. The final ledger may still be superseded by a later
                    // success/reclassification; MissionContinuityLayer alone writes cross-turn
                    // cooldown after Finalize() has established the authoritative outcome.
                    _rejectedThisTurn.Add(key);
                    break;
                default: // RetryNextTurn / DropThisTurn — out this turn, re-proposed fresh next turn, no cooldown
                    _rejectedThisTurn.Add(key);
                    break;
            }
        }

        // Step 6 calls this after a real atomic provisioning success — locks in the funding
        // provenance (strict per-axis draw + remainder part) and the AP actually claimed, so a
        // later re-pack applies that spend to the right slices instead of recomputing a fresh
        // Tentative for work that is done.
        // RECON-AIR-01 — `claimedPhysical` lets a caller (ProvisioningManager.ProvisionAir) lock the
        // REAL physical draw (Energy) an air actor's bound cost resolved to, mirroring how
        // `claimedAp` already overrides the funded AP envelope with the real figure. Omitted (null)
        // keeps the pre-existing behaviour — the funded Desired estimate is what gets locked — which
        // is exactly right for Ground/Raid, whose real physical draw never differs from what was
        // funded.
        public void RegisterProvisionSuccess(FundedEntry funded, float claimedAp, ResourceVector? claimedPhysical = null)
        {
            if (funded?.Mission == null)
                return;
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

            // 1. `pool` = the raw AP budget base for this turn: snapshot AP minus the Manager
            //    reserve. Used only for the commitment / global-overdraft ceiling checks below; the
            //    actual fundable AP is `budget` (the ledger pool, net of Phase-A spend), computed
            //    in step 2. `pool` is deliberately NOT reduced by locked-mission spend — that is
            //    applied to `budget` instead.
            float rawAp = _snap?.Self?.ActionPoints ?? 0;
            float reserve = Mathf.Max(0f, AiConfigV2.housekeepingApReserve);
            // AI-RECON-01 — the recon-air launch AP the prepass reserved is off the top here too,
            // not only in the ledger pool: commitments may overdraft the pool and are checked
            // against THIS global pool, so without the debit a Hard raid could grab AP a guaranteed
            // sortie needs and the terminal air fallback would then fail to launch.
            var pool = new ResourceVector(Mathf.Max(0f, rawAp - reserve - _protectedAp));
            alloc.InitialPool = pool;
            alloc.ManagerReserve = new ResourceVector(reserve + _protectedAp);

            // Locked funding provenance, resolved WATERFALL-style (remainder top-up disappears
            // first when the real claim < granted envelope; strict only shrinks below the strict
            // level): strict consumption is taken off the top of the one pool, fungible remainder
            // consumption is removed from the remainder pool in step 5.
            float lockedStrict = 0f;
            float lockedRemainderConsumed = 0f;
            float lockedTotal = 0f;
            foreach (KeyValuePair<StableMissionKey, LockedAllocation> lc in _lockedClaims)
            {
                lc.Value.Resolve(eps, out float strictScale, out float remainderConsumed, out bool overclaim);
                if (overclaim)
                {
                    // §2.2 — surface the same fact as a structured invariant error tied to the
                    // concrete MissionAttemptId, not just a context-free allocator WARN.
                    AiV2Trace.CheckError(lc.Value.Mission?.AttemptId, "ProvisionClaimExceedsEnvelope",
                        $"claimed={LogNum(lc.Value.ClaimedAp)} granted={LogNum(lc.Value.GrantedAp)} key={lc.Key}");
                    AiDebugLog.Write($"[AI][V2] allocator — WARN locked claim {LogNum(lc.Value.ClaimedAp)} "
                        + $"exceeds granted {LogNum(lc.Value.GrantedAp)} for {lc.Key} — clamped (provisioning "
                        + "must stay within Tentative)");
                }
                lockedTotal += Mathf.Min(lc.Value.ClaimedAp, lc.Value.GrantedAp);
                lockedRemainderConsumed += remainderConsumed;
                lockedStrict += lc.Value.StrictAp * strictScale;
            }
            alloc.LockedClaim = new ResourceVector(lockedTotal);

            // 2. ONE AP pool (radar model #1a — no per-axis slices). WITH a shared AxisBudgetLedger
            //    (the normal V2 path) the pool size IS ledger.Balance() — already net of Phase-A
            //    demand-fulfilment spend. Without a ledger (bare test / sim) fall back to the raw
            //    Pack pool. Strict AP that a locked mission already drew is taken off the top.
            float budget = Mathf.Max(0f, (_ledger != null ? _ledger.Balance() : pool.Ap) - lockedStrict);

            // 2b. Step 9 — the ONE global physical pool (Human/Energy/Materials/Tech). NOT
            //     axis-sliced (spec §18): it is the real post-Initiative + post-Phase-A stockpile,
            //     minus what missions provisioned in an earlier pass this turn already claimed
            //     (spec §19.5 — a re-pack can never re-hand-out the same physical units). AP stays
            //     on the radar slices above; physical is a flat atomic gate below.
            ResourceBundle stock = _snap?.Self?.Stockpile ?? default;
            // AI-RECON-01 — the recon-air reservation's protected Energy is not part of the pool the
            // allocator may hand to a raid / materialisation chain.
            float poolEnergy = Mathf.Max(0f, stock.Energy - _protectedPhysicalEnergy);
            var physicalPool = new ResourceVector(0f, stock.Human, poolEnergy, stock.Materials, stock.Tech);
            var lockedPhysical = ResourceVector.Zero;
            foreach (LockedAllocation lc in _lockedClaims.Values)
                lockedPhysical += lc.PhysicalClaim;
            ResourceVector physicalRemaining = (physicalPool - lockedPhysical).ClampLow0();
            alloc.PhysicalPool = physicalPool;
            alloc.PhysicalLocked = lockedPhysical;

            int priority = 0;

            // Step 7.1 — execution-capacity admission. K is a portfolio constraint, NOT a resource:
            // it is not in ResourceVector and does not slice. fundedCount(lane) <= Capacity(lane)
            // for every Pack(), counting (in this order of consumption) locked successes from an
            // earlier pass this turn, then commitments, then fresh missions. Seed it with the
            // locked successes so a re-pack can top up to K but never past it.
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

            // 3. Commitments first. Sticky/pre-paid: they MAY drive the one AP pool negative (that
            //    is the point — a funding protection against Radar noise). Step-7 guards:
            //      · skip a key already provisioned this turn (_lockedClaims — its provenance is
            //        already applied to the slices) or already failed this turn (_rejectedThisTurn),
            //        or defensively on structural cooldown (ReconcileAfterTurn should have retired
            //        the intent, but the allocator does not depend on that);
            //      · Σ commitment Tentative may NOT exceed the real AP pool. A commitment can
            //        borrow another axis's budget; it can never conjure AP that isn't there
            //        (invariant for step 9's multi-raid case). Overflow -> deferred +
            //        CommitmentsStarveFreshDecisions.
            float committedApSoFar = 0f;
            foreach (Commitment c in _commitments)
            {
                MissionProposal m = c?.Mission;
                if (m == null)
                    continue;

                StableMissionKey ckey = StableMissionKey.For(m);
                if (_lockedClaims.ContainsKey(ckey) || _rejectedThisTurn.Contains(ckey) || _state.OnCooldown(ckey, turn))
                    continue;

                if (!HasValidContribution(m))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.InvalidContribution });
                    continue;
                }

                // Commitments consume K before any fresh mission — but they get NO magic extra
                // slot. Two Soft commitments on a K=2 lane leave zero fresh capacity; a third
                // commitment defers on ExecutionCapacity (ordered Hard->Soft->older by
                // ResolveActive, so which one loses is deterministic).
                ExecutionLane clane = MissionAdmissionPolicy.LaneFor(m);
                if (AtCapacity(clane))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.ExecutionCapacity });
                    alloc.CommitmentsStarveFreshDecisions = true;
                    continue;
                }

                float askAp = ApDesired(m);
                if (committedApSoFar + askAp > pool.Ap + eps)
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.CommitmentPoolExhausted });
                    alloc.CommitmentsStarveFreshDecisions = true;
                    continue;
                }

                // Step 9 — a commitment must also fit the GLOBAL physical pool, atomically with AP
                // (spec §19.4). A Hard raid the AI has started still cannot conjure Energy/Materials
                // that are not there — it suspends as PoolExhausted and gets a fresh shot next turn.
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
                    Mission = m,
                    Priority = priority++,
                    Tentative = ask,
                    StrictAp = askAp,
                    IsCommitment = true,
                    Stage = FundingStage.Strict,
                    PhysicalDraw = cPhys,
                };

                // Sticky/pre-paid: a commitment MAY drive the pool negative — that is the point, a
                // funding protection against Radar noise. The global-pool guard above already
                // bounded Σ commitments to the real AP pool.
                budget -= askAp;

                alloc.Funded.Add(fe);
                alloc.CommitmentDraw += ask;
                alloc.PhysicalFunded += cPhys;
            }

            // 4. Fresh missions — TRUE CROSS-LANE k-way MERGE (spec §21). Per-lane queues are each
            //    ordered by MissionAdmissionPolicy.AdmissionRank (the None lane by EffectiveValue)
            //    so the WITHIN-lane balance (Recon Explore-vs-Surveil, Raid feasibility ordering)
            //    survives the N>K beam. Then, repeatedly, the queue HEAD with the highest
            //    EffectiveValue is taken and admission-tested — LocalAdmissionScore orders inside a
            //    lane, EffectiveValue (BaseValue x radar-weight scale) orders BETWEEN lanes. Radar
            //    model #1a: the radar does not size an AP budget here at all (one shared pool). Tie-
            //    break: EffectiveValue DESC, then StableMissionKey ASC — deterministic regardless of
            //    Dictionary iteration order. Per candidate: conflict -> capacity -> AP budget ->
            //    global physical (atomic H/E/M/T). A proposal that is ALSO an active commitment is
            //    funded through the commitment loop above only.
            var commitmentKeys = new HashSet<StableMissionKey>(_commitments
                .Where(c => c?.Mission != null)
                .Select(c => StableMissionKey.For(c.Mission)));
            List<MissionProposal> freshPool = _missions
                .Where(m => m != null
                    && !_lockedClaims.ContainsKey(StableMissionKey.For(m))
                    && !commitmentKeys.Contains(StableMissionKey.For(m)))
                .ToList();

            var laneQueues = new Dictionary<ExecutionLane, Queue<MissionProposal>>();
            foreach (IGrouping<ExecutionLane, MissionProposal> g in freshPool
                .GroupBy(m => MissionAdmissionPolicy.LaneFor(m)))
            {
                IEnumerable<MissionProposal> ordered = g.Key == ExecutionLane.None
                    ? g.OrderByDescending(RankValue)
                        .ThenBy(m => StableMissionKey.For(m), MissionKeyComparer.Instance)
                    : g.OrderByDescending(m => MissionAdmissionPolicy.AdmissionRank(m))
                        .ThenBy(m => StableMissionKey.For(m), MissionKeyComparer.Instance);
                laneQueues[g.Key] = new Queue<MissionProposal>(ordered);
            }

            while (laneQueues.Values.Any(q => q.Count > 0))
            {
                ExecutionLane lane = ExecutionLane.None;
                MissionProposal m = null;
                foreach (KeyValuePair<ExecutionLane, Queue<MissionProposal>> kv in laneQueues)
                {
                    if (kv.Value.Count == 0)
                        continue;
                    MissionProposal head = kv.Value.Peek();
                    // Radar model #1a — cross-lane ordering is by EffectiveValue (BaseValue scaled
                    // by radar weight). Within-lane order is already baked into the queue above.
                    if (m == null
                        || RankValue(head) > RankValue(m) + eps
                        || (Mathf.Abs(RankValue(head) - RankValue(m)) <= eps
                            && StableMissionKey.For(head).CompareTo(StableMissionKey.For(m)) < 0))
                    {
                        m = head;
                        lane = kv.Key;
                    }
                }
                laneQueues[lane].Dequeue();

                {
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
                            Mission = m,
                            Reason = DeferReason.OnCooldown,
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

                    // Mission -> ResourceAllocator -> Provisioning -> actor binding (original V2
                    // pipeline order, spec §7). A Scout mission is funded exactly like every other
                    // mission kind here — no pre-reserved-actor gate; ProvisioningManager/
                    // ReconAssignmentPlanner bind the concrete scout AFTER funding and report a
                    // generic ProvisionFailure (MoverContended / NoMoverExists / ...) if none exists.

                    // Conflict BEFORE capacity: a conflicting candidate is categorically inadmissible
                    // against the current portfolio (and consumes no slot), so the more-specific reason
                    // is reported and a re-pack that drops the conflicting incumbent frees it. Tested
                    // against every funded mission in this lane (commitments included) + every locked
                    // success this turn. commitment-vs-commitment is NOT tested here — dropping an
                    // already-bound commitment over a conflict is pre-emption (deferred).
                    if (lane != ExecutionLane.None
                        && (alloc.Funded.Any(fe => fe.Mission != null
                                && MissionAdmissionPolicy.LaneFor(fe.Mission) == lane
                                && MissionAdmissionPolicy.Conflicts(fe.Mission, m))
                            || _lockedClaims.Values.Any(lc => MissionAdmissionPolicy.LaneFor(lc.Mission) == lane
                                && MissionAdmissionPolicy.Conflicts(lc.Mission, m))))
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

                    // Global physical gate — atomic with the AP axis draws (spec §19.4). Checked at
                    // MINIMUM before we commit any slice mutation; a funded mission draws Desired.
                    ResourceVector physMin = PhysicalMinimum(m);
                    ResourceVector physDraw = PhysicalDesired(m);
                    if ((physMin.AnyPhysical || physDraw.AnyPhysical)
                        && !physicalRemaining.CoversPhysical(physMin, eps))
                    {
                        alloc.Deferred.Add(new DeferredEntry
                        {
                            Mission = m,
                            Reason = DeferReason.InsufficientPhysical,
                            Required = physMin,
                            Available = physicalRemaining,
                            Missing = (physMin - physicalRemaining).ClampLow0(),
                        });
                        continue;
                    }
                    // Fund physical at Desired only if the whole Desired still fits; otherwise fall
                    // back to Minimum (which just passed). No fungible top-up pool for physical.
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
                        Mission = m,
                        Priority = priority++,
                        Tentative = v,
                        StrictAp = fundAp,
                        IsCommitment = false,
                        Stage = FundingStage.Strict,
                        PhysicalDraw = physDraw,
                    };
                    budget -= fundAp;
                    physicalRemaining = (physicalRemaining - physDraw).ClampLow0();

                    alloc.Funded.Add(funded);
                    alloc.StrictFunded += v;
                    alloc.PhysicalFunded += physDraw;
                    ConsumeSlot(lane);
                }
            }

            // 4b. Telemetry: did a funded commitment crowd out a fresh decision — on budget (a more
            //     valuable fresh mission deferred) or on execution capacity (commitments ate every
            //     K slot in a lane)? Step 7.1: ExecutionCapacity is not a failure, but a fresh
            //     recon deferred purely because commitments held all K slots is exactly the
            //     "commitments starve fresh" signal the future pre-emption pass wants.
            if (!alloc.CommitmentsStarveFreshDecisions && alloc.Funded.Any(f => f.IsCommitment))
            {
                float minCommitVal = alloc.Funded.Where(f => f.IsCommitment).Min(f => RankValue(f.Mission));
                bool starvedOnBudget = alloc.Deferred.Any(d => d.Reason == DeferReason.InsufficientBudget
                    && d.Mission != null && RankValue(d.Mission) > minCommitVal);
                bool starvedOnCapacity = alloc.Deferred.Any(d => d.Reason == DeferReason.ExecutionCapacity)
                    && alloc.Funded.Any(f => f.IsCommitment
                        && MissionAdmissionPolicy.LaneFor(f.Mission) != ExecutionLane.None);
                if (starvedOnBudget || starvedOnCapacity)
                    alloc.CommitmentsStarveFreshDecisions = true;
            }

            // 5. Whatever is left of the one pool after strict funding is the fungible remainder.
            //    AP a locked mission already spent as a remainder top-up in an earlier pass was
            //    never taken off `budget` (only its strict part was) — take it out now.
            float remainder = Mathf.Max(0f, budget - lockedRemainderConsumed);
            alloc.RemainderGenerated = new ResourceVector(remainder);

            // 5b. Spillover admission: strict funding is the first pass; a mission deferred ONLY on
            // InsufficientBudget gets one more admission attempt from the remainder. This is not
            // general overdraft: conflict/capacity/physical gates are rechecked and the mission
            // receives only its executable AP minimum; ordinary top-up happens afterwards.
            List<DeferredEntry> spillover = alloc.Deferred
                .Where(d => d != null && d.Reason == DeferReason.InsufficientBudget && d.Mission != null)
                .ToList();
            foreach (DeferredEntry deferred in spillover)
            {
                if (remainder <= eps)
                    break;

                MissionProposal m = deferred.Mission;
                float min = ApMinimum(m);
                if (min <= eps || remainder + eps < min)
                    continue;

                ExecutionLane lane = MissionAdmissionPolicy.LaneFor(m);
                bool conflict = lane != ExecutionLane.None
                    && (alloc.Funded.Any(fe => fe.Mission != null
                            && MissionAdmissionPolicy.LaneFor(fe.Mission) == lane
                            && MissionAdmissionPolicy.Conflicts(fe.Mission, m))
                        || _lockedClaims.Values.Any(lc => MissionAdmissionPolicy.LaneFor(lc.Mission) == lane
                            && MissionAdmissionPolicy.Conflicts(lc.Mission, m)));
                if (conflict || AtCapacity(lane))
                    continue;

                ResourceVector physMin = PhysicalMinimum(m);
                if (physMin.AnyPhysical && !physicalRemaining.CoversPhysical(physMin, eps))
                    continue;

                var v = new ResourceVector(min);
                var funded = new FundedEntry
                {
                    Mission = m,
                    Priority = priority++,
                    Tentative = v,
                    IsCommitment = false,
                    Stage = FundingStage.Remainder,
                    RemainderTopUp = v,
                    PhysicalDraw = physMin,
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

            // Stage-4 starvation telemetry was computed before spillover existed. If every budget-
            // starved fresh decision was just recovered, do not leave a stale starvation flag.
            if (alloc.CommitmentsStarveFreshDecisions && alloc.Funded.Any(f => f.IsCommitment))
            {
                float minCommitVal = alloc.Funded.Where(f => f.IsCommitment).Min(f => RankValue(f.Mission));
                bool stillStarvedOnBudget = alloc.Deferred.Any(d => d.Reason == DeferReason.InsufficientBudget
                    && d.Mission != null && RankValue(d.Mission) > minCommitVal);
                bool stillStarvedOnCapacity = alloc.Deferred.Any(d => d.Reason == DeferReason.ExecutionCapacity)
                    && alloc.Funded.Any(f => f.IsCommitment
                        && MissionAdmissionPolicy.LaneFor(f.Mission) != ExecutionLane.None);
                alloc.CommitmentsStarveFreshDecisions = stillStarvedOnBudget || stillStarvedOnCapacity;
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
                    if (remainder <= eps)
                        break;

                    float want = target(fe.Mission) - fe.Tentative.Ap;
                    if (want <= eps)
                        continue;

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

            // 6. Overdraft diagnostics.
            //    AxisOverdraft: retained field, always 0 under the single pool (no per-axis slice
            //    to drive negative). GlobalOverdraft: total AP actually committed this turn (fresh
            //    Tentative + locked claims) beyond the whole pool — the real alarm; ~0 unless
            //    commitments outright exceed the pool.
            alloc.AxisOverdraft = ResourceVector.Zero;

            float committed = alloc.Funded.Sum(fe => fe.Tentative.Ap) + lockedTotal;
            alloc.GlobalOverdraft = new ResourceVector(Mathf.Max(0f, committed - pool.Ap));

            for (int i = 0; i < alloc.Funded.Count; i++)
                alloc.Funded[i].Priority = i;

            // 7. Fingerprint describes allocation/resource outcome, not pass number.
            string fp = Fingerprint(alloc);
            Converged = fp == _lastFingerprint;
            _lastFingerprint = fp;

            LogDump(alloc, turn);
            return alloc;
        }

        // Radar model #1a — a mission still has to name at least one axis it serves (>0), but the
        // axis no longer sizes an AP slice. It only tags the spend for telemetry and drives the
        // EffectiveValue scaling upstream. No positive contribution => invalid mission allocation.
        private static bool HasValidContribution(MissionProposal m)
        {
            if (m?.Requirements == null || m.Axes?.Value == null)
                return false;
            foreach (DesireAxis a in DesireAxes.All)
                if (m.Axes.Value.TryGetValue(a, out float v) && v > 0f)
                    return true;
            return false;
        }

        private static void AddBudgetDeferred(TentativeAllocation alloc, MissionProposal m,
            float min, float available)
        {
            var required = new ResourceVector(Mathf.Max(0f, min));
            var have = new ResourceVector(Mathf.Max(0f, available));
            alloc.Deferred.Add(new DeferredEntry
            {
                Mission = m,
                Reason = DeferReason.InsufficientBudget,
                Required = required,
                Available = have,
                Missing = (required - have).ClampLow0(),
            });
        }

        // Instance (not static) since step 6: ApMinimum folds in any step-6 repricing floor for
        // this mission key. ApDesired/ApMaximum float up with it automatically.
        private float ApMinimum(MissionProposal m)
        {
            float baseMin = Mathf.Max(0f, m.Requirements?.ApMinimum ?? 0f);
            return _repricedFloors.TryGetValue(StableMissionKey.For(m), out ProvisionRequirement floor)
                ? Mathf.Max(baseMin, floor.Ap)
                : baseMin;
        }
        private float ApDesired(MissionProposal m) =>
            Mathf.Max(ApMinimum(m), m.Requirements?.ApDesired ?? m.Requirements?.ApMinimum ?? 0f);
        private float ApMaximum(MissionProposal m) =>
            Mathf.Max(ApDesired(m), m.Requirements?.ApMaximum ?? m.Requirements?.ApDesired ?? 0f);

        // Radar model #1a — the cross-lane ranking key. EffectiveValue is stamped by the
        // orchestrator (BaseValue x radar-weight scale); a bare unit test / sim that builds
        // proposals directly and never stamps it falls back to the radar-blind BaseValue.
        private static float RankValue(MissionProposal m) =>
            m == null ? 0f : (m.EffectiveValue > 0f ? m.EffectiveValue : m.BaseValue);

        // Step 9 / Round 7 (Problem 3) — the physical (H/E/M/T) side of a mission's requirements as
        // one vector, folding in any repriced physical floor exactly the way ApMinimum folds in the
        // AP floor. Minimum gates admission; Desired is what a funded mission actually draws from
        // the global pool. AP is deliberately 0 here — that dimension is the axis-slice path above.
        // For every existing AP-only caller (Aggression/Defence/Economy/Development, Ground Scout,
        // Raid) `floor.Physical` is always Zero, so this is byte-for-byte the pre-round-7 computation.
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

        // Instance since step 6 — the repriced floors are part of "which allocation problem is
        // this", so two passes that funded/deferred the same set but at different floors must not
        // read as converged.
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
            AiDebugLog.Write($"[AI][V2] allocator p{a.PassNumber} — pool {LogNum(a.InitialPool.Ap)} "
                + $"(ap {LogNum(a.InitialPool.Ap + a.ManagerReserve.Ap)} − mgr {LogNum(a.ManagerReserve.Ap)}) "
                + $"| locked {LogNum(a.LockedClaim.Ap)} (off the top)");
            if (a.PhysicalPool.AnyPhysical || a.PhysicalFunded.AnyPhysical)
                AiDebugLog.Write($"[AI][V2] allocator p{a.PassNumber} — physical pool [{a.PhysicalPool.FmtPhysical()}] "
                    + $"− locked [{a.PhysicalLocked.FmtPhysical()}] − funded [{a.PhysicalFunded.FmtPhysical()}]");

            foreach (FundedEntry fe in a.Funded)
            {
                string axes = fe.Mission?.Axes?.Value == null ? "" : string.Join(",", DesireAxes.All
                    .Where(ax => fe.Mission.Axes.Value.TryGetValue(ax, out float v) && v > 0f)
                    .Select(DesireAxes.Abbrev));
                AiDebugLog.Write($"[AI][V2]   {(fe.IsCommitment ? "commit" : "fund  ")} "
                    + $"[{fe.Mission.AttemptId}] {StableMissionKey.For(fe.Mission)} "
                    + $"base {LogNum(fe.Mission.BaseValue)} eff {LogNum(fe.Mission.EffectiveValue)} "
                    + $"ap {LogNum(fe.Tentative.Ap)} (strict {LogNum(fe.StrictAp)}) axes[{axes}] "
                    + $"rem+ {LogNum(fe.RemainderTopUp.Ap)} {fe.Stage.ToString().ToLowerInvariant()}");
            }

            foreach (DeferredEntry d in a.Deferred)
            {
                string why = d.Reason == DeferReason.InsufficientBudget
                    ? $"need {LogNum(d.Required.Ap)} have {LogNum(d.Available.Ap)} miss {LogNum(d.Missing.Ap)}"
                    : d.Reason == DeferReason.InsufficientPhysical
                        ? $"need [{d.Required.FmtPhysical()}] have [{d.Available.FmtPhysical()}]"
                        : d.Reason == DeferReason.OnCooldown
                            ? $"reason={d.CooldownReason ?? "StructuralFailure"} start=t{d.CooldownStartedTurn} "
                              + $"until=t{d.CooldownUntilTurn} remaining={Mathf.Max(0, d.CooldownUntilTurn - turn + 1)}"
                            : "";
                AiDebugLog.Write($"[AI][V2]   defer [{d.Mission?.AttemptId}] {StableMissionKey.For(d.Mission)} "
                    + $"base {LogNum(d.Mission.BaseValue)} eff {LogNum(d.Mission.EffectiveValue)} — {d.Reason} {why}");
            }

            AiDebugLog.Write($"[AI][V2]   remainder {LogNum(a.RemainderGenerated.Ap)} gen "
                + $"→ spent {LogNum(a.RemainderSpent.Ap)} | unused {LogNum(a.Unused.Ap)}");
            AiDebugLog.Write($"[AI][V2] allocator p{a.PassNumber} — funded {a.Funded.Count} "
                + $"({a.Funded.Count(f => f.IsCommitment)} commit), deferred {a.Deferred.Count}, "
                + $"strictAp {LogNum(a.StrictFunded.Ap)}, commitmentAp {LogNum(a.CommitmentDraw.Ap)}, "
                + $"axisOverdraft {LogNum(a.AxisOverdraft.Ap)}, globalOverdraft {LogNum(a.GlobalOverdraft.Ap)}"
                + (a.CommitmentsStarveFreshDecisions ? " [commitments starve fresh]" : ""));
        }
    }
}
