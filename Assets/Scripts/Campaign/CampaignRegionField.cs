using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Campaign
{
    // Map look tunables; pushed to the shared map material, border lines use them on the CPU.
    // Distances are canvas units. The map palette is separate from CampaignMapView.ColorFor (UI dots, arrows).
    [Serializable]
    public sealed class CampaignHighlightSettings
    {
        [Header("Map palette")]
        public Color IronConcordColor = new Color(.95f, .52f, .18f);
        public Color AshenColor = new Color(.3f, .52f, .92f);
        public Color VesselsColor = new Color(.93f, .8f, .24f);
        [Header("Terrain")]
        [Range(.5f, 2.5f)] public float TerrainContrast = 1.4f;
        [Range(.3f, 1.5f)] public float TerrainBrightness = .8f;
        // 0 = grey relief under the tint, 1 = original sand color.
        [Range(0, 1)] public float TerrainSaturation = .35f;
        [Range(0, 1)] public float TintAmount = .55f;
        [Range(0, 1)] public float TintCenter = .15f;
        [Range(1, CampaignRegionField.Range)] public float TintEdgeWidth = 48;
        [Range(.5f, 2)] public float TintGain = 1.1f;
        [Header("Borders")]
        [Range(0, 6)] public float LineWidth = 1.6f;
        [Range(0, 2)] public float LineStrength = .8f;
        [Range(0, 6)] public float SeamWidth = 1.2f;
        [Range(0, 1)] public float SeamStrength = .7f;
        // Optional mesh outline on top of the shader edges; transparent by default.
        public Color BorderColor = new Color(.105f, .09f, .065f, 0);
        [Range(.2f, 4)] public float BorderWidth = .8f;
        [Header("Faction rim")]
        [Range(0, CampaignRegionField.Range)] public float RimWidth = 20;
        [Range(.5f, 5)] public float RimFalloff = 1.6f;
        [Range(0, 2)] public float RimStrength = .45f;
        [Range(.5f, 3)] public float RimBrightness = 1.15f;
        [Range(0, 1)] public float RimLift = .22f;
        [Header("Hover")]
        [Range(0, 1)] public float HoverBrighten = .12f;
        [Range(0, 3)] public float HoverRimBoost = 1;
        [Header("Selection")]
        public Color SelectionColor = new Color(1f, .60f, .20f);
        [Range(0, 3)] public float SelectionRimStrength = 1.4f;
        [Range(0, CampaignRegionField.Range)] public float SelectionInnerWidth = 44;
        [Range(0, 1.5f)] public float SelectionInnerStrength = .85f;
        [Range(0, CampaignRegionField.Range)] public float SelectionOuterWidth = 12;
        [Range(0, 2)] public float SelectionOuterStrength = 1.1f;
        [Range(0, 1)] public float SelectionFill = .5f;
        [Range(0, 4)] public float SelectionBorderWidth = 1f;
        [Header("Attack targets")]
        public Color AttackColor = new Color(1f, .22f, .10f);
        [Range(0, 3)] public float AttackStrength = 1.6f;
        [Range(0, 1)] public float AttackFill = .32f;
        [Range(0, 10)] public float AttackPulseSpeed = 3.2f;

        public Color MapColor(Game.Players.Faction faction) => faction == Game.Players.Faction.IronConcord ? IronConcordColor
            : faction == Game.Players.Faction.Ashen ? AshenColor : VesselsColor;

        public void ApplyTo(Material material, Texture field)
        {
            if (material == null) return;
            material.SetTexture("_RegionField", field != null ? field : Texture2D.whiteTexture);
            material.SetFloat("_FieldRange", CampaignRegionField.Range);
            material.SetFloat("_TerrainContrast", TerrainContrast); material.SetFloat("_TerrainBrightness", TerrainBrightness); material.SetFloat("_TerrainSaturation", TerrainSaturation);
            material.SetFloat("_TintAmount", TintAmount); material.SetFloat("_TintCenter", TintCenter); material.SetFloat("_TintEdgeWidth", TintEdgeWidth); material.SetFloat("_TintGain", TintGain);
            material.SetFloat("_LineWidth", LineWidth); material.SetFloat("_LineStrength", LineStrength);
            material.SetFloat("_SeamWidth", SeamWidth); material.SetFloat("_SeamStrength", SeamStrength);
            material.SetFloat("_RimWidth", RimWidth); material.SetFloat("_RimFalloff", RimFalloff); material.SetFloat("_RimStrength", RimStrength);
            material.SetFloat("_RimBrightness", RimBrightness); material.SetFloat("_RimLift", RimLift);
            material.SetFloat("_HoverBrighten", HoverBrighten); material.SetFloat("_HoverRimBoost", HoverRimBoost);
            material.SetColor("_SelectionColor", SelectionColor); material.SetFloat("_SelectionRimStrength", SelectionRimStrength);
            material.SetFloat("_SelectionInnerWidth", SelectionInnerWidth); material.SetFloat("_SelectionInnerStrength", SelectionInnerStrength);
            material.SetFloat("_SelectionOuterWidth", SelectionOuterWidth); material.SetFloat("_SelectionOuterStrength", SelectionOuterStrength);
            material.SetFloat("_SelectionFill", SelectionFill);
            material.SetColor("_AttackColor", AttackColor); material.SetFloat("_AttackStrength", AttackStrength); material.SetFloat("_AttackPulseSpeed", AttackPulseSpeed); material.SetFloat("_AttackFill", AttackFill);
        }
    }

    // Distance field over the map viewport, sampled by CampaignSurface through the batching-safe viewport UVs.
    // R: distance to the containing region's own border. G: distance to the selected region's border, both sides.
    // Built once per map/rect size; only G is rewritten when the selection changes.
    public sealed class CampaignRegionField : IDisposable
    {
        public const float Range = 64; // canvas units mapped to 0..1
        private readonly int width, height;
        private readonly float scale;
        private readonly int[] ids;
        private readonly RectInt[] bounds;
        private readonly Color32[] pixels;
        private readonly Dictionary<int, int> indexById = new Dictionary<int, int>();
        private int? selected;
        private RectInt selectedArea;
        public Texture2D Texture { get; }
        public Vector2 CanvasSize { get; }

        public CampaignRegionField(IReadOnlyList<RegionState> regions, Vector2 canvasSize, float resolution = 1)
        {
            CanvasSize = canvasSize; scale = resolution;
            width = Math.Max(2, Mathf.CeilToInt(canvasSize.x * scale)); height = Math.Max(2, Mathf.CeilToInt(canvasSize.y * scale));
            ids = new int[width * height]; bounds = new RectInt[regions.Count]; pixels = new Color32[width * height];
            for (int k = 0; k < regions.Count; k++) { indexById[regions[k].RegionId] = k; bounds[k] = Rasterize(regions[k], k + 1); }
            var own = Distances(0, 0, width, height, i => ids[i], (a, b) => a != b);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(Encode(own[i]), 255, 255, 255);
            Texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            { name = "CampaignRegionField (runtime)", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            Texture.SetPixels32(pixels); Texture.Apply(false);
        }

        public void SetSelected(int? regionId)
        {
            if (selected == regionId) return;
            selected = regionId;
            for (int y = selectedArea.yMin; y < selectedArea.yMax; y++)
                for (int x = selectedArea.xMin; x < selectedArea.xMax; x++) pixels[y * width + x].g = 255;
            selectedArea = new RectInt();
            if (regionId.HasValue && indexById.TryGetValue(regionId.Value, out int k) && bounds[k].width > 0)
            {
                int id = k + 1, margin = Mathf.CeilToInt(Range * scale) + 1;
                int x0 = Math.Max(0, bounds[k].xMin - margin), y0 = Math.Max(0, bounds[k].yMin - margin);
                int x1 = Math.Min(width, bounds[k].xMax + margin), y1 = Math.Min(height, bounds[k].yMax + margin);
                var d = Distances(x0, y0, x1, y1, i => ids[i] == id ? 1 : 0, (a, b) => a != b);
                selectedArea = new RectInt(x0, y0, x1 - x0, y1 - y0);
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++) pixels[y * width + x].g = Encode(d[(y - y0) * (x1 - x0) + (x - x0)]);
            }
            Texture.SetPixels32(pixels); Texture.Apply(false);
        }

        // Canvas-unit distances at a projected map point (the disk spans -1..1), for tests and tooling.
        public float OwnDistance(Vector2 projected) => Decode(Sample(projected).r);
        public float SelectedDistance(Vector2 projected) => Decode(Sample(projected).g);
        private Color32 Sample(Vector2 projected)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt((projected.x + 1) * .5f * width), 0, width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt((projected.y + 1) * .5f * height), 0, height - 1);
            return pixels[y * width + x];
        }
        private static byte Encode(float distance) => (byte)Mathf.RoundToInt(Mathf.Clamp01(distance / Range) * 255);
        private static float Decode(byte value) => value / 255f * Range;

        // Scanline fill of the displayed (projected) polygon at pixel centers; returns the covered pixel bounds.
        private RectInt Rasterize(RegionState region, int id)
        {
            var points = region.PolygonVertices.ConvertAll(p => { var q = CampaignMapView.Project(p); return new Vector2((q.x + 1) * .5f * width, (q.y + 1) * .5f * height); });
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in points) { minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y); }
            int xMin = width, xMax = 0, yMin = height, yMax = 0;
            var crossings = new List<float>();
            for (int y = Math.Max(0, (int)Math.Floor(minY)); y < Math.Min(height, (int)Math.Ceiling(maxY) + 1); y++)
            {
                float sy = y + .5f; crossings.Clear();
                for (int i = 0; i < points.Count; i++)
                {
                    var a = points[i]; var b = points[(i + 1) % points.Count];
                    if ((a.y <= sy) != (b.y <= sy)) crossings.Add(a.x + (sy - a.y) * (b.x - a.x) / (b.y - a.y));
                }
                crossings.Sort();
                for (int c = 0; c + 1 < crossings.Count; c += 2)
                {
                    int from = Math.Max(0, (int)Math.Ceiling(crossings[c] - .5f)), to = Math.Min(width, (int)Math.Ceiling(crossings[c + 1] - .5f));
                    for (int x = from; x < to; x++) ids[y * width + x] = id;
                    if (from < to) { xMin = Math.Min(xMin, from); xMax = Math.Max(xMax, to); yMin = Math.Min(yMin, y); yMax = Math.Max(yMax, y + 1); }
                }
            }
            return xMin < xMax ? new RectInt(xMin, yMin, xMax - xMin, yMax - yMin) : new RectInt();
        }

        // Euclidean distance (canvas units) to the nearest pixel edge where the label changes.
        // Pixels on either side of a change are seeds; outside the field counts as label 0.
        private float[] Distances(int x0, int y0, int x1, int y1, Func<int, int> label, Func<int, int, bool> differs)
        {
            int w = x1 - x0, h = y1 - y0; var f = new double[w * h];
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int l = label(y * width + x);
                    bool seed = differs(l, x > 0 ? label(y * width + x - 1) : 0) || differs(l, x + 1 < width ? label(y * width + x + 1) : 0)
                        || differs(l, y > 0 ? label((y - 1) * width + x) : 0) || differs(l, y + 1 < height ? label((y + 1) * width + x) : 0);
                    f[(y - y0) * w + (x - x0)] = seed ? 0 : 1e20;
                }
            int n = Math.Max(w, h); var d = new double[n]; var v = new int[n]; var z = new double[n + 1];
            for (int x = 0; x < w; x++) Transform(f, x, w, h, d, v, z);
            for (int y = 0; y < h; y++) Transform(f, y * w, 1, w, d, v, z);
            var result = new float[w * h];
            // A seed's center sits about half a field pixel from the actual border.
            for (int i = 0; i < result.Length; i++) result[i] = (float)((Math.Sqrt(f[i]) + .5) / scale);
            return result;
        }
        // Felzenszwalb–Huttenlocher 1D squared distance transform (lower envelope of parabolas).
        private static void Transform(double[] f, int start, int stride, int n, double[] d, int[] v, double[] z)
        {
            int k = 0; v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                double fq = f[start + q * stride] + (double)q * q, s;
                // z[0] is -inf, so k never drops below zero.
                while ((s = (fq - (f[start + v[k] * stride] + (double)v[k] * v[k])) / (2.0 * (q - v[k]))) <= z[k]) k--;
                k++; v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                double dq = q - v[k]; d[q] = dq * dq + f[start + v[k] * stride];
            }
            for (int q = 0; q < n; q++) f[start + q * stride] = d[q];
        }

        public void Dispose() { if (Texture != null) UnityEngine.Object.Destroy(Texture); }
    }
}
