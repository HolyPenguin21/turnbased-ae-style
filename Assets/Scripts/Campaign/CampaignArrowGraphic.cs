using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Campaign
{
    public sealed class CampaignArrowGraphic : MaskableGraphic
    {
        private Vector2? source, target;
        public void Set(Vector2? a, Vector2? b) { source = a; target = b; SetVerticesDirty(); }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); if (!source.HasValue || !target.HasValue) return;
            Vector2 Pixel(Vector2 p) => CampaignMapView.ToPixel(rectTransform.rect, p);
            var a = Pixel(source.Value); var b = Pixel(target.Value); var d = (b - a).normalized; var n = new Vector2(-d.y, d.x);
            var color = new Color(.93f, .78f, .49f);
            vh.AddVert(a - n * 2, color, Vector2.zero); vh.AddVert(a + n * 2, color, Vector2.zero); vh.AddVert(b + n * 2, color, Vector2.zero); vh.AddVert(b - n * 2, color, Vector2.zero);
            vh.AddTriangle(0, 1, 2); vh.AddTriangle(0, 2, 3);
            vh.AddVert(b, color, Vector2.zero); vh.AddVert(b - d * 20 + n * 10, color, Vector2.zero); vh.AddVert(b - d * 20 - n * 10, color, Vector2.zero); vh.AddTriangle(4, 5, 6);
        }
    }
}

