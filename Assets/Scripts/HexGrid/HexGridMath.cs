using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.HexGrid
{
    // Axial-coordinate helpers for a flat-top hex grid (matches HexTileMeshGenerator's
    // flat-top orientation).
    public static class HexGridMath
    {
        // Ordered so direction[i] is the neighbour across the edge between hex corners i and
        // (i+1)%6 (corner i sits at angle 60*i degrees) — required for BuildOuterBoundary to
        // pair the right edge with the right corners.
        public static readonly (int dq, int dr)[] NeighborDirectionsByEdge =
        {
            (1, 0), (0, 1), (-1, 1), (-1, 0), (0, -1), (1, -1)
        };

        public static Vector3 AxialToWorld(int q, int r, float outerRadius)
        {
            float x = outerRadius * 1.5f * q;
            float z = outerRadius * Mathf.Sqrt(3f) * (r + q * 0.5f);
            return new Vector3(x, 0f, z);
        }

        // Inverse of AxialToWorld: which hex contains this world point. Raw inversion gives
        // fractional axial coordinates, so this snaps to the nearest actual hex via the
        // standard cube-coordinate rounding algorithm (naively rounding q and r independently
        // gives the wrong hex near cell boundaries).
        public static HexCoord WorldToAxial(Vector3 worldPos, float outerRadius)
        {
            float q = worldPos.x / (1.5f * outerRadius);
            float r = worldPos.z / (Mathf.Sqrt(3f) * outerRadius) - q * 0.5f;
            float s = -q - r;

            float rq = Mathf.Round(q);
            float rr = Mathf.Round(r);
            float rs = Mathf.Round(s);

            float qDiff = Mathf.Abs(rq - q);
            float rDiff = Mathf.Abs(rr - r);
            float sDiff = Mathf.Abs(rs - s);

            if (qDiff > rDiff && qDiff > sDiff)
                rq = -rr - rs;
            else if (rDiff > sDiff)
                rr = -rq - rs;

            return new HexCoord(Mathf.RoundToInt(rq), Mathf.RoundToInt(rr));
        }

        // The 6 actual neighbor coordinates of `cell` — several callers were re-deriving this by
        // hand from NeighborDirectionsByEdge every time they needed it; existence on the map
        // isn't checked here (callers already have their own map/registry to validate against).
        public static IEnumerable<HexCoord> Neighbors(HexCoord cell)
        {
            foreach ((int dq, int dr) in NeighborDirectionsByEdge)
                yield return new HexCoord(cell.Q + dq, cell.R + dr);
        }

        // Standard axial-coordinate hex distance (number of hex steps between two cells).
        public static int Distance(HexCoord a, HexCoord b)
        {
            int dq = a.Q - b.Q;
            int dr = a.R - b.R;
            return (Mathf.Abs(dq) + Mathf.Abs(dq + dr) + Mathf.Abs(dr)) / 2;
        }

        // The real outer boundary of a set of hex cells, tracing actual hex edges — not an
        // idealised shape. An edge belongs to the boundary when the cell across it isn't also
        // in the set (including "doesn't exist on the map", so a cluster clipped by the map's
        // edge gets a correctly stepped outline instead of overshooting past the border).
        public static List<Vector3> BuildOuterBoundary(IEnumerable<HexCoord> cells, Func<HexCoord, Vector3> hexToWorld, float outerRadius)
        {
            var cellSet = new HashSet<HexCoord>(cells);
            var boundaryEdges = new List<(Vector3 a, Vector3 b)>();

            foreach (HexCoord cell in cellSet)
            {
                Vector3 center = hexToWorld(cell);
                var corners = new Vector3[6];
                for (int i = 0; i < 6; i++)
                {
                    float angle = Mathf.Deg2Rad * (60f * i);
                    corners[i] = center + new Vector3(outerRadius * Mathf.Cos(angle), 0f, outerRadius * Mathf.Sin(angle));
                }

                for (int i = 0; i < 6; i++)
                {
                    (int dq, int dr) = NeighborDirectionsByEdge[i];
                    var neighbor = new HexCoord(cell.Q + dq, cell.R + dr);
                    if (!cellSet.Contains(neighbor))
                        boundaryEdges.Add((corners[i], corners[(i + 1) % 6]));
                }
            }

            return ChainEdgesIntoLoop(boundaryEdges);
        }

        // Stitches an unordered bag of boundary line segments into one ordered, closed loop
        // by walking from shared endpoint to shared endpoint. Endpoints are matched by
        // position rounded to whole millimetres, since two edges from different hexes should
        // land on the exact same corner but float arithmetic can differ by a hair.
        private static List<Vector3> ChainEdgesIntoLoop(List<(Vector3 a, Vector3 b)> edges)
        {
            var loop = new List<Vector3>();
            if (edges.Count == 0)
                return loop;

            (int, int, int) Key(Vector3 v) => (Mathf.RoundToInt(v.x * 1000f), Mathf.RoundToInt(v.y * 1000f), Mathf.RoundToInt(v.z * 1000f));

            var adjacency = new Dictionary<(int, int, int), List<(int, int, int)>>();
            var positions = new Dictionary<(int, int, int), Vector3>();

            void AddAdjacency(Vector3 from, Vector3 to)
            {
                var key = Key(from);
                if (!adjacency.TryGetValue(key, out List<(int, int, int)> list))
                {
                    list = new List<(int, int, int)>();
                    adjacency[key] = list;
                }
                list.Add(Key(to));
                positions[key] = from;
            }

            foreach ((Vector3 a, Vector3 b) in edges)
            {
                AddAdjacency(a, b);
                AddAdjacency(b, a);
            }

            (int, int, int) startKey = Key(edges[0].a);
            (int, int, int) currentKey = startKey;
            (int, int, int)? previousKey = null;

            int safety = adjacency.Count * 2 + 10;
            while (safety-- > 0)
            {
                loop.Add(positions[currentKey]);
                List<(int, int, int)> neighbors = adjacency[currentKey];
                (int, int, int) nextKey = (previousKey.HasValue && neighbors[0].Equals(previousKey.Value) && neighbors.Count > 1)
                    ? neighbors[1]
                    : neighbors[0];

                if (nextKey.Equals(startKey))
                    break;

                previousKey = currentKey;
                currentKey = nextKey;
            }

            return loop;
        }
    }
}
