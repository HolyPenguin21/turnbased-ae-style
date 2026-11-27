using System;
using Game.Map;
using Game.Styles;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Campaign
{
    // Campaign profile in canvas pixels, tessellated once by the shared arrow mesh builder.
    public sealed class CampaignArrowGraphic : MaskableGraphic
    {
        [Header("Timing (unscaled seconds)")]
        [SerializeField, Min(0)] private float drawSeconds = .35f;
        [SerializeField, Min(0)] private float holdSeconds = 1.3f;
        [SerializeField, Min(0)] private float fadeSeconds = .35f;
        [Header("Map units (Canvas reference pixels)")]
        [SerializeField, Min(1)] private float headLength = 22;
        [SerializeField, Min(1)] private float headWidth = 34;
        [SerializeField, Min(1)] private float tailWidth = 63;
        [SerializeField, Min(7)] private float markerClearance = 24;
        [SerializeField, Range(0, .5f)] private float regionInsetFraction = .25f;
        [Header("Tint")]
        [SerializeField] private Color attackTint = new Color(.85f, .16f, .10f, 1f);
        [SerializeField, Range(0, 1)] private float factionTintWeight = 0;
        [SerializeField, Range(0, .2f)] private float pulseAmount = .06f;
        [SerializeField, Min(0)] private float pulseFrequency = 1.5f;

        private const float OutlineThickness = 1.25f;
        private static readonly Color OutlineTint = new Color(.05f, .04f, .06f, 1f);
        private struct Shape { public Vector3[] vertices; public Color[] colors; public int[] triangles; }
        private Shape fill, outline;
        private float elapsed, reveal, opacity;
        private Vector2 source, target;
        private float sourceExtent, targetExtent;
        private Color tint;
        private readonly UIVertex[] clipped = new UIVertex[4];
        public bool IsPlaying { get; private set; }
        public float Duration => Math.Max(0, drawSeconds) + Math.Max(0, holdSeconds) + Math.Max(0, fadeSeconds);
        private float NeckHalfWidth => headWidth / 12f;

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;
        }

        private void BuildShapes()
        {
            var style = new MoveArrowStyle
            {
                segmentsPerLeg = 20,
                tailWidth = tailWidth,
                tailAlpha = .08f,
                gradientStart = .2f,
                headProfile = new[]
                {
                    new ArrowHeadPoint { distanceFromTip = 0, halfWidth = 0 },
                    new ArrowHeadPoint { distanceFromTip = headLength * .99f, halfWidth = headWidth * .5f },
                    new ArrowHeadPoint { distanceFromTip = headLength * .77f, halfWidth = NeckHalfWidth },
                    new ArrowHeadPoint { distanceFromTip = headLength, halfWidth = NeckHalfWidth }
                }
            };
            var geometry = new ArrowMeshGeometry(style);
            var curve = geometry.BuildCurve(new[] { Vector3.zero, new Vector3(headLength * 2, 0, 0) });
            fill = Sample(geometry.BuildArrowMesh(curve, 0, false));
            outline = Sample(geometry.BuildArrowMesh(curve, OutlineThickness, true));
        }
        private static Shape Sample(Mesh mesh)
        {
            var shape = new Shape { vertices = mesh.vertices, colors = mesh.colors, triangles = mesh.triangles };
            Game.Core.UnityObjectLifetime.Destroy(mesh); return shape;
        }
        public void Play(Vector2 projectedSource, Vector2 projectedTarget, float fromExtent, float toExtent, Color factionColor)
        {
            if (fill.vertices == null) BuildShapes();
            Hide(); source = projectedSource; target = projectedTarget;
            sourceExtent = fromExtent; targetExtent = toExtent;
            tint = Color.Lerp(attackTint, factionColor, factionTintWeight); tint.a = attackTint.a;
            elapsed = 0; IsPlaying = Duration > 0 && (target - source).sqrMagnitude > 1e-10f;
            enabled = true; SetFrame(0);
        }
        public void Hide() { IsPlaying = false; reveal = opacity = 0; SetVerticesDirty(); }
        private void Update()
        {
            if (!IsPlaying) return;
            elapsed += Time.unscaledDeltaTime; SetFrame(elapsed);
        }
        // Deterministic sampling for mesh previews/tests; never changes campaign state.
        public void SetFrame(float seconds)
        {
            Evaluate(seconds, drawSeconds, holdSeconds, fadeSeconds, out reveal, out opacity);
            if (seconds >= Duration) IsPlaying = false;
            else if (seconds >= drawSeconds && seconds < drawSeconds + holdSeconds)
                opacity *= 1 - pulseAmount * (.5f + .5f * Mathf.Sin((seconds - drawSeconds) * pulseFrequency * Mathf.PI * 2));
            SetVerticesDirty();
        }
        public static void Evaluate(float seconds, float draw, float hold, float fade, out float progress, out float alpha)
        {
            draw = Math.Max(0, draw); hold = Math.Max(0, hold); fade = Math.Max(0, fade);
            progress = draw > 0 ? Mathf.Clamp01(seconds / draw) : 1;
            alpha = seconds < 0 || seconds >= draw + hold + fade ? 0
                : seconds < draw + hold ? 1 : fade > 0 ? 1 - Mathf.Clamp01((seconds - draw - hold) / fade) : 0;
        }
        protected override void OnDisable() { Hide(); base.OnDisable(); }

        // Insets are bounded by distance so tiny adjacent regions cannot invert the arrow.
        public static bool Endpoints(Rect rect, Vector2 from, Vector2 to, float fromGap, float toGap, float padding, out Vector2 a, out Vector2 b)
        {
            padding = Math.Min(Math.Max(0, padding), Math.Min(rect.width, rect.height) * .2f);
            Vector2 Clamp(Vector2 p) => new Vector2(Mathf.Clamp(p.x, rect.xMin + padding, rect.xMax - padding), Mathf.Clamp(p.y, rect.yMin + padding, rect.yMax - padding));
            a = Clamp(CampaignMapView.ToPixel(rect, from)); b = Clamp(CampaignMapView.ToPixel(rect, to));
            var delta = b - a; float distance = delta.magnitude;
            if (distance < .001f) return false;
            var direction = delta / distance;
            a += direction * Math.Min(Math.Max(0, fromGap), distance * .3f);
            b -= direction * Math.Min(Math.Max(0, toGap), distance * .3f);
            return true;
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); if (fill.vertices == null || reveal <= 0 || opacity <= 0) return;
            var rect = rectTransform.rect;
            if (rect.width <= 0 || rect.height <= 0) return;
            float centerDistance = Vector2.Distance(CampaignMapView.ToPixel(rect, source), CampaignMapView.ToPixel(rect, target));
            float sourceDepth = centerDistance * sourceExtent;
            float targetDepth = centerDistance * targetExtent;
            float fittingLength = Math.Max(2 * headLength, 1.25f * tailWidth);
            float centerScale = Mathf.Clamp01(centerDistance / fittingLength);
            float headScale = Math.Min(centerScale, targetDepth / Math.Max(headLength, headWidth * .5f));
            float tailScale = Math.Min(centerScale, sourceDepth / (tailWidth * .5f));

            float padding = Math.Max((headWidth * .5f + OutlineThickness) * headScale,
                (tailWidth * .5f + OutlineThickness) * tailScale) + 2;
            // Keep close markers distinct when both are near the same corner of the map.
            padding = Math.Min(padding, centerDistance * .4f);
            if (!Endpoints(rect, source, target, MarkerGap(sourceDepth), MarkerGap(targetDepth), padding, out var a, out var b)) return;

            Vector2 delta = b - a;
            float length = delta.magnitude;
            Vector2 direction = delta / length;
            float pathScale = length / fittingLength;
            headScale = Math.Min(headScale, pathScale);
            tailScale = Math.Min(tailScale, pathScale);
            Emit(vh, outline, OutlineTint, a, direction, length, headScale, tailScale);
            Emit(vh, fill, tint, a, direction, length, headScale, tailScale);
        }

        // Small regions can have less room than the standard marker clearance.
        private float MarkerGap(float depth) => Math.Min(Math.Max(markerClearance, depth * regionInsetFraction), depth * .65f);

        private void Emit(VertexHelper vh, Shape shape, Color baseColor, Vector2 origin, Vector2 direction, float length, float headScale, float tailScale)
        {
            var normal = new Vector2(-direction.y, direction.x);
            float shaftLength = length - headLength * headScale;
            float tailHalfWidth = tailWidth * .5f;
            float neckHalfWidth = NeckHalfWidth;
            UIVertex Vertex(int index)
            {
                var p = shape.vertices[index];
                float alongShaft = Mathf.Clamp01(p.x / headLength);
                float distance = alongShaft * shaftLength + Math.Max(0, p.x - headLength) * headScale;
                float fittedHalfWidth = Mathf.Lerp(tailHalfWidth * tailScale, neckHalfWidth * headScale, alongShaft);
                float widthScale = fittedHalfWidth / Mathf.Lerp(tailHalfWidth, neckHalfWidth, alongShaft);
                var vertex = UIVertex.simpleVert;
                vertex.position = origin + direction * distance + normal * (p.z * widthScale);
                var c = baseColor; c.a *= shape.colors[index].a * opacity;
                vertex.color = c; vertex.uv0 = new Vector2(distance, 0); return vertex;
            }
            float front = reveal >= 1 ? length + OutlineThickness * headScale : length * reveal;
            for (int i = 0; i < shape.triangles.Length; i += 3)
            {
                var v0 = Vertex(shape.triangles[i]); var v1 = Vertex(shape.triangles[i + 1]); var v2 = Vertex(shape.triangles[i + 2]);
                int count = 0;
                ClipEdge(v2, v0, front, ref count); ClipEdge(v0, v1, front, ref count); ClipEdge(v1, v2, front, ref count);
                if (count < 3) continue;
                int start = vh.currentVertCount;
                for (int j = 0; j < count; j++) { var v = clipped[j]; v.uv0 = Vector2.zero; vh.AddVert(v); }
                for (int j = 1; j < count - 1; j++) vh.AddTriangle(start, start + j, start + j + 1);
            }
        }
        private void ClipEdge(UIVertex previous, UIVertex current, float front, ref int count)
        {
            bool wasInside = previous.uv0.x <= front, inside = current.uv0.x <= front;
            if (wasInside != inside)
            {
                float t = (front - previous.uv0.x) / (current.uv0.x - previous.uv0.x);
                var v = UIVertex.simpleVert; v.position = Vector3.Lerp(previous.position, current.position, t);
                v.color = Color.Lerp(previous.color, current.color, t); v.uv0 = new Vector2(front, 0); clipped[count++] = v;
            }
            if (inside) clipped[count++] = current;
        }
    }
}

