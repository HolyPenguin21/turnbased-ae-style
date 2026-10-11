using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    // Rounded translucent bubble and its pointer share one mesh: no separate world-space
    // billboard, texture rotation, or fixed "bottom" arrow that breaks at screen edges.
    public sealed class ArmyRadioBubbleGraphic : MaskableGraphic
    {
        private const float Radius = 9f;
        private Vector2 _target;
        private float _markerRadius;
        private readonly Vector2[] _perimeter = new Vector2[28];
        public Color BorderColor { get; set; } = new Color(.27f, .20f, .13f, .5f);

        public void AimAt(Vector2 target, float markerRadius)
        {
            if ((_target - target).sqrMagnitude < .01f && Mathf.Abs(_markerRadius - markerRadius) < .01f) return;
            _target = target; _markerRadius = markerRadius; SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); Rect rect = rectTransform.rect;
            Vector2[] perimeter = _perimeter;
            for (int corner = 0; corner < 4; corner++)
            {
                Vector2 centre = new Vector2(corner == 0 || corner == 3 ? rect.xMax - Radius : rect.xMin + Radius,
                    corner < 2 ? rect.yMax - Radius : rect.yMin + Radius);
                for (int step = 0; step <= 6; step++)
                {
                    float angle = (corner * 90f + step * 15f) * Mathf.Deg2Rad;
                    perimeter[corner * 7 + step] = centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * Radius;
                }
            }
            vh.AddVert(rect.center, color, Vector2.zero);
            foreach (Vector2 p in perimeter) vh.AddVert(p, color, Vector2.zero);
            for (int i = 0; i < perimeter.Length; i++) vh.AddTriangle(0, i + 1, (i + 1) % perimeter.Length + 1);

            bool pointer = ArmyRadioLayout.Pointer(rect, _target, Radius, _markerRadius, out var a, out var b, out var tip);
            if (pointer)
            {
                int start = vh.currentVertCount;
                vh.AddVert(a, color, Vector2.zero); vh.AddVert(b, color, Vector2.zero); vh.AddVert(tip, color, Vector2.zero);
                vh.AddTriangle(start, start + 1, start + 2);
            }
            for (int i = 0; i < perimeter.Length; i++)
            {
                Vector2 p = perimeter[i], q = perimeter[(i + 1) % perimeter.Length];
                // Leave the pointer mouth open instead of drawing a line across its base.
                if (pointer && ((p.x == a.x && q.x == a.x) || (p.y == a.y && q.y == a.y)))
                {
                    Vector2 near = Vector2.Distance(p, a) < Vector2.Distance(p, b) ? a : b;
                    Vector2 far = near == a ? b : a;
                    Line(vh, p, near); Line(vh, far, q);
                }
                else Line(vh, p, q);
            }
            if (pointer) { Line(vh, a, tip); Line(vh, tip, b); }
        }

        private void Line(VertexHelper vh, Vector2 a, Vector2 b)
        {
            Vector2 d = (b - a).normalized; Vector2 n = new Vector2(-d.y, d.x) * .4f;
            int start = vh.currentVertCount;
            vh.AddVert(a - n, BorderColor, Vector2.zero); vh.AddVert(a + n, BorderColor, Vector2.zero);
            vh.AddVert(b + n, BorderColor, Vector2.zero); vh.AddVert(b - n, BorderColor, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
