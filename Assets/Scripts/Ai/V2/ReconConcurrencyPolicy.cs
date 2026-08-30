using System.Collections.Generic;
using UnityEngine;

namespace Game.Ai.V2
{
    // Demand-side desired concurrency is deliberately softer than the allocator's hard K.
    // K answers "how many may execute"; this policy answers "how many are worth owning now".
    // A second lane is requested only while the map is materially dark and the second runnable
    // objective retains enough absolute and relative value. This prevents maxConcurrentReconExecutions
    // from becoming an unconditional production target late in exploration.
    internal static class ReconConcurrencyPolicy
    {
        internal const float SecondLaneMinBaseValue = 50f;
        internal const float SecondLaneMinRelativeValue = 0.80f;
        internal const float SecondLaneMinExplorableUnknownFrac = 0.35f;

        public static int DesiredTotal(WorldSnapshot snap, IReadOnlyList<ReconObjective> runnable)
        {
            int hardCap = Mathf.Max(0, AiConfigV2.maxConcurrentReconExecutions);
            if (hardCap == 0 || runnable == null || runnable.Count == 0)
                return 0;

            int desired = 1;
            if (hardCap < 2 || runnable.Count < 2)
                return desired;

            ReconObjective first = runnable[0];
            ReconObjective second = runnable[1];
            float best = Mathf.Max(0.0001f, first?.BaseValue ?? 0f);
            float secondValue = Mathf.Max(0f, second?.BaseValue ?? 0f);
            float ratio = secondValue / best;
            float dark = snap?.MapKnowledge?.ExplorableUnknownFrac ?? 0f;

            if (secondValue >= SecondLaneMinBaseValue
                && ratio >= SecondLaneMinRelativeValue
                && dark >= SecondLaneMinExplorableUnknownFrac)
                desired = 2;

            return Mathf.Min(hardCap, desired);
        }

        public static string Explain(WorldSnapshot snap, IReadOnlyList<ReconObjective> runnable)
        {
            float first = runnable != null && runnable.Count > 0 ? runnable[0].BaseValue : 0f;
            float second = runnable != null && runnable.Count > 1 ? runnable[1].BaseValue : 0f;
            float ratio = first > 0f ? second / first : 0f;
            float dark = snap?.MapKnowledge?.ExplorableUnknownFrac ?? 0f;
            return $"desired={DesiredTotal(snap, runnable)} hard={AiConfigV2.maxConcurrentReconExecutions} "
                + $"best={first:0.0} second={second:0.0} ratio={ratio:0.00} dark={dark:0.00}";
        }
    }
}
