using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Economy;
using Game.Terrain;
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

        public readonly struct Point
        {
            public readonly HexCoord Hex;
            public readonly ResourceType Type;
            public readonly bool Tech;
            public Point(HexCoord hex, ResourceType type, bool tech = false)
            { Hex = hex; Type = type; Tech = tech; }
            public ResourceYields Yields => new ResourceYields {
                human = Type == ResourceType.Human ? 1 : 0,
                energy = Type == ResourceType.Energy ? 1 : 0,
                materials = Type == ResourceType.Materials ? 1 : 0, tech = Tech ? 1 : 0 };
        }

        public sealed class FarLayout
        {
            public readonly List<Point> Points;
            public FarLayout(List<Point> points) { Points = points; }
            public List<HexCoord> Hexes => Points.Select(p => p.Hex).ToList();
            public List<HexCoord> TechHexes => Points.Where(p => p.Tech).Select(p => p.Hex).ToList();
        }

        public sealed class Layout
        {
            public readonly List<List<Point>> Near;
            public readonly FarLayout Far;
            public Layout(List<List<Point>> near, FarLayout far) { Near = near; Far = far; }
            public List<List<HexCoord>> NearHexes => Near.Select(n => n.Select(p => p.Hex).ToList()).ToList();
        }

        public static Layout Plan(HexMap map, IReadOnlyList<HexCoord> citadels, MatchResourceBudget budget)
        {
            var fields = citadels.Select(c => HexPathfinder.FindCosts(map, new[] { c })).ToList();
            var candidates = Candidates(map, citadels, fields);
            var proposals = new List<(Layout Plan, float Score)>();
            int bestNear = -1, bestFar = -1;
            for (int attempt = 0; attempt < LayoutAttempts; attempt++)
            {
                // Sample the COMPLETE layout before quality selection. Selecting the best far
                // shape first can systematically suppress shapes that allow all near guarantees.
                bool farFirst = Random.value < .5f;
                var far = farFirst ? SampleFar(candidates, budget.FarTarget) : null;
                var near = PlanNear(map, citadels, fields, far?.Select(c => c.Hex).ToList() ?? new List<HexCoord>(), bestNear);
                if (far == null)
                {
                    var nearHexes = near.SelectMany(n => n).ToList();
                    far = SampleFar(candidates.Where(c => nearHexes.All(h => HexGridMath.Distance(c.Hex, h) >= 2)).ToList(), budget.FarTarget);
                }
                int nearCount = near.Sum(n => n.Count);
                if (nearCount < bestNear || (nearCount == bestNear && far.Count < bestFar)) continue;
                if (nearCount > bestNear || far.Count > bestFar)
                { bestNear = nearCount; bestFar = far.Count; proposals.Clear(); }
                var typedNear = near.Select(n => {
                    var types = BuildTypes(new MatchResourceBudget(3, 0, ResourceSpecialization.Balanced), n.Count);
                    return n.Select((h, i) => new Point(h, types[i])).ToList();
                }).ToList();
                foreach (var proposal in TypedFarProposals(far, budget, citadels.Count, map.FieldRadius))
                    proposals.Add((new Layout(typedNear, proposal.Plan), proposal.Score));
            }
            return Choose(proposals);
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
        private static List<List<HexCoord>> PlanNear(HexMap map, IReadOnlyList<HexCoord> citadels,
            IReadOnlyList<Dictionary<HexCoord, int>> fields, IReadOnlyList<HexCoord> farHexes, int minimumNear)
        {
            var bands = citadels.Select((c, player) => map.AllCoords.Where(h => map.CanEnter(h) && fields[player].ContainsKey(h)
                && HexResourceBonusRegistry.GetBonus(h) == null && farHexes.All(f => HexGridMath.Distance(f, h) >= 2)
                && HexGridMath.Distance(c, h) >= 2 && HexGridMath.Distance(c, h) <= 3
                && citadels.All(other => HexGridMath.Distance(other, h) >= 2)).ToList()).ToList();
            foreach (var band in bands) Shuffle(band);
            var plan = citadels.Select(_ => new List<HexCoord>()).ToList();
            var best = citadels.Select(_ => new List<HexCoord>()).ToList();
            var occupied = new List<HexCoord>();
            var last = Enumerable.Repeat(-1, citadels.Count).ToArray();
            var closed = new bool[citadels.Count];
            var order = Enumerable.Range(0, citadels.Count).ToList(); Shuffle(order);
            int bestCount = 0, nodes = 0;
            bool Search()
            {
                if (++nodes > 4096) return false;
                if (occupied.Count > bestCount)
                { bestCount = occupied.Count; best = plan.Select(n => new List<HexCoord>(n)).ToList(); }
                if (bestCount == citadels.Count * 3) return true;
                // Most-constrained player first; random band/order ties preserve variety.
                int upper = occupied.Count;
                int player = -1; List<int> options = null; float tightest = float.PositiveInfinity;
                foreach (int p in order)
                {
                    if (closed[p] || plan[p].Count == 3) continue;
                    var available = Enumerable.Range(last[p] + 1, bands[p].Count - last[p] - 1)
                        .Where(i => occupied.All(h => HexGridMath.Distance(h, bands[p][i]) >= 2)).ToList();
                    upper += Mathf.Min(3 - plan[p].Count, available.Count);
                    float freedom = available.Count / (float)(3 - plan[p].Count);
                    if (freedom < tightest) { tightest = freedom; player = p; options = available; }
                }
                if (player < 0 || upper < minimumNear || upper <= bestCount) return false;
                int previous = last[player];
                foreach (int i in options)
                {
                    var h = bands[player][i]; plan[player].Add(h); occupied.Add(h); last[player] = i;
                    if (Search()) return true;
                    occupied.RemoveAt(occupied.Count - 1); plan[player].RemoveAt(plan[player].Count - 1);
                    if (nodes > 4096) break;
                }
                last[player] = previous;
                // Also sample legal partial plans when one player's band cannot be filled.
                closed[player] = true; Search(); closed[player] = false;
                return false;
            }
            Search();
            return best;
        }

        public static FarLayout PlanFar(HexMap map, IReadOnlyList<HexCoord> citadels,
            IReadOnlyList<HexCoord> nearHexes, MatchResourceBudget budget)
        {
            var fields = citadels.Select(c => HexPathfinder.FindCosts(map, new[] { c })).ToList();
            var candidates = Candidates(map, citadels, fields)
                .Where(c => nearHexes.All(h => HexGridMath.Distance(c.Hex, h) >= 2)).ToList();
            var proposals = new List<(FarLayout Plan, float Score)>(); int bestCount = -1;
            for (int attempt = 0; attempt < LayoutAttempts; attempt++)
            {
                var far = SampleFar(candidates, budget.FarTarget);
                if (far.Count < bestCount) continue;
                if (far.Count > bestCount) { bestCount = far.Count; proposals.Clear(); }
                proposals.AddRange(TypedFarProposals(far, budget, citadels.Count, map.FieldRadius));
            }
            return Choose(proposals);
        }

        private static List<Candidate> Candidates(HexMap map, IReadOnlyList<HexCoord> citadels,
            IReadOnlyList<Dictionary<HexCoord, int>> fields)
        {
            // One forward terrain-cost field per actual citadel, reused across all proposals.
            // No second movement-cost rule and no assumed hero speed or AI knowledge here.
            var candidates = new List<Candidate>();
            var center = new HexCoord(0, 0);
            foreach (HexCoord h in map.AllCoords)
            {
                if (citadels.Count == 0 || !map.CanEnter(h) || HexResourceBonusRegistry.GetBonus(h) != null
                    || citadels.Any(c => HexGridMath.Distance(c, h) < 4)
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

            return candidates;
        }

        private static List<Candidate> SampleFar(List<Candidate> candidates, int target)
        {
            var options = new List<Candidate>(candidates); Shuffle(options);
            var plan = new List<Candidate>();
            foreach (var candidate in options)
            {
                if (plan.Count >= target) break;
                if (plan.Any(p => HexGridMath.Distance(p.Hex, candidate.Hex) < 2)) continue;
                plan.Add(candidate);
            }
            return plan;
        }

        private static List<(FarLayout Plan, float Score)> TypedFarProposals(List<Candidate> far,
            MatchResourceBudget budget, int players, int radius)
        {
            var proposals = new List<(FarLayout Plan, float Score)>();
            var tech = new HashSet<HexCoord>(SelectTech(far, budget.TechTarget).Select(c => c.Hex));
            for (int assignment = 0; assignment < 12; assignment++)
            {
                var types = BuildTypes(budget, far.Count);
                var typed = new FarLayout(far.Select((c, i) => new Point(c.Hex, types[i], tech.Contains(c.Hex))).ToList());
                proposals.Add((typed, Score(far, typed.Points, players, radius)));
            }
            return proposals;
        }

        private static T Choose<T>(List<(T Plan, float Score)> proposals)
        {
            float best = proposals.Min(p => p.Score);
            var acceptable = proposals.Where(p => p.Score <= best + AcceptableScoreWindow).ToList();
            return acceptable[Random.Range(0, acceptable.Count)].Plan;
        }

        private static List<ResourceType> BuildTypes(MatchResourceBudget budget, int count)
        {
            ResourceYields quota = budget.OrdinaryCounts(count, Random.Range(0, 3));
            var types = new List<ResourceType>();
            types.AddRange(Enumerable.Repeat(ResourceType.Human, quota.human));
            types.AddRange(Enumerable.Repeat(ResourceType.Energy, quota.energy));
            types.AddRange(Enumerable.Repeat(ResourceType.Materials, quota.materials));
            Shuffle(types); return types;
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

        private static float Score(List<Candidate> plan, IReadOnlyList<Point> points, int players, int radius)
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

            // Far types are evaluated separately: near guarantees must never hide a distant
            // singleton monopoly. Each group considers nearest access AND shared opportunities.
            foreach (var group in points.GroupBy(p => p.Type))
            {
                var hexes = new HashSet<HexCoord>(group.Select(p => p.Hex));
                score += AccessScore(plan.Where(c => hexes.Contains(c.Hex)).ToList(), players, scale);
            }
            var techHexes = new HashSet<HexCoord>(points.Where(p => p.Tech).Select(p => p.Hex));
            if (techHexes.Count > 0)
                score += AccessScore(plan.Where(c => techHexes.Contains(c.Hex)).ToList(), players, scale);
            return score;
        }

        private static float AccessScore(List<Candidate> group, int players, float scale)
        {
            int[] nearest = Enumerable.Range(0, players).Select(p => group.Min(c => c.Costs[p])).ToArray();
            float[] opportunity = Enumerable.Range(0, players)
                .Select(p => group.Sum(c => 1f / Mathf.Max(1, c.Costs[p]))).ToArray();
            float balance = (opportunity.Max() - opportunity.Min()) / Mathf.Max(.001f, opportunity.Max());
            return (nearest.Max() + 2f * (nearest.Max() - nearest.Min())) / scale
                + balance + group.Average(c => c.CompetitionGap);
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
