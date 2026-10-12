using System.Collections.Generic;
using Game.Styles;
using UnityEngine;

namespace Game.Map
{
    // Renderer-independent XZ geometry shared by the tactical map and campaign Canvas.
    // Contains no hex coordinates, camera, badges, materials or animation state.
    public sealed class ArrowMeshGeometry
    {
        private readonly MoveArrowStyle _style;
        public ArrowMeshGeometry(MoveArrowStyle style) { _style = style; }

        // One continuous Hermite spline through every waypoint, instead of an independent
        // quadratic bow per hex-to-hex leg — a per-leg bow picks each bow's direction from
        // that leg's own endpoints alone, so consecutive legs meet at a visible kink (no
        // shared tangent) on longer, multi-hex paths. Here every waypoint gets ONE tangent,
        // built from its neighbours (central difference, so segments on either side of it
        // agree and the path stays C1-continuous through it) with the same sideways curveBend
        // lean added to every interior tangent — consistent handedness is what makes the whole
        // path read as one flowing arc rather than a wavy chain. The final tangent and, for a
        // 2-point path (a single hex hop), the first tangent too are left unbent, so the arrow
        // always approaches the destination hex center straight-on rather than at an angle.
        public List<Vector3> BuildCurve(IReadOnlyList<Vector3> waypoints)
        {
            int n = waypoints.Count;
            var tangents = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 baseDir;
                if (i == 0)
                    baseDir = waypoints[1] - waypoints[0];
                else if (i == n - 1)
                    baseDir = waypoints[n - 1] - waypoints[n - 2];
                else
                    baseDir = waypoints[i + 1] - waypoints[i - 1];

                baseDir.y = 0f;
                baseDir.Normalize();
                Vector3 perpendicular = new Vector3(-baseDir.z, 0f, baseDir.x);
                // The final tangent stays unbent so the last segment approaches the
                // destination hex center straight-on instead of curving in at an angle
                // (the bend on every other tangent is what gives multi-hex paths their
                // flowing arc, but at the endpoint it reads as an S-curve/overshoot).
                // A 2-point path (single hex hop) has no chain to flow with, so it gets
                // no bend at all and renders as a straight segment into the target.
                float bend = (n == 2 || i == n - 1) ? 0f : _style.curveBend;
                tangents[i] = (baseDir + perpendicular * bend).normalized;
            }

            var result = new List<Vector3> { waypoints[0] };
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 p0 = waypoints[i];
                Vector3 p1 = waypoints[i + 1];
                float segmentLength = Vector3.Distance(p0, p1);
                Vector3 m0 = tangents[i] * segmentLength;
                Vector3 m1 = tangents[i + 1] * segmentLength;

                for (int j = 1; j <= _style.segmentsPerLeg; j++)
                {
                    float t = j / (float)_style.segmentsPerLeg;
                    result.Add(HermitePoint(p0, m0, p1, m1, t));
                }
            }
            return result;
        }

        private static Vector3 HermitePoint(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * p0 + h10 * m0 + h01 * p1 + h11 * m1;
        }

        private static Vector3 ComputeTangent(List<Vector3> curve, int i)
        {
            Vector3 prev = curve[Mathf.Max(i - 1, 0)];
            Vector3 next = curve[Mathf.Min(i + 1, curve.Count - 1)];
            Vector3 dir = next - prev;
            dir.y = 0f;
            return dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward;
        }

        // Finds the position and tangent on the (already-built) curve at an arbitrary arc-
        // length distance from its start — every ring in the arrow's profile is placed this
        // way, shaft and arrowhead alike, so they all share one consistent notion of "forward"
        // instead of the arrowhead using a different, only-approximately-matching direction.
        // A distance outside [0, totalLength] (the outline's tip, pushed past the real tip)
        // extrapolates straight along the boundary tangent instead of clamping onto the curve.
        public static void SampleCurveAtDistance(List<Vector3> curve, float[] cumulative, float distance, out Vector3 position, out Vector3 tangent)
        {
            float totalLength = cumulative[cumulative.Length - 1];
            float clamped = Mathf.Clamp(distance, 0f, totalLength);

            int index = 1;
            while (index < curve.Count - 1 && cumulative[index] < clamped)
                index++;

            float segStart = cumulative[index - 1];
            float segLen = cumulative[index] - segStart;
            float t = segLen > 0.0001f ? Mathf.Clamp01((clamped - segStart) / segLen) : 0f;
            position = Vector3.Lerp(curve[index - 1], curve[index], t);

            Vector3 dir = curve[index] - curve[index - 1];
            dir.y = 0f;
            tangent = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward;

            if (distance > totalLength)
                position += tangent * (distance - totalLength);
            else if (distance < 0f)
                position += tangent * distance;
        }

        // Appends one ring — two vertices mirrored ±right*halfWidth around centre — recording
        // where it starts so consecutive rings can be quaded together. Used for the shaft's
        // tail-to-neck taper (see BuildArrowMesh), which is always monotonic along the curve,
        // so a simple "connect consecutive cross-sections" loft is exactly right there.
        // alpha is baked as a vertex colour (RGB unused, shader reads only .a) — GPU
        // interpolation across each quad is what turns discrete per-ring values into a smooth
        // fade, with no per-pixel curve-distance lookup needed in the shader itself.
        private static void AppendRing(Vector3 center, Vector3 right, float halfWidth, float alpha,
            List<Vector3> vertices, List<Color> colors, List<int> sectionStarts)
        {
            int start = vertices.Count;
            vertices.Add(center - right * halfWidth);
            vertices.Add(center + right * halfWidth);
            Color c = new Color(1f, 1f, 1f, alpha);
            colors.Add(c);
            colors.Add(c);
            sectionStarts.Add(start);
        }

        // One quad between ring i and ring i+1 (both always full 2-vertex rings here). Winding
        // doesn't matter since MoveArrowGlow.shader is unlit and double-sided (Cull Off).
        private static void EmitQuad(List<int> sectionStarts, int i, List<int> triangles)
        {
            int a0 = sectionStarts[i], a1 = a0 + 1, b0 = sectionStarts[i + 1], b1 = b0 + 1;
            triangles.Add(a0); triangles.Add(a1); triangles.Add(b0);
            triangles.Add(a1); triangles.Add(b1); triangles.Add(b0);
        }

        // Appends one point of the head's flat 2D silhouette — x = distance from the tip along
        // the spine, y = signed half-width (side = +1 for the right edge, -1 for the left) —
        // see the polygon-triangulation note in BuildArrowMesh for why the head is built this
        // way instead of as a stack of lofted rings.
        private static void AddPolygonPoint(ArrowHeadPoint point, float side, List<Vector2> polygon)
        {
            polygon.Add(new Vector2(point.distanceFromTip, side * point.halfWidth));
        }

        // Standard ear-clipping triangulation of a simple 2D polygon (no self-intersections),
        // returning a flat list of indices into `polygon` (3 per triangle). Handles concave
        // polygons — needed here because a swallowtail notch makes the head's silhouette
        // concave, which a per-cross-section loft can never represent (two consecutive
        // cross-sections are always joined by a straight, convex quad; see BuildArrowMesh).
        private static List<int> TriangulatePolygon(List<Vector2> polygon)
        {
            var remaining = new List<int>(polygon.Count);
            for (int i = 0; i < polygon.Count; i++)
                remaining.Add(i);

            float signedArea = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];
                signedArea += a.x * b.y - b.x * a.y;
            }
            bool clockwise = signedArea < 0f;

            // Small tolerance on both the reflex test and the point-in-triangle test — without
            // it, a vertex or point that's only barely inside/outside due to plain float
            // rounding can get misclassified, rejecting an ear that's actually valid and
            // leaving that part of the polygon permanently untriangulated (a real hole).
            const float epsilon = 1e-4f;

            var triangles = new List<int>();
            int guard = 0;
            while (remaining.Count > 3 && guard++ < 1000)
            {
                int bestIndex = -1;
                float bestCross = float.NegativeInfinity;

                for (int i = 0; i < remaining.Count; i++)
                {
                    int iPrev = remaining[(i - 1 + remaining.Count) % remaining.Count];
                    int iCurr = remaining[i];
                    int iNext = remaining[(i + 1) % remaining.Count];

                    Vector2 a = polygon[iPrev], b = polygon[iCurr], c = polygon[iNext];
                    float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                    if (clockwise) cross = -cross;
                    if (cross <= -epsilon)
                        continue; // clearly reflex — can't be an ear

                    bool anyPointInside = false;
                    for (int j = 0; j < remaining.Count; j++)
                    {
                        int idx = remaining[j];
                        if (idx == iPrev || idx == iCurr || idx == iNext)
                            continue;
                        if (PointInTriangle(polygon[idx], a, b, c, epsilon))
                        {
                            anyPointInside = true;
                            break;
                        }
                    }
                    if (anyPointInside)
                        continue;

                    // Track the most convex valid ear instead of stopping at the first one —
                    // if nothing at all qualifies below (a truly degenerate polygon), falling
                    // back to whichever candidate was closest to valid still guarantees the
                    // whole area gets covered, rather than leaving a gap.
                    if (cross > bestCross)
                    {
                        bestCross = cross;
                        bestIndex = i;
                    }
                }

                if (bestIndex < 0)
                    bestIndex = 0; // last-resort fallback: clip anyway rather than leave a hole

                int prev = remaining[(bestIndex - 1 + remaining.Count) % remaining.Count];
                int curr = remaining[bestIndex];
                int next = remaining[(bestIndex + 1) % remaining.Count];
                triangles.Add(prev);
                triangles.Add(curr);
                triangles.Add(next);
                remaining.RemoveAt(bestIndex);
            }
            if (remaining.Count == 3)
            {
                triangles.Add(remaining[0]);
                triangles.Add(remaining[1]);
                triangles.Add(remaining[2]);
            }
            return triangles;
        }

        private static float Cross(Vector2 p, Vector2 a, Vector2 b) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c, float epsilon)
        {
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool hasNeg = d1 < -epsilon || d2 < -epsilon || d3 < -epsilon;
            bool hasPos = d1 > epsilon || d2 > epsilon || d3 > epsilon;
            return !(hasNeg && hasPos);
        }

        // Alpha at a given distance from the tail's own start (0, the selected unit's hex)
        // towards the neck where the head begins (tailLength). Used for both the fill and the
        // outline (see AppendRing/the head-polygon loop below) — the outline fades in step
        // with the fill instead of staying solid, so the whole silhouette (border included)
        // reads as one fading shape rather than a fading fill inside a solid frame.
        //
        // Measured against tailLength (the shaft only), not the whole arrow's length — see
        // MoveArrowStyle.gradientStart. Everything from the neck onward (distance >= the fade's
        // start point, which includes the entire head, since head distances are always >=
        // tailLength) is pinned flat at 1; only the stretch below that point, nearest the unit,
        // ramps down to tailAlpha.
        private float AlphaAtDistance(float distance, float tailLength)
        {
            if (tailLength <= 0f)
                return 1f;

            float fadeZoneLength = tailLength * (1f - _style.gradientStart);
            if (fadeZoneLength <= 0f || distance >= fadeZoneLength)
                return 1f;

            return Mathf.Lerp(_style.tailAlpha, 1f, distance / fadeZoneLength);
        }

        // The whole silhouette, tail to tip. Tail -> neck is a simple loft (consecutive
        // cross-sections joined by a quad, sampled at every curve point so it follows the
        // path's bow) — always monotonic, never folds on itself.
        //
        // The head is a flat 2D polygon (see AddPolygonPoint): headProfile's RIGHT edge walked
        // from the base (last element) to the tip (element 0), then the LEFT edge back down to
        // the base, triangulated via TriangulatePolygon (handles the concave case, e.g. a
        // swallowtail notch), and only then mapped onto the curve one vertex at a time.
        //
        // widthPad>0 (the outline mesh) changes two things: every head profile point except the
        // tip is widened by it (the tip is instead pushed forward along the spine, staying a
        // true point); and the shaft is built as two independent hollow strips (each widthPad
        // wide) hugging just outside the main mesh's own two edges, instead of one solid wider
        // ring, so the outline can't cover the main mesh's own fill.
        // That boundary (and the shaft loop's own stopping point) sits at whichever profile
        // element reaches farthest from the tip — normally the last element ("base"), but an
        // earlier element (e.g. a wing swept back beyond it) can reach farther, and the shaft
        // must stop there instead or it overlaps the head's own geometry.
        // skipTailRing drops just the very first shaft ring — used for the outline mesh so it
        // doesn't poke out past the main mesh's own tail edge as a small solid "cap"; every
        // ring after it still gets the normal padding, giving a visible border along both sides
        // of the shaft.
        public Mesh BuildArrowMesh(List<Vector3> curve, float widthPad, bool skipTailRing)
        {
            float totalLength = 0f;
            var cumulative = new float[curve.Count];
            for (int i = 1; i < curve.Count; i++)
            {
                totalLength += Vector3.Distance(curve[i - 1], curve[i]);
                cumulative[i] = totalLength;
            }

            ArrowHeadPoint[] headProfile = _style.headProfile;
            bool hasProfile = headProfile != null && headProfile.Length > 1;

            ArrowHeadPoint[] profile = null;
            if (hasProfile)
            {
                profile = new ArrowHeadPoint[headProfile.Length];
                for (int i = 0; i < headProfile.Length; i++)
                {
                    float d = headProfile[i].distanceFromTip;
                    float w = headProfile[i].halfWidth;
                    if (i == 0) d -= widthPad; else w += widthPad;
                    profile[i] = new ArrowHeadPoint { distanceFromTip = d, halfWidth = w };
                }
            }

            // The shaft must stop no later than the FARTHEST-back profile point, whichever
            // element that is — not necessarily the last one. "Last = base, meets the shaft" is
            // the normal case, but an earlier element (e.g. a wing) is free to sweep back past
            // it; if the shaft loop only stopped at the last element's own distance, that
            // element would end up sampled from inside the shaft's own already-built geometry.
            int neckElementIndex = 0;
            if (hasProfile)
            {
                for (int i = 1; i < profile.Length; i++)
                    if (profile[i].distanceFromTip > profile[neckElementIndex].distanceFromTip)
                        neckElementIndex = i;
            }

            // On a short path (a single hex hop can be shorter than the head profile's own
            // reach), a profile point farther from the tip than totalLength would sample the
            // curve at a negative distance and extrapolate out past the tail's own start.
            // Scaling every point's distance down proportionally keeps the profile's shape,
            // and every point within [0, totalLength], while shrinking it to fit the path.
            if (hasProfile && totalLength > 0f && profile[neckElementIndex].distanceFromTip > totalLength)
            {
                float scale = totalLength / profile[neckElementIndex].distanceFromTip;
                for (int i = 0; i < profile.Length; i++)
                    profile[i].distanceFromTip *= scale;
            }

            float headLength = hasProfile ? Mathf.Min(profile[neckElementIndex].distanceFromTip, totalLength) : 0f;
            float neckDistance = totalLength - headLength; // distance FROM START where the head begins

            // Unpadded (true silhouette) widths — used as the INNER edge of the outline's hollow
            // strips below, and as the ring width directly when widthPad is 0 (the main mesh).
            float tailHalfMain = _style.tailWidth * 0.5f;
            float neckHalfMain = hasProfile ? headProfile[neckElementIndex].halfWidth : 0f;

            var vertices = new List<Vector3>();
            var colors = new List<Color>();
            var triangles = new List<int>();

            // neckLeft/neckRight/neckLeftOuter/neckRightOuter index whichever vertices the head
            // needs to bridge to below — a single ring's 2 vertices for the main mesh, or each
            // hollow strip's own last ring for the outline (see the branches below).
            int neckLeft = -1, neckRight = -1, neckLeftOuter = -1, neckRightOuter = -1;

            // The tail-to-neck taper is sampled at every point ALONG THE CURVE (not just its
            // two endpoints) so this stretch keeps following the path's actual bow — skipping
            // straight from a tail ring to a neck ring with nothing in between was the bug
            // that made the shaft look unchanged/straight regardless of the path's curve.
            if (widthPad <= 0f)
            {
                // Main mesh: one solid ring per position — this IS the visible fill.
                var sectionStarts = new List<int>();
                for (int i = 0; i < curve.Count; i++)
                {
                    if (cumulative[i] > neckDistance)
                        break;

                    float t = neckDistance > 0f ? cumulative[i] / neckDistance : 1f;
                    float halfWidth = Mathf.Lerp(tailHalfMain, neckHalfMain, t);
                    Vector3 forward = ComputeTangent(curve, i);
                    Vector3 right = new Vector3(-forward.z, 0f, forward.x).normalized;
                    AppendRing(curve[i], right, halfWidth, AlphaAtDistance(cumulative[i], neckDistance), vertices, colors, sectionStarts);
                }

                // Explicit final ring pinned EXACTLY at neckDistance (same SampleCurveAtDistance
                // the head's own base vertices use below), instead of relying on the discrete
                // curve samples above happening to land there — otherwise the last shaft ring
                // can fall short of the neck, leaving a sliver gap the head's own fill still
                // covers but the outline's hollow strips (see the other branch) don't.
                if (neckDistance > 0f && totalLength > 0f)
                {
                    SampleCurveAtDistance(curve, cumulative, neckDistance, out Vector3 neckPos, out Vector3 neckTangent);
                    Vector3 neckDir = new Vector3(-neckTangent.z, 0f, neckTangent.x).normalized;
                    AppendRing(neckPos, neckDir, neckHalfMain, AlphaAtDistance(neckDistance, neckDistance), vertices, colors, sectionStarts);
                }

                for (int i = 0; i < sectionStarts.Count - 1; i++)
                    EmitQuad(sectionStarts, i, triangles);
                if (sectionStarts.Count > 0)
                {
                    neckLeft = sectionStarts[sectionStarts.Count - 1];
                    neckRight = neckLeft + 1;
                }
            }
            else
            {
                // Outline: two independent thin ribbons (each widthPad wide), hugging just
                // outside the main mesh's own two edges with nothing filling the space between
                // them. A solid duplicate of the whole shaft here would sit behind the main
                // mesh's own see-through centre stripe and show through as a flat, opaque
                // colour wherever that stripe is meant to reveal the terrain instead — this
                // hollow shape can't cover anything the main mesh doesn't already cover itself.
                var rightStarts = new List<int>();
                var leftStarts = new List<int>();
                for (int i = 0; i < curve.Count; i++)
                {
                    if (cumulative[i] > neckDistance)
                        break;
                    if (i == 0 && skipTailRing)
                        continue;

                    float t = neckDistance > 0f ? cumulative[i] / neckDistance : 1f;
                    float mainHalf = Mathf.Lerp(tailHalfMain, neckHalfMain, t);
                    Vector3 forward = ComputeTangent(curve, i);
                    Vector3 right = new Vector3(-forward.z, 0f, forward.x).normalized;

                    float ringAlpha = AlphaAtDistance(cumulative[i], neckDistance);

                    Vector3 rightCenter = curve[i] + right * (mainHalf + widthPad * 0.5f);
                    AppendRing(rightCenter, right, widthPad * 0.5f, ringAlpha, vertices, colors, rightStarts);

                    Vector3 leftCenter = curve[i] - right * (mainHalf + widthPad * 0.5f);
                    AppendRing(leftCenter, right, widthPad * 0.5f, ringAlpha, vertices, colors, leftStarts);
                }

                // Same exact-neck fix as the main mesh above, applied to both hollow strips —
                // without it the residual sliver between the shaft's last sampled ring and the
                // head's own base edge has no outline geometry at all (the strips are hollow,
                // so there's nothing for the side-bridge triangles below to close it off from),
                // which reads as the outline visibly breaking right where the head meets the
                // shaft.
                if (neckDistance > 0f && totalLength > 0f)
                {
                    SampleCurveAtDistance(curve, cumulative, neckDistance, out Vector3 neckPos, out Vector3 neckTangent);
                    Vector3 neckDir = new Vector3(-neckTangent.z, 0f, neckTangent.x).normalized;
                    float neckAlpha = AlphaAtDistance(neckDistance, neckDistance);

                    Vector3 rightCenter = neckPos + neckDir * (neckHalfMain + widthPad * 0.5f);
                    AppendRing(rightCenter, neckDir, widthPad * 0.5f, neckAlpha, vertices, colors, rightStarts);

                    Vector3 leftCenter = neckPos - neckDir * (neckHalfMain + widthPad * 0.5f);
                    AppendRing(leftCenter, neckDir, widthPad * 0.5f, neckAlpha, vertices, colors, leftStarts);
                }

                for (int i = 0; i < rightStarts.Count - 1; i++) EmitQuad(rightStarts, i, triangles);
                for (int i = 0; i < leftStarts.Count - 1; i++) EmitQuad(leftStarts, i, triangles);

                // AppendRing's first vertex is centre-right*half, second is centre+right*half —
                // for the right strip that's (inner, outer); mirrored, the left strip comes out
                // (outer, inner) instead.
                if (rightStarts.Count > 0) { neckRight = rightStarts[rightStarts.Count - 1]; neckRightOuter = neckRight + 1; }
                if (leftStarts.Count > 0) { neckLeftOuter = leftStarts[leftStarts.Count - 1]; neckLeft = neckLeftOuter + 1; }
            }

            if (hasProfile)
            {
                var polygon = new List<Vector2>();
                for (int i = profile.Length - 1; i >= 0; i--)
                    AddPolygonPoint(profile[i], +1f, polygon); // right edge: base -> tip
                for (int i = 1; i < profile.Length; i++)
                    AddPolygonPoint(profile[i], -1f, polygon); // left edge: tip -> base

                List<int> headTriangles = TriangulatePolygon(polygon);

                int headVertexStart = vertices.Count;
                foreach (Vector2 point in polygon)
                {
                    float distance = totalLength - point.x;
                    SampleCurveAtDistance(curve, cumulative, distance, out Vector3 position, out Vector3 tangent);
                    Vector3 right = new Vector3(-tangent.z, 0f, tangent.x).normalized;
                    vertices.Add(position + right * point.y);
                    colors.Add(new Color(1f, 1f, 1f, AlphaAtDistance(distance, neckDistance)));
                }

                for (int i = 0; i < headTriangles.Count; i += 3)
                {
                    triangles.Add(headVertexStart + headTriangles[i]);
                    triangles.Add(headVertexStart + headTriangles[i + 1]);
                    triangles.Add(headVertexStart + headTriangles[i + 2]);
                }

                // Bridge the shaft's neck vertices to the head polygon's own vertices for
                // neckElementIndex (whichever element actually reaches farthest back — see
                // above), not blindly polygon[0]/polygon[last]. Its right-side copy sits at
                // (profile.Length-1-neckElementIndex) in the right-edge run built above; its
                // left-side copy (absent only if neckElementIndex is the tip, element 0 — a
                // degenerate profile with no real head) sits at profile.Length+(neckElementIndex-1).
                if (neckElementIndex >= 1)
                {
                    int baseRight = headVertexStart + (profile.Length - 1 - neckElementIndex);
                    int baseLeft = headVertexStart + (profile.Length + neckElementIndex - 1);

                    if (widthPad <= 0f)
                    {
                        // Main mesh: one solid quad from the neck ring to the head's base edge.
                        if (neckLeft >= 0)
                        {
                            triangles.Add(neckLeft); triangles.Add(neckRight); triangles.Add(baseRight);
                            triangles.Add(neckLeft); triangles.Add(baseRight); triangles.Add(baseLeft);
                        }
                    }
                    else
                    {
                        // Outline: each hollow strip tapers down to the head's single boundary
                        // point on its own side — no quad needed, the strips never touch.
                        if (neckRight >= 0)
                            { triangles.Add(neckRight); triangles.Add(neckRightOuter); triangles.Add(baseRight); }
                        if (neckLeft >= 0)
                            { triangles.Add(neckLeftOuter); triangles.Add(neckLeft); triangles.Add(baseLeft); }
                    }
                }
            }

            var mesh = new Mesh { name = "MoveArrow" };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

    }
}
