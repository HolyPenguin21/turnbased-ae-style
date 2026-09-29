using Game.Units;

namespace Game.Combat
{
    /// <summary>
    /// Canonical legality rules for the pre-battle Arrangement phase.
    /// Tactical movement after round start is intentionally NOT governed by this class.
    /// </summary>
    public static class BattlePlacementRules
    {
        public static bool IsOwnDeploymentRow(int row, int frontRow, int backRow)
            => row == frontRow || row == backRow;

        public static bool CanPlace(UnitData unit, int row, int col, int frontRow, int backRow)
        {
            if (unit == null || !BattleGrid.InBounds(row, col))
                return false;
            if (!IsOwnDeploymentRow(row, frontRow, backRow))
                return false;

            // Heroes are commanders/support pieces, not ordinary front-line combatants.
            // They may occupy any column of the back row; the historical column-0 reservation
            // is only a default-layout convention, not a unique legal hero slot.
            if (unit.IsHero)
                return row == backRow;

            // Ordinary combatants may be arranged freely between the side's front/back rows.
            // Range influences AI preference, not legality.
            return true;
        }

        public static bool CanSwap(BattleGrid grid, int fromRow, int fromCol, int toRow, int toCol,
            int frontRow, int backRow)
        {
            if (grid == null
                || !BattleGrid.InBounds(fromRow, fromCol)
                || !BattleGrid.InBounds(toRow, toCol)
                || !IsOwnDeploymentRow(fromRow, frontRow, backRow)
                || !IsOwnDeploymentRow(toRow, frontRow, backRow))
                return false;

            UnitData moving = grid.Get(fromRow, fromCol);
            UnitData displaced = grid.Get(toRow, toCol);
            if (moving == null || !CanPlace(moving, toRow, toCol, frontRow, backRow))
                return false;

            return displaced == null || CanPlace(displaced, fromRow, fromCol, frontRow, backRow);
        }
    }
}
