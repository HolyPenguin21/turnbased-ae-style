using System.Collections.Generic;
using Game.Cards;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    // One resolved Challenge (see ChallengeResolver) — mirrors Game.Turns.DiceRollResult's
    // bool-array-of-hits shape so a future battle UI can reuse the same per-die "1"/"X" display
    // convention already established for turn-order dice (see DiceRowUI).
    public class ChallengeResult
    {
        public readonly bool[] AttackerDice;
        public readonly bool[] DefenderDice;

        public ChallengeResult(bool[] attackerDice, bool[] defenderDice)
        {
            AttackerDice = attackerDice;
            DefenderDice = defenderDice;
        }

        public int AttackerSuccesses => CountHits(AttackerDice);
        public int DefenderSuccesses => CountHits(DefenderDice);

        // The manual: "the number of success rolls for the defender is subtracted from the
        // number of success rolls for the attacker and the damage if any is the difference
        // between them. A zero or negative result means no damage is done."
        public int Damage => System.Math.Max(0, AttackerSuccesses - DefenderSuccesses);

        // Attacker/defender ability modifiers on top of a raw roll — CriticalDamage (x2) then
        // Hyperkinetic (+flat vs Armored) then Pyrokinetic (+flat vs Bio) then CeramicArmor
        // (-flat), same order the manual's "multiply then subtract flat armor" stat stack always
        // applies in. Each step is gated on damage already being > 0 — a miss has nothing for
        // either side's ability to modify, and neither bonus-damage ability turns a miss into a
        // hit on its own.
        // Shared by BattleAttackPopupUI.ResolveDamage (the actual hit), BattleTargetSelector.
        // TryScoreTarget (the AI's target-pick prediction of that same hit) and FateDuelAi (the
        // AI's Fate-spend prediction) so none of them can ever disagree — see the bug this fixed:
        // Fate-spend logic used to compare fateAvailable against this raw Damage value directly,
        // so a defender's own CeramicArmor (which would've reduced a real hit to 0 for free) or an
        // attacker's Hyperkinetic (which could push a real hit's true size past what the raw dice
        // alone showed) never factored into whether spending Fate was actually worth it.
        public static int ApplyAbilityModifiers(int rawDamage, UnitData attacker, UnitData defender, AbilityMagnitudes magnitudes)
            => ApplyAbilityModifiers(rawDamage, attacker, defender, magnitudes, out _);

        // Same modifier chain, plus which of the four actually fired — so a result screen can
        // tell "hit for less than expected because of an ability" apart from "hit for the plain
        // rolled amount" (see BattleAttackPopupUI.ShowResult's "Affected by Skill" line).
        public static int ApplyAbilityModifiers(int rawDamage, UnitData attacker, UnitData defender,
            AbilityMagnitudes magnitudes, out List<string> appliedAbilities)
        {
            appliedAbilities = new List<string>();
            int damage = rawDamage;
            if (damage > 0 && attacker.HasAbility(UnitAbilities.CriticalDamage))
            {
                damage = Mathf.RoundToInt(damage * magnitudes.CriticalDamageMultiplier);
                appliedAbilities.Add(UnitAbilities.CriticalDamage);
            }
            if (damage > 0 && attacker.HasAbility(UnitAbilities.Hyperkinetic) && defender.TypeTags.Contains(UnitTypeTag.Armored))
            {
                damage += magnitudes.HyperkineticBonusDamage;
                appliedAbilities.Add(UnitAbilities.Hyperkinetic);
            }
            if (damage > 0 && attacker.HasAbility(UnitAbilities.Pyrokinetic) && defender.TypeTags.Contains(UnitTypeTag.Bio))
            {
                damage += magnitudes.PyrokineticBonusDamage;
                appliedAbilities.Add(UnitAbilities.Pyrokinetic);
            }
            if (damage > 0 && defender.HasAbility(UnitAbilities.CeramicArmor))
            {
                damage = Mathf.Max(0, damage - magnitudes.CeramicArmorReduction);
                appliedAbilities.Add(UnitAbilities.CeramicArmor);
            }
            return damage;
        }

        private static int CountHits(bool[] dice)
        {
            int hits = 0;
            foreach (bool hit in dice)
                if (hit) hits++;
            return hits;
        }
    }
}
