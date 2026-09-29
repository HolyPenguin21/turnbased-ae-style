using Game.Map;
using Game.Units;

namespace Game.Combat
{
    /// <summary>
    /// Spatial defensive rules that are derived from the live tactical grid.
    /// </summary>
    public static class BattleProtectionRules
    {
        public const int GuardedDefenseBonus = 1;

        public static int GetTotalDefenseBonus(BattleGrid grid, UnitData unit, ArmyData battleDefender,
            int defenderHexBonus)
        {
            int hex = battleDefender != null && unit != null && battleDefender.Members.Contains(unit)
                ? defenderHexBonus : 0;
            return hex + GetGuardedDefenseBonus(grid, unit, battleDefender);
        }

        public static int GetGuardedDefenseBonus(BattleGrid grid, UnitData unit, ArmyData battleDefender)
        {
            if (grid == null || unit == null || !unit.IsGroundCombatant
                || !grid.TryFindPosition(unit, out int row, out int col))
                return 0;

            bool defenderSide = battleDefender != null && battleDefender.Members.Contains(unit);
            int ownBackRow = defenderSide ? BattleGrid.DefenderBackRow : BattleGrid.AttackerBackRow;
            int ownFrontRow = defenderSide ? BattleGrid.DefenderFrontRow : BattleGrid.AttackerFrontRow;
            if (row != ownBackRow)
                return 0;

            int minCol = System.Math.Max(0, col - 1);
            int maxCol = System.Math.Min(BattleGrid.Columns - 1, col + 1);
            for (int c = minCol; c <= maxCol; c++)
            {
                UnitData guard = grid.Get(ownFrontRow, c);
                if (guard != null && guard.IsGroundCombatant && guard.Owner == unit.Owner
                    && guard.HitPointsCurrent > 0)
                    return GuardedDefenseBonus;
            }

            return 0;
        }
    }
}
