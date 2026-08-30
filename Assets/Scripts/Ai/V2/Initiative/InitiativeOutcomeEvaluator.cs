using System;
using System.Collections.Generic;
using System.Text;
using Game.Turns;

namespace Game.Ai.V2.Initiative
{
    // Pure, deterministic, RNG-free N-player evaluator for the REAL TurnOrderResolver semantics:
    // every player rolls independent 50/50 dice; higher score ranks earlier; a tied subgroup
    // rerolls, recursively, using each tied player's OWN dice pool.
    //
    // Unequal pools are therefore NOT uniform inside a tie. A 6-die player tied with a 5-die
    // player still has the stronger distribution on the reroll. This evaluator solves that exact
    // recursive process. For initiative value we only need P(first) and P(second), because every
    // later rank grants the same 6 base AP.
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

        // Secondary tempo scalar only. 1 == certain first; second gets half credit.
        public double EarlinessScore => ProbabilityFirst + 0.5 * ProbabilitySecond;
    }

    public static class InitiativeOutcomeEvaluator
    {
        private readonly struct Top2
        {
            public readonly double First;
            public readonly double Second;

            public Top2(double first, double second)
            {
                First = first;
                Second = second;
            }
        }

        private static readonly Dictionary<int, double[]> PmfByPool = new Dictionary<int, double[]>();
        private static readonly int PoolKinds = InitiativeRules.MaxTotalDice - InitiativeRules.BaseDice + 1;

        public static InitiativeOutcome Evaluate(int myTotalDice, IReadOnlyList<int> opponentTotalDice)
        {
            int myDice = ClampDice(myTotalDice);
            var counts = new int[PoolKinds];
            if (opponentTotalDice != null)
            {
                for (int i = 0; i < opponentTotalDice.Count; i++)
                {
                    int dice = ClampDice(opponentTotalDice[i]);
                    counts[dice - InitiativeRules.BaseDice]++;
                }
            }

            var memo = new Dictionary<string, Top2>();
            Top2 top = SolveTop2(myDice, counts, memo);

            double p1 = Math.Max(0.0, Math.Min(1.0, top.First));
            double p2 = Math.Max(0.0, Math.Min(1.0, top.Second));
            double topSum = p1 + p2;
            if (topSum > 1.0)
            {
                // Only protects against tiny accumulated floating-point drift.
                p1 /= topSum;
                p2 /= topSum;
            }
            double p3 = Math.Max(0.0, 1.0 - p1 - p2);
            return new InitiativeOutcome(p1, p2, p3);
        }

        private static Top2 SolveTop2(int myDice, int[] opponentCounts, Dictionary<string, Top2> memo)
        {
            int opponentCount = Count(opponentCounts);
            if (opponentCount == 0)
                return new Top2(1.0, 0.0);

            string key = StateKey(myDice, opponentCounts);
            if (memo.TryGetValue(key, out Top2 cached))
                return cached;

            double[] myPmf = Pmf(myDice);
            double firstNumerator = 0.0;
            double secondNumerator = 0.0;
            double selfLoop = 0.0;

            for (int score = 0; score < myPmf.Length; score++)
            {
                double myScoreProb = myPmf[score];
                if (myScoreProb <= 0.0)
                    continue;

                var equalCounts = new int[PoolKinds];
                EnumerateEqualStates(myDice, opponentCounts, score, 0, equalCounts,
                    1.0, 0.0, (equal, zeroAboveProb, oneAboveProb) =>
                    {
                        if (zeroAboveProb <= 0.0 && oneAboveProb <= 0.0)
                            return;

                        double jointZeroAbove = myScoreProb * zeroAboveProb;
                        double jointOneAbove = myScoreProb * oneAboveProb;

                        // The only recursive cycle is "everybody tied again". Move that term to
                        // the left-hand side and solve P = numerator / (1 - loopProbability).
                        if (SameCounts(equal, opponentCounts))
                        {
                            selfLoop += jointZeroAbove;
                            return;
                        }

                        Top2 sub = SolveTop2(myDice, equal, memo);

                        // Nobody was already above us: our final first/second place is exactly our
                        // place inside the recursively resolved tied subgroup.
                        firstNumerator += jointZeroAbove * sub.First;
                        secondNumerator += jointZeroAbove * sub.Second;

                        // Exactly one opponent scored above us this round: that opponent is fixed
                        // ahead forever. We finish second only if we win the tied subgroup.
                        secondNumerator += jointOneAbove * sub.First;
                    });
            }

            double denominator = 1.0 - selfLoop;
            Top2 result = denominator > 1e-15
                ? new Top2(firstNumerator / denominator, secondNumerator / denominator)
                : new Top2(0.0, 0.0);
            memo[key] = result;
            return result;
        }

        // Enumerates only how many opponents of each dice-pool size TIED our current score.
        // Opponents above us are collapsed to two useful cases: zero above or exactly one above;
        // two-or-more can never produce a top-two finish and are intentionally discarded.
        private static void EnumerateEqualStates(int myDice, int[] opponentCounts, int score,
            int poolIndex, int[] equalCounts, double zeroAboveSoFar, double oneAboveSoFar,
            Action<int[], double, double> onState)
        {
            if (poolIndex >= PoolKinds)
            {
                onState((int[])equalCounts.Clone(), zeroAboveSoFar, oneAboveSoFar);
                return;
            }

            int n = opponentCounts[poolIndex];
            if (n == 0)
            {
                equalCounts[poolIndex] = 0;
                EnumerateEqualStates(myDice, opponentCounts, score, poolIndex + 1, equalCounts,
                    zeroAboveSoFar, oneAboveSoFar, onState);
                return;
            }

            int opponentDice = InitiativeRules.BaseDice + poolIndex;
            double[] pmf = Pmf(opponentDice);
            double pEqual = ProbEqual(pmf, score);
            double pAbove = ProbGreater(pmf, score);
            double pBelow = ProbLess(pmf, score);

            for (int equal = 0; equal <= n; equal++)
            {
                int nonEqual = n - equal;
                double chooseEqual = BinomialCoefficient(n, equal);
                double equalFactor = chooseEqual * Math.Pow(pEqual, equal);

                // Exactly `equal` tie us and every remaining opponent is below us.
                double groupZeroAbove = equalFactor * Math.Pow(pBelow, nonEqual);

                // Exactly `equal` tie us, exactly one remaining opponent is above, all others below.
                double groupOneAbove = 0.0;
                if (nonEqual > 0 && pAbove > 0.0)
                    groupOneAbove = equalFactor * nonEqual * pAbove * Math.Pow(pBelow, nonEqual - 1);

                double nextZero = zeroAboveSoFar * groupZeroAbove;
                double nextOne = oneAboveSoFar * groupZeroAbove + zeroAboveSoFar * groupOneAbove;
                if (nextZero <= 0.0 && nextOne <= 0.0)
                    continue;

                equalCounts[poolIndex] = equal;
                EnumerateEqualStates(myDice, opponentCounts, score, poolIndex + 1, equalCounts,
                    nextZero, nextOne, onState);
            }
        }

        private static int ClampDice(int dice)
        {
            if (dice < InitiativeRules.BaseDice)
                return InitiativeRules.BaseDice;
            if (dice > InitiativeRules.MaxTotalDice)
                return InitiativeRules.MaxTotalDice;
            return dice;
        }

        private static double[] Pmf(int poolSize)
        {
            if (PmfByPool.TryGetValue(poolSize, out double[] cached))
                return cached;

            var pmf = new double[poolSize + 1];
            double denom = Math.Pow(2.0, poolSize);
            double coefficient = 1.0;
            for (int hits = 0; hits <= poolSize; hits++)
            {
                pmf[hits] = coefficient / denom;
                coefficient = coefficient * (poolSize - hits) / (hits + 1);
            }
            PmfByPool[poolSize] = pmf;
            return pmf;
        }

        private static double ProbGreater(double[] pmf, int threshold)
        {
            double sum = 0.0;
            for (int hits = threshold + 1; hits < pmf.Length; hits++)
                sum += pmf[hits];
            return sum;
        }

        private static double ProbLess(double[] pmf, int threshold)
        {
            double sum = 0.0;
            for (int hits = 0; hits < pmf.Length && hits < threshold; hits++)
                sum += pmf[hits];
            return sum;
        }

        private static double ProbEqual(double[] pmf, int value) =>
            value >= 0 && value < pmf.Length ? pmf[value] : 0.0;

        private static double BinomialCoefficient(int n, int k)
        {
            if (k < 0 || k > n)
                return 0.0;
            if (k > n - k)
                k = n - k;
            double result = 1.0;
            for (int i = 1; i <= k; i++)
                result = result * (n - (k - i)) / i;
            return result;
        }

        private static int Count(int[] counts)
        {
            int total = 0;
            for (int i = 0; i < counts.Length; i++)
                total += counts[i];
            return total;
        }

        private static bool SameCounts(int[] a, int[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        private static string StateKey(int myDice, int[] counts)
        {
            var sb = new StringBuilder();
            sb.Append(myDice).Append('|');
            for (int i = 0; i < counts.Length; i++)
                sb.Append(counts[i]).Append(',');
            return sb.ToString();
        }
    }
}
