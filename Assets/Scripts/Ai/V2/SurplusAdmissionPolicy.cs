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

    // Hard reserve safety remains StrategicManager.ReservesOkAfterChain. This policy only changes
    // the SOFT utility admission line inside that already-safe set: stranded AP/resources lower
    // the threshold toward a floor, while a plan sitting on the reserves keeps the original
    // conservative threshold.
    //
    // IMPORTANT: a configured reserve is a floor we must not CROSS because of a surplus action,
    // not a prerequisite stockpile the player must already own. If the turn begins below a reserve
    // (for example E=1 while surplusEnergyReserve=2), a plan that costs 0 of that resource is still
    // safe: it leaves the existing deficit unchanged. The protected floor is therefore
    // min(configuredReserve, currentlyAvailable) per dimension.
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

            float configuredApReserve = AiConfigV2.housekeepingApReserve + AiConfigV2.surplusApReserve;
            float protectedApFloor = Mathf.Min(configuredApReserve, Mathf.Max(0f, root.ActionPoints));
            float apSlack = Mathf.Max(0f, root.ActionPoints - plan.ApCost - protectedApFloor);

            float resFactor = 0f;
            int dimensions = 0;
            Add(ResourceType.Human, AiConfigV2.surplusHumanReserve, plan.ResCost?.human ?? 0);
            Add(ResourceType.Energy, AiConfigV2.surplusEnergyReserve, plan.ResCost?.energy ?? 0);
            Add(ResourceType.Materials, AiConfigV2.surplusMaterialsReserve, plan.ResCost?.materials ?? 0);
            Add(ResourceType.Tech, AiConfigV2.surplusTechReserve, plan.ResCost?.tech ?? 0);

            void Add(ResourceType type, int configuredReserve, int cost)
            {
                float available = Mathf.Max(0f, AiResourceReservation.Available(root, player, type));
                float protectedFloor = Mathf.Min(configuredReserve, available);
                float afterSlack = Mathf.Max(0f, available - cost - protectedFloor);
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
