using System.Collections.Generic;
using Game.Core;
using Game.HexGrid;
using Game.Players;
using Game.Turns;
using UnityEngine;

namespace Game.Map
{
    // Owns every EventMarkerVisual currently shown on the map, keyed by hex — spawned the moment
    // a Hex Event gets left unresolved via Skip (see HexEventRegistry.EventSkipped), destroyed
    // the moment its reward is actually claimed (see HexEventRegistry.EventConsumed). Lives on
    // the same GameObject as HexMap/MapResourceDisplay so it survives for the whole game.
    [RequireComponent(typeof(HexMap))]
    public class MapEventDisplay : MonoBehaviour
    {
        [SerializeField] private GameConfig gameConfig;
        // Only wired for the TurnStateChanged subscription below — mirrors MapResourceDisplay's
        // own turnController slot, needed because a hot-seat turn switch reassigns
        // VisionSystem.CurrentViewer WITHOUT firing VisibilityChanged, so per-viewer marker
        // visibility (see IsActiveFor) would otherwise not re-evaluate until that viewer's first
        // army move. Null-guarded — an unwired reference just means that one refresh is skipped.
        [SerializeField] private GameTurnController turnController;

        private readonly Dictionary<HexCoord, EventMarkerVisual> _markersByHex = new Dictionary<HexCoord, EventMarkerVisual>();

        private HexMap Map => GetComponent<HexMap>();

        private void OnEnable()
        {
            HexEventRegistry.EventSkipped += OnEventSkipped;
            HexEventRegistry.EventConsumed += OnEventConsumed;
            VisionSystem.VisibilityChanged += OnVisibilityChanged;
            if (turnController != null)
                turnController.TurnStateChanged += RefreshVisibility;
        }

        private void OnDisable()
        {
            HexEventRegistry.EventSkipped -= OnEventSkipped;
            HexEventRegistry.EventConsumed -= OnEventConsumed;
            VisionSystem.VisibilityChanged -= OnVisibilityChanged;
            if (turnController != null)
                turnController.TurnStateChanged -= RefreshVisibility;
        }

        private void OnVisibilityChanged(PlayerSetupData player)
        {
            if (player == VisionSystem.CurrentViewer)
                RefreshVisibility();
        }

        // Same "remembered once seen" exception the resource row already has (see
        // MapResourceDisplay.GetVisibilityTier) — once a viewer has discovered a still-active
        // event's hex, its marker stays up even after fog covers it again, instead of re-hiding
        // the instant vision leaves like an army/building marker would.
        private void RefreshVisibility()
        {
            foreach (KeyValuePair<HexCoord, EventMarkerVisual> entry in _markersByHex)
                if (entry.Value != null)
                    entry.Value.gameObject.SetActive(IsActiveFor(entry.Key));
        }

        // A hex-event marker is a per-player fact: only a viewer who has PERSONALLY discovered
        // this event (see HexEventRegistry.Entry.DiscoveredBy) ever sees it — an event another
        // player scouted or skipped stays invisible to everyone else even when they share vision
        // of the hex (the project owner's own report). On top of that, the same "remembered once
        // seen" fog rule the resource row uses.
        private static bool IsActiveFor(HexCoord hex)
        {
            return HexEventRegistry.IsDiscoveredBy(hex, VisionSystem.CurrentViewer)
                && (VisionSystem.IsVisibleToCurrentViewer(hex) || VisionSystem.HasEverSeenByCurrentViewer(hex));
        }

        private void OnEventSkipped(HexCoord hex)
        {
            if (_markersByHex.ContainsKey(hex) || gameConfig == null || gameConfig.eventMarkerPrefab == null)
                return;

            HexMap map = Map;
            if (map == null)
                return;

            // Whatever image the prefab already has baked onto its own SpriteRenderer is what
            // shows — no per-event sprite lookup, per the project owner's own call.
            EventMarkerVisual marker = Instantiate(gameConfig.eventMarkerPrefab, map.transform);
            marker.transform.position = map.HexToWorld(hex) + ToWorldOffset(gameConfig.eventIconOffset, map.OuterRadius);
            marker.SetSortingOrder(MapSortingOrder.EventIcon);
            // A hex whose event was just skipped is, by definition, the one the mover is
            // standing on/next to right now — always currently visible — but computed properly
            // rather than hardcoded true, so this stays correct if that ever stops holding.
            marker.gameObject.SetActive(IsActiveFor(hex));
            _markersByHex[hex] = marker;
        }

        private void OnEventConsumed(HexCoord hex)
        {
            if (!_markersByHex.TryGetValue(hex, out EventMarkerVisual marker))
                return;
            if (marker != null)
                Destroy(marker.gameObject);
            _markersByHex.Remove(hex);
        }

        private static Vector3 ToWorldOffset(Vector2 offset, float radius)
        {
            return new Vector3(offset.x, 0f, offset.y) * radius;
        }
    }
}
