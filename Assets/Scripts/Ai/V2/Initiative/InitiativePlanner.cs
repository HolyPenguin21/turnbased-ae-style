using System.Collections.Generic;
using System.Globalization;
using Game.Economy;
using Game.Turns;
using UnityEngine;

namespace Game.Ai.V2.Initiative
{
    public sealed class InitiativePlan
    {
        public readonly List<ResourceType> PaymentResources; // one H/E/M/T source per die, in purchase order
        public readonly int DiceToBuy;
        public readonly float NetValue;
        public readonly float ResourceOpportunityCost;
        public readonly string Rationale;

        public InitiativePlan(List<ResourceType> paymentResources, int diceToBuy, float netValue,
            float oppCost, string rationale)
        {
            PaymentResources = paymentResources ?? new List<ResourceType>();
            DiceToBuy = diceToBuy;
            NetValue = netValue;
            ResourceOpportunityCost = oppCost;
            Rationale = rationale;
        }

        public static InitiativePlan None(string why) =>
            new InitiativePlan(new List<ResourceType>(), 0, 0f, 0f, why);
    }

    // Values additional initiative dice PURELY as extra AP capacity and (secondarily) earlier
    // turn position, against the strategic cost of the resources a purchase would consume.
    // Initiative is never a strategic objective here — no DesireVector, no mission priorities,
    // no radar. Every candidate 0..MaxBonusDice is evaluated for its real marginal value, so the
    // planner never targets a fixed dice count.
    public static class InitiativePlanner
    {
        private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

        public static InitiativePlan Plan(PreTurnCapacityAnalysis analysis, IReadOnlyList<int> opponentTotalDice)
        {
            if (analysis == null)
                return InitiativePlan.None("no analysis");

            // History is evidence about how tight AP tends to be; it is never allowed to invent a
            // reason to buy initiative on a turn where no current structural AP workload exists.
            if (analysis.CurrentApPressure <= 0.0001f)
                return InitiativePlan.None("no current AP workload");

            InitiativeOutcome baseline = InitiativeOutcomeEvaluator.Evaluate(InitiativeRules.BaseDice, opponentTotalDice);
            InitiativePlan best = InitiativePlan.None("zero dice beats every purchase");

            for (int n = 1; n <= InitiativeRules.MaxBonusDice; n++)
            {
                // Feasibility comes from the REAL payment rule, not from a summed H+E+M+T unit
                // total. If N dice are impossible then N+1 is impossible too because it contains
                // the same first N purchases plus another more expensive one.
                InitiativeFundingResult funding = InitiativeFundingOptimizer.Plan(analysis, 0, n);
                if (!funding.Feasible)
                    break;

                InitiativeOutcome cand = InitiativeOutcomeEvaluator.Evaluate(
                    InitiativeRules.BaseDice + n, opponentTotalDice);

                double expectedApGain = cand.ExpectedBaseAp - baseline.ExpectedBaseAp;
                double earlinessGain = cand.EarlinessScore - baseline.EarlinessScore;

                float apBenefit = (float)expectedApGain * analysis.ApPressure
                    * AiConfigV2.initiativeApBenefitPerExpectedAp;
                float tempoBenefit = (float)earlinessGain * analysis.TurnOrderPressure
                    * AiConfigV2.initiativeTempoBenefitPerEarliness;
                float gross = apBenefit + tempoBenefit;
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
                    best = new InitiativePlan(funding.PaymentResources, n, net,
                        funding.TotalOpportunityCost, why);
                }
            }

            return best.DiceToBuy > 0 && best.NetValue > 0f
                ? best
                : InitiativePlan.None("zero dice beats every legal purchase");
        }
    }
}
