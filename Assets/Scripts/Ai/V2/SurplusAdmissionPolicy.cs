using Game.Economy;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    internal readonly struct SurplusAdmission
    {
        public readonly float BaseThreshold;
        public readonly float EffectiveThreshold;
        public readonly float ApSlack;
        public readonly float ResourceSlackFactor;

        public SurplusAdmission(float baseThreshold, float effectiveThreshold, float apSlack, float resourceSlackFactor)
        {
            BaseThreshold = baseThreshold;
            EffectiveThreshold = effectiveThreshold;
            ApSlack = apSlack;
            ResourceSlackFactor = resourceSlackFactor;
        }
    }

    // Hard physical safety remains StrategicManager.ReservesOkAfterChain. This policy only changes
    // the SOFT utility admission line inside that already-safe set: genuinely stranded AP/resources
    // lower the threshold toward a floor, while scarce capacity keeps the original conservative
    // threshold.
    //
    // IMPORTANT: Phase B runs after ordinary mission execution. It must not invent fixed AP or
    // H/E/M/T reserves for hypothetical future work. AP slack is simply real AP left after the
    // candidate. Resource slack is based on AiResourceReservation.Available — the authoritative
    // dynamic balance after every real reservation owned elsewhere. If aviation or another late
    // subsystem has genuinely reserved Energy, that Energy is already absent here; otherwise it is
    // legitimate surplus and may relax the threshold.
    internal static class SurplusAdmissionPolicy
    {
        internal const float ThresholdFloor = 0.35f;
        internal const float ApSlackForFullRelaxation = 6f;
        internal const float ResourceSlackForFullRelaxation = 4f;

        public static SurplusAdmission Evaluate(PlayerRoot root, PlayerSetupData player, MaterializationPlan plan)
        {
            float baseThreshold = AiConfigV2.surplusUtilityThreshold;
            if (root == null || plan == null)
                return new SurplusAdmission(baseThreshold, baseThreshold, 0f, 0f);

            float apSlack = Mathf.Max(0f, root.ActionPoints - plan.ApCost);

            float resFactor = 0f;
            int dimensions = 0;
            Add(ResourceType.Human, plan.ResCost?.human ?? 0);
            Add(ResourceType.Energy, plan.ResCost?.energy ?? 0);
            Add(ResourceType.Materials, plan.ResCost?.materials ?? 0);
            Add(ResourceType.Tech, plan.ResCost?.tech ?? 0);

            void Add(ResourceType type, int cost)
            {
                float available = Mathf.Max(0f, AiResourceReservation.Available(root, player, type));
                float afterSlack = Mathf.Max(0f, available - Mathf.Max(0, cost));
                resFactor += Mathf.Clamp01(afterSlack / ResourceSlackForFullRelaxation);
                dimensions++;
            }

            if (dimensions > 0)
                resFactor /= dimensions;
            return EvaluateFromSlack(apSlack, resFactor);
        }

        internal static SurplusAdmission EvaluateFromSlack(float apSlack, float resourceSlackFactor)
        {
            float baseThreshold = AiConfigV2.surplusUtilityThreshold;
            float safeApSlack = Mathf.Max(0f, apSlack);
            float safeResourceSlack = Mathf.Clamp01(resourceSlackFactor);
            float apFactor = Mathf.Clamp01(safeApSlack / ApSlackForFullRelaxation);
            float relaxation = Mathf.Clamp01(0.75f * apFactor + 0.25f * safeResourceSlack);
            float effective = Mathf.Lerp(baseThreshold, ThresholdFloor, relaxation);
            return new SurplusAdmission(baseThreshold, effective, safeApSlack, safeResourceSlack);
        }
    }
}
