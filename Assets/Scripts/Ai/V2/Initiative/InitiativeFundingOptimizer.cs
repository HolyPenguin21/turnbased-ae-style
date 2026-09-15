using System.Collections.Generic;
using Game.Economy;
using Game.Turns;

namespace Game.Ai.V2.Initiative
{
    public sealed class InitiativeFundingResult
    {
        // Flat, one entry per resource UNIT consumed, in purchase order — a die can now be paid
        // from a mix of H/E/M/T (see PlayerRoot.PurchaseInitiativeDie). Length always equals
        // InitiativeRules.CumulativeUnitsForDiceCount(diceToBuy); the caller (InitiativeCoordinatorV2)
        // slices it back into per-die chunks using the same progressive-cost ladder.
        public readonly List<ResourceType> PaymentUnits;
        public readonly float TotalOpportunityCost;
        public readonly bool Feasible;

        public InitiativeFundingResult(List<ResourceType> paymentUnits, float totalCost, bool feasible)
        {
            PaymentUnits = paymentUnits ?? new List<ResourceType>();
            TotalOpportunityCost = totalCost;
            Feasible = feasible;
        }
    }

    // Finds the least strategically damaging LEGAL funding plan for "buy N dice", now that a
    // single die's progressive cost can be split across any mix of H/E/M/T (one resource-unit
    // purchase at a time — see PlayerRoot.PurchaseInitiativeDie). Die boundaries no longer
    // constrain WHICH resource pays for a unit, only how many units are needed in total.
    //
    // PreTurnCapacityAnalysis.MarginalCostAt is non-decreasing as a resource's hypothetical
    // stock drops (draining toward empty only ever gets more expensive per additional unit,
    // never cheaper). With a convex per-type cost curve like that, greedily taking the globally
    // cheapest next available unit — across all 4 types — for each of the diceToBuy dice'
    // combined price is optimal: any allocation that ever passed up a cheaper available unit in
    // favour of a pricier one can be swapped for strictly lower (or equal) total cost. This
    // replaces the old exhaustive per-die search, which only ever considered paying a whole die
    // from a single resource because that used to be the only legal payment shape.
    public static class InitiativeFundingOptimizer
    {
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

            int totalUnits = InitiativeRules.CumulativeUnitsForDiceCount(alreadyPaidDice + diceToBuy)
                - InitiativeRules.CumulativeUnitsForDiceCount(alreadyPaidDice);

            var stock = (int[])analysis.Available.Clone();
            var units = new List<ResourceType>(totalUnits);
            float totalCost = 0f;

            for (int taken = 0; taken < totalUnits; taken++)
            {
                int bestType = -1;
                float bestCost = float.MaxValue;
                for (int i = 0; i < PreTurnCapacityAnalysis.Types.Length; i++)
                {
                    if (stock[i] <= 0)
                        continue;
                    float cost = analysis.MarginalCostAt(i, stock[i] - 1);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestType = i;
                    }
                }

                if (bestType < 0)
                    return new InitiativeFundingResult(null, 0f, false);

                stock[bestType]--;
                units.Add(PreTurnCapacityAnalysis.Types[bestType]);
                totalCost += bestCost;
            }

            return new InitiativeFundingResult(units, totalCost, true);
        }
    }
}
