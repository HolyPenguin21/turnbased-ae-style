using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using UnityEngine;

namespace Game.Setup
{
    // Pure helpers of the existing content owner, separated for managed algorithm tests.
    public partial class CitadelSetupController
    {
        private const float CityRuinsGarrisonChance = 0.3f;

        // Pure proposal phase within the existing content owner. Ruins count as potential
        // enemy concentrations with their unchanged garrison probability, never as fixed armies.
        public static List<HexCoord> PlanNeutralArmyHexes(IReadOnlyList<HexCoord> candidates,
            IReadOnlyList<HexCoord> citadels, IReadOnlyList<HexCoord> ruins, int target, int radius)
        {
            var proposals = new List<List<HexCoord>>();
            var scores = new List<float>();
            int bestCount = -1; float bestScore = float.PositiveInfinity;
            for (int attempt = 0; attempt < 128; attempt++)
            {
                var plan = new List<HexCoord>();
                foreach (var hex in PickRandomDistinct(candidates.ToList(), candidates.Count))
                {
                    if (plan.Count >= target) break;
                    if (plan.Any(p => HexGridMath.Distance(p, hex) < 2)) continue;
                    plan.Add(hex);
                }
                if (plan.Count < bestCount) continue;
                if (plan.Count > bestCount)
                { bestCount = plan.Count; bestScore = float.PositiveInfinity; proposals.Clear(); scores.Clear(); }
                float score = NeutralLayoutScore(plan, candidates, citadels, ruins, radius);
                proposals.Add(plan); scores.Add(score); bestScore = Mathf.Min(bestScore, score);
            }
            var acceptable = Enumerable.Range(0, proposals.Count).Where(i => scores[i] <= bestScore + .04f).ToList();
            return proposals[acceptable[Random.Range(0, acceptable.Count)]];
        }

        private static float NeutralLayoutScore(List<HexCoord> plan, IReadOnlyList<HexCoord> candidates,
            IReadOnlyList<HexCoord> citadels, IReadOnlyList<HexCoord> ruins, int radius)
        {
            if (plan.Count == 0) return 0;
            float scale = Mathf.Max(1, radius);
            float score = candidates.Average(h => (float)plan.Min(p => HexGridMath.Distance(p, h))) / scale;
            if (plan.Count > 1)
                score += 1.5f * plan.Average(h => 1f / plan.Where(p => !p.Equals(h)).Min(p => HexGridMath.Distance(p, h)));
            if (citadels.Count > 1)
            {
                var pressure = citadels.Select(c => plan.Sum(p => 1f / Mathf.Max(1, HexGridMath.Distance(c, p)))).ToList();
                score += (pressure.Max() - pressure.Min()) / Mathf.Max(.001f, pressure.Max());
            }
            if (ruins.Count > 0)
                score += CityRuinsGarrisonChance * plan.Average(h => 1f / Mathf.Max(1, ruins.Min(r => HexGridMath.Distance(h, r))));
            return score;
        }

        // Shared by content proposals and events — up to `count` distinct entries
        // picked without replacement from `pool` (fewer than `count` if the pool runs out).
        private static List<T> PickRandomDistinct<T>(List<T> pool, int count)
        {
            var working = new List<T>(pool);
            var result = new List<T>(Mathf.Min(count, working.Count));
            while (result.Count < count && working.Count > 0)
            {
                int index = Random.Range(0, working.Count);
                result.Add(working[index]);
                working.RemoveAt(index);
            }
            return result;
        }
    }
}
