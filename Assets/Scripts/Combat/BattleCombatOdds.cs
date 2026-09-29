using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>
    /// Exact pre-Fate odds for one Ground Combat exchange. This is deliberately deterministic:
    /// live combat still rolls actual dice, while target selection needs an expectation that obeys
    /// the same success-count subtraction and ability modifier chain instead of Attack*0.5-Defense*0.5.
    /// </summary>
    public static class BattleCombatOdds
    {
        public readonly struct ExchangeOdds
        {
            public readonly float ExpectedDamage;
            public readonly float HitProbability;
            public readonly float KillProbability;

            public ExchangeOdds(float expectedDamage, float hitProbability, float killProbability)
            {
                ExpectedDamage = expectedDamage;
                HitProbability = hitProbability;
                KillProbability = killProbability;
            }
        }

        public static ExchangeOdds Evaluate(UnitData attacker, UnitData defender, int defenderBonusDice,
            AbilityMagnitudes magnitudes, float defenderHp, int? attackDiceOverride = null,
            int? defenderDefenseOverride = null)
        {
            if (attacker == null || defender == null)
                return default;

            int attackDice = Mathf.Max(0, attackDiceOverride ?? attacker.Attack);
            int defenseDice = Mathf.Max(0, (defenderDefenseOverride ?? defender.Defense) + defenderBonusDice);
            float expected = 0f;
            float hitProbability = 0f;
            float killProbability = 0f;

            for (int a = 0; a <= attackDice; a++)
            {
                float pa = BinomialHalfProbability(attackDice, a);
                if (pa <= 0f)
                    continue;
                for (int d = 0; d <= defenseDice; d++)
                {
                    float probability = pa * BinomialHalfProbability(defenseDice, d);
                    if (probability <= 0f)
                        continue;

                    int raw = Mathf.Max(0, a - d);
                    int damage = ChallengeResult.ApplyAbilityModifiers(raw, attacker, defender, magnitudes);
                    expected += probability * damage;
                    if (damage > 0)
                        hitProbability += probability;
                    if (defenderHp > 0f && damage >= defenderHp)
                        killProbability += probability;
                }
            }

            return new ExchangeOdds(expected, hitProbability, killProbability);
        }

        private static float BinomialHalfProbability(int n, int k)
        {
            if (k < 0 || k > n)
                return 0f;
            if (n == 0)
                return k == 0 ? 1f : 0f;

            double combinations = 1d;
            int r = System.Math.Min(k, n - k);
            for (int i = 1; i <= r; i++)
                combinations = combinations * (n - r + i) / i;
            return (float)(combinations / System.Math.Pow(2d, n));
        }
    }
}
