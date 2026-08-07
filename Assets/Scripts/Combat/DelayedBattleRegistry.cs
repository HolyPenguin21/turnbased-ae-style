using System.Collections.Generic;

namespace Game.Combat
{
    // Every battle delayed this turn (see PendingBattle / the manual's "Delay Attack") —
    // drained by GameTurnController right before the next round actually starts, so both the
    // human and AI (once AI can decide anything here) get the rest of this turn to bring in
    // reinforcements before it's resolved.
    public static class DelayedBattleRegistry
    {
        private static readonly List<PendingBattle> Pending = new List<PendingBattle>();

        // Tied to the hex, per the user's own spec — a second Delay against a hex that already
        // has one pending is silently dropped instead of queuing a duplicate. Without this, the
        // same stand-off could ask to be delayed more than once and then replay the battle-prep
        // popup that many times over for what the player experiences as a single confrontation.
        public static void Add(PendingBattle battle)
        {
            if (battle == null)
                return;
            foreach (PendingBattle existing in Pending)
                if (existing.Hex.Equals(battle.Hex))
                    return;
            Pending.Add(battle);
        }

        public static bool HasAny => Pending.Count > 0;

        public static PendingBattle TakeNext()
        {
            if (Pending.Count == 0)
                return null;
            PendingBattle next = Pending[0];
            Pending.RemoveAt(0);
            return next;
        }

        public static void Clear() => Pending.Clear();
    }
}
