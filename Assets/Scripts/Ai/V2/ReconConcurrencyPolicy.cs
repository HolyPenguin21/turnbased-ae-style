using System.Collections.Generic;
using UnityEngine;

namespace Game.Ai.V2
{
    // Demand-side desired concurrency is deliberately softer than the allocator's hard K.
    // K answers "how many may execute"; this policy answers "how many are worth owning now".
    // Additional lanes are requested only while the map remains materially dark and the next
    // runnable objective retains enough absolute and relative value. This prevents the hard cap
    // from becoming an unconditional production target late in exploration.
    internal static class ReconConcurrencyPolicy
    {
        // ReconOnly is the isolated Ground-Recon acceptance environment. It deliberately permits a
        // three-scout portfolio so deconfliction/spread can be exercised without changing the Full
        // strategy's historical K=2 tuning before the Ground Recon acceptance suite passes.
        internal const int ReconOnlyHardCap = 3;

        internal const float SecondLaneMinBaseValue = 50f;
        internal const float SecondLaneMinRelativeValue = 0.80f;
        internal const float SecondLaneMinExplorableUnknownFrac = 0.35f;

        // The third lane is a coverage lane: require a materially darker map, but allow its absolute
        // objective value to be below lane two because marginal frontier/Refresh value naturally
        // falls as scouts spread. It still must retain a meaningful fraction of the best job.
        internal const float ThirdLaneMinBaseValue = 40f;
        internal const float ThirdLaneMinRelativeValue = 0.65f;
        internal const float ThirdLaneMinExplorableUnknownFrac = 0.55f;

        public static int HardCap => AiStrategyV2Scope.IsReconOnly
            ? ReconOnlyHardCap
            : Mathf.Max(0, AiConfigV2.maxConcurrentReconExecutions);

        public static int DesiredTotal(WorldSnapshot snap, IReadOnlyList<ReconObjective> runnable)
        {
            int hardCap = Mathf.Max(0, HardCap);
            if (hardCap == 0 || runnable == null || runnable.Count == 0)
                return 0;

            int desired = 1;
            if (hardCap < 2 || runnable.Count < 2)
                return desired;

            ReconObjective first = runnable[0];
            ReconObjective second = runnable[1];
            float best = Mathf.Max(0.0001f, first?.BaseValue ?? 0f);
            float secondValue = Mathf.Max(0f, second?.BaseValue ?? 0f);
            float secondRatio = secondValue / best;
            float dark = snap?.MapKnowledge?.ExplorableUnknownFrac ?? 0f;

            if (secondValue >= SecondLaneMinBaseValue
                && secondRatio >= SecondLaneMinRelativeValue
                && dark >= SecondLaneMinExplorableUnknownFrac)
                desired = 2;

            // A third lane is never requested unless lane two already qualified. This keeps the
            // concurrency curve monotonic and prevents a very dark map from skipping a weak second
            // objective just because a third entry happens to look acceptable in isolation.
            if (desired >= 2 && hardCap >= 3 && runnable.Count >= 3)
            {
                ReconObjective third = runnable[2];
                float thirdValue = Mathf.Max(0f, third?.BaseValue ?? 0f);
                float thirdRatio = thirdValue / best;
                if (thirdValue >= ThirdLaneMinBaseValue
                    && thirdRatio >= ThirdLaneMinRelativeValue
                    && dark >= ThirdLaneMinExplorableUnknownFrac)
                    desired = 3;
            }

            return Mathf.Min(hardCap, desired);
        }

        public static string Explain(WorldSnapshot snap, IReadOnlyList<ReconObjective> runnable)
        {
            float first = runnable != null && runnable.Count > 0 ? runnable[0].BaseValue : 0f;
            float second = runnable != null && runnable.Count > 1 ? runnable[1].BaseValue : 0f;
            float third = runnable != null && runnable.Count > 2 ? runnable[2].BaseValue : 0f;
            float secondRatio = first > 0f ? second / first : 0f;
            float thirdRatio = first > 0f ? third / first : 0f;
            float dark = snap?.MapKnowledge?.ExplorableUnknownFrac ?? 0f;
            return $"desired={DesiredTotal(snap, runnable)} hard={HardCap} "
                + $"best={first:0.0} second={second:0.0} r2={secondRatio:0.00} "
                + $"third={third:0.0} r3={thirdRatio:0.00} dark={dark:0.00}";
        }
    }
}
