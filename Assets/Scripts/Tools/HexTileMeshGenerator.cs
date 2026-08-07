using System.Collections.Generic;
using UnityEngine;

namespace Game.Map
{
    // Just the flat painted terrain face of a hex tile — no extruded rim/side walls.
    public static class HexTileMeshGenerator
    {
        // Appends one flat hex face's vertices/uvs/colors/triangles into shared lists at a
        // given world-space centre. Lets a whole map be built as a single combined mesh
        // (grouped per terrain type into submeshes) without any per-tile GameObjects/Mesh
        // instances.
        //
        // The hex's own geometry never extends past its true outerRadius (no overlap with
        // neighbours). Instead it fades toward transparency near its own edge, revealing
        // whatever renders behind it (the shared ground plane) — the two parameters are:
        //   blend (0..1): fraction of the radius, measured inward from the edge, over which
        //                 the fade gradient runs. 0 = no fade at all. 1 = the gradient spans
        //                 the whole hex, edge to centre.
        //   alpha (0..1): how transparent the tile becomes at its outer edge (the strongest
        //                 point of the fade). 0 = stays fully opaque even at the edge. 1 =
        //                 fully transparent at the edge.
        // The side of the band closer to the centre is always the more opaque (closer to the
        // original colour) side; the true edge is always the most transparent point.
        public static void AppendFlatHexFace(List<Vector3> vertices, List<Vector3> normals,
            List<Vector2> uvs, List<Color> colors, List<int> triangles, Vector3 center,
            float outerRadius, float blend, float alpha)
        {
            blend = Mathf.Clamp01(blend);
            alpha = Mathf.Clamp01(alpha);

            float bandStartRadius = outerRadius * (1f - blend);
            float edgeVertexAlpha = 1f - alpha; // vertex-colour alpha: 1 = opaque, 0 = transparent

            int centerIndex = vertices.Count;
            vertices.Add(center);
            normals.Add(Vector3.up);
            uvs.Add(new Vector2(0.5f, 0.5f));
            colors.Add(new Color(1f, 1f, 1f, 1f));

            var bandStartRing = BuildRing(vertices, normals, uvs, colors, center, bandStartRadius, outerRadius, 1f);
            var outerRing = BuildRing(vertices, normals, uvs, colors, center, outerRadius, outerRadius, edgeVertexAlpha);

            // Solid core: fan from the centre to where the fade band begins.
            for (int i = 0; i < 6; i++)
                AddTriangleSelfCorrecting(vertices, triangles, centerIndex, bandStartRing[i], bandStartRing[(i + 1) % 6], Vector3.up);

            // Fade band: alpha runs from 1 (opaque) at bandStartRing to edgeVertexAlpha at the true edge.
            AddRingStrip(vertices, triangles, bandStartRing, outerRing);
        }

        private static int[] BuildRing(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs,
            List<Color> colors, Vector3 center, float radius, float uvRadius, float alpha)
        {
            var ring = new int[6];
            for (int i = 0; i < 6; i++)
            {
                // Flat-top orientation: vertices at 0/60/120/180/240/300 degrees so the
                // top/bottom edges of the hexagon are horizontal (flat), matching HexGridMath.
                float angle = Mathf.Deg2Rad * (60f * i);
                float x = radius * Mathf.Cos(angle);
                float z = radius * Mathf.Sin(angle);

                ring[i] = vertices.Count;
                vertices.Add(center + new Vector3(x, 0f, z));
                normals.Add(Vector3.up);
                uvs.Add(new Vector2(0.5f + x / (2f * uvRadius), 0.5f + z / (2f * uvRadius)));
                colors.Add(new Color(1f, 1f, 1f, alpha));
            }
            return ring;
        }

        private static void AddRingStrip(List<Vector3> vertices, List<int> triangles, int[] innerRing, int[] outerRing)
        {
            for (int i = 0; i < 6; i++)
            {
                int j = (i + 1) % 6;
                AddTriangleSelfCorrecting(vertices, triangles, innerRing[i], innerRing[j], outerRing[j], Vector3.up);
                AddTriangleSelfCorrecting(vertices, triangles, innerRing[i], outerRing[j], outerRing[i], Vector3.up);
            }
        }

        // Same self-correcting winding pattern used throughout this project's mesh generators:
        // flip the triangle if its face normal points against the expected direction.
        private static void AddTriangleSelfCorrecting(List<Vector3> vertices, List<int> triangles,
            int a, int b, int c, Vector3 expectedNormal)
        {
            Vector3 faceNormal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            if (Vector3.Dot(faceNormal, expectedNormal) < 0f)
            {
                triangles.Add(a);
                triangles.Add(c);
                triangles.Add(b);
            }
            else
            {
                triangles.Add(a);
                triangles.Add(b);
                triangles.Add(c);
            }
        }
    }
}
