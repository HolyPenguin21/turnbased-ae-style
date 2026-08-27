using System.Collections.Generic;
using Game.Map;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    // The battlefield for one Tactical Battle Module instance — 5 columns wide, 5 rows deep,
    // per the user's own spec: each side gets a Back row (with a reserved hero slot at column 0,
    // per the manual) and a Front row, with one shared neutral row between the two Front rows
    // (new — not in the manual, the user's own addition; melee units must move into it before
    // they can reach the enemy Front row at all). The "one more card in front than back" rule
    // from the manual is explicitly dropped — rows are populated by ordinary placement, not a
    // fixed ratio.
    //
    // "Attacker"/"Defender" here just means participants[0]/participants[1] from
    // BattleContactPopupUI — whichever army initiated contact sits in the Attacker rows,
    // regardless of who ends up actually attacking first (that's decided by initiative, not by
    // which side is Attacker/Defender here).
    public class BattleGrid
    {
        public const int Columns = 5;
        public const int Rows = 5;

        public const int DefenderBackRow = 0;
        public const int DefenderFrontRow = 1;
        public const int NeutralRow = 2;
        public const int AttackerFrontRow = 3;
        public const int AttackerBackRow = 4;

        // The one column of each Back row reserved for that army's hero (see the manual) — a
        // hero placed here doesn't occupy a regular combat cell alongside it.
        public const int HeroColumn = 0;

        private readonly UnitData[,] _cells = new UnitData[Rows, Columns];

        public UnitData Get(int row, int col) => InBounds(row, col) ? _cells[row, col] : null;

        public void Set(int row, int col, UnitData unit)
        {
            if (InBounds(row, col))
                _cells[row, col] = unit;
        }

        public static bool InBounds(int row, int col) => row >= 0 && row < Rows && col >= 0 && col < Columns;

        // Swaps whatever occupies the two cells (either may be empty) — used by the Arrangement
        // phase's drag-and-drop (see BattleScreenUI.TryDropOnCell) and by replaying a saved
        // ArmyData.SavedArrangement layout. Doesn't validate ownership/side — callers are
        // expected to only ever swap within one side's own two rows.
        public void Swap(int rowA, int colA, int rowB, int colB)
        {
            if (!InBounds(rowA, colA) || !InBounds(rowB, colB))
                return;
            (_cells[rowA, colA], _cells[rowB, colB]) = (_cells[rowB, colB], _cells[rowA, colA]);
        }

        public static bool IsHeroSlot(int row, int col) => (row == DefenderBackRow || row == AttackerBackRow) && col == HeroColumn;

        public static bool IsAttackerSideRow(int row) => row == AttackerFrontRow || row == AttackerBackRow;
        public static bool IsDefenderSideRow(int row) => row == DefenderFrontRow || row == DefenderBackRow;

        // Up/down/left/right only, no diagonals — the Round phase's own movement step (see
        // BattleScreenUI.OnCellClicked/BattleAi.ChooseAction), distinct from IsInRange's
        // Chebyshev/square check used for attack Range.
        public static bool IsOrthogonallyAdjacent(int fromRow, int fromCol, int toRow, int toCol)
        {
            int rowDist = Mathf.Abs(fromRow - toRow);
            int colDist = Mathf.Abs(fromCol - toCol);
            return rowDist + colDist == 1;
        }

        // A square (Chebyshev-distance) radius around the attacker, per the user's own spec —
        // range 1 is the surrounding 3x3 block (all 8 neighbours, diagonals included), range 2
        // is 5x5, and so on. NOT a plus/cross shape, and NOT "how many rows deep" like the
        // manual's original row-based Range.
        public static bool IsInRange(int fromRow, int fromCol, int toRow, int toCol, int range)
        {
            int rowDist = Mathf.Abs(fromRow - toRow);
            int colDist = Mathf.Abs(fromCol - toCol);
            return Mathf.Max(rowDist, colDist) <= range;
        }

        // Finds whichever (row, col) `unit` currently occupies, if any — used to look up a
        // unit's own position for range checks/turn order without every caller having to track
        // it separately.
        public bool TryFindPosition(UnitData unit, out int row, out int col)
        {
            if (unit != null)
                for (int r = 0; r < Rows; r++)
                    for (int c = 0; c < Columns; c++)
                        if (_cells[r, c] == unit)
                        {
                            row = r; col = c;
                            return true;
                        }
            row = col = -1;
            return false;
        }

        // Every occupied cell across the whole grid, both sides — used to build the turn order
        // (see BattleTurnOrder) and to enumerate everyone still on the field.
        public IEnumerable<UnitData> AllUnits()
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Columns; c++)
                    if (_cells[r, c] != null)
                        yield return _cells[r, c];
        }

        // Places `attacker`'s and `defender`'s members into their respective rows. Each member
        // already present in that army's own ArmyData.SavedArrangement (see the Arrangement
        // phase in BattleScreenUI) goes straight to its remembered cell; everyone else (a brand
        // new army, or a member added since the layout was last saved) falls back to the plain
        // default: the hero (if any — always the front of ArmyData.Members, see
        // AddMemberSorted) into the Back row's reserved slot, every other member filling the
        // Front row left-to-right and then overflowing into the remaining Back row columns
        // (1..4) if there are more than 5.
        public static BattleGrid FromArmies(ArmyData attacker, ArmyData defender)
        {
            var grid = new BattleGrid();
            // Every hidden member of either side has already dropped stealth by the time a
            // battle starts (project owner's own call — see BattleScreenUI.Show / the
            // contact path in HexSelectionController.Movement.cs), so the full roster is
            // placed here exactly as for any ordinary army.
            PlaceArmy(grid, attacker, AttackerFrontRow, AttackerBackRow);
            PlaceArmy(grid, defender, DefenderFrontRow, DefenderBackRow);
            return grid;
        }

        private static void PlaceArmy(BattleGrid grid, ArmyData army, int frontRow, int backRow)
        {
            if (army == null)
                return;

            var unplaced = new List<UnitData>();
            foreach (UnitData member in army.Members)
            {
                if (army.SavedArrangement.TryGetValue(member, out var slot)
                    && (slot.row == frontRow || slot.row == backRow)
                    && grid.Get(slot.row, slot.col) == null)
                    grid.Set(slot.row, slot.col, member);
                else
                    unplaced.Add(member);
            }

            int frontCol = 0;
            int backCol = 1; // column 0 of the back row is the hero slot by default
            foreach (UnitData member in unplaced)
            {
                if (member.IsHero && grid.Get(backRow, HeroColumn) == null)
                {
                    grid.Set(backRow, HeroColumn, member);
                    continue;
                }
                while (frontCol < Columns && grid.Get(frontRow, frontCol) != null)
                    frontCol++;
                while (backCol < Columns && grid.Get(backRow, backCol) != null)
                    backCol++;
                if (frontCol < Columns)
                    grid.Set(frontRow, frontCol++, member);
                else if (backCol < Columns)
                    grid.Set(backRow, backCol++, member);
                // Beyond 5 front + 4 back slots there's nowhere left on this grid — not reachable
                // today (ArmyData.Capacity caps well under 9), so no overflow handling.
            }
        }
    }
}
