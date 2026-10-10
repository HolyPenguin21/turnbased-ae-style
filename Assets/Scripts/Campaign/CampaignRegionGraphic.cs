using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Campaign
{
    public sealed class CampaignRegionGraphic : MaskableGraphic, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        private List<Vector2> polygon;
        private List<int> triangles;
        private Action clicked;
        private Action<bool> hovered;
        private bool selected, hover, attackable, attacked, captured, enabledInput, silhouette;
        private float changedTime;
        public void Configure(RegionState region, Action click, Action<bool> onHover, bool isShadow = false)
        {
            silhouette = isShadow; raycastTarget = !isShadow;
            polygon = region.PolygonVertices.Select(CampaignMapView.Project).ToList(); triangles = CampaignGeometry.Triangulate(polygon);
            clicked = click; hovered = onHover; SetVerticesDirty();
        }
        public void Refresh(Color tint, bool selection, bool over, bool legal, bool target, bool capture, bool input)
        {
            if (color != tint) { changedTime = Time.unscaledTime; color = tint; }
            selected = selection; hover = over; attackable = legal; attacked = target; captured = capture; enabledInput = input; SetVerticesDirty();
        }
        public void OnPointerClick(PointerEventData data) { if (enabledInput) clicked?.Invoke(); }
        public void OnPointerEnter(PointerEventData data) { if (enabledInput) hovered?.Invoke(true); }
        public void OnPointerExit(PointerEventData data) { hovered?.Invoke(false); }
        public override bool Raycast(Vector2 screenPoint, Camera eventCamera)
        {
            if (!enabledInput || polygon == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, screenPoint, eventCamera, out var local)) return false;
            var rect = rectTransform.rect;
            return CampaignGeometry.Contains(polygon, new Vector2((local.x - rect.center.x) / (rect.width * .49f), (local.y - rect.center.y) / (rect.height * .59f)));
        }
        private Vector2 Pixel(Vector2 p) => rectTransform.rect.center + new Vector2(p.x * rectTransform.rect.width * .49f, p.y * rectTransform.rect.height * .59f);
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); if (polygon == null) return;
            for (int i = 0; i < polygon.Count; i++)
            {
                var p = polygon[i]; float shade = .88f + .10f * p.y - .08f * p.sqrMagnitude;
                var tint = color * shade; tint.a = 1;
                if (hover || selected) tint = Color.Lerp(tint, new Color(.86f, .8f, .59f), selected ? .32f : .18f);
                if (captured && Time.unscaledTime - changedTime < .6f) tint = Color.Lerp(tint, Color.white, .15f);
                vh.AddVert(Pixel(p), tint, Vector2.zero);
            }
            for (int i = 0; i < triangles.Count; i += 3) vh.AddTriangle(triangles[i], triangles[i + 1], triangles[i + 2]);
            if (silhouette) return;
            var border = selected ? new Color(.96f, .86f, .59f) : attacked ? new Color(.9f, .49f, .26f) : attackable ? new Color(.8f, .69f, .39f) : new Color(.10f, .13f, .13f);
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = Pixel(polygon[i]); var b = Pixel(polygon[(i + 1) % polygon.Count]); var d = (b - a).normalized;
                var n = new Vector2(-d.y, d.x) * (selected || attacked ? 2.5f : 1.2f);
                int v = vh.currentVertCount; vh.AddVert(a - n, border, Vector2.zero); vh.AddVert(a + n, border, Vector2.zero); vh.AddVert(b + n, border, Vector2.zero); vh.AddVert(b - n, border, Vector2.zero);
                vh.AddTriangle(v, v + 1, v + 2); vh.AddTriangle(v, v + 2, v + 3);
            }
        }
        private void Update() { if (captured && Time.unscaledTime - changedTime < .7f) SetVerticesDirty(); }
    }
}
