using System.Collections.Generic;
using Game.Economy;
using Game.Turns;

namespace Game.Ai.V2.Initiative
{
    public sealed class InitiativeFundingResult
    {
        // Flat, one entry per resource UNIT consumed, in purchase order — a die can now be paid
        // from a mix of H/E/M/T (see PlayerRoot.PurchaseInitiativeToken). Length always equals
        // InitiativeRules.CumulativeUnitsForTokenCount(tokensToBuy); the caller (InitiativeCoordinatorV2)
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

    // Finds the least strategically damaging LEGAL funding plan for "buy N tokens", now that a
    // single die's progressive cost can be split across any mix of H/E/M/T (one resource-unit
    // purchase at a time — see PlayerRoot.PurchaseInitiativeToken). Token boundaries no longer
    // constrain WHICH resource pays for a unit, only how many units are needed in total.
    //
    // PreTurnCapacityAnalysis.MarginalCostAt is non-decreasing as a resource's hypothetical
    // stock drops (draining toward its protected floor only ever gets more expensive per additional unit,
    // never cheaper). With a convex per-type cost curve like that, greedily taking the globally
    // cheapest next available unit — across all 4 types — for each of the tokensToBuy tokens'
    // combined price is optimal: any allocation that ever passed up a cheaper spendable unit
    // above its committed floor in favour of a pricier one can be swapped for strictly lower
    // (or equal) total cost. This
    // replaces the old exhaustive per-die search, which only ever considered paying a whole die
    // from a single resource because that used to be the only legal payment shape.
    public static class InitiativeFundingOptimizer
    {
        public static InitiativeFundingResult Plan(PreTurnCapacityAnalysis analysis, int alreadyPaidTokens, int tokensToBuy)
        {
            if (analysis == null)
                return new InitiativeFundingResult(null, 0f, false);
            if (tokensToBuy <= 0)
                return new InitiativeFundingResult(new List<ResourceType>(), 0f, true);

            if (alreadyPaidTokens < 0)
                alreadyPaidTokens = 0;
            if (alreadyPaidTokens + tokensToBuy > InitiativeRules.MaxBonusTokens)
                return new InitiativeFundingResult(null, 0f, false);

            int totalUnits = InitiativeRules.CumulativeUnitsForTokenCount(alreadyPaidTokens + tokensToBuy)
                - InitiativeRules.CumulativeUnitsForTokenCount(alreadyPaidTokens);

            var stock = (int[])analysis.Available.Clone();
            var units = new List<ResourceType>(totalUnits);
            float totalCost = 0f;

            for (int taken = 0; taken < totalUnits; taken++)
            {
                int bestType = -1;
                float bestCost = float.MaxValue;
                for (int i = 0; i < PreTurnCapacityAnalysis.Types.Length; i++)
                {
                    if (stock[i] <= analysis.CommittedResourceFloor[i])
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
