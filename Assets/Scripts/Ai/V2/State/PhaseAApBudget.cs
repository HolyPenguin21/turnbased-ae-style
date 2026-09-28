using System.Globalization;
using Game.Map;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  PHASE A AP BUDGET  (Strategy V2 — Strategic Manager)
    // ===========================================================================================
    //  What one Phase A pass may still admit, read LIVE from the player's AP. It is not a second
    //  pool: every AP Phase A, the allocator or a mission spends is already gone from it, and the
    //  holds of other owners are applied at each chain's guard (StrategicSpendability /
    //  TurnResourceBook) under that chain's own SpendAuthority.
    //
    //  The one piece of state it owns: AP promised to capabilities Phase A already delivered this
    //  pass (their follow-up step). Later Phase A chains must leave it; the allocator, which funds
    //  those follow-ups, never reads it.
    //
    //  Radar model #1a — the radar does not slice AP per axis. It scales OBJECTIVE VALUE
    //  (EffectiveValue), not the AP a card chain may draw. AP ONLY: Human / Energy / Materials /
    //  Tech are handled by affordability + reservations + opportunity cost.
    // ===========================================================================================
    public sealed class PhaseAApBudget
    {
        private readonly PlayerRoot _root;
        private float _followupReserved;

        private PhaseAApBudget(PlayerRoot root) => _root = root;

        public static PhaseAApBudget Create(PlayerRoot root) => new PhaseAApBudget(root);

        // The player's live AP.
        public float Balance() => _root != null ? Mathf.Max(0f, _root.ActionPoints) : 0f;
        public float ReservedFollowup() => Mathf.Max(0f, _followupReserved);

        // Live AP left after follow-up AP already promised to delivered capabilities. Not clamped:
        // a negative room must still reject a zero-cost admission check (cost > room).
        public float UnreservedBalance() => Balance() - ReservedFollowup();

        public void ReserveFollowup(float ap)
        {
            if (ap > 0f)
                _followupReserved += ap;
        }

        // Admission budget for a discrete Phase A chain: the whole live AP.
        public float DiscreteAdmissionBudget() => Balance();

        public string DebugLine()
        {
            string reserve = ReservedFollowup() > AiConfigV2.allocatorSliceEpsilon
                ? $" followup {ReservedFollowup().ToString("0.00", CultureInfo.InvariantCulture)}"
                : "";
            return $"liveAP {Balance().ToString("0.00", CultureInfo.InvariantCulture)}{reserve}";
        }
    }
}
