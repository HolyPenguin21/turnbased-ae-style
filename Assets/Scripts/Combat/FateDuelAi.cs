using UnityEngine;
using Game.Cards;
using Game.Units;

namespace Game.Combat
{
    // AI decision logic for whether to spend Fate during a duel turn (BattleAttackPopupUI.
    // RunAiTurn) — split out of BattleAi.cs to keep that file to arrangement/retreat/tactical-move
    // logic only.
    //
    // ---- Priority-cap model (per the user's own spec) ----
    // Both sides always want ONE outcome: attacker wants damage > 0, defender wants damage <= 0.
    // Ability combos never change WHAT is being pursued, only how many of the side's own CURRENT
    // missed dice it's willing to spend Fate rerolling toward it — `cap` = 2 when the attacker
    // carries ShockAttack, or a type-matched Hyperkinetic-vs-Armored/Pyrokinetic-vs-Bio combo (see
    // AttackerCap/DefenderCap — both read the ATTACKER's own abilities, since it's the attacker's
    // combo that makes an eventual landed hit worth chasing harder for the attacker and blocking
    // harder for the defender), else 1 (base — don't blow the whole Fate pool on one attack).
    // `minRerolls` = fewest of the side's own current misses that need to flip to a hit for the
    // goal to be reached, found by literally trying k = 1, 2, ... against the SAME
    // ChallengeResult.ApplyAbilityModifiers the real roll resolves through (so this can never
    // disagree with what rerolling actually produces) — not just "how many misses exist". If no k
    // within the misses actually on the table gets there, the goal is physically unreachable and
    // spending is never worth it, regardless of cap.
    //
    // The companion "stop trying after any single failed reroll" rule (per the user's own spec)
    // lives in BattleAttackPopupUI.RunAiTurn instead of here — it's a turn-loop control decision
    // (react to what a reroll's OWN result was), not a fresh "should I spend" evaluation.
    public static class FateDuelAi
    {
        // isDefender: true when evaluating the DEFENDER's own spend, false for the ATTACKER's.
        // isRetreating/defendingUnitHp (defender only): a retreating army can have several of its
        // own units attacked in sequence within the same grace round before it actually leaves —
        // while retreating, only spend on a hit that would genuinely kill the defender right now;
        // Fate doesn't replenish again until this battle ends (see UnitData.
        // ReplenishFateForNewBattle), so spending it on a survivable hit here could starve a later,
        // actually-lethal one against a different unit.
        // isCaptureKill: the target hero's own Fate spend during a Capture Kill Challenge (see
        // BattleAttackPopupUI.ResolveCaptureKill) — a TIED roll resolves as Killed there, not the
        // safe non-event a 0-damage tie is in Ground Combat, so the defending hero must keep
        // rerolling on a tie too. That challenge deals no HP damage, so none of the ability-cap
        // logic below applies to it — same branch as before this refactor, unchanged.
        public static bool ShouldSpendFate(bool[] attackerDice, bool[] defenderDice, int fateAvailable, bool isDefender,
            UnitData attacker, UnitData defender, AbilityMagnitudes magnitudes,
            bool isRetreating = false, int defendingUnitHp = int.MaxValue, bool isCaptureKill = false)
        {
            if (fateAvailable <= 0)
                return false;
            bool[] ownDice = isDefender ? defenderDice : attackerDice;
            if (ownDice == null || !HasMiss(ownDice))
                return false;

            var result = new ChallengeResult(attackerDice, defenderDice);

            if (isCaptureKill)
                return isDefender
                    ? result.AttackerSuccesses >= result.DefenderSuccesses
                    : result.Damage <= 0;

            int damage = ChallengeResult.ApplyAbilityModifiers(result.Damage, attacker, defender, magnitudes);

            return isDefender
                ? ShouldDefenderSpend(result, attacker, defender, magnitudes, damage, isRetreating, defendingUnitHp)
                : ShouldAttackerSpend(result, attacker, defender, magnitudes, damage);
        }

        private static bool ShouldAttackerSpend(ChallengeResult result, UnitData attacker, UnitData defender,
            AbilityMagnitudes magnitudes, int currentDamage)
        {
            if (currentDamage > 0)
                return false; // already lands, nothing to fix

            int ownMisses = CountMisses(result.AttackerDice);
            if (!TryMinRerollsForAttackerDamage(result, attacker, defender, magnitudes, ownMisses, out int minRerolls))
                return false; // physically unreachable within the misses actually on the table
            return minRerolls <= AttackerCap(attacker, defender);
        }

        private static bool ShouldDefenderSpend(ChallengeResult result, UnitData attacker, UnitData defender,
            AbilityMagnitudes magnitudes, int currentDamage, bool isRetreating, int defendingUnitHp)
        {
            if (currentDamage <= 0)
                return false; // nothing to defend against right now

            // Hopeless check (unchanged from the previous ShouldSpendFate) — even flipping EVERY
            // remaining defender miss to a hit still doesn't get damage under HP; spending here is
            // pure waste of a resource that won't replenish until this battle ends.
            int bestCaseDefenderSuccesses = result.DefenderDice.Length;
            int bestCaseRawDamage = Mathf.Max(0, result.AttackerSuccesses - bestCaseDefenderSuccesses);
            int bestCaseDamage = ChallengeResult.ApplyAbilityModifiers(bestCaseRawDamage, attacker, defender, magnitudes);
            if (bestCaseDamage >= defendingUnitHp)
                return false;

            // A hit that would kill the defender outright right now always justifies spending
            // Fate to survive it, however many misses that takes — bypassing the priority cap
            // below, which exists only to stop the AI blowing its Fate pool chasing ordinary chip
            // damage, not to let a savable unit die while holding Fate that could have saved it.
            // Reachability is already guaranteed by the hopeless check just above: not hopeless
            // means rerolling every remaining miss gets damage under defendingUnitHp. Per the
            // user's own report: HP 2 / Defense 2 against a 3-hit roll (0 defender successes)
            // can never reach a full block (only 2 dice to reroll, so raw damage bottoms out at
            // 1, never 0) — the old "reroll until damage <= 0" goal judged that unreachable and
            // let the AI sit on 2 unspent Fate while its own unit died to a roll it could have
            // survived.
            bool lethal = currentDamage >= defendingUnitHp;
            if (lethal)
                return true;

            // Retreat-conservation: while retreating and NOT facing a lethal hit (the branch
            // above already handled lethal), bank Fate for a later hit against a different unit
            // in this same grace round instead of spending on a hit that's merely survivable.
            if (isRetreating)
                return false;

            int ownMisses = CountMisses(result.DefenderDice);
            if (!TryMinRerollsForDefenderSafety(result, attacker, defender, magnitudes, ownMisses, out int minRerolls))
                return false;
            return minRerolls <= DefenderCap(attacker, defender);
        }

        // cap = 2 whenever the ATTACKER carries a combo that makes an eventual landed hit worth
        // more than a plain one — ShockAttack (knocks the target out of the rest of this round on
        // a hit) or a type-matched Hyperkinetic/Pyrokinetic bonus (the hit itself deals more once
        // it lands). Same underlying fact drives both sides' willingness to keep pushing/blocking
        // it, hence AttackerCap/DefenderCap share one implementation.
        private static int AttackerCap(UnitData attacker, UnitData defender)
        {
            bool boosted = attacker.HasAbility(UnitAbilities.ShockAttack)
                || (attacker.HasAbility(UnitAbilities.Hyperkinetic) && defender.TypeTags.Contains(UnitTypeTag.Armored))
                || (attacker.HasAbility(UnitAbilities.Pyrokinetic) && defender.TypeTags.Contains(UnitTypeTag.Bio));
            return boosted ? 2 : 1;
        }

        private static int DefenderCap(UnitData attacker, UnitData defender) => AttackerCap(attacker, defender);

        private static bool TryMinRerollsForAttackerDamage(ChallengeResult result, UnitData attacker, UnitData defender,
            AbilityMagnitudes magnitudes, int ownMisses, out int minRerolls)
        {
            for (int k = 1; k <= ownMisses; k++)
            {
                int rawDamage = Mathf.Max(0, (result.AttackerSuccesses + k) - result.DefenderSuccesses);
                int damage = ChallengeResult.ApplyAbilityModifiers(rawDamage, attacker, defender, magnitudes);
                if (damage > 0)
                {
                    minRerolls = k;
                    return true;
                }
            }
            minRerolls = 0;
            return false;
        }

        private static bool TryMinRerollsForDefenderSafety(ChallengeResult result, UnitData attacker, UnitData defender,
            AbilityMagnitudes magnitudes, int ownMisses, out int minRerolls)
        {
            for (int k = 1; k <= ownMisses; k++)
            {
                int rawDamage = Mathf.Max(0, result.AttackerSuccesses - (result.DefenderSuccesses + k));
                int damage = ChallengeResult.ApplyAbilityModifiers(rawDamage, attacker, defender, magnitudes);
                if (damage <= 0)
                {
                    minRerolls = k;
                    return true;
                }
            }
            minRerolls = 0;
            return false;
        }

        private static int CountMisses(bool[] dice)
        {
            int misses = 0;
            foreach (bool hit in dice)
                if (!hit) misses++;
            return misses;
        }

        private static bool HasMiss(bool[] dice)
        {
            if (dice == null)
                return false;
            foreach (bool hit in dice)
                if (!hit)
                    return true;
            return false;
        }
    }
}
