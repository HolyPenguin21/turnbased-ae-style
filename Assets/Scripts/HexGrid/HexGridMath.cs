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
        // (i+1)%6 (corner i sits at angle 60*i degrees) — callers pair each edge
        // with its corners by this order.
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

        // Every hex within `radius` steps of `center` (inclusive), center itself included at
        // radius 0 — used by VisionSystem to expand an army/building's own hex into its actual
        // vision footprint. Existence on the map isn't checked here, same convention as
        // Neighbors — callers already have their own map/registry to validate against.
        public static IEnumerable<HexCoord> HexesInRange(HexCoord center, int radius)
        {
            if (radius <= 0)
            {
                yield return center;
                yield break;
            }
            for (int dq = -radius; dq <= radius; dq++)
            {
                int rMin = Mathf.Max(-radius, -dq - radius);
                int rMax = Mathf.Min(radius, -dq + radius);
                for (int dr = rMin; dr <= rMax; dr++)
                    yield return new HexCoord(center.Q + dq, center.R + dr);
            }
        }
    }
}
