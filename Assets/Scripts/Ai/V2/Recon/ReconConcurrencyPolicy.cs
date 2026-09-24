using System.Collections.Generic;
using Game.HexGrid;
using UnityEngine;

namespace Game.Ai.V2
{
    // Desired Recon capacity is 0..K. Coverage facts may justify a second lane only when a second
    // runnable objective already has material value on the canonical TaskScore scale.
    internal static class ReconConcurrencyPolicy
    {
        internal const float SecondLaneMinRelativeValue =
            AiConfigV2.reconConcurrencySecondLaneMinRelValue;
        internal const float SecondLaneMinExplorableUnknownFrac =
            AiConfigV2.reconConcurrencySecondLaneMinDarkFrac;

        public static int HardCap => Mathf.Max(0, AiConfigV2.maxConcurrentReconExecutions);

        internal enum ReconCoverageClass { Combined, Observation, GroundTraversal }

        public static int DesiredTotal(WorldSnapshot snap, IReadOnlyList<ReconObjective> runnable) =>
            DesiredForClass(snap, runnable, ReconCoverageClass.Combined);

        public static int DesiredForClass(WorldSnapshot snap, IReadOnlyList<ReconObjective> runnable,
            ReconCoverageClass klass)
        {
            int hardCap = HardCap;
            if (hardCap == 0 || runnable == null || runnable.Count == 0
                || !HasMaterialValue(runnable[0]))
                return 0;

            int desired = 1;
            if (hardCap < 2 || runnable.Count < 2 || !HasMaterialValue(runnable[1]))
                return desired;

            float best = Mathf.Max(AiConfigV2.allocatorSliceEpsilon,
                runnable[0]?.BaseValue ?? 0f);
            float secondValue = Mathf.Max(0f, runnable[1]?.BaseValue ?? 0f);
            if (secondValue / best < SecondLaneMinRelativeValue)
                return desired;

            bool frontierCoverage = klass != ReconCoverageClass.Observation
                && CountFrontierRegions(snap?.MapKnowledge?.Frontier) >= 2;
            bool refreshCoverage = klass != ReconCoverageClass.GroundTraversal
                && ReconIntelSnapshotRegistry.StalePressure(snap)
                    >= AiConfigV2.reconDemandRefreshLaneThreshold;
            bool darkCoverage = (snap?.MapKnowledge?.ExplorableUnknownFrac ?? 0f)
                >= SecondLaneMinExplorableUnknownFrac;

            if (frontierCoverage || refreshCoverage || darkCoverage)
                desired = 2;
            return Mathf.Min(desired, Mathf.Min(hardCap, runnable.Count));
        }

        private static bool HasMaterialValue(ReconObjective objective) => objective != null
            && DemandUrgencyPolicy.NormalizedWorldValue(objective.BaseValue)
                > AiConfigV2.allocatorSliceEpsilon;

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
                        if (HexGridMath.Distance(cur, other)
                            <= AiConfigV2.reconDemandRegionMergeDistance)
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
            float ratio = first > 0f ? second / first : 0f;
            float dark = snap?.MapKnowledge?.ExplorableUnknownFrac ?? 0f;
            int regions = CountFrontierRegions(snap?.MapKnowledge?.Frontier);
            float refresh = ReconIntelSnapshotRegistry.StalePressure(snap);
            return $"desired={DesiredTotal(snap, runnable)} hard={HardCap} "
                + $"best={first:0.0} second={second:0.0} r2={ratio:0.00} "
                + $"dark={dark:0.00} regions={regions} refresh={refresh:0.00}";
        }
    }
}
