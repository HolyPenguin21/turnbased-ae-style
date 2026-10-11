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
        private readonly List<Vector2> screenTriangles = new List<Vector2>();
        private Vector2 marker, glowSize;
        private readonly List<float> glowWeights = new List<float>();
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
            surfaceTriangles.Clear(); screenTriangles.Clear(); glowWeights.Clear();
            var indices = CampaignGeometry.Triangulate(projected);
            for (int i = 0; i < indices.Count; i += 3)
                Subdivide(polygon[indices[i]], polygon[indices[i + 1]], polygon[indices[i + 2]],
                    projected[indices[i]], projected[indices[i + 1]], projected[indices[i + 2]], isShadow ? 0 : 3);
            marker = VisibleCenter(region);
            clicked = click; hovered = onHover; SetVerticesDirty();
        }

        // Area weighting is independent of how densely individual borders were subdivided.
        public static Vector2 InteriorCenter(RegionState region)
        {
            var p = region.PolygonVertices;
            var center = AreaCenter(p);
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

        // Center the marker in the visible polygon; do not pull outer cells toward the map center.
        public static Vector2 VisibleCenter(RegionState region)
        {
            var displayedPolygon = region.PolygonVertices.ConvertAll(CampaignMapView.Project);
            var desired = AreaCenter(displayedPolygon);
            var center = CampaignMapView.Unproject(desired);
            if (CampaignGeometry.Contains(region.PolygonVertices, center) && CampaignGeometry.Contains(displayedPolygon, desired)) return center;
            center = InteriorCenter(region);
            if (CampaignGeometry.Contains(displayedPolygon, CampaignMapView.Project(center))) return center;
            var vertices = region.PolygonVertices;
            var triangles = CampaignGeometry.Triangulate(vertices);
            var best = center; double bestScore = double.MaxValue;
            for (int t = 0; t < triangles.Count; t += 3)
                for (int a = 1; a < 12; a++)
                    for (int b = 1; a + b < 12; b++)
                    {
                        var point = (vertices[triangles[t]] * a + vertices[triangles[t + 1]] * b
                            + vertices[triangles[t + 2]] * (12 - a - b)) / 12f;
                        var projectedPoint = CampaignMapView.Project(point);
                        if (!CampaignGeometry.Contains(vertices, point) || !CampaignGeometry.Contains(displayedPolygon, projectedPoint)) continue;
                        double score = (projectedPoint - desired).sqrMagnitude;
                        if (score < bestScore) { bestScore = score; best = point; }
                    }
            return best;
        }

        private static Vector2 AreaCenter(IReadOnlyList<Vector2> vertices)
        {
            double area = 0, x = 0, y = 0;
            for (int i = 0; i < vertices.Count; i++)
            {
                var a = vertices[i]; var b = vertices[(i + 1) % vertices.Count];
                double cross = (double)a.x * b.y - (double)b.x * a.y;
                area += cross; x += (a.x + b.x) * cross; y += (a.y + b.y) * cross;
            }
            return Math.Abs(area) > 1e-12 ? new Vector2((float)(x / (3 * area)), (float)(y / (3 * area))) : vertices[0];
        }

        // Distance to the actual displayed contour, including concave notches.
        public static float SelectionGlow(Vector2 point, IReadOnlyList<Vector2> boundary, float width)
        {
            float distance = float.MaxValue;
            for (int i = 0; i < boundary.Count; i++)
            {
                var a = boundary[i]; var direction = boundary[(i + 1) % boundary.Count] - a;
                float t = direction.sqrMagnitude > 1e-12f
                    ? Math.Max(0f, Math.Min(1f, Vector2.Dot(point - a, direction) / direction.sqrMagnitude)) : 0;
                distance = Math.Min(distance, (point - a - direction * t).magnitude);
            }
            float progress = Math.Max(0f, Math.Min(1f, distance / Math.Max(.001f, width)));
            return 1 - progress * progress * (3 - 2 * progress);
        }

        private void Subdivide(Vector2 a, Vector2 b, Vector2 c, Vector2 screenA, Vector2 screenB, Vector2 screenC, int depth)
        {
            if (depth == 0)
            {
                surfaceTriangles.Add(a); surfaceTriangles.Add(b); surfaceTriangles.Add(c);
                screenTriangles.Add(screenA); screenTriangles.Add(screenB); screenTriangles.Add(screenC); return;
            }
            var ab = (a + b) / 2; var bc = (b + c) / 2; var ca = (c + a) / 2;
            var screenAB = (screenA + screenB) / 2; var screenBC = (screenB + screenC) / 2; var screenCA = (screenC + screenA) / 2;
            Subdivide(a, ab, ca, screenA, screenAB, screenCA, depth - 1);
            Subdivide(ab, b, bc, screenAB, screenB, screenBC, depth - 1);
            Subdivide(ca, bc, c, screenCA, screenBC, screenC, depth - 1);
            Subdivide(ab, bc, ca, screenAB, screenBC, screenCA, depth - 1);
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

        private void CacheSelectionGlow()
        {
            var rect = rectTransform.rect; var size = new Vector2(rect.width, rect.height);
            if (glowWeights.Count == screenTriangles.Count && (glowSize - size).sqrMagnitude == 0) return;
            glowWeights.Clear(); glowSize = size;
            var boundary = projected.ConvertAll(p => CampaignMapView.ToPixel(rect, p));
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var point in boundary)
            { minX = Math.Min(minX, point.x); minY = Math.Min(minY, point.y); maxX = Math.Max(maxX, point.x); maxY = Math.Max(maxY, point.y); }
            float width = Math.Max(18f, Math.Min(48f, Math.Min(maxX - minX, maxY - minY) * .30f));
            foreach (var point in screenTriangles)
                glowWeights.Add(SelectionGlow(CampaignMapView.ToPixel(rect, point), boundary, width));
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); if (polygon == null) return;
            if (selected && !silhouette) CacheSelectionGlow();
            var ownership = color;
            if (hover || selected) ownership = Color.Lerp(ownership, new Color(.96f, .75f, .38f), selected ? .18f : .10f);
            if (captured && Time.unscaledTime - changedTime < .6f)
                ownership = Color.Lerp(ownership, Color.white, .18f * (1 - (Time.unscaledTime - changedTime) / .6f));
            for (int i = 0; i < surfaceTriangles.Count; i += 3)
            {
                int start = vh.currentVertCount;
                for (int j = 0; j < 3; j++)
                {
                    var p = surfaceTriangles[i + j];
                    // Shared map coordinates produce continuous lighting and horizon fading.
                    var tint = silhouette ? color : ownership; tint.a = 1;
                    // Linear screen UVs preserve texture scale inside subdivided triangles.
                    AddVertex(vh, CampaignMapView.ToPixel(rectTransform.rect, screenTriangles[i + j]), tint,
                        TerrainUV(screenTriangles[i + j]), !silhouette,
                        1, selected && !silhouette ? glowWeights[i + j] : 0, p);
                }
                vh.AddTriangle(start, start + 1, start + 2);
            }
            if (silhouette) return;
            var border = selected ? new Color(1f, .68f, .18f) : attacked ? new Color(.94f, .47f, .24f)
                : attackable ? new Color(.61f, .43f, .23f) : new Color(.105f, .09f, .065f, .96f);
            float width = selected || attacked ? 1.65f : hover ? 1.15f : .95f;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = Pixel(polygon[i]); var b = Pixel(polygon[(i + 1) % polygon.Count]);
                var direction = (b - a).normalized; var n = new Vector2(-direction.y, direction.x) * width;
                int start = vh.currentVertCount;
                var uvA = TerrainUV(projected[i]);
                var uvB = TerrainUV(projected[(i + 1) % polygon.Count]);
                AddVertex(vh, a - n, border, uvA, false, mapPoint: polygon[i]); AddVertex(vh, a + n, border, uvA, false, mapPoint: polygon[i]);
                AddVertex(vh, b + n, border, uvB, false, mapPoint: polygon[(i + 1) % polygon.Count]); AddVertex(vh, b - n, border, uvB, false, mapPoint: polygon[(i + 1) % polygon.Count]);
                vh.AddTriangle(start, start + 1, start + 2); vh.AddTriangle(start, start + 2, start + 3);
            }
            Circle(vh, Pixel(marker), 6.4f, new Color(.11f, .10f, .085f), marker);
            Circle(vh, Pixel(marker), selected ? 4.9f : 4.7f, selected ? new Color(.88f, .53f, .15f) : Color.Lerp(color, new Color(.15f, .12f, .08f), .35f), marker);
        }
        private Vector2 TerrainUV(Vector2 point)
        {
            var rect = rectTransform.rect;
            return CampaignMapView.ProjectedSurfaceUV(point, rect.width / Math.Max(1, rect.height), surface == null ? 1 : (float)surface.width / surface.height);
        }
        private void AddVertex(VertexHelper vh, Vector2 position, Color tint, Vector2 uv, bool textured, float lighting = 1, float glow = 0, Vector2 mapPoint = default)
        {
            var vertex = UIVertex.simpleVert; vertex.position = position; vertex.color = tint;
            // -1 is the depth silhouette: solid color with terrain's horizon fade, without sampling.
            vertex.uv0 = uv; vertex.uv1 = new Vector4(textured ? 1 : silhouette ? -1 : 0, lighting, mapPoint.x, mapPoint.y);
            // UV data is not transformed by Canvas batching; POSITION may be in Canvas space.
            var viewport = CampaignMapView.FromPixel(rectTransform.rect, position) * .5f + new Vector2(.5f, .5f);
            vertex.uv2 = new Vector4(glow, 0, viewport.x, viewport.y); vh.AddVert(vertex);
        }
        private void Circle(VertexHelper vh, Vector2 center, float radius, Color tint, Vector2 mapPoint)
        {
            var uv = TerrainUV(CampaignMapView.Project(mapPoint));
            int start = vh.currentVertCount; AddVertex(vh, center, tint, uv, false, mapPoint: mapPoint);
            const int sides = 16;
            for (int i = 0; i <= sides; i++)
            {
                float angle = i * Mathf.PI * 2 / sides;
                AddVertex(vh, center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, tint, uv, false, mapPoint: mapPoint);
                if (i > 0) vh.AddTriangle(start, start + i, start + i + 1);
            }
        }
        private void Update() { if (captured && Time.unscaledTime - changedTime < .7f) SetVerticesDirty(); }
    }
}
