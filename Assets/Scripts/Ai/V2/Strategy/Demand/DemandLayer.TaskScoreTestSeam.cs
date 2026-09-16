#if UNITY_INCLUDE_TESTS
using UnityEngine;

namespace Game.Ai.V2
{
    public static partial class DemandLayer
    {
        // Test-only compatibility seam for the pre-TaskScore invariant test. Production demand
        // construction no longer calls this signature. The seam deliberately has no scoring math
        // of its own: legacy inputs are mapped to canonical TaskScore slots and folded by the one
        // TaskScoreEvaluator owner.
        internal static float ScoreEconomySite(float deficit, float expectedIncomeGain,
            float baseNetworkSynergy, float nearbyResourceClusterValue, float travelCost,
            float threatExposure, float heroOpportunityCost, float resourceCost,
            float assignmentApCost, float paybackTurns)
        {
            var score = new TaskScore(
                economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(
                    Mathf.Max(0f, expectedIncomeGain), Mathf.Clamp01(deficit)),
                payback: TaskScoreEvaluator.Payback(paybackTurns),
                cardPrice: TaskScoreEvaluator.CardPrice(
                    Mathf.Max(0f, assignmentApCost), Mathf.Max(0f, resourceCost)),
                delivery: Mathf.Max(0f, travelCost) * AiConfigV2.taskScoreReactivationApWeight,
                moverOpportunityCost: Mathf.Max(0f, heroOpportunityCost),
                hexThreatRisk: TaskScoreEvaluator.HexThreatRisk(threatExposure));
            return score.Value;
        }
    }
}
#endif
