using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Campaign
{
    public sealed class CampaignRegionGraphic : MaskableGraphic, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        private List<Vector2> polygon, projected;
        private readonly List<Vector2> surfaceTriangles = new List<Vector2>();
        private Vector2 marker;
        private Texture2D surface;
        private Action clicked;
        private Action<bool> hovered;
        private bool selected, hover, attackable, attacked, captured, enabledInput, silhouette;
        private float changedTime;
        public override Texture mainTexture => silhouette || surface == null ? Texture2D.whiteTexture : surface;

        public void SetSurface(Texture2D texture, Material sharedMaterial)
        { surface = texture; material = sharedMaterial; SetMaterialDirty(); }

        public void Configure(RegionState region, Action click, Action<bool> onHover, bool isShadow = false)
        {
            silhouette = isShadow; raycastTarget = !isShadow;
            polygon = new List<Vector2>(region.PolygonVertices);
            projected = polygon.ConvertAll(CampaignMapView.Project);
            surfaceTriangles.Clear();
            var indices = CampaignGeometry.Triangulate(polygon);
            for (int i = 0; i < indices.Count; i += 3)
                Subdivide(polygon[indices[i]], polygon[indices[i + 1]], polygon[indices[i + 2]], isShadow ? 0 : 3);
            marker = InteriorCenter(region);
            clicked = click; hovered = onHover; SetVerticesDirty();
        }

        // A concave generated cell's vertex average can lie outside it. Use an actual interior triangle.
        public static Vector2 InteriorCenter(RegionState region)
        {
            var p = region.PolygonVertices;
            var center = CampaignGeometry.Center(region);
            if (CampaignGeometry.Contains(p, center)) return center;
            var indices = CampaignGeometry.Triangulate(p);
            double largest = -1; Vector2 result = center;
            for (int i = 0; i < indices.Count; i += 3)
            {
                var a = p[indices[i]]; var b = p[indices[i + 1]]; var c = p[indices[i + 2]];
                double area = CampaignGeometry.Cross(a, b, c);
                if (area > largest) { largest = area; result = (a + b + c) / 3; }
            }
            return result;
        }

        private void Subdivide(Vector2 a, Vector2 b, Vector2 c, int depth)
        {
            if (depth == 0) { surfaceTriangles.Add(a); surfaceTriangles.Add(b); surfaceTriangles.Add(c); return; }
            var ab = (a + b) / 2; var bc = (b + c) / 2; var ca = (c + a) / 2;
            Subdivide(a, ab, ca, depth - 1); Subdivide(ab, b, bc, depth - 1);
            Subdivide(ca, bc, c, depth - 1); Subdivide(ab, bc, ca, depth - 1);
        }

        public void Refresh(Color tint, bool selection, bool over, bool legal, bool target, bool capture, bool input)
        {
            if (color == tint && selected == selection && hover == over && attackable == legal
                && attacked == target && captured == capture && enabledInput == input) return;
            if (color != tint) changedTime = Time.unscaledTime;
            color = tint; selected = selection; hover = over; attackable = legal;
            attacked = target; captured = capture; enabledInput = input; SetVerticesDirty();
        }
        public void OnPointerClick(PointerEventData data) { if (enabledInput) clicked?.Invoke(); }
        public void OnPointerEnter(PointerEventData data) { if (enabledInput) hovered?.Invoke(true); }
        public void OnPointerExit(PointerEventData data) { hovered?.Invoke(false); }
        public override bool Raycast(Vector2 screenPoint, Camera eventCamera)
        {
            if (!enabledInput || polygon == null || rectTransform.rect.width <= 0 || rectTransform.rect.height <= 0
                || !base.Raycast(screenPoint, eventCamera)
                || !RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, screenPoint, eventCamera, out var local)) return false;
            var point = CampaignMapView.FromPixel(rectTransform.rect, local);
            return CampaignGeometry.Contains(projected, point);
        }
        private Vector2 Pixel(Vector2 p) => CampaignMapView.ToPixel(rectTransform.rect, CampaignMapView.Project(p));

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); if (polygon == null) return;
            var ownership = color;
            if (hover || selected) ownership = Color.Lerp(ownership, new Color(1f, .86f, .59f), selected ? .23f : .10f);
            if (captured && Time.unscaledTime - changedTime < .6f)
                ownership = Color.Lerp(ownership, Color.white, .18f * (1 - (Time.unscaledTime - changedTime) / .6f));
            for (int i = 0; i < surfaceTriangles.Count; i += 3)
            {
                int start = vh.currentVertCount;
                for (int j = 0; j < 3; j++)
                {
                    var p = surfaceTriangles[i + j];
                    // Shared map coordinates produce continuous shading and UVs across every cell.
                    float radius = Mathf.Clamp01(p.sqrMagnitude);
                    float shade = .94f + .06f * p.y - .34f * radius * radius;
                    var tint = silhouette ? color : ownership; tint.a = 1;
                    AddVertex(vh, Pixel(p), tint, CampaignMapView.SurfaceUV(p), !silhouette, shade);
                }
                vh.AddTriangle(start, start + 1, start + 2);
            }
            if (silhouette) return;
            var border = selected ? new Color(1f, .78f, .32f) : attacked ? new Color(.94f, .47f, .24f)
                : attackable ? new Color(.85f, .68f, .36f) : new Color(.16f, .145f, .115f, .8f);
            float width = selected || attacked ? 2f : hover || attackable ? 1.2f : .55f;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = Pixel(polygon[i]); var b = Pixel(polygon[(i + 1) % polygon.Count]);
                var direction = (b - a).normalized; var n = new Vector2(-direction.y, direction.x) * width;
                int start = vh.currentVertCount;
                AddVertex(vh, a - n, border, Vector2.zero, false); AddVertex(vh, a + n, border, Vector2.zero, false);
                AddVertex(vh, b + n, border, Vector2.zero, false); AddVertex(vh, b - n, border, Vector2.zero, false);
                vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
            }
            Circle(vh, Pixel(marker), 5.5f, new Color(.11f, .10f, .085f));
            Circle(vh, Pixel(marker), selected ? 4.1f : 3.9f, selected ? new Color(1f, .69f, .24f) : color);
        }
        private static void AddVertex(VertexHelper vh, Vector2 position, Color tint, Vector2 uv, bool textured, float lighting = 1)
        {
            var vertex = UIVertex.simpleVert; vertex.position = position; vertex.color = tint;
            vertex.uv0 = uv; vertex.uv1 = new Vector2(textured ? 1 : 0, lighting); vh.AddVert(vertex);
        }
        private static void Circle(VertexHelper vh, Vector2 center, float radius, Color tint)
        {
            int start = vh.currentVertCount; AddVertex(vh, center, tint, Vector2.zero, false);
            const int sides = 16;
            for (int i = 0; i <= sides; i++)
            {
                float angle = i * Mathf.PI * 2 / sides;
                AddVertex(vh, center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, tint, Vector2.zero, false);
                if (i > 0) vh.AddTriangle(start, start + i, start + i + 1);
            }
        }
        private void Update() { if (captured && Time.unscaledTime - changedTime < .7f) SetVerticesDirty(); }
    }
}
