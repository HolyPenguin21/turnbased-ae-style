using System.Collections.Generic;
using System.Linq;
using Game.Map;
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
    // of its own to Pass through. The bonus hero is the side's COMMANDER (ArmyData.Commander —
    // the army's first hero), the same hero that gives the army its capacity and battle Fate,
    // never whichever hero happens to stand first on the grid.
    public static class BattleTurnOrder
    {
        // Both armies are required (not just the grid) because a non-hero unit can advance
        // across the neutral row into the opposing side's own rows during a Round's movement
        // step to reach melee range (see BattleGrid's row-layout comment) — at that point "which
        // row group a unit sits in" no longer matches "which army it belongs to", so side
        // identity has to come from ArmyData.Members, not grid position. Same class of bug as
        // the Spend-button fix noted on BattleScreenUI.OwningArmy: this used to derive a unit's
        // side purely from its row, which silently dropped/misfiled units that had moved into
        // the enemy's rows — most visibly, the defending side's Round 2+ initiative roster
        // going blank once its melee units had crossed over.
        public static List<UnitData> BuildOrder(BattleGrid grid, ArmyData attacker, ArmyData defender)
        {
            UnitData attackerHero = attacker?.Commander;
            UnitData defenderHero = defender?.Commander;

            var order = new List<UnitData>(grid.AllUnits().Where(u => u.IsGroundCombatant));
            order.Sort((a, b) => EffectiveInitiative(b, attacker, defender, attackerHero, defenderHero)
                .CompareTo(EffectiveInitiative(a, attacker, defender, attackerHero, defenderHero)));
            return order;
        }

        // One side's roster for the Round-start popup: its hero (if any, shown as its own
        // "bonus" line rather than mixed into the acting list) plus every acting member sorted
        // by descending effective initiative — same ordering BuildOrder itself uses, just split
        // per side instead of merged across both.
        public static (UnitData hero, List<(UnitData unit, int initiative)> acting) BuildSideSummary(BattleGrid grid, ArmyData attacker, ArmyData defender, bool attackerSide)
        {
            UnitData attackerHero = attacker?.Commander;
            UnitData defenderHero = defender?.Commander;
            UnitData hero = attackerSide ? attackerHero : defenderHero;
            ArmyData side = attackerSide ? attacker : defender;

            var acting = new List<(UnitData, int)>();
            foreach (UnitData unit in AllOnSide(grid, side))
            {
                if (unit.IsHero)
                    continue;
                acting.Add((unit, EffectiveInitiative(unit, attacker, defender, attackerHero, defenderHero)));
            }
            acting.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return (hero, acting);
        }

        // Every unit still on the grid that belongs to `army`, wherever it currently sits —
        // membership-based rather than row-based so units that have crossed into the enemy's
        // rows are still counted on their own, real side.
        private static IEnumerable<UnitData> AllOnSide(BattleGrid grid, ArmyData army)
        {
            if (army == null)
                yield break;
            foreach (UnitData unit in grid.AllUnits())
                if (army.Members.Contains(unit))
                    yield return unit;
        }

        private static int EffectiveInitiative(UnitData unit, ArmyData attacker, ArmyData defender, UnitData attackerHero, UnitData defenderHero)
        {
            if (unit.IsHero)
                return unit.Initiative;

            bool isAttackerSide = attacker != null && attacker.Members.Contains(unit);
            UnitData ownHero = isAttackerSide ? attackerHero : defenderHero;
            int bonus = ownHero != null ? ownHero.Initiative : 0;
            return unit.Initiative + bonus;
        }
    }
}
