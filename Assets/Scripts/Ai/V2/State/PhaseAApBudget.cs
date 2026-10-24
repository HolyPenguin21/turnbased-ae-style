using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using Game.Map;
using Game.Units;
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
    //  The one piece of state it owns: AP promised to capabilities Phase A already delivered
    //  (their follow-up step), one promise per army — an army has ONE next activation. Later Phase
    //  A chains must leave it; the allocator, which funds those follow-ups, never reads it. The
    //  budget is shared by every Phase A entry of the turn, so a promise lives only until its army
    //  has stepped (ReleaseSpentDeliveryFollowups) or, for a local refit, is recomputed on entry.
    //
    //  Radar model #1a — the radar does not slice AP per axis. It scales OBJECTIVE VALUE
    //  (EffectiveValue), not the AP a card chain may draw. AP ONLY: Human / Energy / Materials /
    //  Tech are handled by affordability + reservations + opportunity cost.
    // ===========================================================================================
    public sealed class PhaseAApBudget
    {
        private readonly PlayerRoot _root;
        private readonly Dictionary<int, Followup> _followup = new Dictionary<int, Followup>();

        // Refit: recomputed from the live operation on every entry. Delivery: kept until the army
        // steps — its activation flag and its members' own movement when the promise was made tell
        // when. Per member, not the army minimum: a slower body joining later lowers the army's
        // movement without any step being taken.
        private struct Followup
        {
            public float Ap;
            public bool Refit;
            public bool ActivatedAtPromise;
            public List<(UnitData unit, int move)> MovesAtPromise;
        }

        private PhaseAApBudget(PlayerRoot root) => _root = root;

        public static PhaseAApBudget Create(PlayerRoot root) => new PhaseAApBudget(root);

        // The player's live AP.
        public float Balance() => _root != null ? Mathf.Max(0f, _root.ActionPoints) : 0f;
        public float ReservedFollowup() => Mathf.Max(0f, _followup.Values.Sum(f => f.Ap));

        // Live AP left after follow-up AP already promised to delivered capabilities. Not clamped:
        // a negative room must still reject a zero-cost admission check (cost > room).
        public float UnreservedBalance() => Balance() - ReservedFollowup();

        // A delivered capability's follow-up step on its recipient army. Several deliveries into
        // one army serve its one next activation, so the larger promise stands, never the sum. A
        // promise is only ever made right after a delivery, before the army can have moved, so
        // the step markers are taken afresh each time.
        internal void ReserveDeliveryFollowup(ArmyData army, float ap)
        {
            if (army == null || ap <= 0f)
                return;
            bool refit = false;
            if (_followup.TryGetValue(army.Id, out Followup prior))
            {
                ap = Mathf.Max(prior.Ap, ap);
                refit = prior.Refit;
            }
            _followup[army.Id] = new Followup
            {
                Ap = ap,
                Refit = refit,
                ActivatedAtPromise = army.HasActivatedThisTurn,
                MovesAtPromise = army.Members.Where(m => m != null).Select(m => (m, m.MoveCurrent)).ToList(),
            };
        }

        // A delivery promise ends when its army is gone, has activated since the promise, or one of
        // the members it had then has spent movement since (the army took the step the AP was for).
        internal void ReleaseSpentDeliveryFollowups(System.Func<int, ArmyData> armyById)
        {
            foreach (KeyValuePair<int, Followup> entry in _followup.Where(kv => !kv.Value.Refit).ToList())
            {
                ArmyData army = armyById(entry.Key);
                if (army == null
                    || (!entry.Value.ActivatedAtPromise && army.HasActivatedThisTurn)
                    || entry.Value.MovesAtPromise.Any(m => army.Members.Contains(m.unit) && m.unit.MoveCurrent < m.move))
                    _followup.Remove(entry.Key);
            }
        }

        // Several local refits serve one next activation, so replace its promise, never sum it.
        internal float ActorFollowup(int id) => _followup.TryGetValue(id, out Followup f) ? f.Ap : 0f;
        internal IEnumerable<int> ActorFollowupIds => _followup.Where(kv => kv.Value.Refit).Select(kv => kv.Key);
        internal void ReserveActorFollowup(int id, float ap)
        {
            if (ap > 0f) _followup[id] = new Followup { Ap = ap, Refit = true };
            else _followup.Remove(id);
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
