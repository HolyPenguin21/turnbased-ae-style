using Game.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Campaign
{
    // UI-only projection and meshes. Authoritative polygons/neighbors are never modified.
    public sealed class CampaignMapView : MonoBehaviour
    {
        private readonly Dictionary<int, CampaignRegionGraphic> graphics = new Dictionary<int, CampaignRegionGraphic>();
        [SerializeField] private CampaignArrowGraphic arrow;
        public bool AttackAnimationPlaying => arrow != null && arrow.IsPlaying;
        private Material surfaceMaterial;
        [SerializeField] private CampaignCaptureSettings capture = new CampaignCaptureSettings();
        private CampaignCaptureEffect captureEffect;
        [SerializeField] private CampaignHighlightSettings highlight = new CampaignHighlightSettings();
        private CampaignRegionField field;
        private IReadOnlyList<RegionState> fieldRegions;
        public CampaignHighlightSettings Highlight => highlight;
        // Optional emblem + name over every region; without a prefab only the selected region is labelled.
        [SerializeField] private CampaignRegionMarkerView markerPrefab;
        // Off: markers show only the name plate, and only for the selected region and its legal attack targets.
        [SerializeField] private bool markerEmblems;
        private readonly Dictionary<int, CampaignRegionMarkerView> markers = new Dictionary<int, CampaignRegionMarkerView>();
        private readonly Dictionary<int, Vector2> markerCenters = new Dictionary<int, Vector2>();
        private RectTransform markerRoot;
        public Func<Game.Players.Faction, Sprite> EmblemFor { get; set; }
        public bool IsCapturePlaying => captureEffect != null && captureEffect.IsPlaying;
        private RectTransform selectedLabelRoot;
        private TMPro.TMP_Text selectedLabel;
        public Texture2D SurfaceTexture { get; private set; }
        // Map the disk to a rounded rectangle, filling the panel's corners without cropping cells.
        // A radial bijection keeps saves and shared boundaries in their original map space.
        public static Vector2 Project(Vector2 p) => p.sqrMagnitude < 1e-12f ? Vector2.zero : p * (float)(.98 * Math.Sqrt(p.sqrMagnitude) / Math.Pow(Math.Pow(p.x, 8) + Math.Pow(p.y, 8), .125));
        public static Vector2 Unproject(Vector2 p) => p.sqrMagnitude < 1e-12f ? Vector2.zero : p * (float)(Math.Pow(Math.Pow(p.x, 8) + Math.Pow(p.y, 8), .125) / (.98 * Math.Sqrt(p.sqrMagnitude)));
        public static Vector2 ToPixel(Rect rect, Vector2 projected) => rect.center + new Vector2(projected.x * rect.width * .50f, projected.y * rect.height * .50f);
        public static Vector2 FromPixel(Rect rect, Vector2 pixel) => new Vector2((pixel.x - rect.center.x) / (rect.width * .50f), (pixel.y - rect.center.y) / (rect.height * .50f));
        // Cover the viewport by cropping, preserving the texture's pixel aspect ratio.
        public static Vector2 SurfaceUV(Vector2 point, float viewportAspect = 1, float textureAspect = 1) => ProjectedSurfaceUV(Project(point), viewportAspect, textureAspect);
        public static Vector2 ProjectedSurfaceUV(Vector2 projected, float viewportAspect = 1, float textureAspect = 1) => new Vector2(.5f + projected.x * .48f * Math.Min(1, viewportAspect / textureAspect), .5f + projected.y * .48f * Math.Min(1, textureAspect / viewportAspect));
        public Vector2 SurfaceUVAt(Vector2 point)
        {
            var rect = ((RectTransform)transform).rect;
            return SurfaceUV(point, rect.width / Math.Max(1, rect.height), SurfaceTexture == null ? 1 : (float)SurfaceTexture.width / SurfaceTexture.height);
        }
        public static string BackgroundResourcePath(int backgroundId)
        {
            if (backgroundId < 0 || backgroundId > CampaignMapGenerator.BackgroundCount) throw new ArgumentOutOfRangeException(nameof(backgroundId));
            return backgroundId == 0 ? "Campaign/Backgrounds/WastelandSurface" : "Campaign/Backgrounds/WastelandSurface_" + backgroundId.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }
        public void Build(IReadOnlyList<RegionState> regions, Action<int> clicked, Action<int?> hovered, int backgroundId = 0, Game.Styles.MoveArrowStyle arrowStyle = null)
        {
            CancelCapture(); CancelAttackAnimation();
            foreach (Transform child in transform) { if (arrow != null && child == arrow.transform) continue; child.gameObject.SetActive(false); Destroy(child.gameObject); }
            graphics.Clear(); markers.Clear(); markerCenters.Clear(); markerRoot = null;
            SurfaceTexture = GameAssetCatalog.Load<Texture2D>(BackgroundResourcePath(backgroundId));
            var shader = GameAssetCatalog.Load<Shader>("Shaders/CampaignSurface");
            if (SurfaceTexture == null || shader == null) throw new InvalidOperationException("Campaign surface assets are missing.");
            if (surfaceMaterial != null) Destroy(surfaceMaterial);
            surfaceMaterial = new Material(shader) { name = "CampaignSurface (runtime)" };
            OnRectTransformDimensionsChange();
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null) canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2 | AdditionalCanvasShaderChannels.TexCoord3;
            fieldRegions = regions; RebuildField();
            foreach (var region in regions)
            {
                var shadow = new GameObject("PlanetDepth_" + region.RegionId, typeof(RectTransform), typeof(CanvasRenderer)); shadow.transform.SetParent(transform, false);
                var rect = (RectTransform)shadow.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = new Vector2(0, -12);
                var graphic = shadow.AddComponent<CampaignRegionGraphic>(); graphic.SetSurface(SurfaceTexture, surfaceMaterial); graphic.Configure(region, null, null, true);
                graphic.Refresh(new Color(.023f, .027f, .027f), false, false, false, false, false);
            }
            foreach (var region in regions)
            {
                var go = new GameObject("Region_" + region.RegionId + "_" + region.Name, typeof(RectTransform), typeof(CanvasRenderer)); go.transform.SetParent(transform, false);
                var rect = (RectTransform)go.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
                var graphic = go.AddComponent<CampaignRegionGraphic>();
                graphic.SetSurface(SurfaceTexture, surfaceMaterial); graphic.SetHighlight(highlight);
                graphic.Configure(region, () => clicked(region.RegionId), active => hovered(active ? (int?)region.RegionId : null)); graphics.Add(region.RegionId, graphic);
            }
            if (markerPrefab != null)
            {
                markerRoot = Game.UI.CollectionUIElements.Rect(transform, "RegionMarkers");
                markerRoot.anchorMin = Vector2.zero; markerRoot.anchorMax = Vector2.one; markerRoot.offsetMin = markerRoot.offsetMax = Vector2.zero;
                foreach (var region in regions)
                {
                    var marker = Instantiate(markerPrefab, markerRoot, false); marker.name = "Marker_" + region.RegionId;
                    foreach (var g in marker.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                    var rt = (RectTransform)marker.transform; rt.anchorMin = rt.anchorMax = new Vector2(.5f, .5f);
                    markers.Add(region.RegionId, marker); graphics[region.RegionId].SetCenterDot(!markerEmblems); markerCenters[region.RegionId] = Project(CampaignRegionGraphic.VisibleCenter(region));
                }
            }
            if (arrow == null) // Compatibility for code-built maps/tests; the Campaign scene authors this child.
            {
                var ar = new GameObject("AttackDirection", typeof(RectTransform), typeof(CanvasRenderer)); ar.transform.SetParent(transform, false);
                var rt = (RectTransform)ar.transform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
                arrow = ar.AddComponent<CampaignArrowGraphic>();
            }
            arrow.Initialize(arrowStyle); arrow.transform.SetAsLastSibling();
            var labelRect = Game.UI.CollectionUIElements.Rect(transform, "SelectedRegionName");
            // Dark plate keeps the name readable over the bright selection glow.
            var plate = labelRect.gameObject.AddComponent<Image>(); plate.raycastTarget = false; plate.color = new Color(.05f, .042f, .032f, .6f);
            var rim = labelRect.gameObject.AddComponent<Outline>(); rim.effectColor = new Color(.78f, .56f, .27f, .75f); rim.effectDistance = new Vector2(1, -1);
            var text = Game.UI.CollectionUIElements.Label(labelRect, "", 0, 0, 190, 28, 17);
            var textRect = text.rectTransform; textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one; textRect.offsetMin = new Vector2(12, 0); textRect.offsetMax = new Vector2(-12, 0);
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.color = new Color(.95f, .89f, .78f);
            text.fontStyle = TMPro.FontStyles.Bold;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap; text.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            selectedLabelRoot = labelRect; selectedLabel = text;
            captureEffect = GetComponent<CampaignCaptureEffect>() ?? gameObject.AddComponent<CampaignCaptureEffect>();
            captureEffect.Initialize(surfaceMaterial, capture);
        }
        public bool PlayCapture(CampaignRegionOwnershipChange change, CampaignState state)
        {
            if (!change.Matches(state) || captureEffect == null || IsCapturePlaying
                || !graphics.TryGetValue(change.TargetRegionId, out var graphic)) return false;
            var target = state.Regions.Find(r => r.RegionId == change.TargetRegionId);
            var source = state.Regions.Find(r => r.RegionId == change.SourceRegionId);
            captureEffect.Play(graphic, source, target, highlight.MapColor(change.PreviousOwner));
            if (selectedLabelRoot != null) selectedLabelRoot.gameObject.SetActive(false);
            return IsCapturePlaying;
        }
        public void CancelCapture() { if (captureEffect != null) captureEffect.Finish(); }
        public void Refresh(CampaignState state, int? selected, int? hovered, bool inputEnabled)
        {
            var size = ((RectTransform)transform).rect.size;
            if (fieldRegions != null && (field == null || field.CanvasSize != size)) RebuildField();
            field?.SetSelected(IsCapturePlaying ? null : selected);
            highlight.ApplyTo(surfaceMaterial, field?.Texture);
            foreach (var r in state.Regions)
            {
                bool attackable = selected.HasValue && CampaignRules.CanAttack(state, state.HumanFaction, selected.Value, r.RegionId);
                var op = state.PendingOperation;
                if (!graphics.TryGetValue(r.RegionId, out var graphic)) continue;
                graphic.Refresh(highlight.MapColor(r.OwnerFaction), selected == r.RegionId, hovered == r.RegionId, attackable,
                    op?.TargetRegionId == r.RegionId, inputEnabled && !IsCapturePlaying);
            }
            // The selected contour draws above its neighbours' halves of the shared border lines.
            var overlay = markerRoot != null ? markerRoot.transform : arrow != null ? arrow.transform : null;
            if (selected.HasValue && graphics.TryGetValue(selected.Value, out var top) && overlay != null && overlay.parent == transform
                && top.transform.GetSiblingIndex() != overlay.GetSiblingIndex() - 1)
                top.transform.SetSiblingIndex(overlay.GetSiblingIndex() - 1);
            RefreshMarkers(state, selected);
            if (selectedLabelRoot == null) return;
            var labelRegion = selected.HasValue && markers.Count == 0 ? state.Regions.Find(r => r.RegionId == selected.Value) : null;
            selectedLabelRoot.gameObject.SetActive(labelRegion != null && !IsCapturePlaying);
            if (labelRegion != null)
            {
                var rect = (RectTransform)transform;
                var position = ToPixel(rect.rect, Project(CampaignRegionGraphic.VisibleCenter(labelRegion)));
                selectedLabelRoot.anchorMin = selectedLabelRoot.anchorMax = new Vector2(.5f, .5f);
                selectedLabelRoot.pivot = new Vector2(.5f, .5f);
                selectedLabel.text = labelRegion.Name.ToUpperInvariant();
                float labelWidth = Mathf.Clamp(selectedLabel.GetPreferredValues(selectedLabel.text).x + 28, 110, 320);
                selectedLabelRoot.sizeDelta = new Vector2(labelWidth, 30);
                var labelPosition = position - rect.rect.center + new Vector2(0, -24);
                labelPosition.x = Mathf.Clamp(labelPosition.x, -rect.rect.width / 2 + labelWidth / 2 + 8, rect.rect.width / 2 - labelWidth / 2 - 8);
                labelPosition.y = Mathf.Clamp(labelPosition.y, -rect.rect.height / 2 + 22, rect.rect.height / 2 - 22);
                selectedLabelRoot.anchoredPosition = labelPosition;
            }
        }
        private void RefreshMarkers(CampaignState state, int? selected)
        {
            if (markers.Count == 0) return;
            var rect = ((RectTransform)transform).rect;
            foreach (var r in state.Regions)
            {
                if (!markers.TryGetValue(r.RegionId, out var marker)) continue;
                bool named = markerEmblems || selected == r.RegionId
                    || selected.HasValue && CampaignRules.CanAttack(state, state.HumanFaction, selected.Value, r.RegionId);
                marker.gameObject.SetActive(named && !IsCapturePlaying);
                if (!named) continue;
                ((RectTransform)marker.transform).anchoredPosition = ToPixel(rect, markerCenters[r.RegionId]) - rect.center;
                if (marker.Emblem != null)
                {
                    marker.Emblem.gameObject.SetActive(markerEmblems);
                    marker.Emblem.sprite = EmblemFor?.Invoke(r.OwnerFaction);
                    marker.Emblem.color = marker.Emblem.sprite == null ? ColorFor(r.OwnerFaction) : Color.white;
                }
                if (marker.Label != null) { marker.Label.text = r.Name.ToUpperInvariant(); FitPlate(marker); }
                if (marker.SelectedState != null) marker.SelectedState.SetActive(markerEmblems && selected == r.RegionId);
            }
        }
        // Widens the label's plate (its top-left anchored parent) to the name and keeps it centered.
        private static void FitPlate(CampaignRegionMarkerView marker)
        {
            var label = marker.Label.rectTransform; var plate = label.parent as RectTransform;
            if (plate == null || plate == marker.transform || plate.anchorMin != new Vector2(0, 1) || plate.anchorMax != new Vector2(0, 1)) return;
            float width = Mathf.Clamp(marker.Label.GetPreferredValues(marker.Label.text).x + 22, 70, 280);
            float rootWidth = ((RectTransform)marker.transform).rect.width;
            plate.sizeDelta = new Vector2(width, plate.sizeDelta.y);
            plate.anchoredPosition = new Vector2((rootWidth - width) / 2, plate.anchoredPosition.y);
            label.sizeDelta = new Vector2(width, label.sizeDelta.y);
        }
        public void PlayAttack(CampaignOperation operation)
        {
            CancelAttackAnimation();
            if (operation == null || operation.ResultRecorded || arrow == null || !isActiveAndEnabled
                || !graphics.TryGetValue(operation.SourceRegionId, out var from) || !graphics.TryGetValue(operation.TargetRegionId, out var to)) return;
            var a = from.ProjectedMarker; var b = to.ProjectedMarker;
            arrow.Play(a, b, from.BoundaryFractionTowards(b), to.BoundaryFractionTowards(a), ColorFor(operation.AttackerFaction));
        }
        public void CancelAttackAnimation() { if (arrow != null) arrow.Hide(); }
        private void OnDisable() { CancelAttackAnimation(); CancelCapture(); }
        public static Color ColorFor(Game.Players.Faction faction) => faction == Game.Players.Faction.IronConcord ? new Color(.48f, .55f, .61f)
            : faction == Game.Players.Faction.Ashen ? new Color(.68f, .49f, .38f) : new Color(.46f, .49f, .37f);
        private void RebuildField()
        {
            field?.Dispose(); field = null;
            var size = ((RectTransform)transform).rect.size;
            if (fieldRegions != null && size.x > 1 && size.y > 1) field = new CampaignRegionField(fieldRegions, size);
            highlight.ApplyTo(surfaceMaterial, field?.Texture);
        }
        private void OnValidate() { if (surfaceMaterial != null) highlight.ApplyTo(surfaceMaterial, field?.Texture); }
        private void OnRectTransformDimensionsChange()
        {
            if (surfaceMaterial == null) return;
            var rect = ((RectTransform)transform).rect;
            surfaceMaterial.SetVector("_SurfaceSize", new Vector4(rect.width, rect.height, 0, 0));
        }
        private void OnDestroy() { field?.Dispose(); if (surfaceMaterial != null) Destroy(surfaceMaterial); }
    }
}


