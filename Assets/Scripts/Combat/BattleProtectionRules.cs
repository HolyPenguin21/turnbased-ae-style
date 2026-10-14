using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Map;
using Game.Units;

namespace Game.Combat
{
    /// <summary>
    /// Once-per-round reactions already spent this round (UnitAbilities.Bodyguard /
    /// UnitAbilities.Counterattack). BattleEngine owns the live one and clears it at round start;
    /// a shadow-grid projection keeps its own per simulated round.
    /// </summary>
    public sealed class BattleReactionState
    {
        public readonly HashSet<UnitData> BodyguardsUsed = new HashSet<UnitData>();
        public readonly HashSet<UnitData> CountersUsed = new HashSet<UnitData>();

        public void Clear()
        {
            BodyguardsUsed.Clear();
            CountersUsed.Clear();
        }
    }

    /// <summary>
    /// Spatial defensive rules that are derived from the live tactical grid.
    /// </summary>
    public static class BattleProtectionRules
    {
        public const int GuardedDefenseBonus = 1;

        private static readonly int[] OrthogonalRows = { -1, 1, 0, 0 };
        private static readonly int[] OrthogonalCols = { 0, 0, -1, 1 };

        public static int GetTotalDefenseBonus(BattleGrid grid, UnitData unit, ArmyData battleDefender,
            int defenderHexBonus)
        {
            int hex = battleDefender != null && unit != null && battleDefender.Members.Contains(unit)
                ? defenderHexBonus : 0;
            return hex + GetGuardedDefenseBonus(grid, unit, battleDefender);
        }

        public static int GetGuardedDefenseBonus(BattleGrid grid, UnitData unit, ArmyData battleDefender)
        {
            if (grid == null || unit == null || (!unit.IsGroundCombatant && !unit.IsHero)
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

        // UnitAbilities.Bodyguard — the guard that takes a hit aimed at `victim`: a living,
        // unspent, same-owner ground combatant carrying Bodyguard on an orthogonal neighbour cell
        // (never the victim or the attacker). The most current HP wins, ties go to the first cell
        // in up/down/left/right order. `hpOf` lets a projection read its shadow HP.
        public static UnitData FindBodyguard(BattleGrid grid, UnitData victim, UnitData attacker,
            Func<UnitData, float> hpOf, BattleReactionState reactions)
        {
            if (grid == null || victim == null || hpOf == null
                || !grid.TryFindPosition(victim, out int row, out int col))
                return null;

            UnitData best = null;
            float bestHp = 0f;
            for (int i = 0; i < OrthogonalRows.Length; i++)
            {
                UnitData guard = grid.Get(row + OrthogonalRows[i], col + OrthogonalCols[i]);
                if (guard == null || guard == victim || guard == attacker || guard.Owner != victim.Owner
                    || !guard.IsGroundCombatant || guard.IsPrisoner
                    || !guard.HasAbility(UnitAbilities.Bodyguard)
                    || (reactions != null && reactions.BodyguardsUsed.Contains(guard)))
                    continue;
                float hp = hpOf(guard);
                if (hp > bestHp)
                {
                    best = guard;
                    bestHp = hp;
                }
            }
            return best;
        }

        // UnitAbilities.Counterattack — whether `victim` (after the hit, at `victimHp`) answers
        // `attacker` now. "Within 1 cell" is the same square range BattleGrid.IsInRange uses for
        // a Range-1 attack.
        public static bool CanCounterattack(BattleGrid grid, UnitData victim, UnitData attacker,
            float victimHp, int primaryDamage, BattleReactionState reactions)
        {
            if (grid == null || victim == null || attacker == null || victim.Owner == attacker.Owner
                || victim.IsPrisoner
                || !grid.TryFindPosition(victim, out int vr, out int vc)
                || !grid.TryFindPosition(attacker, out int ar, out int ac))
                return false;
            return BattleSimulationKernel.TriggersCounterattack(victim.Abilities, victim.IsHero, victimHp,
                BattleGrid.IsInRange(vr, vc, ar, ac, 1),
                reactions != null && reactions.CountersUsed.Contains(victim),
                primaryDamage, attacker.Abilities);
        }
    }
}
