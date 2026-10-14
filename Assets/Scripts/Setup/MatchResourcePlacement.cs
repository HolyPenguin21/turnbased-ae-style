using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using UnityEngine;

namespace Game.Setup
{
    // Plans only: no registry writes or icons while trying alternatives. CitadelSetupController
    // commits the chosen result once. Random proposals and random selection among similarly
    // good proposals preserve variety without fixed sectors, rings or player-owned far points.
    public static class MatchResourcePlacement
    {
        private const int LayoutAttempts = 128;
        private const float AcceptableScoreWindow = 0.35f;

        public sealed class FarLayout
        {
            public readonly List<HexCoord> Hexes;
            public readonly List<HexCoord> TechHexes;
            public FarLayout(List<HexCoord> hexes, List<HexCoord> techHexes)
            { Hexes = hexes; TechHexes = techHexes; }
        }

        public sealed class Layout
        {
            public readonly List<List<HexCoord>> NearHexes;
            public readonly FarLayout Far;
            public Layout(List<List<HexCoord>> nearHexes, FarLayout far)
            { NearHexes = nearHexes; Far = far; }
        }

        public static Layout Plan(HexMap map, IReadOnlyList<HexCoord> citadels, int farTarget, int techTarget)
        {
            var fields = citadels.Select(c => HexPathfinder.FindCosts(map, new[] { c })).ToList();
            Layout best = null;
            int bestNear = -1;
            // A near point on ring 3 can exclude a far candidate on ring 4. Re-plan BOTH groups
            // before stamping anything, with a bounded retry and no relaxed spacing fallback.
            for (int attempt = 0; attempt < 32; attempt++)
            {
                var near = PlanNear(map, citadels);
                int nearCount = near.Sum(n => n.Count);
                if (nearCount < bestNear) continue;
                FarLayout far = PlanFar(map, citadels, near.SelectMany(n => n).ToList(),
                    farTarget, techTarget, fields);
                if (nearCount > bestNear || best == null || far.Hexes.Count > best.Far.Hexes.Count)
                { best = new Layout(near, far); bestNear = nearCount; }
                if (bestNear == citadels.Count * 3 && best.Far.Hexes.Count == farTarget) break;
            }
            return best;
        }

        private sealed class Candidate
        {
            public HexCoord Hex;
            public int CenterDistance;
            public int[] Costs;
            public float CompetitionGap;
        }

        // Preserve the near rules, but retry the whole plan instead of permanently skipping a
        // type after an unlucky early pick. Randomise player order so overlapping bands do not
        // always favour the first player. If no full plan is found, return the largest sampled
        // legal result; callers report the shortfall without weakening the spacing rules.
        public static List<List<HexCoord>> PlanNear(HexMap map, IReadOnlyList<HexCoord> citadels)
        {
            var bands = citadels.Select(c => map.AllCoords.Where(h => map.CanEnter(h)
                && HexResourceBonusRegistry.GetBonus(h) == null
                && HexGridMath.Distance(c, h) >= 2 && HexGridMath.Distance(c, h) <= 3
                && citadels.All(other => HexGridMath.Distance(other, h) >= 2)).ToList()).ToList();
            List<List<HexCoord>> best = null;
            int bestCount = -1;
            for (int attempt = 0; attempt < LayoutAttempts; attempt++)
            {
                var plan = citadels.Select(c => new List<HexCoord>()).ToList();
                var occupied = new List<HexCoord>();
                var order = Enumerable.Range(0, citadels.Count).ToList();
                Shuffle(order);
                foreach (int player in order)
                {
                    var options = new List<HexCoord>(bands[player]);
                    Shuffle(options);
                    foreach (HexCoord h in options)
                    {
                        if (occupied.Any(p => HexGridMath.Distance(p, h) < 2)) continue;
                        plan[player].Add(h);
                        occupied.Add(h);
                        if (plan[player].Count == 3) break;
                    }
                }
                if (occupied.Count > bestCount) { best = plan; bestCount = occupied.Count; }
                if (bestCount == citadels.Count * 3) break;
            }
            return best;
        }

        public static FarLayout PlanFar(HexMap map, IReadOnlyList<HexCoord> citadels,
            IReadOnlyList<HexCoord> nearHexes, int target, int techTarget)
        {
            var fields = citadels.Select(c => HexPathfinder.FindCosts(map, new[] { c })).ToList();
            return PlanFar(map, citadels, nearHexes, target, techTarget, fields);
        }

        private static FarLayout PlanFar(HexMap map, IReadOnlyList<HexCoord> citadels,
            IReadOnlyList<HexCoord> nearHexes, int target, int techTarget,
            IReadOnlyList<Dictionary<HexCoord, int>> fields)
        {
            if (target <= 0 || citadels.Count == 0)
                return new FarLayout(new List<HexCoord>(), new List<HexCoord>());

            // One forward terrain-cost field per actual citadel, reused across all proposals.
            // No second movement-cost rule and no assumed hero speed or AI knowledge here.
            var candidates = new List<Candidate>();
            var center = new HexCoord(0, 0);
            foreach (HexCoord h in map.AllCoords)
            {
                if (!map.CanEnter(h) || HexResourceBonusRegistry.GetBonus(h) != null
                    || citadels.Any(c => HexGridMath.Distance(c, h) < 4)
                    || nearHexes.Any(n => HexGridMath.Distance(n, h) < 2)
                    || fields.Any(f => !f.ContainsKey(h))) continue;
                int[] costs = fields.Select(f => f[h]).ToArray();
                int[] sorted = costs.OrderBy(c => c).ToArray();
                candidates.Add(new Candidate
                {
                    Hex = h, CenterDistance = HexGridMath.Distance(center, h), Costs = costs,
                    CompetitionGap = sorted.Length < 2 ? 0f
                        : (sorted[1] - sorted[0]) / (float)Mathf.Max(4, sorted[1]),
                });
            }

            var proposals = new List<List<Candidate>>();
            var scores = new List<float>();
            int bestCount = -1;
            float bestScore = float.PositiveInfinity;
            for (int attempt = 0; attempt < LayoutAttempts; attempt++)
            {
                var options = new List<Candidate>(candidates);
                Shuffle(options);
                var plan = new List<Candidate>();
                foreach (Candidate candidate in options)
                {
                    if (plan.Any(p => HexGridMath.Distance(p.Hex, candidate.Hex) < 2)) continue;
                    plan.Add(candidate);
                    if (plan.Count == target) break;
                }
                if (plan.Count < bestCount) continue;
                if (plan.Count > bestCount)
                { proposals.Clear(); scores.Clear(); bestCount = plan.Count; bestScore = float.PositiveInfinity; }
                float score = Score(plan, citadels.Count, map.FieldRadius, techTarget);
                proposals.Add(plan);
                scores.Add(score);
                bestScore = Mathf.Min(bestScore, score);
            }

            // Count has priority over quality. Within a bounded quality window choose randomly,
            // rather than always returning the mathematical optimum (often a repetitive shape).
            var acceptable = Enumerable.Range(0, proposals.Count)
                .Where(i => scores[i] <= bestScore + AcceptableScoreWindow).ToList();
            List<Candidate> chosen = proposals[acceptable[Random.Range(0, acceptable.Count)]];
            return new FarLayout(chosen.Select(c => c.Hex).ToList(),
                SelectTech(chosen, techTarget).Select(c => c.Hex).ToList());
        }

        private static List<Candidate> SelectTech(List<Candidate> plan, int target)
        {
            // Distance to the map centre is primary. Equal-radius ties prefer access by two
            // players and separation from other Tech points. Remaining ties keep the proposal's
            // random order, never coordinate order.
            var remaining = plan.OrderBy(c => c.CenterDistance).ToList();
            var selected = new List<Candidate>();
            while (selected.Count < target && remaining.Count > 0)
            {
                int distance = remaining[0].CenterDistance;
                Candidate next = remaining.Where(c => c.CenterDistance == distance)
                    .OrderBy((Candidate c) => c.CompetitionGap + (selected.Count == 0 ? 0f
                        : 0.25f / selected.Min((Candidate p) => HexGridMath.Distance(c.Hex, p.Hex)))).First();
                selected.Add(next);
                remaining.Remove(next);
            }
            return selected;
        }

        private static float Score(List<Candidate> plan, int players, int radius, int techTarget)
        {
            if (plan.Count == 0) return 0f;
            float scale = Mathf.Max(1, radius);
            int[] nearest = Enumerable.Range(0, players).Select(p => plan.Min(c => c.Costs[p])).ToArray();
            // Every player gets an expansion opportunity. Penalise the worst approach and the
            // difference between players, with a softer preference for the map's interior.
            float score = (2f * nearest.Max() + 1.5f * (nearest.Max() - nearest.Min())
                + 0.5f * nearest.Average(c => (float)c)) / scale;
            score += 0.8f * plan.Average(c => (float)c.CenterDistance) / scale;
            score += plan.Average(c => c.CompetitionGap);
            if (plan.Count > 1)
                score += 0.8f * plan.Average((Candidate c) => 1f / plan.Where(o => o != c)
                    .Min((Candidate o) => HexGridMath.Distance(c.Hex, o.Hex)));

            List<Candidate> tech = SelectTech(plan, techTarget);
            if (tech.Count > 0)
            {
                int[] techCosts = Enumerable.Range(0, players).Select(p => tech.Min(c => c.Costs[p])).ToArray();
                score += 0.75f * (techCosts.Max() + techCosts.Max() - techCosts.Min()) / scale;
                score += tech.Average(c => c.CompetitionGap);
            }
            return score;
        }

        private static void Shuffle<T>(List<T> items)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }
    }
}
