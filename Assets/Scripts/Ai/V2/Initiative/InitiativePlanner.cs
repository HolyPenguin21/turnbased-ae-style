using System.Collections.Generic;
using System.Globalization;
using Game.Turns;
using UnityEngine;

namespace Game.Ai.V2.Initiative
{
    public sealed class InitiativePlan
    {
        public readonly List<int[]> DiePayments;   // empty => buy nothing
        public readonly int DiceToBuy;
        public readonly float NetValue;
        public readonly float ResourceOpportunityCost;
        public readonly string Rationale;

        public InitiativePlan(List<int[]> diePayments, int diceToBuy, float netValue, float oppCost, string rationale)
        {
            DiePayments = diePayments ?? new List<int[]>();
            DiceToBuy = diceToBuy;
            NetValue = netValue;
            ResourceOpportunityCost = oppCost;
            Rationale = rationale;
        }

        public static InitiativePlan None(string why) =>
            new InitiativePlan(new List<int[]>(), 0, 0f, 0f, why);
    }

    // Values additional initiative dice PURELY as extra AP capacity and (secondarily) earlier
    // turn position, against the strategic cost of the resources a purchase would consume.
    // Initiative is never a strategic objective here — no DesireVector, no mission priorities,
    // no radar. Every candidate 0..MaxBonusDice is evaluated for its real marginal value, so the
    // planner never targets a fixed dice count and stops the moment another die stops paying for
    // itself.
    public static class InitiativePlanner
    {
        private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        public static InitiativePlan Plan(PreTurnCapacityAnalysis analysis, IReadOnlyList<int> opponentTotalDice)
        {
            if (analysis == null)
                return InitiativePlan.None("no analysis");

            InitiativeOutcome baseline = InitiativeOutcomeEvaluator.Evaluate(InitiativeRules.BaseDice, opponentTotalDice);

            int totalUnits = 0;
            for (int i = 0; i < 4; i++)
                totalUnits += analysis.Available[i];

            // Affordability ceiling: the running cost ladder is 1,3,7,15,31.
            int affordableDice = 0;
            for (int n = 1; n <= InitiativeRules.MaxBonusDice; n++)
            {
                if (InitiativeRules.TotalCostThrough(n) <= totalUnits)
                    affordableDice = n;
                else
                    break;
            }
            if (affordableDice == 0)
                return InitiativePlan.None("cannot afford even the first die");

            InitiativePlan best = InitiativePlan.None("zero dice beats every purchase");

            for (int n = 1; n <= affordableDice; n++)
            {
                InitiativeOutcome cand = InitiativeOutcomeEvaluator.Evaluate(InitiativeRules.BaseDice + n, opponentTotalDice);

                double expectedApGain = cand.ExpectedBaseAp - baseline.ExpectedBaseAp;
                double earlinessGain = cand.EarlinessScore - baseline.EarlinessScore;

                float apBenefit = (float)expectedApGain * analysis.ApPressure * AiConfigV2.initiativeApBenefitPerExpectedAp;
                float tempoBenefit = (float)earlinessGain * analysis.TurnOrderPressure * AiConfigV2.initiativeTempoBenefitPerEarliness;
                float gross = apBenefit + tempoBenefit;

                InitiativeFundingResult funding = InitiativeFundingOptimizer.Plan(analysis, 0, n);
                if (!funding.Feasible)
                    break;

                float net = gross - funding.TotalOpportunityCost;

                bool better =
                    net > best.NetValue + AiConfigV2.initiativeNetValueEpsilon
                    || (Mathf.Abs(net - best.NetValue) <= AiConfigV2.initiativeNetValueEpsilon
                        && best.DiceToBuy > 0
                        && funding.TotalOpportunityCost < best.ResourceOpportunityCost - 1e-4f);

                if (better)
                {
                    string why = $"{n} dice: ΔEAp={F(expectedApGain)}*apP={F(analysis.ApPressure)} -> apBen={F(apBenefit)}, "
                        + $"ΔEarly={F(earlinessGain)}*toP={F(analysis.TurnOrderPressure)} -> tempo={F(tempoBenefit)}, "
                        + $"gross={F(gross)} - oppCost={F(funding.TotalOpportunityCost)} = net {F(net)}";
                    best = new InitiativePlan(funding.DiePayments, n, net, funding.TotalOpportunityCost, why);
                }
            }

            if (best.DiceToBuy > 0 && best.NetValue > 0f)
                return best;
            return InitiativePlan.None(best.DiceToBuy > 0
                ? $"best candidate net {F(best.NetValue)} not positive"
                : "zero dice beats every purchase");
        }
    }
}
