using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace Game.Campaign
{
    [Serializable]
    public sealed class CampaignCaptureSettings
    {
        [Range(.1f, 3f)] public float SpreadSeconds = 1.275f;
        public AnimationCurve SpreadEasing = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [Range(0, .5f)] public float StampDelay = .08f;
        [Range(.1f, 3f)] public float StampSeconds = 1.3f;
        [Range(.1f, .5f)] public float StampPopFraction = .22f;
        [Range(1f, 1.6f)] public float StampStartScale = 1.24f;
        [Range(0, 1)] public float StampAlpha = .88f;
        public Vector2 StampSize = new Vector2(132, 38);
        [Range(-20, 20)] public float StampRotation = -8f;
        [Range(.001f, .08f)] public float RevealFeather = .018f;
    }

    // UI-only timed presentation. One stamp is built with the map, and no materials, objects or
    // region meshes are created per frame/capture. Uses the existing map shader and mask material.
    public sealed class CampaignCaptureEffect : MonoBehaviour
    {
        private static readonly int ProgressId = Shader.PropertyToID("_CaptureProgress");
        private static readonly int OriginId = Shader.PropertyToID("_CaptureOrigin");
        private static readonly int RadiusId = Shader.PropertyToID("_CaptureRadius");
        private static readonly int FeatherId = Shader.PropertyToID("_CaptureFeather");
        private static readonly int PreviousId = Shader.PropertyToID("_CapturePreviousColor");
        private static readonly int SizeId = Shader.PropertyToID("_SurfaceSize");
        private Material surfaceMaterial;
        private CampaignCaptureSettings settings;
        private CampaignRegionGraphic graphic;
        private RegionState target;
        private RectTransform stamp;
        private CanvasGroup stampGroup;
        private TMP_Text stampText;
        private readonly Image[] rules = new Image[4];
        private Vector2 origin, stampCenter;
        private Color previousColor;
        private float elapsed, spreadSeconds, stampDelay, stampSeconds;
        public bool IsPlaying { get; private set; }

        public void Initialize(Material sharedSurface, CampaignCaptureSettings configuration)
        {
            Finish();
            surfaceMaterial = sharedSurface; settings = configuration ?? new CampaignCaptureSettings();
            // Build can be called again; CampaignMapView has already removed the old children.
            stamp = Game.UI.CollectionUIElements.Rect(transform, "CaptureStamp");
            stamp.anchorMin = stamp.anchorMax = stamp.pivot = new Vector2(.5f, .5f);
            stampGroup = stamp.gameObject.AddComponent<CanvasGroup>();
            stampGroup.interactable = false; stampGroup.blocksRaycasts = false;
            stampText = Game.UI.CollectionUIElements.Label(stamp, "CAPTURED", 0, 0, 132, 38, 18);
            stampText.alignment = TextAlignmentOptions.Center; stampText.fontStyle = FontStyles.Bold;
            stampText.textWrappingMode = TextWrappingModes.NoWrap;
            stampText.enableAutoSizing = true; stampText.fontSizeMin = 10; stampText.fontSizeMax = 18;
            stampText.raycastTarget = false;
            var textRect = stampText.rectTransform;
            textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            for (int i = 0; i < rules.Length; i++)
            {
                var rect = Game.UI.CollectionUIElements.Rect(stamp, "InkRule" + i);
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
                rules[i] = rect.gameObject.AddComponent<Image>(); rules[i].raycastTarget = false;
            }
            stamp.gameObject.SetActive(false);
        }
        public void Play(CampaignRegionGraphic regionGraphic, RegionState source, RegionState capturedRegion, Color previousOwnerColor)
        {
            Finish();
            if (!isActiveAndEnabled || surfaceMaterial == null || stamp == null || regionGraphic == null) return;
            graphic = regionGraphic; target = capturedRegion; previousColor = previousOwnerColor;
            origin = FindOrigin(source, target);
            stampCenter = CampaignRegionGraphic.VisibleCenter(target);
            spreadSeconds = Mathf.Clamp(settings.SpreadSeconds, .1f, 3f);
            stampDelay = Mathf.Clamp(settings.StampDelay, 0, .5f);
            stampSeconds = Mathf.Clamp(settings.StampSeconds, .1f, 3f);
            var ink = Color.Lerp(CampaignMapView.ColorFor(target.OwnerFaction), new Color(.88f, .81f, .65f), .5f);
            stampText.color = ink;
            var size = new Vector2(Mathf.Clamp(settings.StampSize.x, 64, 180), Mathf.Clamp(settings.StampSize.y, 24, 56));
            stamp.sizeDelta = size; stamp.localRotation = Quaternion.Euler(0, 0, settings.StampRotation);
            for (int i = 0; i < rules.Length; i++)
            {
                rules[i].color = ink;
                var rect = rules[i].rectTransform;
                bool horizontal = i < 2;
                rect.sizeDelta = horizontal ? new Vector2(size.x, 1.5f) : new Vector2(1.5f, size.y);
                rect.anchoredPosition = horizontal ? new Vector2(0, (i == 0 ? -1 : 1) * size.y / 2)
                    : new Vector2((i == 2 ? -1 : 1) * size.x / 2, 0);
            }
            elapsed = 0; IsPlaying = true; graphic.SetCaptureReveal(true);
            UpdateVisual(0);
        }
        private void Update()
        {
            if (!IsPlaying) return;
            if (graphic == null || surfaceMaterial == null || stamp == null) { Finish(); return; }
            elapsed += Time.unscaledDeltaTime;
            if (elapsed >= spreadSeconds + stampDelay + stampSeconds) { Finish(); return; }
            UpdateVisual(elapsed);
        }
        private void UpdateVisual(float time)
        {
            var rect = ((RectTransform)transform).rect;
            var pixelOrigin = CampaignMapView.ToPixel(rect, CampaignMapView.Project(origin));
            var viewportOrigin = CampaignMapView.FromPixel(rect, pixelOrigin) * .5f + new Vector2(.5f, .5f);
            float progress = Mathf.Clamp01(time / spreadSeconds);
            if (progress > 0 && progress < 1 && settings.SpreadEasing != null)
                progress = Mathf.Clamp01(settings.SpreadEasing.Evaluate(progress));
            float feather = Mathf.Clamp(settings.RevealFeather, .001f, .08f);
            float maxRadius = RevealRadius(rect, target, origin) / Mathf.Max(1, Mathf.Min(rect.width, rect.height));
            WriteReveal(surfaceMaterial, rect, viewportOrigin, progress, (maxRadius + feather) * progress, feather);
            // MaskableGraphic uses a cached stencil copy. Updating only the base material would
            // freeze the reveal in the actual masked campaign panel (also after Canvas batching).
            var rendered = graphic.materialForRendering;
            if (rendered != null && rendered != surfaceMaterial)
                WriteReveal(rendered, rect, viewportOrigin, progress, (maxRadius + feather) * progress, feather);
            float stampTime = time - spreadSeconds - stampDelay;
            bool showStamp = stampTime >= 0;
            if (stamp.gameObject.activeSelf != showStamp) stamp.gameObject.SetActive(showStamp);
            if (!showStamp) return;
            var center = CampaignMapView.ToPixel(rect, CampaignMapView.Project(stampCenter)) - rect.center;
            // Keep the short confirmation readable near the map edge.
            float insetX = stamp.sizeDelta.x * .6f, insetY = stamp.sizeDelta.y * .8f;
            stamp.anchoredPosition = new Vector2(Mathf.Clamp(center.x, -Mathf.Max(0, rect.width / 2 - insetX), Mathf.Max(0, rect.width / 2 - insetX)),
                Mathf.Clamp(center.y, -Mathf.Max(0, rect.height / 2 - insetY), Mathf.Max(0, rect.height / 2 - insetY)));
            float stampProgress = Mathf.Clamp01(stampTime / stampSeconds);
            float pop = Mathf.Clamp01(stampProgress / Mathf.Clamp(settings.StampPopFraction, .1f, .5f));
            float scale = Mathf.Lerp(Mathf.Clamp(settings.StampStartScale, 1, 1.6f), 1, 1 - Mathf.Pow(1 - pop, 3));
            stamp.localScale = Vector3.one * scale;
            stampGroup.alpha = Mathf.Clamp01(settings.StampAlpha) * pop
                * (1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.55f, 1, stampProgress)));
        }
        private void WriteReveal(Material material, Rect rect, Vector2 viewportOrigin, float progress, float radius, float feather)
        {
            material.SetFloat(ProgressId, progress); material.SetFloat(RadiusId, radius); material.SetFloat(FeatherId, feather);
            material.SetColor(PreviousId, previousColor);
            material.SetVector(OriginId, new Vector4(viewportOrigin.x, viewportOrigin.y, 0, 0));
            material.SetVector(SizeId, new Vector4(rect.width, rect.height, 0, 0));
        }
        public void Finish()
        {
            if (graphic != null) graphic.SetCaptureReveal(false);
            if (stamp != null) stamp.gameObject.SetActive(false);
            graphic = null; target = null; IsPlaying = false;
        }
        private void OnDisable() => Finish();
        private void OnDestroy() => Finish();

        // Use the shared, displayed boundary (the same projected polygon used for drawing/hit tests).
        // Step a short distance along its inward normal, checking both spaces for concave regions.
        public static Vector2 FindOrigin(RegionState source, RegionState target)
        {
            var center = CampaignRegionGraphic.VisibleCenter(target);
            if (source == null) return center;
            Vector2 best = center; float bestDistance = float.MaxValue;
            var sourceCenter = CampaignMapView.Project(CampaignRegionGraphic.VisibleCenter(source));
            var a = target.PolygonVertices; var b = source.PolygonVertices;
            var displayed = a.ConvertAll(CampaignMapView.Project);
            for (int i = 0; i < a.Count; i++)
                for (int j = 0; j < b.Count; j++)
                {
                    if ((a[i] - b[(j + 1) % b.Count]).sqrMagnitude > 1e-12f
                        || (a[(i + 1) % a.Count] - b[j]).sqrMagnitude > 1e-12f) continue;
                    var edge = displayed[(i + 1) % a.Count] - displayed[i];
                    var midpoint = (displayed[i] + displayed[(i + 1) % a.Count]) / 2;
                    float score = (midpoint - sourceCenter).sqrMagnitude;
                    if (score >= bestDistance) continue;
                    var inward = new Vector2(-edge.y, edge.x).normalized;
                    float inset = Mathf.Min(.006f, edge.magnitude * .1f);
                    for (int attempt = 0; attempt < 10; attempt++, inset *= .5f)
                    {
                        var point = midpoint + inward * inset;
                        var original = CampaignMapView.Unproject(point);
                        if (!CampaignGeometry.Contains(displayed, point) || !CampaignGeometry.Contains(a, original)) continue;
                        best = original; bestDistance = score; break;
                    }
                }
            return best;
        }
        public static float RevealRadius(Rect viewport, RegionState region, Vector2 start)
        {
            var originPixel = CampaignMapView.ToPixel(viewport, CampaignMapView.Project(start));
            float radius = 0;
            foreach (var vertex in region.PolygonVertices)
                radius = Mathf.Max(radius, (CampaignMapView.ToPixel(viewport, CampaignMapView.Project(vertex)) - originPixel).magnitude);
            return radius;
        }
    }
}
