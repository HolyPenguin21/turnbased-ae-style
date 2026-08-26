using System.Collections.Generic;
using Game.Players;
using Game.Units;

namespace Game.Aviation
{
    // Turn-scoped AA availability — reset for one owner at that owner's own turn start (see
    // GameTurnController.ReplenishMoveForOwner, alongside HasAirAttackedThisTurn). Two
    // independent facts, per the manual's own AA rule:
    //   1. An AA unit that has already FIRED (chose Attack, not Skip) against ANY air army this
    //      turn cannot fire again at all until its owner's next turn.
    //   2. A (aaUnit, specific air army) PAIR that has already been offered a reaction this turn
    //      — fired OR skipped — is never re-prompted for that SAME air army re-entering/leaving
    //      the same radius again this turn, even though the AA unit itself may still be free to
    //      react to a DIFFERENT air army.
    public static class AntiAirState
    {
        private static readonly HashSet<UnitData> FiredThisTurn = new HashSet<UnitData>();
        private static readonly HashSet<(UnitData aaUnit, int airArmyId)> PromptedThisTurn =
            new HashSet<(UnitData, int)>();

        public static bool CanReact(UnitData aaUnit, int airArmyId)
        {
            return aaUnit != null && !FiredThisTurn.Contains(aaUnit)
                && !PromptedThisTurn.Contains((aaUnit, airArmyId));
        }

        // Called once a reaction has actually been offered (Attack or Skip both count — see this
        // class's own comment on why Skip still suppresses re-prompting for THIS air army).
        public static void RecordPrompted(UnitData aaUnit, int airArmyId, bool fired)
        {
            if (aaUnit == null)
                return;
            PromptedThisTurn.Add((aaUnit, airArmyId));
            if (fired)
                FiredThisTurn.Add(aaUnit);
        }

        // `owner` is deliberately allowed to be null here — that's Neutral's own turn slot (see
        // GameTurnController.ReplenishMoveForOwner/BeginPlayerTurn's own "Neutral's fixed last
        // slot" comment), and Neutral-owned AA units need this same per-turn reset just as much
        // as a real player's do. An early `owner == null` return used to skip it entirely, so a
        // Neutral AA unit that ever fired once stayed stuck in FiredThisTurn forever — it could
        // never react again on any later turn (project owner's own report, 2026-08-26).
        public static void ResetForOwner(PlayerSetupData owner)
        {
            FiredThisTurn.RemoveWhere(unit => unit.Owner == owner);
            PromptedThisTurn.RemoveWhere(entry => entry.aaUnit.Owner == owner);
        }
    }
}
