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
    //  Radar model #1a — the radar does not slice AP per axis. It scales OBJECTIVE VALUE
    //  (EffectiveValue), not the AP a mission may draw. Every demand-driven Phase A card play and
    //  every mission the ResourceAllocator funds spends from this one pool, ranked by value. The
    //  requesting axis appears only as a label in logs/trace.
    //
    //  AP ONLY. Human / Energy / Materials / Tech are shared physical stockpiles and are NEVER
    //  pooled here; their scarcity is handled by affordability + reserves + opportunity cost.
    //
    //  OWNERSHIP BOUNDARY. This ledger records ONLY Strategic Manager Phase A's real
    //  committed card-play spend. It does NOT track mission spending — once the mission
    //  ResourceAllocator reads Balance() as its pool size, the allocator owns the rest of the
    //  lifecycle via its own _lockedClaims. Do NOT also call Debit() from RegisterProvisionSuccess.
    //  Phase B never reads it (it works off real remaining PlayerRoot resources).
    // ===========================================================================================
    public sealed class ApBudgetLedger
    {
        private float _pool;
        private float _initialPool;
        private float _followupReserved;

        public static ApBudgetLedger Create(float realActionPoints)
        {
            float allocatable = Mathf.Max(0f,
                realActionPoints - Mathf.Max(0f, AiConfigV2.housekeepingApReserve));
            return new ApBudgetLedger
            {
                _initialPool = allocatable,
                _pool = allocatable,
                _followupReserved = 0f,
            };
        }

        public float Balance() => _pool;
        public float ReservedFollowup() => Mathf.Max(0f, _followupReserved);

        // Pool left after follow-up AP already promised to delivered capabilities. Not clamped:
        // a negative room must still reject a zero-cost admission check (cost > room).
        public float UnreservedBalance() => _pool - ReservedFollowup();

        public void ReserveFollowup(float ap)
        {
            if (ap > 0f)
                _followupReserved += ap;
        }

        // Admission budget for a discrete Phase A chain: the whole pool, never negative.
        public float DiscreteAdmissionBudget() => Mathf.Max(0f, _pool);

        // Real, committed spend. A failed/partial chain may drive the pool negative because real AP
        // was already consumed; the mission allocator clamps its own pool at 0 and physical AP is
        // the final backstop.
        public void Debit(float ap)
        {
            if (ap > 0f)
                _pool -= ap;
        }

        public string DebugLine()
        {
            string reserve = ReservedFollowup() > AiConfigV2.allocatorSliceEpsilon
                ? $" reserved {ReservedFollowup().ToString("0.00", CultureInfo.InvariantCulture)}"
                : "";
            return $"sharedAP {_pool.ToString("0.00", CultureInfo.InvariantCulture)}"
                + $"/{_initialPool.ToString("0.00", CultureInfo.InvariantCulture)}{reserve} axis=value-only";
        }
    }
}
