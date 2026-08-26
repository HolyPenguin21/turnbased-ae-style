using System.Collections.Generic;
using Game.Core;
using Game.Styles;
using TMPro;
using UnityEngine;

namespace Game.Map
{
    // The full move-order preview: a comet-trail-style arrow from the selected unit's hex
    // through each hex of the planned route, plus a badge showing the total move cost near
    // the start.
    //
    // The shaft (tail to neck) is a simple loft: consecutive cross-sections along the curve,
    // each a ring of 2 vertices mirrored ±half-width from the centreline. The head is a flat
    // 2D polygon (see MoveArrowStyle.headProfile) triangulated on its own and only then mapped
    // onto the curve, since a lofted ring can't represent a concave shape like a swallowtail
    // notch — see BuildArrowMesh for both.
    //
    // The curve itself is a Hermite spline through every waypoint (a consistent sideways bow,
    // not a per-leg independent one), so multi-hex paths read as one flowing arc rather than a
    // wavy chain, and even a single one-hex hop still bows.

    // One point of the arrowhead's silhouette: distance back from the tip, and half-width at
    // that distance. See MoveArrowStyle.headProfile for the ordering rules.
    [System.Serializable]
    public struct ArrowHeadPoint
    {
        public float distanceFromTip;
        public float halfWidth;
    }

    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class MoveArrowMarker : MonoBehaviour
    {
        // Tunables live in GameConfig.moveArrowStyle (see MoveArrowStyle) rather than
        // [SerializeField]s here, since this component is always created purely from code
        // (HexSelectionController.ShowPathArrow's AddComponent<MoveArrowMarker>()) and never has
        // its own Inspector instance. Defaults to a fresh MoveArrowStyle so it still works if
        // Initialize is skipped or called with a null config.
        private MoveArrowStyle _style = new MoveArrowStyle();

        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Material _material;
        private MeshFilter _outlineFilter;
        private MeshRenderer _outlineRenderer;
        private MeshFilter _badgeFilter;
        private MeshRenderer _badgeRenderer;
        private TextMeshPro _badgeText;
        private MeshFilter _badgeBorderFilter;
        private MeshRenderer _badgeBorderRenderer;
        private MeshFilter _apBadgeFilter;
        private MeshRenderer _apBadgeRenderer;
        private TextMeshPro _apBadgeText;
        private MeshFilter _apBadgeBorderFilter;
        private MeshRenderer _apBadgeBorderRenderer;
        private MeshFilter _energyBadgeFilter;
        private MeshRenderer _energyBadgeRenderer;
        private TextMeshPro _energyBadgeText;
        private MeshFilter _energyBadgeBorderFilter;
        private MeshRenderer _energyBadgeBorderRenderer;
        private bool _initialized;

        private void Awake()
        {
            _filter = GetComponent<MeshFilter>();
            _renderer = GetComponent<MeshRenderer>();
        }

        // Called once, right after AddComponent — Awake() alone can't pull config values,
        // since AddComponent runs it synchronously before the caller has any chance to hand
        // over a GameConfig reference. Safe to call more than once; only the first call does
        // anything. Falls back to MoveArrowStyle's own defaults if config is null.
        public void Initialize(GameConfig config)
        {
            if (_initialized)
                return;
            _initialized = true;

            _style = config != null ? config.moveArrowStyle : new MoveArrowStyle();

            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.sortingOrder = _style.fillSortingOrder;

            Shader shader = Shader.Find("Custom/MoveArrowGlow");
            if (shader == null)
                Debug.LogWarning("MoveArrowMarker: shader 'Custom/MoveArrowGlow' not found.");
            _material = new Material(shader);
            _renderer.sharedMaterial = _material;

            // Outline and badge share the fill's own shader now (Custom/MoveArrowGlow, always
            // Transparent queue) instead of the built-in URP Unlit, which defaults to the
            // opaque Geometry queue — sortingOrder only orders renderers within the SAME
            // queue, so as long as they were in different queues no sortingOrder value could
            // ever control draw order between the outline/badge and the fill/map/highlights.
            // Everything sits flat at Y=0 now; sortingOrder is the only thing left to do that
            // job. See MoveArrowStyle.outlineSortingOrder for why the outline must stay below
            // the fill (its head silhouette is solid, not hollow like the shaft).
            _outlineFilter = BuildChildMesh("Outline", shader, out _outlineRenderer);
            _outlineRenderer.sharedMaterial.color = _style.outlineColor;
            _outlineRenderer.sortingOrder = _style.outlineSortingOrder;

            // Each badge's border is a hollow ring (badgeRadius -> badgeRadius+thickness), not a
            // solid larger disc — a solid disc relying only on sortingOrder to stay behind the
            // badge would, if draw order ever didn't cooperate, blot out the badge (and its
            // number) entirely. A true ring can never cover the badge regardless of draw order,
            // same reasoning as the arrow's own hollow outline strips (see BuildArrowMesh).
            _badgeBorderFilter = BuildChildMesh("BadgeBorder", shader, out _badgeBorderRenderer);
            _badgeBorderFilter.mesh = BuildRingMesh(_style.badgeRadius, _style.badgeRadius + _style.badgeBorderThickness);
            _badgeBorderRenderer.sharedMaterial.color = _style.badgeBorderColor;
            _badgeBorderRenderer.sortingOrder = _style.badgeBorderSortingOrder;

            _badgeFilter = BuildChildMesh("Badge", shader, out _badgeRenderer);
            _badgeFilter.mesh = BuildCircleMesh(_style.badgeRadius);
            _badgeRenderer.sharedMaterial.color = _style.badgeColor;
            _badgeRenderer.sortingOrder = _style.badgeSortingOrder;
            _badgeText = BuildBadgeText(_badgeFilter, _style.badgeTextColor);

            _apBadgeBorderFilter = BuildChildMesh("ApBadgeBorder", shader, out _apBadgeBorderRenderer);
            _apBadgeBorderFilter.mesh = BuildRingMesh(_style.badgeRadius, _style.badgeRadius + _style.badgeBorderThickness);
            _apBadgeBorderRenderer.sharedMaterial.color = _style.badgeBorderColor;
            _apBadgeBorderRenderer.sortingOrder = _style.badgeBorderSortingOrder;

            // Same circle-plus-number badge, just AP-coloured — sits beside the move-cost
            // badge (see Show), not stacked on it.
            _apBadgeFilter = BuildChildMesh("ApBadge", shader, out _apBadgeRenderer);
            _apBadgeFilter.mesh = BuildCircleMesh(_style.badgeRadius);
            _apBadgeRenderer.sharedMaterial.color = _style.apBadgeColor;
            _apBadgeRenderer.sortingOrder = _style.badgeSortingOrder;
            _apBadgeText = BuildBadgeText(_apBadgeFilter, _style.badgeTextColor);

            _energyBadgeBorderFilter = BuildChildMesh("EnergyBadgeBorder", shader, out _energyBadgeBorderRenderer);
            _energyBadgeBorderFilter.mesh = BuildRingMesh(_style.badgeRadius, _style.badgeRadius + _style.badgeBorderThickness);
            _energyBadgeBorderRenderer.sharedMaterial.color = _style.badgeBorderColor;
            _energyBadgeBorderRenderer.sortingOrder = _style.badgeBorderSortingOrder;
            _energyBadgeFilter = BuildChildMesh("EnergyBadge", shader, out _energyBadgeRenderer);
            _energyBadgeFilter.mesh = BuildCircleMesh(_style.badgeRadius);
            _energyBadgeRenderer.sharedMaterial.color = new Color(0.851f, 0.749f, 0.149f, 1f); // #D9BF26
            _energyBadgeRenderer.sortingOrder = _style.badgeSortingOrder;
            _energyBadgeText = BuildBadgeText(_energyBadgeFilter, _style.badgeTextColor);

            Hide();
        }

        private MeshFilter BuildChildMesh(string objectName, Shader shader, out MeshRenderer renderer)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(transform, false);
            // Vertices for these meshes are baked in world space — force world identity so
            // this child isn't double-offset by whatever transform this marker sits at.
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var filter = go.AddComponent<MeshFilter>();
            renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var material = new Material(shader);
            renderer.sharedMaterial = material;
            return filter;
        }

        // The number inside a cost badge — shared setup for both the move-cost and AP badges.
        private TextMeshPro BuildBadgeText(MeshFilter badgeFilter, Color textColor)
        {
            var textGo = new GameObject("BadgeText");
            textGo.transform.SetParent(badgeFilter.transform, false);
            // TMP's own front face is a vertical plane by default — tip it flat (matches the
            // same 90°-about-X convention already used for ResourceIcon's world-space text)
            // so it lies on the map plane instead of standing up out of it.
            textGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            var text = textGo.AddComponent<TextMeshPro>();
            text.font = TMP_Settings.defaultFontAsset;
            text.fontSize = 3.2f;
            text.alignment = TextAlignmentOptions.Center;
            text.color = textColor;
            text.rectTransform.sizeDelta = new Vector2(_style.badgeRadius * 2.2f, _style.badgeRadius * 2.2f);
            text.renderer.sortingOrder = _style.badgeTextSortingOrder;
            return text;
        }

        // hexCenters is the full route (start hex included) in world space, matching
        // HexPath.Hexes converted via HexMap.HexToWorld. apCost is whatever the caller will
        // actually charge for this move (see HexSelectionController.TryIssueMoveOrder) — 0
        // once the unit's already "activated" this turn, not always 1.
        public void Show(IReadOnlyList<Vector3> hexCenters, int cost, int apCost, int energyCost, Color color)
        {
            if (!_initialized)
                Initialize(null);

            if (hexCenters == null || hexCenters.Count < 2)
            {
                Hide();
                return;
            }

            List<Vector3> curve = BuildCurve(hexCenters);

            _filter.mesh = BuildArrowMesh(curve, 0f, skipTailRing: false);
            // Fully opaque — a partial alpha here let the terrain underneath show through,
            // and since the terrain's own colour varies along the arrow's length, the exact
            // same _Color value visibly read as different shades at the tip vs. the tail.
            _material.SetColor("_Color", new Color(color.r, color.g, color.b, 1f));
            _renderer.enabled = true;

            // Outline skips just its very first ring (see BuildArrowMesh's skipTailRing note) —
            // every other shaft ring, and the whole head, still get the normal outline padding.
            _outlineFilter.mesh = BuildArrowMesh(curve, _style.outlineThickness, skipTailRing: true);
            _outlineRenderer.enabled = true;

            // Always laid out along world X, regardless of which way the arrow itself points —
            // a badge pair offset by the curve's own sideways direction would tilt with the
            // arrow instead of staying level.
            Vector3 midPosition = BadgePosition(curve);
            float spacing = _style.badgeSpacing;

            bool showEnergy = energyCost > 0;
            Vector3 badgePos = showEnergy ? midPosition - Vector3.right * spacing : midPosition - Vector3.right * spacing * 0.5f;
            _badgeFilter.transform.position = badgePos;
            _badgeBorderFilter.transform.position = badgePos;
            _badgeText.text = cost.ToString();
            _badgeRenderer.enabled = true;
            _badgeBorderRenderer.enabled = true;
            _badgeText.gameObject.SetActive(true);

            Vector3 apBadgePos = showEnergy ? midPosition : midPosition + Vector3.right * spacing * 0.5f;
            _apBadgeFilter.transform.position = apBadgePos;
            _apBadgeBorderFilter.transform.position = apBadgePos;
            _apBadgeText.text = apCost.ToString();
            _apBadgeRenderer.enabled = true;
            _apBadgeBorderRenderer.enabled = true;
            _apBadgeText.gameObject.SetActive(true);

            if (!showEnergy)
            {
                _energyBadgeRenderer.enabled = false;
                _energyBadgeBorderRenderer.enabled = false;
                _energyBadgeText.gameObject.SetActive(false);
                return;
            }
            Vector3 energyBadgePos = midPosition + Vector3.right * spacing;
            _energyBadgeFilter.transform.position = energyBadgePos;
            _energyBadgeBorderFilter.transform.position = energyBadgePos;
            _energyBadgeText.text = energyCost.ToString();
            _energyBadgeRenderer.enabled = true;
            _energyBadgeBorderRenderer.enabled = true;
            _energyBadgeText.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_renderer != null) _renderer.enabled = false;
            if (_outlineRenderer != null) _outlineRenderer.enabled = false;
            if (_badgeRenderer != null) _badgeRenderer.enabled = false;
            if (_badgeBorderRenderer != null) _badgeBorderRenderer.enabled = false;
            if (_badgeText != null) _badgeText.gameObject.SetActive(false);
            if (_apBadgeRenderer != null) _apBadgeRenderer.enabled = false;
            if (_apBadgeBorderRenderer != null) _apBadgeBorderRenderer.enabled = false;
            if (_apBadgeText != null) _apBadgeText.gameObject.SetActive(false);
            if (_energyBadgeRenderer != null) _energyBadgeRenderer.enabled = false;
            if (_energyBadgeBorderRenderer != null) _energyBadgeBorderRenderer.enabled = false;
            if (_energyBadgeText != null) _energyBadgeText.gameObject.SetActive(false);
        }

        // One continuous Hermite spline through every waypoint, instead of an independent
        // quadratic bow per hex-to-hex leg — a per-leg bow picks each bow's direction from
        // that leg's own endpoints alone, so consecutive legs meet at a visible kink (no
        // shared tangent) on longer, multi-hex paths. Here every waypoint gets ONE tangent,
        // built from its neighbours (central difference, so segments on either side of it
        // agree and the path stays C1-continuous through it) with the same sideways curveBend
        // lean added to every tangent — consistent handedness is what makes the whole path
        // read as one flowing arc rather than a wavy chain. A 2-point path (a single hex hop)
        // still bows, since both its tangents come from the same one segment direction plus
        // that same bend.
        private List<Vector3> BuildCurve(IReadOnlyList<Vector3> waypoints)
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
                tangents[i] = (baseDir + perpendicular * _style.curveBend).normalized;
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
        private static void SampleCurveAtDistance(List<Vector3> curve, float[] cumulative, float distance, out Vector3 position, out Vector3 tangent)
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

        // Where the cost badges sit — the curve's true arc-length midpoint (half the total
        // travelled distance, so it stays centred on the arrow even though the curve bows to
        // one side), EXCEPT never closer to the start than badgeMinDistanceFromStart (see that
        // field's own comment) — whichever of the two is farther along the path wins, capped at
        // the path's own total length so a path shorter than the minimum still places it at the
        // far end instead of overshooting past the arrowhead.
        private Vector3 BadgePosition(List<Vector3> curve)
        {
            var cumulative = new float[curve.Count];
            float totalLength = 0f;
            for (int i = 1; i < curve.Count; i++)
            {
                totalLength += Vector3.Distance(curve[i - 1], curve[i]);
                cumulative[i] = totalLength;
            }

            float minDistance = Mathf.Min(_style.badgeMinDistanceFromStart, totalLength);
            float distance = Mathf.Max(totalLength * 0.5f, minDistance);
            SampleCurveAtDistance(curve, cumulative, distance, out Vector3 position, out _);
            return position;
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
        private Mesh BuildArrowMesh(List<Vector3> curve, float widthPad, bool skipTailRing)
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

        private static Mesh BuildCircleMesh(float radius, int segments = 20)
        {
            var vertices = new List<Vector3> { Vector3.zero };
            var triangles = new List<int>();
            for (int i = 0; i <= segments; i++)
            {
                float angle = Mathf.Deg2Rad * (360f * i / segments);
                vertices.Add(new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius));
                if (i > 0)
                {
                    triangles.Add(0);
                    triangles.Add(i);
                    triangles.Add(i + 1);
                }
            }

            var mesh = new Mesh { name = "Badge" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // A hollow annulus between innerRadius and outerRadius — used for each badge's border
        // so it physically can't cover the badge sitting inside it (see the border-mesh comment
        // in Initialize).
        private static Mesh BuildRingMesh(float innerRadius, float outerRadius, int segments = 20)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int i = 0; i <= segments; i++)
            {
                float angle = Mathf.Deg2Rad * (360f * i / segments);
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);
                vertices.Add(new Vector3(cos * innerRadius, 0f, sin * innerRadius));
                vertices.Add(new Vector3(cos * outerRadius, 0f, sin * outerRadius));
                if (i > 0)
                {
                    int a0 = (i - 1) * 2, a1 = a0 + 1, b0 = i * 2, b1 = b0 + 1;
                    triangles.Add(a0); triangles.Add(a1); triangles.Add(b0);
                    triangles.Add(a1); triangles.Add(b1); triangles.Add(b0);
                }
            }

            var mesh = new Mesh { name = "BadgeBorder" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
