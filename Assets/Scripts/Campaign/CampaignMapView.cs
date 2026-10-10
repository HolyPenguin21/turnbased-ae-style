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
        public static Vector2 Project(Vector2 p) => new Vector2(p.x * (.87f + .10f * p.y), p.y * .73f + p.x * .035f);
        public void Build(IReadOnlyList<RegionState> regions, Action<int> clicked, Action<int?> hovered)
        {
            foreach (var region in regions)
            {
                var shadow = new GameObject("PlanetDepth_" + region.RegionId, typeof(RectTransform), typeof(CanvasRenderer)); shadow.transform.SetParent(transform, false);
                var rect = (RectTransform)shadow.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = new Vector2(0, -12);
                var graphic = shadow.AddComponent<CampaignRegionGraphic>(); graphic.Configure(region, null, null, true);
                graphic.Refresh(new Color(.023f, .027f, .027f), false, false, false, false, false, false);
            }
            foreach (var region in regions)
            {
                var go = new GameObject("Region_" + region.RegionId + "_" + region.Name, typeof(RectTransform), typeof(CanvasRenderer)); go.transform.SetParent(transform, false);
                var rect = (RectTransform)go.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
                var graphic = go.AddComponent<CampaignRegionGraphic>();
                graphic.Configure(region, () => clicked(region.RegionId), active => hovered(active ? (int?)region.RegionId : null)); graphics.Add(region.RegionId, graphic);
            }
            var ar = new GameObject("AttackDirection", typeof(RectTransform), typeof(CanvasRenderer)); ar.transform.SetParent(transform, false);
            var rt = (RectTransform)ar.transform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            arrow = ar.AddComponent<CampaignArrowGraphic>(); arrow.raycastTarget = false;
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
            arrow?.Set(operation == null ? (Vector2?)null : Project(CampaignGeometry.Center(state.Regions.Find(r => r.RegionId == operation.SourceRegionId))),
                operation == null ? (Vector2?)null : Project(CampaignGeometry.Center(state.Regions.Find(r => r.RegionId == operation.TargetRegionId))));
        }
        public static Color ColorFor(Game.Players.Faction faction) => faction == Game.Players.Faction.IronConcord ? new Color(.37f, .47f, .53f)
            : faction == Game.Players.Faction.Ashen ? new Color(.64f, .37f, .24f) : new Color(.39f, .51f, .34f);
    }
}
