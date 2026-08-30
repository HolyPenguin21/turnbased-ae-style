using System.Collections.Generic;
using Game.Economy;
using Game.Turns;

namespace Game.Ai.V2.Initiative
{
    public sealed class InitiativeFundingResult
    {
        // One resource type per die, in purchase order. The real amount is the progressive price
        // of that die (1/2/4/8/16); a die is never split across resource types because the current
        // player UI cannot make such a payment either.
        public readonly List<ResourceType> PaymentResources;
        public readonly float TotalOpportunityCost;
        public readonly bool Feasible;

        public InitiativeFundingResult(List<ResourceType> paymentResources, float totalCost, bool feasible)
        {
            PaymentResources = paymentResources ?? new List<ResourceType>();
            TotalOpportunityCost = totalCost;
            Feasible = feasible;
        }
    }

    // Finds the least strategically damaging LEGAL funding plan for "buy N dice".
    //
    // Important: this is deliberately NOT greedy per die. With progressive prices a cheap choice
    // for the 1-resource first die can consume the only stockpile capable of paying the 2/4/8/16
    // die later (e.g. H=2,E=1: paying die #1 from H makes die #2 impossible, while E then H is
    // legal). MaxBonusDice is only five and there are four resource types, so exhaustive search is
    // tiny (at most 4^5 leaves) and gives both exact feasibility and exact minimum opportunity cost.
    // Marginal cost is still recomputed for every unit as a hypothetical stockpile drains.
    public static class InitiativeFundingOptimizer
    {
        private const float CostEpsilon = 1e-6f;

        public static InitiativeFundingResult Plan(PreTurnCapacityAnalysis analysis, int alreadyPaidDice, int diceToBuy)
        {
            if (analysis == null)
                return new InitiativeFundingResult(null, 0f, false);
            if (diceToBuy <= 0)
                return new InitiativeFundingResult(new List<ResourceType>(), 0f, true);

            if (alreadyPaidDice < 0)
                alreadyPaidDice = 0;
            if (alreadyPaidDice + diceToBuy > InitiativeRules.MaxBonusDice)
                return new InitiativeFundingResult(null, 0f, false);

            var stock = (int[])analysis.Available.Clone();
            var current = new ResourceType[diceToBuy];
            ResourceType[] best = null;
            float bestCost = float.MaxValue;

            Search(analysis, alreadyPaidDice, diceToBuy, 0, stock, current, 0f, ref best, ref bestCost);

            return best == null
                ? new InitiativeFundingResult(null, 0f, false)
                : new InitiativeFundingResult(new List<ResourceType>(best), bestCost, true);
        }

        private static void Search(PreTurnCapacityAnalysis analysis, int alreadyPaidDice, int diceToBuy,
            int dieIndex, int[] stock, ResourceType[] current, float costSoFar,
            ref ResourceType[] best, ref float bestCost)
        {
            if (costSoFar > bestCost + CostEpsilon)
                return;

            if (dieIndex >= diceToBuy)
            {
                // Resource indices are visited in canonical H/E/M/T order, so keeping the first
                // equal-cost solution also gives a deterministic lexicographic tie-break.
                if (best == null || costSoFar < bestCost - CostEpsilon)
                {
                    best = (ResourceType[])current.Clone();
                    bestCost = costSoFar;
                }
                return;
            }

            int price = InitiativeRules.NextBonusDieCost(alreadyPaidDice + dieIndex);
            for (int i = 0; i < PreTurnCapacityAnalysis.Types.Length; i++)
            {
                if (stock[i] < price)
                    continue;

                float paymentCost = OpportunityCostOfPayment(analysis, i, stock[i], price);
                stock[i] -= price;
                current[dieIndex] = PreTurnCapacityAnalysis.Types[i];
                Search(analysis, alreadyPaidDice, diceToBuy, dieIndex + 1, stock, current,
                    costSoFar + paymentCost, ref best, ref bestCost);
                stock[i] += price;
            }
        }

        private static float OpportunityCostOfPayment(PreTurnCapacityAnalysis analysis, int typeIndex,
            int stockBeforePayment, int amount)
        {
            float total = 0f;
            for (int unit = 0; unit < amount; unit++)
                total += analysis.MarginalCostAt(typeIndex, stockBeforePayment - unit - 1);
            return total;
        }
    }
}
