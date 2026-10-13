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
        private Vector2 marker;
        public Vector2 ProjectedMarker { get; private set; }
        private CampaignHighlightSettings highlight;
        private Texture2D surface;
        private Action clicked;
        private Action<bool> hovered;
        private bool selected, hover, attackable, attacked, enabledInput, silhouette, captureReveal;
        public override Texture mainTexture => silhouette || surface == null ? Texture2D.whiteTexture : surface;

        public void SetSurface(Texture2D texture, Material sharedMaterial)
        { surface = texture; material = sharedMaterial; SetMaterialDirty(); }
        public void SetHighlight(CampaignHighlightSettings settings) { highlight = settings; SetVerticesDirty(); }
        // The center dot is a fallback marker; emblem markers replace it.
        private bool centerDot = true;
        public void SetCenterDot(bool visible) { if (centerDot == visible) return; centerDot = visible; SetVerticesDirty(); }

        public void Configure(RegionState region, Action click, Action<bool> onHover, bool isShadow = false)
        {
            silhouette = isShadow; raycastTarget = !isShadow;
            polygon = new List<Vector2>(region.PolygonVertices);
            projected = polygon.ConvertAll(CampaignMapView.Project);
            surfaceTriangles.Clear(); screenTriangles.Clear();
            var indices = CampaignGeometry.Triangulate(projected);
            for (int i = 0; i < indices.Count; i += 3)
                Subdivide(polygon[indices[i]], polygon[indices[i + 1]], polygon[indices[i + 2]],
                    projected[indices[i]], projected[indices[i + 1]], projected[indices[i + 2]], isShadow ? 0 : 3);
            marker = VisibleCenter(region);
            ProjectedMarker = CampaignMapView.Project(marker);
            clicked = click; hovered = onHover; SetVerticesDirty();
        }

        // Fraction of the center-to-center segment up to the first displayed boundary.
        // Projected polygons/marker are already cached for rendering; no per-operation polygon copy.
        public float BoundaryFractionTowards(Vector2 target)
        {
            var direction = target - ProjectedMarker; float nearest = 1;
            for (int i = 0; i < projected.Count; i++)
            {
                var a = projected[i]; var edge = projected[(i + 1) % projected.Count] - a;
                float denominator = direction.x * edge.y - direction.y * edge.x;
                if (Math.Abs(denominator) < 1e-10f) continue;
                var offset = a - ProjectedMarker;
                float t = (offset.x * edge.y - offset.y * edge.x) / denominator;
                float u = (offset.x * direction.y - offset.y * direction.x) / denominator;
                if (t >= 0 && u >= 0 && u <= 1) nearest = Math.Min(nearest, t);
            }
            return nearest;
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

        // Edges shorter than this (in projected map units, the disk spans 2) gain nothing from further subdivision.
        private const float MinSubdivisionEdgeSqr = .12f * .12f;

        private void Subdivide(Vector2 a, Vector2 b, Vector2 c, Vector2 screenA, Vector2 screenB, Vector2 screenC, int depth)
        {
            // Adaptive: only triangles that are large enough to show the projection's curvature are split.
            if (depth > 0 && Math.Max((screenA - screenB).sqrMagnitude, Math.Max((screenB - screenC).sqrMagnitude, (screenC - screenA).sqrMagnitude)) < MinSubdivisionEdgeSqr)
                depth = 0;
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

        public void SetCaptureReveal(bool active)
        { if (captureReveal == active) return; captureReveal = active; SetVerticesDirty(); }

        // Refresh projects current state only; a refresh/load can never initiate capture presentation.
        public void Refresh(Color tint, bool selection, bool over, bool legal, bool target, bool input)
        {
            if (color == tint && selected == selection && hover == over && attackable == legal
                && attacked == target && enabledInput == input) return;
            color = tint; selected = selection; hover = over; attackable = legal;
            attacked = target; enabledInput = input; SetVerticesDirty();
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
            var style = highlight ?? DefaultHighlight;
            // Hover/selection/attack shading is drawn by the shader from these flags and the region distance field.
            var state = silhouette ? Vector4.zero : new Vector4(selected ? 1 : 0, hover ? 1 : 0, attackable ? 1 : 0, attacked ? 1 : 0);
            for (int i = 0; i < surfaceTriangles.Count; i += 3)
            {
                int start = vh.currentVertCount;
                for (int j = 0; j < 3; j++)
                {
                    var p = surfaceTriangles[i + j];
                    // Shared map coordinates produce continuous lighting and horizon fading.
                    var tint = color; tint.a = 1;
                    // Linear screen UVs preserve texture scale inside subdivided triangles.
                    AddVertex(vh, CampaignMapView.ToPixel(rectTransform.rect, screenTriangles[i + j]), tint,
                        TerrainUV(screenTriangles[i + j]), !silhouette, 1, state, p);
                }
                vh.AddTriangle(start, start + 1, start + 2);
            }
            if (silhouette) return;
            var border = selected ? Color.Lerp(style.SelectionColor, Color.white, .25f) : attacked ? style.AttackColor
                : attackable ? Color.Lerp(style.AttackColor, style.BorderColor, .35f) : style.BorderColor;
            float width = selected || attacked ? style.SelectionBorderWidth : hover ? style.BorderWidth * 1.2f : style.BorderWidth;
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
            if (!centerDot) return;
            Circle(vh, Pixel(marker), 6.4f, new Color(.11f, .10f, .085f), marker);
            Circle(vh, Pixel(marker), selected ? 4.9f : 4.7f, selected ? style.SelectionColor : Color.Lerp(color, new Color(.15f, .12f, .08f), .35f), marker);
        }
        private Vector2 TerrainUV(Vector2 point)
        {
            var rect = rectTransform.rect;
            return CampaignMapView.ProjectedSurfaceUV(point, rect.width / Math.Max(1, rect.height), surface == null ? 1 : (float)surface.width / surface.height);
        }
        private static readonly CampaignHighlightSettings DefaultHighlight = new CampaignHighlightSettings();
        private void AddVertex(VertexHelper vh, Vector2 position, Color tint, Vector2 uv, bool textured, float lighting = 1, Vector4 state = default, Vector2 mapPoint = default)
        {
            var vertex = UIVertex.simpleVert; vertex.position = position; vertex.color = tint;
            // -1 is the depth silhouette: solid color with terrain's horizon fade, without sampling.
            vertex.uv0 = uv; vertex.uv1 = new Vector4(textured ? 1 : silhouette ? -1 : 0, lighting, mapPoint.x, mapPoint.y);
            // UV data is not transformed by Canvas batching; POSITION may be in Canvas space.
            var viewport = CampaignMapView.FromPixel(rectTransform.rect, position) * .5f + new Vector2(.5f, .5f);
            vertex.uv2 = new Vector4(0, captureReveal && textured ? 1 : 0, viewport.x, viewport.y);
            vertex.uv3 = state; vh.AddVert(vertex);
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
    }
}

