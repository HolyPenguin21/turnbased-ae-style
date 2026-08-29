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
    //      mission ordering — the greedy key is BaseValue alone (stable-key tie-break, never
    //      SelectionScore, never radar weight).
    //   2. A mission may be funded from SEVERAL axes at once: its AxisContribution is normalised to
    //      shares, and funding it at AP C draws C*share[axis] from each slice.
    //   3. Positive slice leftovers become one fungible REMAINDER pool that can only top up
    //      ALREADY-funded missions toward Desired then Max. A deferred mission is never resurrected
    //      by remainder, and remainder loses all axis identity once collected.
    //   4. The allocator NEVER assigns a concrete army / mover. MoverKnown is ignored here.
    //
    //  RE-ALLOCATE ON FAIL — BOUNDED (risk 2)
    //  --------------------------------------------------------------------------------------------
    //  AllocationSession owns the retry state/policy but never calls ProvisioningManager. Step 5
    //  executes exactly one Pack per turn. Step 6 wires real provisioning failures through
    //  RegisterProvisionFailure -> Pack, bounded by maxReallocIterations + RejectedThisTurn +
    //  structural cooldown + fingerprint convergence.
    //
    //  RESOURCE DIMENSIONS
    //  --------------------------------------------------------------------------------------------
    //  AP only in step 5. ResourceVector intentionally has one live component. Energy/H/M/T and
    //  multi-resource atomic funding stay out until step 9.
    // ===========================================================================================

    public readonly struct ResourceVector
    {
        public readonly float Ap;

        public ResourceVector(float ap) { Ap = ap; }

        public static readonly ResourceVector Zero = new ResourceVector(0f);

        public bool IsPositive => Ap > AiConfigV2.allocatorSliceEpsilon;

        public static ResourceVector operator +(ResourceVector a, ResourceVector b) => new ResourceVector(a.Ap + b.Ap);
        public static ResourceVector operator -(ResourceVector a, ResourceVector b) => new ResourceVector(a.Ap - b.Ap);
        public static ResourceVector operator *(ResourceVector a, float k) => new ResourceVector(a.Ap * k);

        public ResourceVector ClampLow0() => new ResourceVector(Mathf.Max(0f, Ap));
        public float Magnitude => Ap;
        public string Fmt() => Ap.ToString("0.00", CultureInfo.InvariantCulture);
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
        NoMoverExists,       // no eligible executor on the map at all (or none that can satisfy a stealth-Required leg)
        EnvelopeTooSmall,    // the funded AP envelope cannot cover the real mover's cost — carries RequiredAp for repricing
        NoExecutableStep,    // mover + budget are fine, but no safe first step toward the target exists right now
        TargetSatisfied,     // the objective is already met (Explore focus hex already visited) — drop, not fail
        TargetInvalidated,   // the world changed under the mission (focus hex now holds a known army)
        AssemblyInfeasible,  // structural: the mission cannot be made executable by any assemblable means
    }

    // The retry semantics the allocator applies to a ProvisionFailure, kept orthogonal to Kind.
    public enum ProvisionDisposition
    {
        RetryNextTurn,       // out of the running THIS turn; a fresh snapshot re-proposes it next turn. No cooldown.
        DropThisTurn,        // same mechanics as RetryNextTurn, but semantically "no longer wanted" (telemetry only).
        RepriceThisTurn,     // re-fund THIS turn at a raised AP floor (ProvisionFailure.RequiredAp), still <= funded.Tentative.
        RejectWithCooldown,  // structural dead end — reject and suppress the mission key for allocatorRejectCooldownTurns.
    }

    // Stable across turns so ordering/reject/cooldown/fingerprint all address the same mission.
    // Scout is the only live kind in step 5; later kinds add their typed target identity here.
    public readonly struct StableMissionKey : IEquatable<StableMissionKey>
    {
        public readonly MissionKind Kind;
        public readonly int SubKind;
        public readonly int Q;
        public readonly int R;

        public StableMissionKey(MissionKind kind, int subKind, int q, int r)
        {
            Kind = kind;
            SubKind = subKind;
            Q = q;
            R = r;
        }

        public static StableMissionKey For(MissionProposal m)
        {
            if (m != null && m.Kind == MissionKind.Scout && m.Target is ScoutMissionTarget t)
                return new StableMissionKey(MissionKind.Scout, (int)t.Kind, t.FocusHex.Q, t.FocusHex.R);
            return new StableMissionKey(m?.Kind ?? MissionKind.Scout, 0, 0, 0);
        }

        public bool Equals(StableMissionKey o) => Kind == o.Kind && SubKind == o.SubKind && Q == o.Q && R == o.R;
        public override bool Equals(object obj) => obj is StableMissionKey o && Equals(o);
        public override int GetHashCode() => ((int)Kind, SubKind, Q, R).GetHashCode();
        public override string ToString() =>
            Kind == MissionKind.Scout ? $"{Kind}({(ScoutTargetKind)SubKind} {Q},{R})" : $"{Kind}";

        public int CompareTo(StableMissionKey o)
        {
            int c = Kind.CompareTo(o.Kind); if (c != 0) return c;
            c = SubKind.CompareTo(o.SubKind); if (c != 0) return c;
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

        public bool IsCommitment;
        public FundingStage Stage;
    }

    public enum DeferReason
    {
        InsufficientBudget,
        InvalidContribution,
        RejectedThisTurn,
        OnCooldown,
    }

    public sealed class DeferredEntry
    {
        public MissionProposal Mission;
        public DeferReason Reason;
        public DesireAxis? BottleneckAxis;
        public ResourceVector Required;
        public ResourceVector Available;
        public ResourceVector Missing;
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

        // Two DISTINCT overdraft measures:
        //  AxisOverdraft  — Σ of the amounts individual slices were driven negative (a commitment /
        //                   locked claim outrunning its OWN axis budget). Expected under many-to-many.
        //  GlobalOverdraft— max(0, total AP actually committed − the whole sliceable pool). The real
        //                   "spent more than we have" alarm; ~0 unless commitments exceed the pool.
        public ResourceVector AxisOverdraft;
        public ResourceVector GlobalOverdraft;
        public int PassNumber;
    }

    // Cross-turn state only. RejectedThisTurn/pass/fingerprint live in AllocationSession.
    public sealed class AiAllocatorState
    {
        private readonly Dictionary<StableMissionKey, int> _cooldownUntilTurn =
            new Dictionary<StableMissionKey, int>();

        // Inclusive `until`: a failure on turn T with cooldown=2 suppresses T+1 and T+2.
        public bool OnCooldown(StableMissionKey k, int turn) =>
            _cooldownUntilTurn.TryGetValue(k, out int until) && turn <= until;

        public void StartCooldown(StableMissionKey k, int untilTurn)
        {
            if (!_cooldownUntilTurn.TryGetValue(k, out int cur) || untilTurn > cur)
                _cooldownUntilTurn[k] = untilTurn;
        }

        public void PurgeExpired(int turn)
        {
            var dead = _cooldownUntilTurn.Where(kv => kv.Value < turn).Select(kv => kv.Key).ToList();
            foreach (StableMissionKey k in dead)
                _cooldownUntilTurn.Remove(k);
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
            List<MissionProposal> missions, List<Commitment> commitments, PlayerSetupData player)
        {
            AiAllocatorState state = AiAllocatorStateRegistry.GetOrCreate(player);
            state.PurgeExpired(snapshot?.TurnNumber ?? 0);
            return new AllocationSession(snapshot, radar ?? Radar.Even(),
                missions ?? new List<MissionProposal>(), commitments ?? new List<Commitment>(), state);
        }
    }

    public sealed class AllocationSession
    {
        private readonly WorldSnapshot _snap;
        private readonly Radar _radar;
        private readonly List<MissionProposal> _missions;
        private readonly List<Commitment> _commitments;
        private readonly AiAllocatorState _state;

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

            public LockedAllocation(Dictionary<DesireAxis, float> strictDraw, float remainderAp,
                float grantedAp, float claimedAp)
            {
                StrictDraw = strictDraw;
                StrictAp = 0f;
                foreach (KeyValuePair<DesireAxis, float> kv in strictDraw)
                    StrictAp += kv.Value;
                RemainderAp = remainderAp;
                GrantedAp = grantedAp;
                ClaimedAp = claimedAp;
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
            List<Commitment> commitments, AiAllocatorState state)
        {
            _snap = snap;
            _radar = radar;
            _missions = missions;
            _commitments = commitments;
            _state = state;
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
                    _rejectedThisTurn.Add(key);
                    _state.StartCooldown(key, (_snap?.TurnNumber ?? 0) + AiConfigV2.allocatorRejectCooldownTurns);
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
                new LockedAllocation(strict, funded.RemainderTopUp.Ap, funded.Tentative.Ap, claimedAp);
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
            float rawAp = _snap?.Self?.ActionPoints ?? 0;
            float reserve = Mathf.Max(0f, AiConfigV2.allocatorManagerApReserve);
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
                    AiDebugLog.Write($"[AI][V2] allocator — WARN locked claim {LogNum(lc.Value.ClaimedAp)} "
                        + $"exceeds granted {LogNum(lc.Value.GrantedAp)} for {lc.Key} — clamped (provisioning "
                        + "must stay within Tentative)");
                lockedTotal += Mathf.Min(lc.Value.ClaimedAp, lc.Value.GrantedAp);
                lockedRemainderConsumed += remainderConsumed;
                foreach (KeyValuePair<DesireAxis, float> kv in lc.Value.StrictDraw)
                {
                    lockedStrictByAxis.TryGetValue(kv.Key, out float cur);
                    lockedStrictByAxis[kv.Key] = cur + kv.Value * strictScale;
                }
            }
            alloc.LockedClaim = new ResourceVector(lockedTotal);

            // 2. Radar cuts the pool into per-axis slices; each slice then loses the strict AP a
            //    locked mission already drew from it. Radar has no role in mission ordering.
            var slices = new Dictionary<DesireAxis, BudgetSlice>();
            foreach (DesireAxis axis in DesireAxes.All)
            {
                float w = _radar.Weight.TryGetValue(axis, out float ww) ? Mathf.Max(0f, ww) : 0f;
                ResourceVector budget = pool * w;
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

            var shareCache = new Dictionary<MissionProposal, Dictionary<DesireAxis, float>>();
            int priority = 0;

            // 3. Commitments first. Sticky/pre-paid: they can drive slices negative.
            //    SEAM DEFECT — fix WITH build-order step 7. This loop gates on NEITHER
            //    _rejectedThisTurn NOR _lockedClaims, so on a re-Pack it re-funds (and re-draws the
            //    slices for) a commitment that already FAILED provisioning this turn AND one that
            //    already SUCCEEDED (its locked strict draw is then double-counted — once here, once
            //    via lockedStrictByAxis). Harmless only while CommitmentLayer.Active returns empty.
            //    Step 7 must skip a commitment whose key is in _rejectedThisTurn or _lockedClaims
            //    (the latter already has its provenance applied to the slices), and give a failed
            //    commitment a release / cancellation path.
            foreach (Commitment c in _commitments)
            {
                MissionProposal m = c?.Mission;
                if (m == null)
                    continue;

                Dictionary<DesireAxis, float> shares = Shares(m, shareCache);
                if (shares == null)
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.InvalidContribution });
                    continue;
                }

                var ask = new ResourceVector(ApDesired(m));
                var fe = new FundedEntry
                {
                    Mission = m,
                    Priority = priority++,
                    Tentative = ask,
                    IsCommitment = true,
                    Stage = FundingStage.Strict,
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
            }

            // 4. Fresh missions — BaseValue desc + stable key. Strict admission is atomic:
            //    calculate ALL draws -> check ALL slices -> mutate ALL or mutate NONE.
            var ordered = _missions
                .Where(m => m != null && !_lockedClaims.ContainsKey(StableMissionKey.For(m)))
                .OrderByDescending(m => m.BaseValue)
                .ThenBy(m => StableMissionKey.For(m), MissionKeyComparer.Instance)
                .ToList();

            foreach (MissionProposal m in ordered)
            {
                StableMissionKey key = StableMissionKey.For(m);

                if (_rejectedThisTurn.Contains(key))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.RejectedThisTurn });
                    continue;
                }
                if (_state.OnCooldown(key, turn))
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.OnCooldown });
                    continue;
                }

                Dictionary<DesireAxis, float> shares = Shares(m, shareCache);
                if (shares == null)
                {
                    alloc.Deferred.Add(new DeferredEntry { Mission = m, Reason = DeferReason.InvalidContribution });
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
                };
                foreach (KeyValuePair<DesireAxis, ResourceVector> kv in draws)
                    funded.PerAxisDraw[kv.Key] = kv.Value;
                foreach (KeyValuePair<DesireAxis, ResourceVector> kv in draws)
                    slices[kv.Key].Remaining -= kv.Value;

                alloc.Funded.Add(funded);
                alloc.StrictFunded += v;
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

            LogDump(alloc);
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

        private static void LogDump(TentativeAllocation a)
        {
            string slices = string.Join(" ", a.Slices.Select(s =>
                $"{DesireAxes.Abbrev(s.Axis)} {s.Weight.ToString("0.00", CultureInfo.InvariantCulture)}"
                + $"→{LogNum(s.Initial.Ap)} left {LogNum(s.Remaining.Ap)}"));
            AiDebugLog.Write($"[AI][V2] allocator p{a.PassNumber} — pool {LogNum(a.InitialPool.Ap)} "
                + $"(ap {LogNum(a.InitialPool.Ap + a.ManagerReserve.Ap)} − mgr {LogNum(a.ManagerReserve.Ap)}) "
                + $"| locked {LogNum(a.LockedClaim.Ap)} (applied to slices) | {slices}");

            foreach (FundedEntry fe in a.Funded)
            {
                string draw = string.Join(" ", fe.PerAxisDraw
                    .Where(kv => kv.Value.Ap > AiConfigV2.allocatorSliceEpsilon)
                    .Select(kv => $"{DesireAxes.Abbrev(kv.Key)} {LogNum(kv.Value.Ap)}"));
                AiDebugLog.Write($"[AI][V2]   {(fe.IsCommitment ? "commit" : "fund  ")} "
                    + $"{StableMissionKey.For(fe.Mission)} base {LogNum(fe.Mission.BaseValue)} "
                    + $"ap {LogNum(fe.Tentative.Ap)} draw[{draw}] rem+ {LogNum(fe.RemainderTopUp.Ap)} "
                    + $"{fe.Stage.ToString().ToLowerInvariant()}");
            }

            foreach (DeferredEntry d in a.Deferred)
            {
                string why = d.Reason == DeferReason.InsufficientBudget && d.BottleneckAxis.HasValue
                    ? $"@{DesireAxes.Abbrev(d.BottleneckAxis.Value)} need {LogNum(d.Required.Ap)} "
                      + $"have {LogNum(d.Available.Ap)} miss {LogNum(d.Missing.Ap)}"
                    : "";
                AiDebugLog.Write($"[AI][V2]   defer {StableMissionKey.For(d.Mission)} "
                    + $"base {LogNum(d.Mission.BaseValue)} — {d.Reason} {why}");
            }

            AiDebugLog.Write($"[AI][V2]   remainder {LogNum(a.RemainderGenerated.Ap)} gen "
                + $"→ spent {LogNum(a.RemainderSpent.Ap)} | unused {LogNum(a.Unused.Ap)}");
            AiDebugLog.Write($"[AI][V2] allocator p{a.PassNumber} — funded {a.Funded.Count} "
                + $"({a.Funded.Count(f => f.IsCommitment)} commit), deferred {a.Deferred.Count}, "
                + $"strictAp {LogNum(a.StrictFunded.Ap)}, commitmentAp {LogNum(a.CommitmentDraw.Ap)}, "
                + $"axisOverdraft {LogNum(a.AxisOverdraft.Ap)}, globalOverdraft {LogNum(a.GlobalOverdraft.Ap)}");
        }
    }
}
