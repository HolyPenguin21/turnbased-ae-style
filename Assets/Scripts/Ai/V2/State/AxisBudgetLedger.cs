using System.Globalization;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  AP BUDGET LEDGER  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  ONE scalar AP pool for the turn. Created right after the radar from
    //      allocatableAP = max(0, realAP - housekeepingApReserve)
    //
    //  Radar model #1a — the radar NO LONGER slices AP per axis. It scales OBJECTIVE VALUE
    //  (EffectiveValue), not the AP a mission may draw. Every demand-driven Phase A card play and
    //  every mission the ResourceAllocator funds spends from this one pool, ranked by value; the
    //  axis a spend is "charged to" is telemetry only.
    //
    //  The per-axis method signatures are kept (the `DesireAxis` argument is accepted and ignored)
    //  so Strategic Manager Phase A, the Materialization feasibility/candidate path and
    //  InfrastructureFulfillment compile unchanged — they now transparently read/charge the single
    //  pool. Dropping those vestigial arguments is a follow-up cleanup.
    //
    //  AP ONLY. Human / Energy / Materials / Tech are shared physical stockpiles and are NEVER
    //  pooled here; their scarcity is handled by affordability + reserves + opportunity cost.
    //
    //  OWNERSHIP BOUNDARY (unchanged). This ledger records ONLY Strategic Manager Phase A's real
    //  committed card-play spend. It does NOT track mission spending — once the mission
    //  ResourceAllocator reads Balance() as its pool size, the allocator owns the rest of the
    //  lifecycle via its own _lockedClaims. Do NOT also call Debit() from RegisterProvisionSuccess.
    //  Phase B never reads it (it works off real remaining PlayerRoot resources).
    // ===========================================================================================
    public sealed class AxisBudgetLedger
    {
        private float _pool;
        private float _initialPool;
        private float _followupReserved;

        public float AllocatableApAtCreation { get; private set; }
        public float HousekeepingReserve { get; private set; }

        public static AxisBudgetLedger Create(float realActionPoints)
        {
            var ledger = new AxisBudgetLedger
            {
                HousekeepingReserve = Mathf.Max(0f, AiConfigV2.housekeepingApReserve),
            };
            float allocatable = Mathf.Max(0f, realActionPoints - ledger.HousekeepingReserve);
            ledger.AllocatableApAtCreation = allocatable;
            ledger._initialPool = allocatable;
            ledger._pool = allocatable;
            ledger._followupReserved = 0f;
            return ledger;
        }

        // Pool-wide reads. The DesireAxis overloads ignore the axis (radar model #1a) and exist
        // only so existing callers compile without an edit.
        public float Balance() => _pool;
        public float Balance(DesireAxis _) => _pool;
        public float Initial() => _initialPool;
        public float Initial(DesireAxis _) => _initialPool;
        public float ReservedFollowup() => Mathf.Max(0f, _followupReserved);
        public float ReservedFollowup(DesireAxis _) => Mathf.Max(0f, _followupReserved);
        public float UnreservedBalance() => Mathf.Max(0f, _pool - ReservedFollowup());
        public float UnreservedBalance(DesireAxis _) => UnreservedBalance();

        public void ReserveFollowup(DesireAxis _, float ap)
        {
            if (ap > 0f)
                _followupReserved += ap;
        }

        // One pool — the fractional-tail cross-axis borrow the sliced ledger needed is gone: there
        // is nothing to borrow FROM, the whole pool is already available to every axis.
        public float DiscreteAdmissionBudget(DesireAxis _) => Mathf.Max(0f, _pool);

        // No-op under a single pool. Kept so StrategicPhaseA's follow-up path compiles.
        public float CommitDiscreteFollowupBorrow(DesireAxis _, float requiredRemaining) => 0f;

        // Real, committed spend. A failed/partial chain may drive the pool negative because real AP
        // was already consumed; the mission allocator clamps its own pool at 0 and physical AP is
        // the final backstop.
        public void Debit(DesireAxis _, float ap)
        {
            if (ap > 0f)
                _pool -= ap;
        }

        public string DebugLine()
        {
            string reserve = ReservedFollowup() > AiConfigV2.allocatorSliceEpsilon
                ? $" reserved {ReservedFollowup().ToString("0.00", CultureInfo.InvariantCulture)}"
                : "";
            return $"pool {_pool.ToString("0.00", CultureInfo.InvariantCulture)}"
                + $"/{_initialPool.ToString("0.00", CultureInfo.InvariantCulture)}{reserve}";
        }
    }
}
