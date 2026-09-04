using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AXIS BUDGET LEDGER  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  The ONE per-turn AP entitlement split. Created right after the radar from
    //      allocatableAP = max(0, realAP - housekeepingApReserve)
    //  sliced by the 5-axis radar. There is exactly ONE radar allocation and ONE shared AP ledger
    //  for the turn:
    //    · StrategicManager Phase A debits demand.RequestingAxis when a demand-driven card play
    //      commits (real, committed spend — never a speculative pack);
    //    · the mission ResourceAllocator then seeds its per-axis slices from Balance(axis) instead
    //      of re-splitting current AP by the radar (NO second radar split);
    //    · Phase B (surplus) does NOT read this — it works off real remaining root resources.
    //
    //  AP ONLY. Human / Energy / Materials / Tech are shared physical stockpiles and are NEVER
    //  axis-sliced; their scarcity is handled by affordability + reserves + opportunity cost.
    //
    //  AP is physically discrete while radar slices are fractional. A high-priority axis must not
    //  lose an otherwise executable card+follow-up chain solely because its slice ends at e.g.
    //  3.75 AP and the smallest useful chain costs 4. DiscreteAdmissionBudget therefore permits
    //  ONLY the fractional tail to the next whole AP (<1 AP), backed by UNRESERVED positive
    //  balances on the other axes. CommitDiscreteFollowupBorrow transfers that exact tail after a
    //  deployment so the mission allocator sees the promised follow-up AP on the requesting axis.
    //  Total ledger AP never increases and one axis can never steal another axis's follow-up claim.
    //
    //  OWNERSHIP BOUNDARY (deliberate, not a half-finished invariant). This ledger is
    //  "AxisEntitlementAfterPreparation": it records ONLY Strategic Manager Phase A's real
    //  committed card-play spend against each axis. It does NOT track mission spending — once the
    //  mission ResourceAllocator reads Balance(axis) as a slice size, the allocator owns the rest
    //  of the lifecycle via its own _lockedClaims (provenance + re-pack). Do NOT also call
    //  Debit() from RegisterProvisionSuccess — that would double-count against _lockedClaims. The
    //  ledger therefore still shows pre-allocation balances after missions run; that is fine
    //  because Phase B never reads it (it works off real remaining PlayerRoot resources).
    // ===========================================================================================
    public sealed class AxisBudgetLedger
    {
        private readonly Dictionary<DesireAxis, float> _balance = new Dictionary<DesireAxis, float>();
        private readonly Dictionary<DesireAxis, float> _initial = new Dictionary<DesireAxis, float>();
        private readonly Dictionary<DesireAxis, float> _followupReserved = new Dictionary<DesireAxis, float>();

        public float AllocatableApAtCreation { get; private set; }
        public float HousekeepingReserve { get; private set; }

        public static AxisBudgetLedger Create(float realActionPoints, Radar radar)
        {
            var ledger = new AxisBudgetLedger
            {
                HousekeepingReserve = Mathf.Max(0f, AiConfigV2.housekeepingApReserve),
            };
            float allocatable = Mathf.Max(0f, realActionPoints - ledger.HousekeepingReserve);
            ledger.AllocatableApAtCreation = allocatable;

            foreach (DesireAxis a in DesireAxes.All)
            {
                float w = radar?.Weight != null && radar.Weight.TryGetValue(a, out float ww) ? Mathf.Max(0f, ww) : 0f;
                float slice = allocatable * w;
                ledger._initial[a] = slice;
                ledger._balance[a] = slice;
                ledger._followupReserved[a] = 0f;
            }
            return ledger;
        }

        public float Balance(DesireAxis a) => _balance.TryGetValue(a, out float v) ? v : 0f;
        public float Initial(DesireAxis a) => _initial.TryGetValue(a, out float v) ? v : 0f;
        public float ReservedFollowup(DesireAxis a) =>
            _followupReserved.TryGetValue(a, out float v) ? Mathf.Max(0f, v) : 0f;
        public float UnreservedBalance(DesireAxis a) =>
            Mathf.Max(0f, Balance(a) - ReservedFollowup(a));

        public void ReserveFollowup(DesireAxis a, float ap)
        {
            if (ap <= 0f)
                return;
            _followupReserved[a] = ReservedFollowup(a) + ap;
        }

        // Candidate-side read only. This is NOT free overdraft: at most the fractional amount to
        // the next integer AP is exposed, and only when other axes still own enough UNRESERVED AP
        // to fund that amount. The actual transfer happens only after a successful deployment.
        public float DiscreteAdmissionBudget(DesireAxis a)
        {
            float eps = Mathf.Max(0.0001f, AiConfigV2.allocatorSliceEpsilon);
            float own = Mathf.Max(0f, Balance(a));
            float rounded = Mathf.Ceil(Mathf.Max(0f, own - eps));
            float fractionalTail = Mathf.Clamp(rounded - own, 0f, 1f);
            if (fractionalTail <= eps)
                return own;

            float donors = DesireAxes.All
                .Where(other => !EqualityComparer<DesireAxis>.Default.Equals(other, a))
                .Sum(UnreservedBalance);
            return own + Mathf.Min(fractionalTail, donors);
        }

        // Called after the selected Phase-A plan really deployed and Debit() recorded its AP.
        // Keeps already-reserved + newly-reserved follow-up AP physically present on this axis by
        // moving only the sub-1-AP discrete tail admitted above. Donors are reduced proportionally
        // from their UNRESERVED balances, preserving the radar split as closely as possible while
        // keeping the ledger sum constant.
        public float CommitDiscreteFollowupBorrow(DesireAxis a, float requiredRemaining)
        {
            float eps = Mathf.Max(0.0001f, AiConfigV2.allocatorSliceEpsilon);
            float gap = Mathf.Max(0f, requiredRemaining - Balance(a));
            if (gap <= eps)
                return 0f;
            if (gap > 1f + eps)
                return 0f; // not a discrete rounding tail; never turn this into general overdraft

            var donors = DesireAxes.All
                .Where(other => !EqualityComparer<DesireAxis>.Default.Equals(other, a))
                .Select(other => new { Axis = other, Amount = UnreservedBalance(other) })
                .Where(x => x.Amount > eps)
                .ToList();
            float donorTotal = donors.Sum(x => x.Amount);
            if (donorTotal + eps < gap)
                return 0f;

            float remaining = gap;
            for (int i = 0; i < donors.Count; i++)
            {
                var donor = donors[i];
                float take = i == donors.Count - 1
                    ? remaining
                    : Mathf.Min(remaining, gap * donor.Amount / donorTotal);
                if (take <= 0f)
                    continue;
                _balance[donor.Axis] = Balance(donor.Axis) - take;
                _balance[a] = Balance(a) + take;
                remaining -= take;
            }
            return gap - Mathf.Max(0f, remaining);
        }

        // Real, committed spend. A failed/partial chain may drive an axis negative because real AP
        // was already consumed; the mission allocator clamps its own slice at 0 and physical AP is
        // the final backstop. Successful discrete admission is rebalanced by the method above.
        public void Debit(DesireAxis a, float ap)
        {
            if (ap <= 0f)
                return;
            _balance[a] = Balance(a) - ap;
        }

        public string DebugLine() => string.Join(" ", DesireAxes.All.Select(a =>
        {
            string reserve = ReservedFollowup(a) > AiConfigV2.allocatorSliceEpsilon
                ? $" r{ReservedFollowup(a).ToString("0.00", CultureInfo.InvariantCulture)}"
                : "";
            return $"{DesireAxes.Abbrev(a)} {Balance(a).ToString("0.00", CultureInfo.InvariantCulture)}"
                + $"/{Initial(a).ToString("0.00", CultureInfo.InvariantCulture)}{reserve}";
        }));
    }
}
