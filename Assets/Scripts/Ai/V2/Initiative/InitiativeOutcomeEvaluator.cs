using System;
using System.Collections.Generic;
using Game.Turns;

namespace Game.Ai.V2.Initiative
{
    // Pure, deterministic, RNG-free N-player evaluator for the real initiative roll rules
    // (TurnOrderResolver): every player rolls a pool of independent 50/50 dice, hits are the
    // score, highest score ranks first, and a tied subgroup rerolls its whole pool — recursively
    // — until every slot is filled. Recursive rerolls among a tied block are symmetric, so a
    // tied player's final position within that block is uniform; that is the only fact this
    // evaluator needs to reproduce the rank distribution exactly.
    //
    // Supports any player count — the opponent pool is just a list of total-dice counts.
    public readonly struct InitiativeOutcome
    {
        public readonly double ProbabilityFirst;
        public readonly double ProbabilitySecond;
        public readonly double ProbabilityThirdOrLower;
        public readonly double ExpectedBaseAp;

        public InitiativeOutcome(double p1, double p2, double p3plus)
        {
            ProbabilityFirst = p1;
            ProbabilitySecond = p2;
            ProbabilityThirdOrLower = p3plus;
            ExpectedBaseAp = InitiativeRules.ApForRank(0) * p1
                           + InitiativeRules.ApForRank(1) * p2
                           + InitiativeRules.ApForRank(2) * p3plus;
        }

        // A bounded [0..1] "how early do I expect to move" scalar: 1 == certain first. Used only
        // for the secondary tempo benefit, never for AP math.
        public double EarlinessScore => ProbabilityFirst + 0.5 * ProbabilitySecond;
    }

    public static class InitiativeOutcomeEvaluator
    {
        // Binomial pmf table cache for pool sizes up to MaxTotalDice.
        private static readonly Dictionary<int, double[]> PmfByPool = new Dictionary<int, double[]>();

        private static double[] Pmf(int poolSize)
        {
            if (poolSize < 0)
                poolSize = 0;
            if (PmfByPool.TryGetValue(poolSize, out double[] cached))
                return cached;

            var pmf = new double[poolSize + 1];
            // C(n,k) / 2^n
            double denom = Math.Pow(2.0, poolSize);
            double c = 1.0;
            for (int k = 0; k <= poolSize; k++)
            {
                pmf[k] = c / denom;
                c = c * (poolSize - k) / (k + 1); // next binomial coefficient
            }
            PmfByPool[poolSize] = pmf;
            return pmf;
        }

        private static double ProbGreater(double[] pmf, int threshold)
        {
            double sum = 0;
            for (int k = threshold + 1; k < pmf.Length; k++)
                sum += pmf[k];
            return sum;
        }

        private static double ProbLess(double[] pmf, int threshold)
        {
            double sum = 0;
            for (int k = 0; k < pmf.Length && k < threshold; k++)
                sum += pmf[k];
            return sum;
        }

        private static double ProbEqual(double[] pmf, int value) =>
            value >= 0 && value < pmf.Length ? pmf[value] : 0.0;

        // myTotalDice / each opponentTotalDice are BASE + bought, already clamped to
        // [BaseDice .. MaxTotalDice] by the caller.
        public static InitiativeOutcome Evaluate(int myTotalDice, IReadOnlyList<int> opponentTotalDice)
        {
            double[] myPmf = Pmf(myTotalDice);
            int oppCount = opponentTotalDice?.Count ?? 0;

            var oppPmf = new double[oppCount][];
            for (int j = 0; j < oppCount; j++)
                oppPmf[j] = Pmf(Math.Max(0, opponentTotalDice[j]));

            double p1 = 0, p2 = 0;

            for (int s = 0; s < myPmf.Length; s++)
            {
                double pS = myPmf[s];
                if (pS <= 0)
                    continue;

                // Joint distribution of (a = opponents strictly above me, t = opponents tied
                // with me) at my score s. dp[a, t].
                var dp = new double[oppCount + 1, oppCount + 1];
                dp[0, 0] = 1.0;
                int maxA = 0, maxT = 0;
                for (int j = 0; j < oppCount; j++)
                {
                    double pAbove = ProbGreater(oppPmf[j], s);
                    double pEq = ProbEqual(oppPmf[j], s);
                    double pBelow = ProbLess(oppPmf[j], s);

                    var next = new double[oppCount + 1, oppCount + 1];
                    for (int a = 0; a <= maxA; a++)
                        for (int t = 0; t <= maxT; t++)
                        {
                            double w = dp[a, t];
                            if (w <= 0)
                                continue;
                            next[a + 1, t] += w * pAbove;
                            next[a, t + 1] += w * pEq;
                            next[a, t] += w * pBelow;
                        }
                    dp = next;
                    maxA++;
                    maxT++;
                }

                for (int a = 0; a <= oppCount; a++)
                    for (int t = 0; t <= oppCount; t++)
                    {
                        double w = dp[a, t];
                        if (w <= 0)
                            continue;
                        double inv = 1.0 / (t + 1); // uniform position within my tied block
                        // Final rank r = a + U, U uniform in 1..t+1.
                        if (a == 0)
                        {
                            p1 += pS * w * inv;                 // U == 1
                            if (t >= 1)
                                p2 += pS * w * inv;             // U == 2
                        }
                        else if (a == 1)
                        {
                            p2 += pS * w * inv;                 // a + 1 == 2
                        }
                    }
            }

            if (p1 < 0) p1 = 0;
            if (p2 < 0) p2 = 0;
            double p3 = 1.0 - p1 - p2;
            if (p3 < 0) p3 = 0;
            return new InitiativeOutcome(p1, p2, p3);
        }
    }
}
