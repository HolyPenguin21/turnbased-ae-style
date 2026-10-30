using System.Collections.Generic;
using System.Linq;
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    // Coverage gate: exact token odds that an attacker wounds a defender (CanDamage / CanDamageAll).
    public static partial class WorthIt
    {
        // 2026-10-19 (project owner's decision) — "can damage" is the share of one exchange's tokens
        // outcomes that wound the defender (HitProbability), at least CoverageMinHitProbability. It
        // replaced the rounded mean `attack*0.5 - defense*0.5`, which live combat had already left
        // (BattleCombatOdds): a Medium Tank (5) against Null Vessel (3) on a Citadel (+2) read as
        // "cannot scratch", the threat model priced the Citadel's PotentialDamage at 0 while the
        // same estimate gave the attack a win of 1.00, and the tank column took it next turn.
        //
        // `extraDefense` — the hex's own terrain/Base-building bonus (HexDefenseBonus), added
        // 2026-08-20 (project owner's own report). DefenderProfile.Defense is always the
        // defender's own raw stat as memorized, never including where the fight would actually
        // happen — every OTHER read in this file that compares against a real hex (Score's own enemyDefense
        // parameter) already folds this in; this coverage check used to
        // be the one exception, which could read a defender as damageable off its raw stat alone
        // while the hex bonus on top would actually make it un-killable. Defaults to 0f so a
        // caller with no hex to check against (a pure stat comparison) is unaffected.
        //
        // Public since 2026-08-23 (project owner's own report) — AiManagementPlanner's own
        // UnitCompositionFitBonus needs this exact per-unit check to ask a hypothetical "would
        // THIS candidate card cover a defender none of a StillAssembling raid's current roster
        // can already damage", the same single-source-of-truth rule every other coverage read in
        // this codebase already follows (see CanDamageAll's own comment) — no second copy of this
        // formula anywhere else.
        public static bool CanDamage(float attack, DefenderProfile defender, float extraDefense = 0f,
            AbilityMagnitudes? magnitudesOverride = null) =>
            CanDamage(new DefenderProfile(0f, false, attack: attack, hitPoints: 1f), defender,
                extraDefense, magnitudesOverride);

        // A defender wounded in fewer than this share of exchanges is a brick the roster cannot
        // wear down within a battle (the coverage gate's meaning). Per exchange, pre-Fate:
        // 5 tokens vs 5 = 0.38, 4 vs 6 = 0.17, 3 vs 6 = 0.09, 3 vs 9 = 0.02.
        public const float CoverageMinHitProbability = 0.10f;

        // Composition-aware penetration check for immutable rosters.
        public static bool CanDamage(DefenderProfile attacker, DefenderProfile defender,
            float extraDefense = 0f, AbilityMagnitudes? magnitudesOverride = null) =>
            HitProbability(attacker, defender, extraDefense, magnitudesOverride)
                >= CoverageMinHitProbability;

        // The chance one exchange wounds `defender`: attack tokens vs defence tokens (+ hex bonus),
        // 50% per die, success-count subtraction, then the canonical ability modifier chain — the
        // same exact pre-Fate distribution BattleCombatOdds gives live target selection.
        public static float HitProbability(DefenderProfile attacker, DefenderProfile defender,
            float extraDefense = 0f, AbilityMagnitudes? magnitudesOverride = null)
        {
            AbilityMagnitudes magnitudes = magnitudesOverride ?? AbilityMagnitudes.Default;
            int attackTokens = Mathf.Max(0, Mathf.RoundToInt(
                ExplosionAttack.AttackPower(attacker.Attack, attacker.Abilities)));
            int defenseTokens = Mathf.Max(0, Mathf.RoundToInt(defender.Defense + extraDefense));
            double[] pa = BinomialHalf(attackTokens), pd = BinomialHalf(defenseTokens);
            double hit = 0d, positive = 0d;
            for (int raw = 1; raw <= attackTokens; raw++)
            {
                double p = 0d;
                for (int d = 0; d <= defenseTokens && d + raw <= attackTokens; d++)
                    p += pd[d] * pa[d + raw];
                positive += p;
                if (ChallengeResult.ApplyAbilityModifiers(raw, attacker.Abilities,
                        defender.TypeTags, defender.Abilities, magnitudes) > 0)
                    hit += p;
            }
            // Every other outcome is a raw 0; a modifier may still turn it into a wound.
            if (ChallengeResult.ApplyAbilityModifiers(0, attacker.Abilities,
                    defender.TypeTags, defender.Abilities, magnitudes) > 0)
                hit += 1d - positive;
            return (float)hit;
        }

        private static readonly Dictionary<int, double[]> BinomialHalfCache = new Dictionary<int, double[]>();

        // P(k successes of n tokens), each die 50%.
        private static double[] BinomialHalf(int n)
        {
            if (BinomialHalfCache.TryGetValue(n, out double[] cached))
                return cached;
            var p = new double[n + 1];
            double c = 1d, scale = System.Math.Pow(0.5d, n);
            for (int k = 0; k <= n; k++)
            {
                p[k] = c * scale;
                c = c * (n - k) / (k + 1);
            }
            if (n <= 256)
                BinomialHalfCache[n] = p;
            return p;
        }

        public static bool CanDamageAll(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyCollection<DefenderProfile> defenders, float extraDefense = 0f,
            AbilityMagnitudes? magnitudes = null)
        {
            defenders = CombatantsOf(defenders);
            if (defenders.Count == 0)
                return true;
            attackerUnits = CombatantsOf(attackerUnits);
            if (attackerUnits.Count == 0)
                return false;
            foreach (DefenderProfile defender in defenders)
                if (!attackerUnits.Any(attacker => CanDamage(attacker, defender, extraDefense, magnitudes)))
                    return false;
            return true;
        }

        // Coverage gate on top of Score's overall power read: every known enemy unit needs at
        // least ONE counter somewhere in `attacker`'s own roster. A lopsided net edge doesn't mean
        // much if one specific enemy unit is a brick wall none of our units can actually dent — it
        // just tanks forever while the rest of the fight plays out around it, dragging the
        // exchange out far longer than Score's own single-number read suggests. Null/empty
        // `defenders` (no guard, or a data source that has no per-unit read at all) is vacuously
        // coverable — nothing to fail to cover.
        public static bool CanDamageAll(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyList<DefendingArmy> opposition, float fallbackDefenseBonus = 0f,
            AbilityMagnitudes? magnitudes = null, SideCommander attackerCommander = default) =>
            UncoveredDefenderCount(attackerUnits, opposition, fallbackDefenseBonus, magnitudes, attackerCommander) == 0;

        // How many known defending combatants no unit of `attackerUnits` can damage — the same
        // coverage rule as CanDamageAll, counted, so a planner can tell whether a candidate
        // reinforcement closes any of the gap (2026-10-17: a host past the power bar was fed
        // power-only supports for six turns while one defender stayed uncovered).
        public static int UncoveredDefenderCount(IReadOnlyCollection<DefenderProfile> attackerUnits,
            IReadOnlyList<DefendingArmy> opposition, float fallbackDefenseBonus = 0f,
            AbilityMagnitudes? magnitudes = null, SideCommander attackerCommander = default)
        {
            if (opposition == null || opposition.Count == 0)
                return 0;
            List<DefenderProfile> attackers = CombatantsOf(attackerUnits);
            int uncovered = 0;
            foreach (DefendingArmy army in opposition)
            {
                float extra = army.DefenseBonus(fallbackDefenseBonus, attackerUnits, attackerCommander);
                foreach (DefenderProfile defender in CombatantsOf(army.Units))
                    if (!attackers.Any(attacker => CanDamage(attacker, defender, extra, magnitudes)))
                        uncovered++;
            }
            return uncovered;
        }

        public static bool CanDamageAll(ArmyData attacker, IReadOnlyCollection<DefenderProfile> defenders,
            float extraDefense = 0f, AbilityMagnitudes? magnitudes = null) =>
            CanDamageAll(attacker?.Members, defenders, extraDefense, magnitudes);

        // Same coverage gate, against a raw unit set instead of a real ArmyData — the shared
        // building block the ArmyData overload above delegates to.
        public static bool CanDamageAll(IEnumerable<UnitData> attackerUnits, IReadOnlyCollection<DefenderProfile> defenders,
            float extraDefense = 0f, AbilityMagnitudes? magnitudes = null)
        {
            defenders = CombatantsOf(defenders);
            if (defenders.Count == 0)
                return true;
            List<UnitData> ourUnits = CombatantsOf(attackerUnits);
            if (ourUnits.Count == 0)
                return false;
            foreach (DefenderProfile defender in defenders)
                if (!ourUnits.Any(u => CanDamage(ExplosionAttack.AttackPower(u.Attack, u.Abilities), defender, extraDefense, magnitudes)))
                    return false;
            return true;
        }
    }
}
