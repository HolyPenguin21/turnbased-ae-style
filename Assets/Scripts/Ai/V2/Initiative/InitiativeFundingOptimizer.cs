using System.Collections.Generic;
using Game.Turns;
using UnityEngine;

namespace Game.Ai.V2.Initiative
{
    public sealed class InitiativeFundingResult
    {
        public readonly List<int[]> DiePayments;   // one int[4] (H,E,M,T) per die; each sums to that die's cost
        public readonly float TotalOpportunityCost;
        public readonly bool Feasible;

        public InitiativeFundingResult(List<int[]> payments, float totalCost, bool feasible)
        {
            DiePayments = payments;
            TotalOpportunityCost = totalCost;
            Feasible = feasible;
        }
    }

    // Turns "buy N dice" into a concrete per-die H/E/M/T payment plan that is the least
    // strategically damaging way to raise the required units. Marginal cost is re-evaluated
    // after every single unit is committed, so the optimizer never just empties whichever
    // resource looked cheapest at the start — draining a resource makes its next unit dearer
    // (PreTurnCapacityAnalysis.MarginalCostAt). Ties break to the lower ResourceType index for
    // a stable, deterministic funding order.
    public static class InitiativeFundingOptimizer
    {
        public static InitiativeFundingResult Plan(PreTurnCapacityAnalysis analysis, int alreadyPaidDice, int diceToBuy)
        {
            if (analysis == null || diceToBuy <= 0)
                return new InitiativeFundingResult(new List<int[]>(), 0f, diceToBuy <= 0);

            var stock = (int[])analysis.Available.Clone();
            var payments = new List<int[]>(diceToBuy);
            float total = 0f;

            for (int d = 0; d < diceToBuy; d++)
            {
                int cost = InitiativeRules.NextBonusDieCost(alreadyPaidDice + d);
                var bundle = new int[4];
                for (int unit = 0; unit < cost; unit++)
                {
                    int best = -1;
                    float bestCost = float.MaxValue;
                    for (int i = 0; i < 4; i++)
                    {
                        if (stock[i] <= 0)
                            continue;
                        float mc = analysis.MarginalCostAt(i, stock[i]);
                        if (mc < bestCost - 1e-6f)
                        {
                            bestCost = mc;
                            best = i;
                        }
                    }
                    if (best < 0)
                        return new InitiativeFundingResult(null, 0f, false); // not enough physical units

                    stock[best]--;
                    bundle[best]++;
                    total += bestCost;
                }
                payments.Add(bundle);
            }
            return new InitiativeFundingResult(payments, total, true);
        }
    }
}
