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
        private ArrowMeshGeometry geometry;

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
            geometry = new ArrowMeshGeometry(_style);

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

            List<Vector3> curve = geometry.BuildCurve(hexCenters);

            _filter.mesh = geometry.BuildArrowMesh(curve, 0f, skipTailRing: false);
            // Fully opaque — a partial alpha here let the terrain underneath show through,
            // and since the terrain's own colour varies along the arrow's length, the exact
            // same _Color value visibly read as different shades at the tip vs. the tail.
            _material.SetColor("_Color", new Color(color.r, color.g, color.b, 1f));
            _renderer.enabled = true;

            // Outline skips just its very first ring (see BuildArrowMesh's skipTailRing note) —
            // every other shaft ring, and the whole head, still get the normal outline padding.
            _outlineFilter.mesh = geometry.BuildArrowMesh(curve, _style.outlineThickness, skipTailRing: true);
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
            ArrowMeshGeometry.SampleCurveAtDistance(curve, cumulative, distance, out Vector3 position, out _);
            return position;
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
