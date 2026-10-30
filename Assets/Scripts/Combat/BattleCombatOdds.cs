using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// Exact pre-Fate odds for one Ground Combat exchange. This is deliberately deterministic:
    /// live combat still rolls actual tokens, while target selection needs an expectation that obeys
    /// the same success-count subtraction and ability modifier chain instead of Attack*0.5-Defense*0.5.
    /// </summary>
    public static class BattleCombatOdds
    {
        public readonly struct ExchangeOdds
        {
            public readonly float ExpectedDamage;
            public readonly float HitProbability;
            public readonly float KillProbability;
            public readonly float RawHitProbability;
            public float PositiveDamageProbability => HitProbability;

            public ExchangeOdds(float expectedDamage, float hitProbability, float killProbability, float rawHitProbability = 0f)
            {
                ExpectedDamage = expectedDamage;
                HitProbability = hitProbability;
                KillProbability = killProbability;
                RawHitProbability = rawHitProbability;
            }
        }

        // interceptor: the UnitAbilities.Bodyguard that takes any landed hit on `defender` — the
        // tokens stay attacker vs defender, a hit's damage is re-run against the interceptor, and
        // `defenderHp` must then be the interceptor's HP (it is the one that can die).
        public static ExchangeOdds Evaluate(UnitData attacker, UnitData defender, int defenderBonusTokens,
            AbilityMagnitudes magnitudes, float defenderHp, int? attackTokensOverride = null,
            int? defenderDefenseOverride = null, UnitData interceptor = null)
        {
            if (attacker == null || defender == null)
                return default;

            int attackTokens = Mathf.Max(0, attackTokensOverride ?? attacker.Attack);
            int baseDefense = defenderDefenseOverride
                ?? (defender.IsHero ? Mathf.Max(0, defender.FateMax) : defender.Defense);
            int defenseTokens = Mathf.Max(0, baseDefense + defenderBonusTokens);
            float expected = 0f;
            float hitProbability = 0f;
            float killProbability = 0f;
            float rawHitProbability = 0f;

            for (int a = 0; a <= attackTokens; a++)
            {
                float pa = BattleSimulationKernel.BinomialHalfProbability(attackTokens, a);
                if (pa <= 0f)
                    continue;
                for (int d = 0; d <= defenseTokens; d++)
                {
                    float probability = pa * BattleSimulationKernel.BinomialHalfProbability(defenseTokens, d);
                    if (probability <= 0f)
                        continue;

                    int raw = Mathf.Max(0, a - d);
                    if (raw > 0) rawHitProbability += probability;
                    int damage = ChallengeResult.ApplyAbilityModifiers(raw, attacker, defender, magnitudes);
                    if (interceptor != null && damage > 0)
                        damage = BattleSimulationKernel.InterceptedDamage(raw, attacker.Abilities,
                            interceptor.TypeTags, interceptor.Abilities, magnitudes);
                    expected += probability * damage;
                    if (damage > 0)
                        hitProbability += probability;
                    if (defenderHp > 0f && damage >= defenderHp)
                        killProbability += probability;
                }
            }

            return new ExchangeOdds(expected, hitProbability, killProbability, rawHitProbability);
        }

    }
}
