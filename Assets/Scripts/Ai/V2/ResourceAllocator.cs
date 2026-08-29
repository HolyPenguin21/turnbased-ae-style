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
    //  radar -> per-axis budget SLICES of the shared pool -> many-to-many packing -> an ORDERED
    //  TentativeAllocation the ProvisioningManager consumes front-to-back.
    //
    //  FOUR HARD RULES (from the pipeline header — designed in here, never patched on later)
    //  --------------------------------------------------------------------------------------------
    //   1. Radar sizes the axis BUDGET. It is NOT a BaseValue multiplier and takes NO part in
    //      mission ordering — the greedy key is BaseValue alone (stable-key tie-break, never
    //      SelectionScore, never radar weight).
    //   2. A mission may be funded from SEVERAL axes at once: its AxisContribution is normalised to
    //      shares, and funding it at vector V draws V*share[axis] from each slice.
    //   3. An unfillable / unused slice is folded back into a fungible REMAINDER pool that can only
    //      top up ALREADY-funded missions (>= their Min) toward Desired then Max — it never
    //      resurrects a deferred mission. That is what makes "generous pool -> Scout funded" and
    //      "tight pool -> Recon slice small, mission deferred" both true.
    //   4. The allocator NEVER assigns a concrete army / mover. It hands missions a RESOURCE
    //      envelope; mover assignment is build-order step 6 (Provisioning). MoverKnown is ignored
    //      here.
    //
    //  RE-ALLOCATE ON FAIL — BOUNDED (risk 2)
    //  --------------------------------------------------------------------------------------------
    //  The allocator does NOT call ProvisioningManager (that mutates game state; the allocator must
    //  stay pure). Instead the per-turn AllocationSession owns the retry POLICY and STATE:
    //    session = ResourceAllocator.BeginTurn(...);   alloc = session.Pack();
    //    // pipeline: provision alloc.Funded; on a FAIL -> session.RegisterProvisionFailure(m, kind)
    //    // pipeline: while (session.HasNewFailures && session.PassCount < maxReallocIterations
    //    //                  && !session.Converged) alloc = session.Pack();
    //  Bound: AiConfigV2.maxReallocIterations passes + a per-turn RejectedThisTurn set + a
    //  cross-turn cooldown (AiConfigV2.allocatorRejectCooldownTurns, parity with
    //  AiConfig.raidPlanRejectCooldownTurns) for STRUCTURAL failures only. An identical re-pack
    //  (same fingerprint) sets Converged so the loop stops early. In build-order step 5 there is no
    //  real ProvisioningManager yet, so Pack() runs exactly once; the loop scaffolding is live but
    //  its trip count is 1.
    //
    //  STATE SPLIT (the class of bug V2 exists to prevent)
    //  --------------------------------------------------------------------------------------------
    //    AiAllocatorState (per-player registry)  : CROSS-TURN only — the cooldown map. Cleared on a
    //                                              new game next to AiRadarStateRegistry.Clear().
    //    AllocationSession (created each turn)    : PER-TURN — slices, RejectedThisTurn, pass
    //                                              counter, fingerprint. Discarded at turn end.
    //
    //  RESOURCE DIMENSIONS
    //  --------------------------------------------------------------------------------------------
    //  AP only this step. All packing math goes through ResourceVector ops and never touches .Ap
    //  directly except at the boundaries (reading MissionRequirements, writing the dump), so
    //  widening the struct in build-order step 9 (Energy / Human / Materials / Tech with the first
    //  Raid) does not reshape the allocator. Energy is already on MissionRequirements but is always
    //  0 for a ground Scout, so it is not carried as a live dimension yet.
    // ===========================================================================================

    // n-dim resource abstraction — ONE live component (Ap). Step 9 adds Energy/Human/Materials/Tech.
    public readonly struct ResourceVector
    {
        public readonly float Ap;

        public ResourceVector(float ap) { Ap = ap; }

        public static readonly ResourceVector Zero = new ResourceVector(0f);

        public bool IsPositive => Ap > 1e-6f;

        public static ResourceVector operator +(ResourceVector a, ResourceVector b) => new ResourceVector(a.Ap + b.Ap);
        public static ResourceVector operator -(ResourceVector a, ResourceVector b) => new ResourceVector(a.Ap - b.Ap);
        public static ResourceVector operator *(ResourceVector a, float k) => new ResourceVector(a.Ap * k);

        // Positive part, component-wise.
        public ResourceVector ClampLow0() => new ResourceVector(Mathf.Max(0f, Ap));

        // The single scalar that ranks two envelopes (for logs / "how big is this ask").
        public float Magnitude => Ap;

        public string Fmt() => Ap.ToString("0.00", CultureInfo.InvariantCulture);
    }

    // How the ProvisioningManager (step 6) failed a mission it was handed. The allocator maps this
    // onto its reject state machine: TransientBudget -> RejectedThisTurn only (retry the rest of
    // the turn, fresh again next turn); every structural kind -> RejectedThisTurn + a cross-turn
    // cooldown so the identical doomed plan is not re-attempted every turn.
    public enum ProvisionFailureKind
    {
        None,
        TransientBudget,      // ran out of AP mid-provision / lost it to integer reconciliation
        ImpossibleMover,      // no eligible executor exists at all
        InvalidTarget,        // target hex no longer a coherent objective (occupied / hostile / off frontier)
        AssemblyInfeasible,   // the required force cannot be assembled to the viability bar
        PersistentObjective,  // the mission's objective itself is stale / incoherent
    }

    // Stable across turns so the cooldown map can find "the same mission" after a re-propose.
    // Scout: Kind + ScoutTargetKind + focus hex. Raid/Defence (step 9): widen with their own
    // target identity; until then they collapse to a kind-only key.
    public readonly struct StableMissionKey : IEquatable<StableMissionKey>
    {
        public readonly MissionKind Kind;
        public readonly int SubKind;   // ScoutTargetKind for Scout
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

        // Total order for the deterministic tie-break (BaseValue desc, then THIS).
        public int CompareTo(StableMissionKey o)
        {
            int c = Kind.CompareTo(o.Kind); if (c != 0) return c;
            c = SubKind.CompareTo(o.SubKind); if (c != 0) return c;
            c = Q.CompareTo(o.Q); if (c != 0) return c;
            return R.CompareTo(o.R);
        }
    }

    // --- Stage 5 intermediate: one axis's share of the pool. (Was a stub in the pipeline file.)
    public sealed class BudgetSlice
    {
        public DesireAxis Axis;
        public float Weight;             // radar.Weight[Axis]
        public ResourceVector Initial;   // pool * Weight
        public ResourceVector Remaining; // decremented by every draw; MAY go negative (sticky commitments)
    }

    public enum FundingStage { Strict, Remainder }

    public sealed class FundedEntry
    {
        public MissionProposal Mission;
        public int Priority;                 // 0-based index into the final Funded order
        public ResourceVector Tentative;     // the envelope handed to Provisioning
        public readonly Dictionary<DesireAxis, ResourceVector> PerAxisDraw = new Dictionary<DesireAxis, ResourceVector>();
        public bool IsCommitment;
        public FundingStage Stage;           // Remainder if it received any second-pass top-up
    }

    public enum DeferReason
    {
        InsufficientBudget,   // normal: slices could not cover Min this cycle
        InvalidContribution,  // AxisContribution sums to <= 0 (or Requirements missing)
        RejectedThisTurn,     // a provisioning failure earlier this turn excluded it
        OnCooldown,           // a structural failure put it on a cross-turn cooldown
    }

    public sealed class DeferredEntry
    {
        public MissionProposal Mission;
        public DeferReason Reason;
        public DesireAxis? BottleneckAxis;   // the binding axis for InsufficientBudget
        public ResourceVector Required;      // Min * share on the bottleneck axis
        public ResourceVector Available;     // that slice's remaining room
        public ResourceVector Missing;       // max(0, Required - Available)
    }

    // --- Stage 5 output: the ordered fund list + full diagnostics for the dump / tuning.
    public sealed class TentativeAllocation
    {
        public readonly List<FundedEntry> Funded = new List<FundedEntry>();
        public readonly List<DeferredEntry> Deferred = new List<DeferredEntry>();
        public readonly List<BudgetSlice> Slices = new List<BudgetSlice>();

        // allocation-level accounting
        public ResourceVector InitialPool;
        public ResourceVector ManagerReserve;
        public ResourceVector CommitmentDraw;
        public ResourceVector StrictFunded;
        public ResourceVector RemainderGenerated;
        public ResourceVector RemainderSpent;
        public ResourceVector Unused;
        public ResourceVector GlobalOverdraft;   // Σ negative slice remainders, as a positive magnitude
        public bool CommitmentsStarveFreshDecisions;
        public int PassNumber;

        // --- bottleneck / opportunity-cost analytics -----------------------------------------
        // Cheap terms populated now (fall straight out of the deferred entries). The counterfactual
        // "what would +1 AP change" and the per-resource BlockedValueBy* fields are reserved for
        // the Initiative module / build-order step 9 — see EstimateMarginalValueOfAp and the
        // step-5 discussion. Not computed here.
        public float BlockedValueByAp;           // Σ BaseValue of missions deferred on budget
        public DesireAxis? PrimaryBottleneckAxis;
        // TODO step 9: BlockedValueByEnergy / Human / Materials / Tech / Army / Hero / Card / Equipment.
    }

    // ===========================================================================================
    //  CROSS-TURN STATE — the cooldown map, and NOTHING else. Per-turn state lives in
    //  AllocationSession. Same registry shape as AiRadarStateRegistry.
    // ===========================================================================================
    public sealed class AiAllocatorState
    {
        private readonly Dictionary<StableMissionKey, int> _cooldownUntilTurn = new Dictionary<StableMissionKey, int>();

        public bool OnCooldown(StableMissionKey k, int turn) =>
            _cooldownUntilTurn.TryGetValue(k, out int until) && turn < until;

        public void StartCooldown(StableMissionKey k, int untilTurn)
        {
            if (!_cooldownUntilTurn.TryGetValue(k, out int cur) || untilTurn > cur)
                _cooldownUntilTurn[k] = untilTurn;
        }

        public void PurgeExpired(int turn)
        {
            var dead = _cooldownUntilTurn.Where(kv => kv.Value <= turn).Select(kv => kv.Key).ToList();
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

    // ===========================================================================================
    //  THE ALLOCATOR
    // ===========================================================================================
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

        // Counterfactual hook for the Initiative module: re-pack with `session.Pack(poolApOverride:
        // currentAp + extraAp)` and diff the funded BaseValue. Body deferred until Initiative has a
        // caller (step-5 discussion) — the only thing step 5 owes it is that Pack takes the override.
        public static float EstimateMarginalValueOfAp(AllocationSession session, int extraAp) => 0f;
    }

    public sealed class AllocationSession
    {
        private readonly WorldSnapshot _snap;
        private readonly Radar _radar;
        private readonly List<MissionProposal> _missions;
        private readonly List<Commitment> _commitments;
        private readonly AiAllocatorState _state;

        private readonly HashSet<StableMissionKey> _rejectedThisTurn = new HashSet<StableMissionKey>();
        private string _lastFingerprint;

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

        // A provisioning FAIL from the pipeline. TransientBudget -> retry only; structural -> also a
        // cross-turn cooldown so the same doomed plan is not re-tried every turn.
        public void RegisterProvisionFailure(MissionProposal mission, ProvisionFailureKind kind)
        {
            if (mission == null)
                return;
            StableMissionKey key = StableMissionKey.For(mission);
            _rejectedThisTurn.Add(key);
            HasNewFailures = true;
            if (kind != ProvisionFailureKind.None && kind != ProvisionFailureKind.TransientBudget)
                _state.StartCooldown(key, (_snap?.TurnNumber ?? 0) + AiConfigV2.allocatorRejectCooldownTurns);
        }

        // ------------------------------------------------------------------------------------- //

        public TentativeAllocation Pack(int? poolApOverride = null)
        {
            HasNewFailures = false;
            PassCount++;
            int turn = _snap?.TurnNumber ?? 0;

            var alloc = new TentativeAllocation { PassNumber = PassCount };

            // 1. Pool = own AP minus the off-budget Manager reserve.
            float rawAp = poolApOverride ?? _snap?.Self?.ActionPoints ?? 0;
            float reserve = AiConfigV2.allocatorManagerApReserve;
            var pool = new ResourceVector(Mathf.Max(0f, rawAp - reserve));
            alloc.InitialPool = pool;
            alloc.ManagerReserve = new ResourceVector(reserve);

            // 2. Radar cuts the pool into per-axis slices.
            var slices = new Dictionary<DesireAxis, BudgetSlice>();
            foreach (DesireAxis axis in DesireAxes.All)
            {
                float w = _radar.Weight.TryGetValue(axis, out float ww) ? ww : 0f;
                var s = new BudgetSlice { Axis = axis, Weight = w, Initial = pool * w, Remaining = pool * w };
                slices[axis] = s;
                alloc.Slices.Add(s);
            }

            var shareCache = new Dictionary<MissionProposal, Dictionary<DesireAxis, float>>();
            int priority = 0;

            // 3. Commitments first — sticky, pre-paid, head of the list. Draw before any fresh
            //    mission; a slice is allowed to go negative (an in-flight raid keeps its resources
            //    even if the radar cooled). Empty until build-order step 7.
            foreach (Commitment c in _commitments)
            {
                MissionProposal m = c?.Mission;
                if (m == null)
                    continue;
                Dictionary<DesireAxis, float> shares = Shares(m, shareCache);
                if (shares == null)
                    continue;
                var ask = new ResourceVector(ApDesired(m));
                var fe = new FundedEntry
                {
                    Mission = m, Priority = priority++, Tentative = ask,
                    IsCommitment = true, Stage = FundingStage.Strict,
                };
                foreach (KeyValuePair<DesireAxis, float> kv in shares)
                {
                    ResourceVector draw = ask * kv.Value;
                    slices[kv.Key].Remaining -= draw;
                    fe.PerAxisDraw[kv.Key] = draw;
                }
                alloc.Funded.Add(fe);
                alloc.CommitmentDraw += ask;
            }

            // 4. Fresh missions — strict admission, greedy by BaseValue (stable-key tie-break).
            //    Radar plays NO part in this ordering (rule 1).
            var ordered = _missions
                .Where(m => m != null)
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

                // Binding affordability across the axes this mission draws on: each slice caps
                // total funding at remaining/share; the smallest such cap is the ceiling.
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
                if (affordable + 1e-4f < min)
                {
                    float reqOnBottleneck = min * shares[bottleneck];
                    var avail = new ResourceVector(Mathf.Max(0f, slices[bottleneck].Remaining.Ap));
                    var required = new ResourceVector(reqOnBottleneck);
                    alloc.Deferred.Add(new DeferredEntry
                    {
                        Mission = m,
                        Reason = DeferReason.InsufficientBudget,
                        BottleneckAxis = bottleneck,
                        Required = required,
                        Available = avail,
                        Missing = (required - avail).ClampLow0(),
                    });
                    alloc.BlockedValueByAp += m.BaseValue;
                    continue;
                }

                float fundAp = Mathf.Clamp(affordable, min, ApDesired(m));
                var v = new ResourceVector(fundAp);
                var funded = new FundedEntry
                {
                    Mission = m, Priority = priority++, Tentative = v,
                    IsCommitment = false, Stage = FundingStage.Strict,
                };
                foreach (KeyValuePair<DesireAxis, float> kv in shares)
                {
                    ResourceVector draw = v * kv.Value;
                    slices[kv.Key].Remaining -= draw;
                    funded.PerAxisDraw[kv.Key] = draw;
                }
                alloc.Funded.Add(funded);
                alloc.StrictFunded += v;
            }

            // 5. Remainder pass — positive slice leftovers become ONE fungible pool. It may only
            //    raise already-funded FRESH missions (never a commitment, never a deferred one)
            //    from their strict level toward Desired, then toward Max, in BaseValue order.
            float remainder = 0f;
            foreach (BudgetSlice s in alloc.Slices)
                if (s.Remaining.Ap > 0f)
                {
                    remainder += s.Remaining.Ap;
                    s.Remaining = ResourceVector.Zero;   // removed from axis accounting — no double-spend
                }
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
                    if (remainder <= 1e-4f)
                        break;
                    float want = target(fe.Mission) - fe.Tentative.Ap;
                    if (want <= 1e-4f)
                        continue;
                    float give = Mathf.Min(want, remainder);
                    fe.Tentative += new ResourceVector(give);
                    fe.Stage = FundingStage.Remainder;
                    Dictionary<DesireAxis, float> shares = Shares(fe.Mission, shareCache);
                    if (shares != null)
                        foreach (KeyValuePair<DesireAxis, float> kv in shares)
                        {
                            fe.PerAxisDraw.TryGetValue(kv.Key, out ResourceVector cur);
                            fe.PerAxisDraw[kv.Key] = cur + new ResourceVector(give) * kv.Value;
                        }
                    remainder -= give;
                    alloc.RemainderSpent += new ResourceVector(give);
                }
            }
            alloc.Unused = new ResourceVector(Mathf.Max(0f, remainder));

            // 6. Totals + analytics.
            float overdraft = 0f;
            foreach (BudgetSlice s in alloc.Slices)
                if (s.Remaining.Ap < 0f)
                    overdraft += -s.Remaining.Ap;
            alloc.GlobalOverdraft = new ResourceVector(overdraft);

            var budgetDeferred = alloc.Deferred.Where(d => d.Reason == DeferReason.InsufficientBudget).ToList();
            alloc.CommitmentsStarveFreshDecisions =
                alloc.CommitmentDraw.Ap > 0f && pool.Ap > 0f
                && alloc.CommitmentDraw.Ap >= pool.Ap && budgetDeferred.Count > 0;
            alloc.PrimaryBottleneckAxis = budgetDeferred
                .Where(d => d.BottleneckAxis.HasValue)
                .GroupBy(d => d.BottleneckAxis.Value)
                .OrderByDescending(g => g.Sum(d => d.Missing.Ap))
                .Select(g => (DesireAxis?)g.Key)
                .FirstOrDefault();

            // Re-index priorities into the final Funded order (commitments already first).
            for (int i = 0; i < alloc.Funded.Count; i++)
                alloc.Funded[i].Priority = i;

            // 7. Fingerprint -> early-stop signal for the retry loop.
            string fp = Fingerprint(alloc);
            Converged = fp == _lastFingerprint;
            _lastFingerprint = fp;

            LogDump(alloc);
            return alloc;
        }

        // ---------------------------------------------------------------------------- helpers ----

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
                if (sum > 1e-6f)
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

        private static float ApMinimum(MissionProposal m) => Mathf.Max(0f, m.Requirements?.ApMinimum ?? 0f);
        private static float ApDesired(MissionProposal m) =>
            Mathf.Max(ApMinimum(m), m.Requirements?.ApDesired ?? m.Requirements?.ApMinimum ?? 0f);
        private static float ApMaximum(MissionProposal m) =>
            Mathf.Max(ApDesired(m), m.Requirements?.ApMaximum ?? m.Requirements?.ApDesired ?? 0f);

        private sealed class MissionKeyComparer : IComparer<StableMissionKey>
        {
            public static readonly MissionKeyComparer Instance = new MissionKeyComparer();
            public int Compare(StableMissionKey a, StableMissionKey b) => a.CompareTo(b);
        }

        private static string Fingerprint(TentativeAllocation a)
        {
            string funded = string.Join(",", a.Funded
                .Select(fe => $"{StableMissionKey.For(fe.Mission)}={fe.Tentative.Ap.ToString("0.0", CultureInfo.InvariantCulture)}"));
            string deferred = string.Join(",", a.Deferred
                .Select(d => $"{StableMissionKey.For(d.Mission)}:{d.Reason}")
                .OrderBy(x => x, StringComparer.Ordinal));
            return funded + "|" + deferred;
        }

        private static string LogNum(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        private static void LogDump(TentativeAllocation a)
        {
            string slices = string.Join(" ", a.Slices.Select(s =>
                $"{DesireAxes.Abbrev(s.Axis)} {s.Weight.ToString("0.00", CultureInfo.InvariantCulture)}"
                + $"→{LogNum(s.Initial.Ap)}"));
            AiDebugLog.Write($"[AI][V2] allocator p{a.PassNumber} — pool {LogNum(a.InitialPool.Ap)} "
                + $"(ap {LogNum(a.InitialPool.Ap + a.ManagerReserve.Ap)} − mgr {LogNum(a.ManagerReserve.Ap)}) | {slices}");

            foreach (FundedEntry fe in a.Funded)
            {
                string draw = string.Join(" ", fe.PerAxisDraw
                    .Where(kv => kv.Value.Ap > 0.001f)
                    .Select(kv => $"{DesireAxes.Abbrev(kv.Key)} {LogNum(kv.Value.Ap)}"));
                AiDebugLog.Write($"[AI][V2]   {(fe.IsCommitment ? "commit" : "fund  ")} "
                    + $"{StableMissionKey.For(fe.Mission)} base {LogNum(fe.Mission.BaseValue)} "
                    + $"ap {LogNum(fe.Tentative.Ap)} draw[{draw}] {fe.Stage.ToString().ToLowerInvariant()}");
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
                + $"strictAp {LogNum(a.StrictFunded.Ap)}, overdraft {LogNum(a.GlobalOverdraft.Ap)}, "
                + $"blockedValByAp {LogNum(a.BlockedValueByAp)}"
                + (a.CommitmentsStarveFreshDecisions ? " [commitments starving fresh]" : ""));
        }
    }
}
