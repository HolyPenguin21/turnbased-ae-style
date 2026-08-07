using System.Collections.Generic;
using System.Linq;
using Game.Units;

namespace Game.Combat
{
    // Computes the order units act in during one round of a Tactical Battle Module — highest
    // effective initiative first (see the manual's own "highest initiative score acts first",
    // simplified to a per-unit stat sum rather than an army-level one — see
    // project_combat_design_decisions memory). A non-hero unit's effective initiative is its own
    // UnitData.Initiative plus its OWN side's hero's Initiative as a flat bonus (only that side's
    // hero — the enemy hero's Initiative never applies).
    //
    // Heroes never appear in the returned acting order — per the user's own confirmed rule,
    // hero cards cannot attack, only be positioned (see BattleScreenUI's Arrangement phase). A
    // hero still contributes its own Initiative as its side's bonus; it just never gets a turn
    // of its own to Pass through. A hero is no longer pinned to the reserved back-row column
    // during Arrangement (draggable anywhere within its own side, see BattleGrid's own comment),
    // so its position is found by scanning both of that side's rows rather than assumed fixed.
    public static class BattleTurnOrder
    {
        public static List<UnitData> BuildOrder(BattleGrid grid)
        {
            UnitData attackerHero = FindHero(grid, attackerSide: true);
            UnitData defenderHero = FindHero(grid, attackerSide: false);

            var order = new List<UnitData>(grid.AllUnits().Where(u => !u.IsHero));
            order.Sort((a, b) => EffectiveInitiative(grid, b, attackerHero, defenderHero)
                .CompareTo(EffectiveInitiative(grid, a, attackerHero, defenderHero)));
            return order;
        }

        // One side's roster for the Round-start popup: its hero (if any, shown as its own
        // "bonus" line rather than mixed into the acting list) plus every acting member sorted
        // by descending effective initiative — same ordering BuildOrder itself uses, just split
        // per side instead of merged across both.
        public static (UnitData hero, List<(UnitData unit, int initiative)> acting) BuildSideSummary(BattleGrid grid, bool attackerSide)
        {
            UnitData attackerHero = FindHero(grid, attackerSide: true);
            UnitData defenderHero = FindHero(grid, attackerSide: false);
            UnitData hero = attackerSide ? attackerHero : defenderHero;

            var acting = new List<(UnitData, int)>();
            foreach (UnitData unit in AllOnSide(grid, attackerSide))
            {
                if (unit.IsHero)
                    continue;
                acting.Add((unit, EffectiveInitiative(grid, unit, attackerHero, defenderHero)));
            }
            acting.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return (hero, acting);
        }

        // Public — reused by BattleScreenUI to look up each side's hero for the attack popup's
        // Fate display (see BattleAttackPopupUI.Begin).
        public static UnitData FindHero(BattleGrid grid, bool attackerSide)
        {
            foreach (UnitData unit in AllOnSide(grid, attackerSide))
                if (unit.IsHero)
                    return unit;
            return null;
        }

        private static IEnumerable<UnitData> AllOnSide(BattleGrid grid, bool attackerSide)
        {
            int frontRow = attackerSide ? BattleGrid.AttackerFrontRow : BattleGrid.DefenderFrontRow;
            int backRow = attackerSide ? BattleGrid.AttackerBackRow : BattleGrid.DefenderBackRow;
            for (int col = 0; col < BattleGrid.Columns; col++)
            {
                UnitData front = grid.Get(frontRow, col);
                if (front != null)
                    yield return front;
                UnitData back = grid.Get(backRow, col);
                if (back != null)
                    yield return back;
            }
        }

        private static int EffectiveInitiative(BattleGrid grid, UnitData unit, UnitData attackerHero, UnitData defenderHero)
        {
            if (unit.IsHero)
                return unit.Initiative;

            bool isAttackerSide = grid.TryFindPosition(unit, out int row, out _)
                && (row == BattleGrid.AttackerFrontRow || row == BattleGrid.AttackerBackRow);
            UnitData ownHero = isAttackerSide ? attackerHero : defenderHero;
            int bonus = ownHero != null ? ownHero.Initiative : 0;
            return unit.Initiative + bonus;
        }
    }
}
