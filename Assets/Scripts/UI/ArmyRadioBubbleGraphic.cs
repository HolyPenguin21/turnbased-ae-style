using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Reference: light text over a feathered dark wash, a gold radio rule/signal,
    // and a thin wire. The original rounded speech balloon is deliberately absent.
    public sealed class ArmyRadioBubbleGraphic : MaskableGraphic
    {
        [Range(0f, 20f)] [SerializeField] private float cornerRadius = 9f;
        [Min(0f)] [SerializeField] private float borderWidth = .9f;
        [SerializeField] private Color borderColor = new Color(.78f, .61f, .35f, .9f);
        [Min(0f)] [SerializeField] private float backgroundFeather = 16f;
        [Min(0f)] [SerializeField] private float signalRadius = 2.5f;
        public float CornerRadius => Mathf.Clamp(cornerRadius, 0f, 20f);
        public float SignalExtent => Mathf.Max(0f, signalRadius) + 6f + Mathf.Max(0f, borderWidth);
        private Vector2 _target;
        private float _markerRadius;
        public Color BorderColor { get => borderColor; set { borderColor = value; SetVerticesDirty(); } }

        public void AimAt(Vector2 target, float markerRadius)
        {
            if ((_target - target).sqrMagnitude < .01f && Mathf.Abs(_markerRadius - markerRadius) < .01f) return;
            _target = target; _markerRadius = markerRadius; SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); Rect rect = rectTransform.rect;
            Wash(vh, rect);
            float radius = Mathf.Min(CornerRadius, Mathf.Max(0f, Mathf.Min(rect.width, rect.height) * .5f - 4f));
            bool pointer = ArmyRadioLayout.Pointer(rect, _target, radius, _markerRadius, out var a, out var b, out var tip);
            Vector2 start = pointer ? (a + b) * .5f : new Vector2(rect.xMax - radius, rect.yMin);
            // Underline follows the text; when the wire exits below, it meets the radio dot.
            float end = pointer && Mathf.Abs(start.y - rect.yMin) < .01f ? start.x : rect.xMax - radius;
            float ruleStart = pointer && Mathf.Abs(start.y - rect.yMin) < .01f && start.x < rect.center.x
                ? rect.xMax - radius : rect.xMin + radius;
            Line(vh, new Vector2(ruleStart, rect.yMin), new Vector2(end, rect.yMin));
            if (!pointer) return;
            Line(vh, start, tip);
            Disc(vh, start, Mathf.Max(0f, signalRadius));
            Vector2 direction = (tip - start).normalized;
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            Arc(vh, start, signalRadius + 3f, angle - 50f, angle + 50f);
            Arc(vh, start, signalRadius + 6f, angle - 50f, angle + 50f);
        }

        private void Wash(VertexHelper vh, Rect rect)
        {
            if (color.a <= 0f) return;
            float feather = Mathf.Max(0f, backgroundFeather);
            Rect inner = ArmyRadioLayout.Inset(rect, Mathf.Min(feather, Mathf.Min(rect.width, rect.height) * .25f));
            Rect outer = rect;
            Color clear = color; clear.a = 0f;
            int start = vh.currentVertCount;
            vh.AddVert(new Vector2(inner.xMin, inner.yMin), color, Vector2.zero);
            vh.AddVert(new Vector2(inner.xMax, inner.yMin), color, Vector2.zero);
            vh.AddVert(new Vector2(inner.xMax, inner.yMax), color, Vector2.zero);
            vh.AddVert(new Vector2(inner.xMin, inner.yMax), color, Vector2.zero);
            vh.AddVert(new Vector2(outer.xMin, outer.yMin), clear, Vector2.zero);
            vh.AddVert(new Vector2(outer.xMax, outer.yMin), clear, Vector2.zero);
            vh.AddVert(new Vector2(outer.xMax, outer.yMax), clear, Vector2.zero);
            vh.AddVert(new Vector2(outer.xMin, outer.yMax), clear, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
            for (int i = 0; i < 4; i++)
            {
                int next = (i + 1) % 4;
                vh.AddTriangle(start + i, start + 4 + i, start + 4 + next);
                vh.AddTriangle(start + i, start + 4 + next, start + next);
            }
        }
        private void Disc(VertexHelper vh, Vector2 centre, float radius)
        {
            int start = vh.currentVertCount;
            vh.AddVert(centre, BorderColor, Vector2.zero);
            for (int i = 0; i < 16; i++)
            {
                float angle = i * Mathf.PI / 8f;
                vh.AddVert(centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, BorderColor, Vector2.zero);
            }
            for (int i = 0; i < 16; i++) vh.AddTriangle(start, start + i + 1, start + (i + 1) % 16 + 1);
        }
        private void Arc(VertexHelper vh, Vector2 centre, float radius, float from, float to)
        {
            Vector2 previous = centre + new Vector2(Mathf.Cos(from * Mathf.Deg2Rad), Mathf.Sin(from * Mathf.Deg2Rad)) * radius;
            for (int i = 1; i <= 10; i++)
            {
                float angle = Mathf.Lerp(from, to, i / 10f) * Mathf.Deg2Rad;
                Vector2 next = centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                Line(vh, previous, next); previous = next;
            }
        }

        private void Line(VertexHelper vh, Vector2 a, Vector2 b)
        {
            Vector2 d = (b - a).normalized; Vector2 n = new Vector2(-d.y, d.x) * (Mathf.Max(0f, borderWidth) * .5f);
            int start = vh.currentVertCount;
            vh.AddVert(a - n, BorderColor, Vector2.zero); vh.AddVert(a + n, BorderColor, Vector2.zero);
            vh.AddVert(b + n, BorderColor, Vector2.zero); vh.AddVert(b - n, BorderColor, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
