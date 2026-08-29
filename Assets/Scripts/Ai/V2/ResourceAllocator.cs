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

    // Declared in step 5; ProvisioningManager fills it in step 6.
    public enum ProvisionFailureKind
    {
        None,
        TransientBudget,
        ImpossibleMover,
        InvalidTarget,
        AssemblyInfeasible,
        PersistentObjective,
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

        // Step 6 calls this after a real atomic provisioning failure.
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

        public TentativeAllocation Pack()
        {
            HasNewFailures = false;
            PassCount++;
            int turn = _snap?.TurnNumber ?? 0;
            float eps = AiConfigV2.allocatorSliceEpsilon;

            var alloc = new TentativeAllocation { PassNumber = PassCount };

            // 1. Pool = snapshot AP minus the Manager reserve.
            float rawAp = _snap?.Self?.ActionPoints ?? 0;
            float reserve = Mathf.Max(0f, AiConfigV2.allocatorManagerApReserve);
            var pool = new ResourceVector(Mathf.Max(0f, rawAp - reserve));
            alloc.InitialPool = pool;
            alloc.ManagerReserve = new ResourceVector(reserve);

            // 2. Radar cuts the pool into per-axis slices. Radar has no role in mission ordering.
            var slices = new Dictionary<DesireAxis, BudgetSlice>();
            foreach (DesireAxis axis in DesireAxes.All)
            {
                float w = _radar.Weight.TryGetValue(axis, out float ww) ? Mathf.Max(0f, ww) : 0f;
                var s = new BudgetSlice { Axis = axis, Weight = w, Initial = pool * w, Remaining = pool * w };
                slices[axis] = s;
                alloc.Slices.Add(s);
            }

            var shareCache = new Dictionary<MissionProposal, Dictionary<DesireAxis, float>>();
            int priority = 0;

            // 3. Commitments first. Sticky/pre-paid: they can drive slices negative.
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

            // 5. Positive leftovers lose axis identity and become one fungible remainder pool.
            float remainder = 0f;
            foreach (BudgetSlice s in alloc.Slices)
            {
                if (s.Remaining.Ap <= 0f)
                    continue;
                remainder += s.Remaining.Ap;
                s.Remaining = ResourceVector.Zero;
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

            // 6. Commitment overdraft diagnostic. Positive slices have already moved to remainder;
            //    any negative slice is therefore sticky commitment debt against the current radar.
            float overdraft = 0f;
            foreach (BudgetSlice s in alloc.Slices)
                if (s.Remaining.Ap < 0f)
                    overdraft += -s.Remaining.Ap;
            alloc.GlobalOverdraft = new ResourceVector(overdraft);

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
                .Select(fe => $"{StableMissionKey.For(fe.Mission)}={fe.Tentative.Ap.ToString("0.00", CultureInfo.InvariantCulture)}"));
            string deferred = string.Join(",", a.Deferred
                .Select(d => $"{StableMissionKey.For(d.Mission)}:{d.Reason}")
                .OrderBy(x => x, StringComparer.Ordinal));
            string slices = string.Join(",", a.Slices
                .Select(s => $"{DesireAxes.Abbrev(s.Axis)}={s.Remaining.Ap.ToString("0.00", CultureInfo.InvariantCulture)}"));
            return funded + "|" + deferred + "|" + slices
                + "|unused=" + a.Unused.Ap.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string LogNum(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        private static void LogDump(TentativeAllocation a)
        {
            string slices = string.Join(" ", a.Slices.Select(s =>
                $"{DesireAxes.Abbrev(s.Axis)} {s.Weight.ToString("0.00", CultureInfo.InvariantCulture)}"
                + $"→{LogNum(s.Initial.Ap)} left {LogNum(s.Remaining.Ap)}"));
            AiDebugLog.Write($"[AI][V2] allocator p{a.PassNumber} — pool {LogNum(a.InitialPool.Ap)} "
                + $"(ap {LogNum(a.InitialPool.Ap + a.ManagerReserve.Ap)} − mgr {LogNum(a.ManagerReserve.Ap)}) | {slices}");

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
                + $"overdraft {LogNum(a.GlobalOverdraft.Ap)}");
        }
    }
}
