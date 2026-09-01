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
    //  radar -> per-axis budget SLICES of the shared AP pool -> many-to-many packing -> an ORDERED
    //  TentativeAllocation the ProvisioningManager consumes front-to-back.
    //
    //  FOUR HARD RULES
    //  --------------------------------------------------------------------------------------------
    //   1. Radar sizes the axis BUDGET. It is NOT a BaseValue multiplier and takes NO part in
    //      mission ordering. Fresh ordering WITHIN one execution lane is
    //      MissionAdmissionPolicy.AdmissionRank (the planner-local LocalAdmissionScore + the
    //      step-7 retarget hysteresis) so the Recon Explore-vs-Surveil balance survives the N>K
    //      beam. Radar weight is never in the key. Today there is exactly ONE fresh lane
    //      (ExecutionLane.Recon), so the effective fresh order is AdmissionRank + stable key.
    //      TODO step 9 (second lane / Raid): the loop below walks lanes group-at-a-time ordered
    //      by each group's max BaseValue — that is NOT a true cross-lane interleave (a low
    //      BaseValue Recon candidate would still be admitted before a high BaseValue Raid one).
    //      Replace with a k-way merge: per-lane AdmissionRank-ordered queues, repeatedly admit the
    //      queue head with the highest BaseValue.
    //   2. A mission may be funded from SEVERAL axes at once: its AxisContribution is normalised to
    //      shares, and funding it at AP C draws C*share[axis] from each slice.
    //   3. Positive slice leftovers become one fungible REMAINDER pool. Missions deferred ONLY
    //      for InsufficientBudget get one second admission pass from it (all non-budget gates are
    //      rechecked); what remains then tops up funded missions toward Desired/Max.
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
    //  AP + Human + Energy + Materials + Tech. AP is still checked through the per-axis radar
    //  SLICES (AxisBudgetLedger). Human/Energy/Materials/Tech are ONE global physical pool — never
    //  axis-sliced (spec §18 / §19.3): a mission is funded only if all its AP axis draws AND the
    //  whole global physical draw succeed together, atomically (spec §19.4 / AC #17). The physical
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

    public sealed class BudgetSlice
    {
        public DesireAxis Axis;
        public float Weight;
        public ResourceVector Initial;
        public ResourceVector Remaining;
    }

    public enum FundingStage { Strict, Remainder }

    public sealed class FundedEntry
    {
        public MissionProposal Mission;
        public int Priority;
        public ResourceVector Tentative;

        // Strict admission draw only. Remainder is fungible and must never be re-attributed to an
        // axis after collection.
        public readonly Dictionary<DesireAxis, ResourceVector> PerAxisDraw =
            new Dictionary<DesireAxis, ResourceVector>();
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
        public readonly List<BudgetSlice> Slices = new List<BudgetSlice>();

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

        public static void Clear() => ByPlayer.Clear();
    }

    internal static class ResourceAllocator
    {
        public static AllocationSession BeginTurn(WorldSnapshot snapshot, Radar radar,
            List<MissionProposal> missions, List<Commitment> commitments, PlayerSetupData player,
            AxisBudgetLedger ledger = null)
        {
            AiAllocatorState state = AiAllocatorStateRegistry.GetOrCreate(player);
            state.PurgeExpired(snapshot?.TurnNumber ?? 0);
            return new AllocationSession(snapshot, radar ?? Radar.Even(),
                missions ?? new List<MissionProposal>(), commitments ?? new List<Commitment>(), state, ledger);
        }
    }

    public sealed class AllocationSession
    {
        private readonly WorldSnapshot _snap;
        private readonly Radar _radar;
        private readonly List<MissionProposal> _missions;
        private readonly List<Commitment> _commitments;
        private readonly AiAllocatorState _state;

        // Strategy V2 Strategic Manager — the shared per-turn AP entitlement split. When present,
        // per-axis slice size comes from this (already net of Phase-A demand-fulfilment spend)
        // instead of re-splitting current AP by the radar (NO second radar split). Null in a bare
        // unit test / sim -> fall back to radar * pool as before.
        private readonly AxisBudgetLedger _ledger;

        private readonly HashSet<StableMissionKey> _rejectedThisTurn = new HashSet<StableMissionKey>();
        // Step 6 repricing feedback (risk 2). A mission that failed provisioning with
        // EnvelopeTooSmall(requiredAp) is NOT rejected — instead its AP minimum is raised to
        // requiredAp for every later Pack() this turn, so the next pass either funds it at the
        // real cost or defers it honestly on budget. Never lowers a floor; cleared each turn with
        // the session. Provisioning still refuses to claim above funded.Tentative, so the raised
        // floor can only ever move a mission from "funded too low to execute" to "funded at cost"
        // or "deferred — the axis slice genuinely can't afford this mover".
        private readonly Dictionary<StableMissionKey, float> _repricedFloors =
            new Dictionary<StableMissionKey, float>();
        // Missions physically provisioned in an earlier pass THIS turn, with their FUNDING
        // PROVENANCE (which axis slices the strict part drew from + the fungible remainder part),
        // scaled to what provisioning actually claimed. A re-pack rebuilds the original radar
        // slices, subtracts each locked mission's strict per-axis draw from the matching slice, and
        // removes its remainder part from the fungible pool — so an axis can never re-slice a
        // shrunken pool and drift past the radar budget it was given for the cycle. The mission
        // itself is dropped from re-funding.
        private readonly Dictionary<StableMissionKey, LockedAllocation> _lockedClaims =
            new Dictionary<StableMissionKey, LockedAllocation>();
        private string _lastFingerprint;

        private readonly struct LockedAllocation
        {
            public readonly Dictionary<DesireAxis, float> StrictDraw; // per-axis, as granted
            public readonly float StrictAp;                           // Σ StrictDraw
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

            public LockedAllocation(MissionProposal mission, Dictionary<DesireAxis, float> strictDraw, float remainderAp,
                float grantedAp, float claimedAp, ResourceVector physicalClaim)
            {
                Mission = mission;
                StrictDraw = strictDraw;
                StrictAp = 0f;
                foreach (KeyValuePair<DesireAxis, float> kv in strictDraw)
                    StrictAp += kv.Value;
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

        internal AllocationSession(WorldSnapshot snap, Radar radar, List<MissionProposal> missions,
            List<Commitment> commitments, AiAllocatorState state, AxisBudgetLedger ledger = null)
        {
            _snap = snap;
            _radar = radar;
            _missions = missions;
            _commitments = commitments;
            _state = state;
            _ledger = ledger;
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
                    float cur = _repricedFloors.TryGetValue(key, out float f) ? f : 0f;
                    _repricedFloors[key] = Mathf.Max(cur, failure.RequiredAp);
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
        public void RegisterProvisionSuccess(FundedEntry funded, float claimedAp)
        {
            if (funded?.Mission == null)
                return;
            var strict = new Dictionary<DesireAxis, float>();
            foreach (KeyValuePair<DesireAxis, ResourceVector> kv in funded.PerAxisDraw)
                strict[kv.Key] = kv.Value.Ap;
            _lockedClaims[StableMissionKey.For(funded.Mission)] =
                new LockedAllocation(funded.Mission, strict, funded.RemainderTopUp.Ap, funded.Tentative.Ap, claimedAp,
                    funded.PhysicalDraw);
        }

        public TentativeAllocation Pack()
        {
            HasNewFailures = false;
            PassCount++;
            int turn = _snap?.TurnNumber ?? 0;
            float eps = AiConfigV2.allocatorSliceEpsilon;

            var alloc = new TentativeAllocation { PassNumber = PassCount };

            // 1. Pool = the radar BUDGET BASE for this turn: snapshot AP minus the Manager reserve.
            //    It is deliberately NOT reduced by AP already spent by locked missions — the radar
            //    slice is an axis's budget for the cycle, not a figure that re-slices a shrinking
            //    pool after every provisioning success. Locked spend is applied to the slices
            //    (strict part) and to the fungible remainder (remainder part) below instead.
            // Physical remaining AP this turn (post Strategic-Manager Phase A via the operational
            // refresh) minus the protected HousekeepingManager reserve. The commitment / global
            // overdraft checks below cap against THIS, never raw AP.
            float rawAp = _snap?.Self?.ActionPoints ?? 0;
            float reserve = Mathf.Max(0f, AiConfigV2.housekeepingApReserve);
            var pool = new ResourceVector(Mathf.Max(0f, rawAp - reserve));
            alloc.InitialPool = pool;
            alloc.ManagerReserve = new ResourceVector(reserve);

            // Locked funding provenance, resolved WATERFALL-style (remainder top-up disappears
            // first when the real claim < granted envelope; strict only shrinks below the strict
            // level): per-axis strict consumption (removed from the matching slice) + fungible
            // remainder consumption (removed from the remainder pool in step 5).
            var lockedStrictByAxis = new Dictionary<DesireAxis, float>();
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
                foreach (KeyValuePair<DesireAxis, float> kv in lc.Value.StrictDraw)
                {
                    lockedStrictByAxis.TryGetValue(kv.Key, out float cur);
                    lockedStrictByAxis[kv.Key] = cur + kv.Value * strictScale;
                }
            }
            alloc.LockedClaim = new ResourceVector(lockedTotal);

            // 2. Per-axis slices. WITH a shared AxisBudgetLedger (the normal V2 path) the slice
            //    size IS ledger.Balance(axis) — the radar was already applied once when the ledger
            //    was created, and Strategic Manager Phase A has since debited the requesting axis
            //    for any demand-driven card play. NO second radar split. Without a ledger (bare
            //    test / sim) fall back to radar * pool. Each slice then also loses the strict AP a
            //    locked mission already drew from it (the re-pack mechanism, unchanged).
            var slices = new Dictionary<DesireAxis, BudgetSlice>();
            foreach (DesireAxis axis in DesireAxes.All)
            {
                float w = _radar.Weight.TryGetValue(axis, out float ww) ? Mathf.Max(0f, ww) : 0f;
                ResourceVector budget = _ledger != null
                    ? new ResourceVector(Mathf.Max(0f, _ledger.Balance(axis)))
                    : pool * w;
                lockedStrictByAxis.TryGetValue(axis, out float lockedHere);
                var s = new BudgetSlice
                {
                    Axis = axis,
                    Weight = w,
                    Initial = budget,
                    Remaining = budget - new ResourceVector(lockedHere),
                };
                slices[axis] = s;
                alloc.Slices.Add(s);
            }

            // 2b. Step 9 — the ONE global physical pool (Human/Energy/Materials/Tech). NOT
            //     axis-sliced (spec §18): it is the real post-Initiative + post-Phase-A stockpile,
            //     minus what missions provisioned in an earlier pass this turn already claimed
            //     (spec §19.5 — a re-pack can never re-hand-out the same physical units). AP stays
            //     on the radar slices above; physical is a flat atomic gate below.
            ResourceBundle stock = _snap?.Self?.Stockpile ?? default;
            var physicalPool = new ResourceVector(0f, stock.Human, stock.Energy, stock.Materials, stock.Tech);
            var lockedPhysical = ResourceVector.Zero;
            foreach (LockedAllocation lc in _lockedClaims.Values)
                lockedPhysical += lc.PhysicalClaim;
            ResourceVector physicalRemaining = (physicalPool - lockedPhysical).ClampLow0();
            alloc.PhysicalPool = physicalPool;
            alloc.PhysicalLocked = lockedPhysical;

            var shareCache = new Dictionary<MissionProposal, Dictionary<DesireAxis, float>>();
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

            // 3. Commitments first. Sticky/pre-paid: they MAY drive an axis slice negative (that is
            //    the point — a funding protection against Radar noise). Step-7 guards:
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

                Dictionary<DesireAxis, float> shares = Shares(m, shareCache);
                if (shares == null)
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
                    IsCommitment = true,
                    Stage = FundingStage.Strict,
                    PhysicalDraw = cPhys,
                };

                foreach (KeyValuePair<DesireAxis, float> kv in shares)
                {
                    ResourceVector draw = ask * kv.Value;
                    fe.PerAxisDraw[kv.Key] = draw;
                }
                foreach (KeyValuePair<DesireAxis, ResourceVector> kv in fe.PerAxisDraw)
                    slices[kv.Key].Remaining -= kv.Value;

                alloc.Funded.Add(fe);
                alloc.CommitmentDraw += ask;
                alloc.PhysicalFunded += cPhys;
            }

            // 4. Fresh missions — TRUE CROSS-LANE k-way MERGE (spec §21, closing rule-1's step-9
            //    TODO). Per-lane queues are each ordered by MissionAdmissionPolicy.AdmissionRank
            //    (the None lane by BaseValue) so the WITHIN-lane balance (Recon Explore-vs-Surveil,
            //    Raid feasibility ordering) survives the N>K beam. Then, repeatedly, the queue HEAD
            //    with the highest BaseValue is taken and admission-tested — so LocalAdmissionScore
            //    orders inside a lane, BaseValue orders BETWEEN lanes, and the radar only ever sized
            //    the AP budget (never a score multiplier). Tie-break: BaseValue DESC, then
            //    StableMissionKey ASC — deterministic regardless of Dictionary iteration order.
            //    Per candidate: conflict -> capacity -> AP budget (atomic axis draws) -> global
            //    physical (atomic H/E/M/T). A proposal that is ALSO an active commitment is funded
            //    through the commitment loop above only.
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
                    ? g.OrderByDescending(m => m.BaseValue)
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
                    if (m == null
                        || head.BaseValue > m.BaseValue + eps
                        || (Mathf.Abs(head.BaseValue - m.BaseValue) <= eps
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

                    Dictionary<DesireAxis, float> shares = Shares(m, shareCache);
                    if (shares == null)
                    {
                        alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.InvalidContribution });
                        continue;
                    }

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

                    float affordable = float.PositiveInfinity;
                    DesireAxis bottleneck = DesireAxis.Recon;
                    foreach (KeyValuePair<DesireAxis, float> kv in shares)
                    {
                        float room = Mathf.Max(0f, slices[kv.Key].Remaining.Ap);
                        float cap = room / kv.Value;
                        if (cap < affordable)
                        {
                            affordable = cap;
                            bottleneck = kv.Key;
                        }
                    }

                    float min = ApMinimum(m);
                    if (affordable + eps < min)
                    {
                        AddBudgetDeferred(alloc, m, shares, slices, bottleneck, min);
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
                    var v = new ResourceVector(fundAp);
                    var draws = new Dictionary<DesireAxis, ResourceVector>();
                    foreach (KeyValuePair<DesireAxis, float> kv in shares)
                        draws[kv.Key] = v * kv.Value;

                    DesireAxis failedAxis = bottleneck;
                    bool allFit = true;
                    foreach (KeyValuePair<DesireAxis, ResourceVector> kv in draws)
                    {
                        if (slices[kv.Key].Remaining.Ap + eps < kv.Value.Ap)
                        {
                            failedAxis = kv.Key;
                            allFit = false;
                            break;
                        }
                    }

                    if (!allFit)
                    {
                        AddBudgetDeferred(alloc, m, shares, slices, failedAxis, min);
                        continue;
                    }

                    var funded = new FundedEntry
                    {
                        Mission = m,
                        Priority = priority++,
                        Tentative = v,
                        IsCommitment = false,
                        Stage = FundingStage.Strict,
                        PhysicalDraw = physDraw,
                    };
                    foreach (KeyValuePair<DesireAxis, ResourceVector> kv in draws)
                        funded.PerAxisDraw[kv.Key] = kv.Value;
                    foreach (KeyValuePair<DesireAxis, ResourceVector> kv in draws)
                        slices[kv.Key].Remaining -= kv.Value;
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
                float minCommitBase = alloc.Funded.Where(f => f.IsCommitment).Min(f => f.Mission.BaseValue);
                bool starvedOnBudget = alloc.Deferred.Any(d => d.Reason == DeferReason.InsufficientBudget
                    && d.Mission != null && d.Mission.BaseValue > minCommitBase);
                bool starvedOnCapacity = alloc.Deferred.Any(d => d.Reason == DeferReason.ExecutionCapacity)
                    && alloc.Funded.Any(f => f.IsCommitment
                        && MissionAdmissionPolicy.LaneFor(f.Mission) != ExecutionLane.None);
                if (starvedOnBudget || starvedOnCapacity)
                    alloc.CommitmentsStarveFreshDecisions = true;
            }

            // 5. Positive leftovers lose axis identity and become one fungible remainder pool. AP a
            //    locked mission already spent as a remainder top-up in an earlier pass still shows
            //    up as slice leftover here (its strict draw was removed from the slice, its
            //    remainder part was not) — take it back out before redistributing.
            float remainder = 0f;
            foreach (BudgetSlice s in alloc.Slices)
            {
                if (s.Remaining.Ap <= 0f)
                    continue;
                remainder += s.Remaining.Ap;
                s.Remaining = ResourceVector.Zero;
            }
            remainder = Mathf.Max(0f, remainder - lockedRemainderConsumed);
            alloc.RemainderGenerated = new ResourceVector(remainder);

            // 5b. Spillover admission: strict radar slices remain the first pass, but once their
            // positive leftovers have explicitly lost axis identity, a mission deferred ONLY on
            // InsufficientBudget gets one more admission attempt from the common remainder. This
            // is not general overdraft: conflict/capacity/physical gates are rechecked and the
            // mission receives only its executable AP minimum; ordinary top-up happens afterwards.
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
                float minCommitBase = alloc.Funded.Where(f => f.IsCommitment).Min(f => f.Mission.BaseValue);
                bool stillStarvedOnBudget = alloc.Deferred.Any(d => d.Reason == DeferReason.InsufficientBudget
                    && d.Mission != null && d.Mission.BaseValue > minCommitBase);
                bool stillStarvedOnCapacity = alloc.Deferred.Any(d => d.Reason == DeferReason.ExecutionCapacity)
                    && alloc.Funded.Any(f => f.IsCommitment
                        && MissionAdmissionPolicy.LaneFor(f.Mission) != ExecutionLane.None);
                alloc.CommitmentsStarveFreshDecisions = stillStarvedOnBudget || stillStarvedOnCapacity;
            }

            List<FundedEntry> topUpOrder = alloc.Funded
                .Where(fe => !fe.IsCommitment)
                .OrderByDescending(fe => fe.Mission.BaseValue)
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

            // 6. Overdraft diagnostics — two distinct measures.
            //    AxisOverdraft: positive slices have already moved to remainder, so any negative
            //    slice is one axis's budget overrun — a commitment or a locked mission's strict
            //    draw exceeding that axis's radar budget. Expected under many-to-many, benign.
            //    GlobalOverdraft: total AP actually committed this turn (fresh Tentative + locked
            //    claims) beyond the whole sliceable pool — the real alarm; ~0 unless commitments
            //    outright exceed the pool.
            float axisOverdraft = 0f;
            foreach (BudgetSlice s in alloc.Slices)
                if (s.Remaining.Ap < 0f)
                    axisOverdraft += -s.Remaining.Ap;
            alloc.AxisOverdraft = new ResourceVector(axisOverdraft);

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

        private static Dictionary<DesireAxis, float> Shares(MissionProposal m,
            Dictionary<MissionProposal, Dictionary<DesireAxis, float>> cache)
        {
            if (cache.TryGetValue(m, out Dictionary<DesireAxis, float> cached))
                return cached;

            Dictionary<DesireAxis, float> result = null;
            if (m?.Requirements != null && m.Axes?.Value != null)
            {
                float sum = 0f;
                foreach (DesireAxis a in DesireAxes.All)
                    if (m.Axes.Value.TryGetValue(a, out float v) && v > 0f)
                        sum += v;

                // Exact semantic guard: no positive contribution => invalid mission allocation.
                if (sum > 0f)
                {
                    result = new Dictionary<DesireAxis, float>();
                    foreach (DesireAxis a in DesireAxes.All)
                        if (m.Axes.Value.TryGetValue(a, out float v) && v > 0f)
                            result[a] = v / sum;
                }
            }

            cache[m] = result;
            return result;
        }

        private static void AddBudgetDeferred(TentativeAllocation alloc, MissionProposal m,
            Dictionary<DesireAxis, float> shares, Dictionary<DesireAxis, BudgetSlice> slices,
            DesireAxis bottleneck, float min)
        {
            float requiredAp = min * shares[bottleneck];
            var required = new ResourceVector(requiredAp);
            var available = new ResourceVector(Mathf.Max(0f, slices[bottleneck].Remaining.Ap));
            alloc.Deferred.Add(new DeferredEntry
            {
                Mission = m,
                Reason = DeferReason.InsufficientBudget,
                BottleneckAxis = bottleneck,
                Required = required,
                Available = available,
                Missing = (required - available).ClampLow0(),
            });
        }

        // Instance (not static) since step 6: ApMinimum folds in any step-6 repricing floor for
        // this mission key. ApDesired/ApMaximum float up with it automatically.
        private float ApMinimum(MissionProposal m)
        {
            float baseMin = Mathf.Max(0f, m.Requirements?.ApMinimum ?? 0f);
            return _repricedFloors.TryGetValue(StableMissionKey.For(m), out float floor)
                ? Mathf.Max(baseMin, floor)
                : baseMin;
        }
        private float ApDesired(MissionProposal m) =>
            Mathf.Max(ApMinimum(m), m.Requirements?.ApDesired ?? m.Requirements?.ApMinimum ?? 0f);
        private float ApMaximum(MissionProposal m) =>
            Mathf.Max(ApDesired(m), m.Requirements?.ApMaximum ?? m.Requirements?.ApDesired ?? 0f);

        // Step 9 — the physical (H/E/M/T) side of a mission's requirements as one vector. Minimum
        // gates admission; Desired is what a funded mission actually draws from the global pool.
        // AP is deliberately 0 here — that dimension is the axis-slice path.
        private static ResourceVector PhysicalMinimum(MissionProposal m)
        {
            MissionRequirements r = m?.Requirements;
            return r == null ? ResourceVector.Zero
                : new ResourceVector(0f, Mathf.Max(0f, r.HumanMinimum), Mathf.Max(0f, r.EnergyMinimum),
                    Mathf.Max(0f, r.MaterialsMinimum), Mathf.Max(0f, r.TechMinimum));
        }
        private static ResourceVector PhysicalDesired(MissionProposal m)
        {
            MissionRequirements r = m?.Requirements;
            if (r == null) return ResourceVector.Zero;
            return new ResourceVector(0f,
                Mathf.Max(r.HumanMinimum, r.HumanDesired),
                Mathf.Max(r.EnergyMinimum, r.EnergyDesired),
                Mathf.Max(r.MaterialsMinimum, r.MaterialsDesired),
                Mathf.Max(r.TechMinimum, r.TechDesired)).ClampLow0();
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
            string slices = string.Join(",", a.Slices
                .Select(s => $"{DesireAxes.Abbrev(s.Axis)}={s.Remaining.Ap.ToString("0.00", CultureInfo.InvariantCulture)}"));
            string repriced = string.Join(",", _repricedFloors
                .OrderBy(kv => kv.Key, MissionKeyComparer.Instance)
                .Select(kv => $"{kv.Key}:{kv.Value.ToString("0.00", CultureInfo.InvariantCulture)}"));
            return funded + "|" + deferred + "|" + slices
                + "|unused=" + a.Unused.Ap.ToString("0.00", CultureInfo.InvariantCulture)
                + "|repriced=" + repriced;
        }

        private static string LogNum(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        private static void LogDump(TentativeAllocation a, int turn)
        {
            string slices = string.Join(" ", a.Slices.Select(s =>
                $"{DesireAxes.Abbrev(s.Axis)} {s.Weight.ToString("0.00", CultureInfo.InvariantCulture)}"
                + $"→{LogNum(s.Initial.Ap)} left {LogNum(s.Remaining.Ap)}"));
            AiDebugLog.Write($"[AI][V2] allocator p{a.PassNumber} — pool {LogNum(a.InitialPool.Ap)} "
                + $"(ap {LogNum(a.InitialPool.Ap + a.ManagerReserve.Ap)} − mgr {LogNum(a.ManagerReserve.Ap)}) "
                + $"| locked {LogNum(a.LockedClaim.Ap)} (applied to slices) | {slices}");
            if (a.PhysicalPool.AnyPhysical || a.PhysicalFunded.AnyPhysical)
                AiDebugLog.Write($"[AI][V2] allocator p{a.PassNumber} — physical pool [{a.PhysicalPool.FmtPhysical()}] "
                    + $"− locked [{a.PhysicalLocked.FmtPhysical()}] − funded [{a.PhysicalFunded.FmtPhysical()}]");

            foreach (FundedEntry fe in a.Funded)
            {
                string draw = string.Join(" ", fe.PerAxisDraw
                    .Where(kv => kv.Value.Ap > AiConfigV2.allocatorSliceEpsilon)
                    .Select(kv => $"{DesireAxes.Abbrev(kv.Key)} {LogNum(kv.Value.Ap)}"));
                AiDebugLog.Write($"[AI][V2]   {(fe.IsCommitment ? "commit" : "fund  ")} "
                    + $"[{fe.Mission.AttemptId}] {StableMissionKey.For(fe.Mission)} base {LogNum(fe.Mission.BaseValue)} "
                    + $"ap {LogNum(fe.Tentative.Ap)} draw[{draw}] rem+ {LogNum(fe.RemainderTopUp.Ap)} "
                    + $"{fe.Stage.ToString().ToLowerInvariant()}");
            }

            foreach (DeferredEntry d in a.Deferred)
            {
                string why = d.Reason == DeferReason.InsufficientBudget && d.BottleneckAxis.HasValue
                    ? $"@{DesireAxes.Abbrev(d.BottleneckAxis.Value)} need {LogNum(d.Required.Ap)} "
                      + $"have {LogNum(d.Available.Ap)} miss {LogNum(d.Missing.Ap)}"
                    : d.Reason == DeferReason.InsufficientPhysical
                        ? $"need [{d.Required.FmtPhysical()}] have [{d.Available.FmtPhysical()}]"
                        : d.Reason == DeferReason.OnCooldown
                            ? $"reason={d.CooldownReason ?? "StructuralFailure"} start=t{d.CooldownStartedTurn} "
                              + $"until=t{d.CooldownUntilTurn} remaining={Mathf.Max(0, d.CooldownUntilTurn - turn + 1)}"
                            : "";
                AiDebugLog.Write($"[AI][V2]   defer [{d.Mission?.AttemptId}] {StableMissionKey.For(d.Mission)} "
                    + $"base {LogNum(d.Mission.BaseValue)} — {d.Reason} {why}");
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
