using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Terrain;
using UnityEngine;

namespace Game.Setup
{
    public static class CitadelPlacementPlanner
    {
        public static IReadOnlyList<HexCoord> Plan(HexMap map, ICitadelPlacementRules rules,
            IReadOnlyList<HexCoord> ruins, int playerCount, IMapGenerationRandom random)
        {
            var picked = new List<HexCoord>();
            List<HexCoord> eligible = GetEdgeEligibleHexes(map, rules, ruins);
            if (eligible.Count == 0 || playerCount == 0) return picked;

            List<HexCoord>[] sectors = BucketByAngleAroundCenter(eligible, playerCount, random);

            // Scales with map size (Small radius 5 → Huge radius 8) so players never spawn
            // adjacent on a small field, even though angular sectors alone don't guarantee that —
            // two hexes straddling a (randomly rotated) sector boundary can be near neighbors.
            int minDistance = Mathf.Max(1, map.FieldRadius);

            for (int i = 0; i < playerCount && eligible.Count > 0; i++)
            {
                // A sector can come up empty if the edge band is sparse there (e.g. corner/City
                // ruins exclusions ate every candidate in it) — fall back to any still-unused
                // eligible hex, but never skip the minimum-separation check while doing so.
                List<HexCoord> pool = sectors[i].Count > 0 ? sectors[i] : eligible;
                HexCoord pick = PickFarEnough(pool, picked, minDistance, random)
                    ?? PickFarEnough(eligible, picked, minDistance, random)
                    ?? PickFarthestFrom(pool.Count > 0 ? pool : eligible, picked, random);

                eligible.Remove(pick);
                foreach (List<HexCoord> sector in sectors)
                    sector.Remove(pick);
                picked.Add(pick);
            }
            return picked;
        }

        public static IEnumerable<HexCoord> PlacementOptions(HexMap map, HexCoord candidate, ISet<HexCoord> reserved = null)
            => HexGridMath.Neighbors(candidate).Prepend(candidate)
                .Where(hex => map.CanEnter(hex) && reserved?.Contains(hex) != true);

        // Random hex from `pool` that keeps at least `minDistance` from every already-picked
        // starting hex — null if none in the pool qualifies, so the caller can widen the search
        // instead of silently placing players too close together.
        private static HexCoord? PickFarEnough(List<HexCoord> pool, List<HexCoord> picked, int minDistance, IMapGenerationRandom random)
        {
            if (pool.Count == 0)
                return null;
            List<HexCoord> candidates = picked.Count == 0
                ? pool
                : pool.Where(c => picked.All(p => HexGridMath.Distance(c, p) >= minDistance)).ToList();
            return candidates.Count == 0 ? (HexCoord?)null : candidates[random.Range(0, candidates.Count)];
        }

        // Last resort when nothing in `pool` (or the whole eligible set) meets minDistance — e.g.
        // an unusually cramped edge band — pick whichever candidate maximises its distance to the
        // nearest already-picked player, rather than a blind random pick with no separation at all.
        private static HexCoord PickFarthestFrom(List<HexCoord> pool, List<HexCoord> picked, IMapGenerationRandom random)
        {
            if (picked.Count == 0)
                return pool[random.Range(0, pool.Count)];
            return pool.OrderByDescending(c => picked.Min(p => HexGridMath.Distance(c, p))).First();
        }

        // Buckets `coords` into `sectorCount` angular slices around the field's (0,0) centre —
        // used by starting-hex assignment above. Resource placement separately evaluates
        // random whole-map layouts against the actual chosen citadels. The
        // slice boundaries themselves are rotated by a random offset each call so which exact
        // hexes fall together isn't identical from one game to the next, on top of the random
        // pick each caller then makes within a bucket.
        private static List<HexCoord>[] BucketByAngleAroundCenter(List<HexCoord> coords, int sectorCount, IMapGenerationRandom random)
        {
            var buckets = new List<HexCoord>[sectorCount];
            for (int i = 0; i < sectorCount; i++)
                buckets[i] = new List<HexCoord>();

            float sectorWidth = 360f / sectorCount;
            float angleOffset = random.Range(0f, sectorWidth);

            foreach (HexCoord coord in coords)
            {
                float adjusted = (AngleAroundCenterDegrees(coord) - angleOffset + 360f) % 360f;
                int sector = Mathf.Clamp((int)(adjusted / sectorWidth), 0, sectorCount - 1);
                buckets[sector].Add(coord);
            }

            return buckets;
        }

        // Angle (0-360) of a hex's world position around the field's (0,0) centre — outerRadius
        // doesn't affect the angle itself, only the distance, so 1f is fine as a stand-in.
        private static float AngleAroundCenterDegrees(HexCoord coord)
        {
            Vector3 world = HexGridMath.AxialToWorld(coord.Q, coord.R, 1f);
            return (Mathf.Atan2(world.z, world.x) * Mathf.Rad2Deg + 360f) % 360f;
        }

        private static List<HexCoord> GetEdgeEligibleHexes(HexMap map, ICitadelPlacementRules rules,
            IReadOnlyList<HexCoord> cityRuinsHexes)
        {
            List<HexCoord> corners = GetFieldCorners(map.FieldRadius);
            var origin = new HexCoord(0, 0);

            var result = new List<HexCoord>();
            foreach (HexCoord coord in map.AllCoords)
            {
                if (!map.CanEnter(coord)) continue;
                int edgeDistance = map.FieldRadius - HexGridMath.Distance(origin, coord);
                if (edgeDistance > rules.MaxEdgeDistance)
                    continue;

                // Off the corner tiles specifically (project owner's own spec, 2026-08-22 —
                // "не прям в угловых участках карты"), and kept at least
                // rules.MinCityRuinsDistance hexes from every "City ruins" hex so a
                // fresh citadel never opens the game already standing next to a garrisoned
                // outpost.
                if (corners.Any(corner => HexGridMath.Distance(coord, corner) <= rules.CornerExclusionRadius))
                    continue;
                if (cityRuinsHexes.Any(ruin => HexGridMath.Distance(coord, ruin) < rules.MinCityRuinsDistance))
                    continue;

                result.Add(coord);
            }
            return result;
        }

        // The field's own 6 corner hexes (a hexagon-of-hexes has 6, not 4) — anchor points for
        // cornerExclusionRadius above. Every candidate already comes from map.AllCoords (see
        // GetEdgeEligibleHexes), so unlike the old rectangular corners these are always real,
        // on-map hexes too.
        private static List<HexCoord> GetFieldCorners(int radius)
        {
            var result = new List<HexCoord>();
            foreach ((int dq, int dr) in HexGridMath.NeighborDirectionsByEdge)
                result.Add(new HexCoord(dq * radius, dr * radius));
            return result;
        }

    }
}
