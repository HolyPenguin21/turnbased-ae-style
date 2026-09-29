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

        // Same canonical modifier chain for immutable combat profiles. WorthIt and live combat
        // must never maintain separate skill math; the UnitData overload below delegates here.
        public static int ApplyAbilityModifiers(int rawDamage, IEnumerable<string> attackerAbilities,
            IReadOnlyCollection<UnitTypeTag> defenderTypeTags, IEnumerable<string> defenderAbilities,
            AbilityMagnitudes magnitudes)
            => ApplyAbilityModifiersCore(rawDamage, attackerAbilities, defenderTypeTags, defenderAbilities,
                magnitudes, null);

        public static int ApplyAbilityModifiers(int rawDamage, UnitData attacker, UnitData defender,
            AbilityMagnitudes magnitudes, out List<string> appliedAbilities)
            => ApplyAbilityModifiers(rawDamage, attacker?.Abilities, defender?.TypeTags,
                defender?.Abilities, magnitudes, out appliedAbilities);

        public static int ApplyAbilityModifiers(int rawDamage, IEnumerable<string> attackerAbilities,
            IReadOnlyCollection<UnitTypeTag> defenderTypeTags, IEnumerable<string> defenderAbilities,
            AbilityMagnitudes magnitudes, out List<string> appliedAbilities)
        {
            appliedAbilities = new List<string>();
            return ApplyAbilityModifiersCore(rawDamage, attackerAbilities, defenderTypeTags,
                defenderAbilities, magnitudes, appliedAbilities);
        }

        // `appliedAbilities` null: the caller only wants the number (Monte Carlo, Fate-spend
        // prediction) — no list is allocated on that hot path.
        private static int ApplyAbilityModifiersCore(int rawDamage, IEnumerable<string> attackerAbilities,
            IReadOnlyCollection<UnitTypeTag> defenderTypeTags, IEnumerable<string> defenderAbilities,
            AbilityMagnitudes magnitudes, List<string> appliedAbilities)
        {
            int damage = rawDamage;
            if (damage > 0 && HasAbility(attackerAbilities, UnitAbilities.CriticalDamage))
            {
                damage = Mathf.RoundToInt(damage * magnitudes.CriticalDamageMultiplier);
                appliedAbilities?.Add(UnitAbilities.CriticalDamage);
            }
            if (damage > 0 && HasAbility(attackerAbilities, UnitAbilities.Hyperkinetic)
                && HasTag(defenderTypeTags, UnitTypeTag.Armored))
            {
                damage += magnitudes.HyperkineticBonusDamage;
                appliedAbilities?.Add(UnitAbilities.Hyperkinetic);
            }
            if (damage > 0 && HasAbility(attackerAbilities, UnitAbilities.Pyrokinetic)
                && HasTag(defenderTypeTags, UnitTypeTag.Bio))
            {
                damage += magnitudes.PyrokineticBonusDamage;
                appliedAbilities?.Add(UnitAbilities.Pyrokinetic);
            }
            if (damage > 0 && HasAbility(defenderAbilities, UnitAbilities.CeramicArmor))
            {
                damage = Mathf.Max(0, damage - magnitudes.CeramicArmorReduction);
                appliedAbilities?.Add(UnitAbilities.CeramicArmor);
            }
            return damage;
        }

        // One authoritative Berserk-on-hit mutation for primary Ground Combat hits and
        // Splash/Scorcher side-hits. Call only when a real positive-damage hit landed.
        // Attack always grows; Defense loss is floored at 1 and only the actually-lost amount
        // is recorded so BattleScreenUI can restore the exact pre-battle value at battle end.
        public static bool ApplyBerserkOnHit(UnitData victim, AbilityMagnitudes magnitudes)
        {
            if (victim == null || !victim.HasAbility(UnitAbilities.Berserk))
                return false;

            int oldDefense = victim.Defense;
            int attack = victim.Attack;
            int defense = victim.Defense;
            BattleSimulationKernel.ApplyBerserkIfHit(
                true, victim.Abilities, ref attack, ref defense, magnitudes);

            victim.Attack = attack;
            victim.Defense = defense;
            victim.BerserkDefenseLost += Mathf.Max(0, oldDefense - defense);
            victim.BerserkStacks++;
            return true;
        }

        private static bool HasAbility(IEnumerable<string> abilities, string wanted)
        {
            if (abilities == null)
                return false;
            // Indexed walk for the usual list shapes: foreach through the interface would box the
            // enumerator (an allocation per check on the Monte Carlo hot path).
            if (abilities is IReadOnlyList<string> list)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i] == wanted)
                        return true;
                return false;
            }
            foreach (string ability in abilities)
                if (ability == wanted)
                    return true;
            return false;
        }

        private static bool HasTag(IReadOnlyCollection<UnitTypeTag> tags, UnitTypeTag wanted)
        {
            if (tags == null)
                return false;
            if (tags is IReadOnlyList<UnitTypeTag> list)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i] == wanted)
                        return true;
                return false;
            }
            foreach (UnitTypeTag tag in tags)
                if (tag == wanted)
                    return true;
            return false;
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
