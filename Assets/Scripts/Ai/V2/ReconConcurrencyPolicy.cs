using System.Collections.Generic;
using Game.HexGrid;
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
        internal const int ReconOnlyHardCap = AiConfigV2.reconConcurrencyReconOnlyHardCap;

        internal const float SecondLaneMinBaseValue = AiConfigV2.reconConcurrencySecondLaneMinBaseValue;
        internal const float SecondLaneMinRelativeValue = AiConfigV2.reconConcurrencySecondLaneMinRelValue;
        internal const float SecondLaneMinExplorableUnknownFrac = AiConfigV2.reconConcurrencySecondLaneMinDarkFrac;

        // The third lane is a coverage lane: require a materially darker map, but allow its absolute
        // objective value to be below lane two because marginal frontier/Refresh value naturally
        // falls as scouts spread. It still must retain a meaningful fraction of the best job.
        internal const float ThirdLaneMinBaseValue = AiConfigV2.reconConcurrencyThirdLaneMinBaseValue;
        internal const float ThirdLaneMinRelativeValue = AiConfigV2.reconConcurrencyThirdLaneMinRelValue;
        internal const float ThirdLaneMinExplorableUnknownFrac = AiConfigV2.reconConcurrencyThirdLaneMinDarkFrac;

        public static int HardCap => AiStrategyV2Scope.IsReconOnly
            ? ReconOnlyHardCap
            : Mathf.Max(0, AiConfigV2.maxConcurrentReconExecutions);

        public static int DesiredTotal(WorldSnapshot snap, IReadOnlyList<ReconObjective> runnable)
        {
            int hardCap = Mathf.Max(0, HardCap);
            if (hardCap == 0 || runnable == null || runnable.Count == 0)
                return 0;

            int desired = 1;
            float dark = snap?.MapKnowledge?.ExplorableUnknownFrac ?? 0f;

            if (hardCap >= 2 && runnable.Count >= 2)
            {
                ReconObjective first = runnable[0];
                ReconObjective second = runnable[1];
                float best = Mathf.Max(0.0001f, first?.BaseValue ?? 0f);
                float secondValue = Mathf.Max(0f, second?.BaseValue ?? 0f);
                float secondRatio = secondValue / best;

                if (secondValue >= SecondLaneMinBaseValue
                    && secondRatio >= SecondLaneMinRelativeValue
                    && dark >= SecondLaneMinExplorableUnknownFrac)
                    desired = 2;

                // A third lane is never requested unless lane two already qualified. This keeps the
                // concurrency curve monotonic and prevents a very dark map from skipping a weak
                // second objective just because a third entry happens to look acceptable alone.
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
            }

            // Spec §28 — coverage sizing on top of the value-driven count: never fewer scouts than
            // distinct reachable unexplored regions (capped), plus one dedicated lane when Refresh
            // pressure alone is high enough that an Explore-only portfolio would let the known
            // picture rot.
            int regions = CountFrontierRegions(snap?.MapKnowledge?.Frontier);
            desired = Mathf.Max(desired, Mathf.Min(hardCap, regions));

            float refresh = ReconIntelSnapshotRegistry.StalePressure(snap);
            if (refresh >= AiConfigV2.reconDemandRefreshLaneThreshold && desired < hardCap)
                desired += 1;

            return Mathf.Min(hardCap, desired);
        }

        // Coarse count of connected reachable unexplored regions: frontier hexes within
        // reconDemandRegionMergeDistance of each other are one region. Frontier is bounded so the
        // O(n^2) flood is cheap.
        internal static int CountFrontierRegions(IReadOnlyList<FrontierHexSnapshot> frontier)
        {
            if (frontier == null || frontier.Count == 0)
                return 0;
            var unassigned = new HashSet<HexCoord>();
            foreach (FrontierHexSnapshot f in frontier)
                unassigned.Add(f.Hex);

            int regions = 0;
            var stack = new Stack<HexCoord>();
            while (unassigned.Count > 0)
            {
                regions++;
                HexCoord seed = default;
                foreach (HexCoord h in unassigned) { seed = h; break; }
                unassigned.Remove(seed);
                stack.Push(seed);
                while (stack.Count > 0)
                {
                    HexCoord cur = stack.Pop();
                    var near = new List<HexCoord>();
                    foreach (HexCoord other in unassigned)
                        if (HexGridMath.Distance(cur, other) <= AiConfigV2.reconDemandRegionMergeDistance)
                            near.Add(other);
                    foreach (HexCoord n in near) { unassigned.Remove(n); stack.Push(n); }
                }
            }
            return regions;
        }

        public static string Explain(WorldSnapshot snap, IReadOnlyList<ReconObjective> runnable)
        {
            float first = runnable != null && runnable.Count > 0 ? runnable[0].BaseValue : 0f;
            float second = runnable != null && runnable.Count > 1 ? runnable[1].BaseValue : 0f;
            float third = runnable != null && runnable.Count > 2 ? runnable[2].BaseValue : 0f;
            float secondRatio = first > 0f ? second / first : 0f;
            float thirdRatio = first > 0f ? third / first : 0f;
            float dark = snap?.MapKnowledge?.ExplorableUnknownFrac ?? 0f;
            int regions = CountFrontierRegions(snap?.MapKnowledge?.Frontier);
            float refresh = ReconIntelSnapshotRegistry.StalePressure(snap);
            return $"desired={DesiredTotal(snap, runnable)} hard={HardCap} "
                + $"best={first:0.0} second={second:0.0} r2={secondRatio:0.00} "
                + $"third={third:0.0} r3={thirdRatio:0.00} dark={dark:0.00} regions={regions} refresh={refresh:0.00}";
        }
    }
}
