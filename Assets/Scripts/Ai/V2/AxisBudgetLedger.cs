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
            }
            return ledger;
        }

        public float Balance(DesireAxis a) => _balance.TryGetValue(a, out float v) ? v : 0f;
        public float Initial(DesireAxis a) => _initial.TryGetValue(a, out float v) ? v : 0f;

        // Real, committed spend. May drive one axis slightly negative (a card straddling the exact
        // slice edge) — the mission allocator still clamps its own slice at 0, and the physical AP
        // cap there is the real backstop.
        public void Debit(DesireAxis a, float ap)
        {
            if (ap <= 0f)
                return;
            _balance[a] = Balance(a) - ap;
        }

        public string DebugLine() => string.Join(" ", DesireAxes.All.Select(a =>
            $"{DesireAxes.Abbrev(a)} {Balance(a).ToString("0.00", CultureInfo.InvariantCulture)}"
            + $"/{Initial(a).ToString("0.00", CultureInfo.InvariantCulture)}"));
    }
}
