using System;
using Game.Map;
using Game.Styles;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Campaign
{
    // Presentation only. Cached tactical geometry is adapted from XZ to the existing Canvas.
    public sealed class CampaignArrowGraphic : MaskableGraphic
    {
        [Header("Timing (unscaled seconds)")]
        [SerializeField, Min(0)] private float drawSeconds = .35f;
        [SerializeField, Min(0)] private float holdSeconds = .8f;
        [SerializeField, Min(0)] private float fadeSeconds = .35f;
        [Header("Map units (Canvas reference pixels)")]
        [SerializeField, Min(1)] private float visualScale = 28;
        [SerializeField, Min(7)] private float markerClearance = 24;
        [SerializeField, Range(0, .5f)] private float regionInsetFraction = .25f;
        [Header("Tint")]
        [SerializeField] private Color attackTint = new Color(.68f, .28f, .24f, .92f);
        [SerializeField, Range(0, 1)] private float factionTintWeight = 0;
        [SerializeField, Range(0, .2f)] private float pulseAmount = .06f;
        [SerializeField, Min(0)] private float pulseFrequency = 1.5f;

        private struct Shape { public Vector3[] vertices; public Color[] colors; public int[] triangles; public Color tint; }
        private Shape fill, outline;
        private float templateLength, headLength, maxHalfWidth, elapsed, reveal, opacity;
        private Vector2 source, target;
        private float sourceExtent, targetExtent;
        private Color tint;
        private bool initialized;
        private readonly UIVertex[] clipped = new UIVertex[4];
        public bool IsPlaying { get; private set; }
        public float Duration => Math.Max(0, drawSeconds) + Math.Max(0, holdSeconds) + Math.Max(0, fadeSeconds);

        public void Initialize(MoveArrowStyle style)
        {
            if (initialized) return;
            initialized = true; raycastTarget = false;
            style = style ?? new MoveArrowStyle();
            if (style.headProfile != null)
                foreach (var point in style.headProfile) { headLength = Math.Max(headLength, point.distanceFromTip); maxHalfWidth = Math.Max(maxHalfWidth, point.halfWidth); }
            maxHalfWidth = Math.Max(maxHalfWidth, style.tailWidth * .5f) + style.outlineThickness;
            templateLength = Math.Max(2, headLength * 2);
            var geometry = new ArrowMeshGeometry(style);
            var curve = geometry.BuildCurve(new[] { Vector3.zero, new Vector3(templateLength, 0, 0) });
            fill = Sample(geometry.BuildArrowMesh(curve, 0, false), Color.white);
            outline = Sample(geometry.BuildArrowMesh(curve, style.outlineThickness, true), style.outlineColor);
        }
        private Shape Sample(Mesh mesh, Color meshTint)
        {
            var shape = new Shape { vertices = mesh.vertices, colors = mesh.colors, triangles = mesh.triangles, tint = meshTint };
            Destroy(mesh); return shape;
        }
        public void Play(Vector2 projectedSource, Vector2 projectedTarget, float fromExtent, float toExtent, Color factionColor)
        {
            if (!initialized) Initialize(null);
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
            vh.Clear(); if (!initialized || reveal <= 0 || opacity <= 0) return;
            var rect = rectTransform.rect;
            if (rect.width <= 0 || rect.height <= 0) return;
            var centers = CampaignMapView.ToPixel(rect, target) - CampaignMapView.ToPixel(rect, source);
            float centerDistance = centers.magnitude;
            float scale = Math.Min(Math.Max(1, visualScale), centerDistance / Math.Max(.001f, headLength * 4));
            if (!Endpoints(rect, source, target, Math.Max(markerClearance, centerDistance * sourceExtent * regionInsetFraction),
                Math.Max(markerClearance, centerDistance * targetExtent * regionInsetFraction), maxHalfWidth * scale + 2, out var a, out var b)) return;
            var direction = (b - a).normalized; var normal = new Vector2(-direction.y, direction.x);
            float length = (b - a).magnitude;
            scale = Math.Min(scale, length / Math.Max(.001f, headLength * 2));
            Emit(vh, outline, outline.tint, a, direction, normal, length, scale);
            Emit(vh, fill, tint, a, direction, normal, length, scale);
        }
        private void Emit(VertexHelper vh, Shape shape, Color baseColor, Vector2 origin, Vector2 direction, Vector2 normal, float length, float scale)
        {
            float neck = templateLength - headLength, shaftLength = length - headLength * scale;
            UIVertex Vertex(int index)
            {
                var p = shape.vertices[index];
                float distance = p.x <= neck ? p.x / neck * shaftLength : shaftLength + (p.x - neck) * scale;
                var vertex = UIVertex.simpleVert;
                vertex.position = origin + direction * distance + normal * (p.z * scale);
                var c = baseColor; c.a *= shape.colors[index].a * opacity;
                vertex.color = c; vertex.uv0 = new Vector2(distance, 0); return vertex;
            }
            float front = reveal >= 1 ? length + maxHalfWidth * scale : length * reveal;
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

