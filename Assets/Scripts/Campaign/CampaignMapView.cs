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
        private CampaignArrowGraphic arrow;
        private Material surfaceMaterial;
        private RectTransform selectedLabelRoot;
        private TMPro.TMP_Text selectedLabel;
        public Texture2D SurfaceTexture { get; private set; }
        // Rounded rectangular projection keeps the complete disk visible. Saves stay in map space.
        public static Vector2 Project(Vector2 p) => new Vector2((float)(Math.Tanh(p.x * 1.65) / Math.Tanh(1.65)) * .98f, (float)(Math.Tanh(p.y * 1.45) / Math.Tanh(1.45)) * .98f);
        public static Vector2 ToPixel(Rect rect, Vector2 projected) => rect.center + new Vector2(projected.x * rect.width * .50f, projected.y * rect.height * .50f);
        public static Vector2 FromPixel(Rect rect, Vector2 pixel) => new Vector2((pixel.x - rect.center.x) / (rect.width * .50f), (pixel.y - rect.center.y) / (rect.height * .50f));
        public static Vector2 SurfaceUV(Vector2 point) => new Vector2(point.x * .48f + .5f, point.y * .48f + .5f);
        public void Build(IReadOnlyList<RegionState> regions, Action<int> clicked, Action<int?> hovered)
        {
            foreach (Transform child in transform) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
            graphics.Clear();
            SurfaceTexture = Resources.Load<Texture2D>("Campaign/WastelandSurface");
            var shader = Resources.Load<Shader>("Campaign/CampaignSurface");
            if (SurfaceTexture == null || shader == null) throw new InvalidOperationException("Campaign surface assets are missing.");
            if (surfaceMaterial != null) Destroy(surfaceMaterial);
            surfaceMaterial = new Material(shader) { name = "CampaignSurface (runtime)" };
            OnRectTransformDimensionsChange();
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null) canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2;
            foreach (var region in regions)
            {
                var shadow = new GameObject("PlanetDepth_" + region.RegionId, typeof(RectTransform), typeof(CanvasRenderer)); shadow.transform.SetParent(transform, false);
                var rect = (RectTransform)shadow.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = new Vector2(0, -12);
                var graphic = shadow.AddComponent<CampaignRegionGraphic>(); graphic.SetSurface(SurfaceTexture, surfaceMaterial); graphic.Configure(region, null, null, true);
                graphic.Refresh(new Color(.023f, .027f, .027f), false, false, false, false, false, false);
            }
            foreach (var region in regions)
            {
                var go = new GameObject("Region_" + region.RegionId + "_" + region.Name, typeof(RectTransform), typeof(CanvasRenderer)); go.transform.SetParent(transform, false);
                var rect = (RectTransform)go.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
                var graphic = go.AddComponent<CampaignRegionGraphic>();
                graphic.SetSurface(SurfaceTexture, surfaceMaterial);
                graphic.Configure(region, () => clicked(region.RegionId), active => hovered(active ? (int?)region.RegionId : null)); graphics.Add(region.RegionId, graphic);
            }
            var ar = new GameObject("AttackDirection", typeof(RectTransform), typeof(CanvasRenderer)); ar.transform.SetParent(transform, false);
            var rt = (RectTransform)ar.transform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            arrow = ar.AddComponent<CampaignArrowGraphic>(); arrow.raycastTarget = false;
            var labelRect = Game.UI.CollectionUIElements.Rect(transform, "SelectedRegionName");
            var nameplate = labelRect.gameObject.AddComponent<Image>();
            nameplate.sprite = Resources.Load<Sprite>("Campaign/Parchment");
            nameplate.color = new Color(.98f, .92f, .78f, .92f); nameplate.raycastTarget = false;
            var text = Game.UI.CollectionUIElements.Label(labelRect, "", 8, 0, 214, 36, 22);
            text.alignment = TMPro.TextAlignmentOptions.Center;
            text.color = new Color(.13f, .105f, .075f);
            text.fontStyle = TMPro.FontStyles.Bold;
            text.enableAutoSizing = true; text.fontSizeMin = 13; text.fontSizeMax = 22;
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap; text.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            selectedLabelRoot = labelRect; selectedLabel = text;
        }
        public void Refresh(CampaignState state, int? selected, int? hovered, bool inputEnabled)
        {
            foreach (var r in state.Regions)
            {
                bool attackable = selected.HasValue && CampaignRules.CanAttack(state, state.HumanFaction, selected.Value, r.RegionId);
                var op = state.PendingOperation;
                if (!graphics.TryGetValue(r.RegionId, out var graphic)) continue;
                graphic.Refresh(ColorFor(r.OwnerFaction), selected == r.RegionId, hovered == r.RegionId, attackable,
                    op?.TargetRegionId == r.RegionId, op?.TargetRegionId == r.RegionId && op.OwnershipApplied && op.Outcome == CampaignOutcome.AttackerVictory, inputEnabled);
            }
            var operation = state.PendingOperation;
            arrow?.Set(operation == null ? (Vector2?)null : Project(CampaignRegionGraphic.VisibleCenter(state.Regions.Find(r => r.RegionId == operation.SourceRegionId))),
                operation == null ? (Vector2?)null : Project(CampaignRegionGraphic.VisibleCenter(state.Regions.Find(r => r.RegionId == operation.TargetRegionId))));
            if (selectedLabelRoot == null) return;
            var labelRegion = selected.HasValue ? state.Regions.Find(r => r.RegionId == selected.Value) : null;
            selectedLabelRoot.gameObject.SetActive(labelRegion != null);
            if (labelRegion != null)
            {
                var rect = (RectTransform)transform;
                var position = ToPixel(rect.rect, Project(CampaignRegionGraphic.VisibleCenter(labelRegion)));
                selectedLabelRoot.anchorMin = selectedLabelRoot.anchorMax = new Vector2(.5f, .5f);
                selectedLabelRoot.pivot = new Vector2(.5f, .5f);
                selectedLabelRoot.sizeDelta = new Vector2(230, 36);
                selectedLabelRoot.anchoredPosition = position - rect.rect.center + new Vector2(0, -22);
                selectedLabel.text = labelRegion.Name.ToUpperInvariant();
            }
        }
        public static Color ColorFor(Game.Players.Faction faction) => faction == Game.Players.Faction.IronConcord ? new Color(.53f, .59f, .64f)
            : faction == Game.Players.Faction.Ashen ? new Color(.73f, .55f, .42f) : new Color(.57f, .58f, .43f);
        private void OnRectTransformDimensionsChange()
        {
            if (surfaceMaterial == null) return;
            var rect = ((RectTransform)transform).rect;
            surfaceMaterial.SetVector("_SurfaceRect", new Vector4(rect.xMin, rect.yMin, rect.xMax, rect.yMax));
        }
        private void OnDestroy() { if (surfaceMaterial != null) Destroy(surfaceMaterial); }
    }
}

