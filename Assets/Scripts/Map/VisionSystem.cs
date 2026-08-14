using System;
using System.Collections.Generic;
using Game.Core;
using Game.HexGrid;
using Game.Players;

namespace Game.Map
{
    // Per-player hex visibility: what a player can see RIGHT NOW (armies/buildings within their
    // own vision radius — see GameConfig.armyVisionRadius/buildingVisionRadius), and which hexes
    // they've actually VISITED — a separate, permanent flag, and NOT the same thing as "currently
    // within vision radius": per the project owner's own call, seeing a hex from a distance never
    // marks it visited, only an army or building physically standing on it does (see
    // RecomputeFor's own `footprint` set vs. `fresh`). Content has no memory either way and
    // re-hides the instant vision leaves; the bare fact "an army of mine has been here" is what's
    // remembered forever, per player, and drives Game.Map.HexCoordLabel. Terrain itself is never
    // gated by any of this — only content (armies/buildings/resource yield), per the owner's
    // "map is always visible, content isn't" spec.
    //
    // CurrentViewer is whichever player's perspective the map is currently rendered from — the
    // human whose turn it is (see GameTurnController), since this is a hot-seat game with
    // possibly several human players sharing one screen. Every player (human or AI) still gets
    // their own tracked visible/visited sets regardless of whether they're the current viewer —
    // the owner's own "честный ИИ" call means AI decision-making will eventually read these too,
    // not just the human-facing render.
    public static class VisionSystem
    {
        private static GameConfig _config;
        private static readonly Dictionary<PlayerSetupData, HashSet<HexCoord>> Visible = new Dictionary<PlayerSetupData, HashSet<HexCoord>>();
        private static readonly Dictionary<PlayerSetupData, HashSet<HexCoord>> Visited = new Dictionary<PlayerSetupData, HashSet<HexCoord>>();
        // Permanent per-player memory of every hex that was ever inside vision radius, footprint
        // or not — a strictly looser, strictly larger set than Visited (which stays footprint-only,
        // see RecomputeFor). Drives the resource display's "seen from a distance" tier: type known,
        // exact amount not (see MapResourceDisplay, HexSelectionController.SelectHex).
        private static readonly Dictionary<PlayerSetupData, HashSet<HexCoord>> EverSeen = new Dictionary<PlayerSetupData, HashSet<HexCoord>>();
        private static readonly HashSet<HexCoord> EmptySet = new HashSet<HexCoord>();

        // Fired after RecomputeFor(player) finishes — subscribers (map markers, resource icons,
        // the fog overlay, hex info panel) re-check IsVisible against their own displayed hexes.
        // Always fires, even if the recomputed set happens to equal the previous one — callers
        // are cheap idempotent refreshes, not worth diffing here.
        public static event Action<PlayerSetupData> VisibilityChanged;

        public static PlayerSetupData CurrentViewer { get; set; }

        public static void Configure(GameConfig config)
        {
            _config = config;
        }

        public static void Clear()
        {
            Visible.Clear();
            Visited.Clear();
            EverSeen.Clear();
            CurrentViewer = null;
        }

        public static bool IsVisible(PlayerSetupData player, HexCoord hex)
        {
            return player != null && Visible.TryGetValue(player, out HashSet<HexCoord> set) && set.Contains(hex);
        }

        public static bool IsVisited(PlayerSetupData player, HexCoord hex)
        {
            return player != null && Visited.TryGetValue(player, out HashSet<HexCoord> set) && set.Contains(hex);
        }

        public static bool HasEverSeen(PlayerSetupData player, HexCoord hex)
        {
            return player != null && EverSeen.TryGetValue(player, out HashSet<HexCoord> set) && set.Contains(hex);
        }

        // Every hex `player` can see RIGHT NOW — for AiMapMemory's own subscription to
        // VisibilityChanged, which snapshots hex content into per-player memory instead of
        // re-checking IsVisible one hex at a time over the whole map. Returns the live backing
        // set, not a copy — callers must only enumerate, never mutate.
        public static IEnumerable<HexCoord> VisibleHexesFor(PlayerSetupData player)
        {
            return player != null && Visible.TryGetValue(player, out HashSet<HexCoord> set) ? set : EmptySet;
        }

        // Fails open (returns true) when there's no current viewer yet — e.g. during citadel
        // setup, before GameTurnController's turn loop has assigned one — so content isn't
        // wrongly hidden before the fog system actually has a perspective to render from.
        public static bool IsVisibleToCurrentViewer(HexCoord hex)
        {
            return CurrentViewer == null || IsVisible(CurrentViewer, hex);
        }

        // Same fail-open rule as IsVisibleToCurrentViewer. The one deliberate exception to "content
        // has no memory" above: a hex's resource yield is terrain-derived and doesn't change on its
        // own, so once an army has actually stood on it, that yield keeps being shown to that player
        // even after their army moves off and the hex drops out of Visible — unlike armies/buildings/
        // ownership, which still re-hide the instant vision leaves (see MapResourceDisplay,
        // HexSelectionController.SelectHex).
        public static bool IsVisitedByCurrentViewer(HexCoord hex)
        {
            return CurrentViewer == null || IsVisited(CurrentViewer, hex);
        }

        // Same fail-open rule again. The looser tier below IsVisitedByCurrentViewer: a hex merely
        // seen from a neighbor's vision radius, never physically stood on, still reveals which
        // resource types are there (see MapResourceDisplay/HexInfoPanelUI's "?" amount display).
        public static bool HasEverSeenByCurrentViewer(HexCoord hex)
        {
            return CurrentViewer == null || HasEverSeen(CurrentViewer, hex);
        }

        // Rebuilds `player`'s own visible set from scratch — every one of their armies and
        // buildings, expanded by its own radius (HexGridMath.HexesInRange) and unioned together.
        // Called from the one places those actually change (ArmyRegistry/BuildingRegistry's own
        // Register/Unregister/MoveArmy/CaptureOrDestroy, and per-step during army movement — see
        // ArmyController.MoveRoutine's shouldStopEarly callback) rather than polled.
        public static void RecomputeFor(PlayerSetupData player)
        {
            if (player == null)
                return;

            int armyRadius = _config != null ? _config.armyVisionRadius : 0;
            int buildingRadius = _config != null ? _config.buildingVisionRadius : 1;

            var fresh = new HashSet<HexCoord>();
            // Separate from `fresh` on purpose — per the project owner's own call, seeing a hex
            // from a distance (inside vision radius) is NOT the same as having visited it: only
            // a hex an army/building actually stands ON ever gets marked visited, radius or not.
            var footprint = new HashSet<HexCoord>();

            foreach (ArmyData army in ArmyRegistry.AllForOwner(player))
            {
                // ArmyData.Hex only updates once a whole move finishes (see ArmyRegistry.
                // MoveArmy's own comment) — mid-move, ArmyController.CurrentHex is the live,
                // per-step position instead (see ArmyController's own comment on the two), which
                // is what a per-step recompute (see HexSelectionController.Movement.cs's
                // HandleVisionStep) actually needs: vision has to track where the army really is
                // RIGHT NOW as it walks, not jump all at once once the whole path is done.
                HexCoord origin = army.Controller != null ? army.Controller.CurrentHex : army.Hex;
                footprint.Add(origin);
                // UnitAbilities.Recce raises this specific army's own radius beyond the flat
                // default — a single shared UnitAbilityCatalog.recceRadius value (see ArmyData.
                // HasRecce's own comment on why it's a flag, not a per-member magnitude).
                int recceRadius = _config != null && _config.abilityCatalog != null ? _config.abilityCatalog.recceRadius : 0;
                int radius = army.HasRecce ? Math.Max(armyRadius, recceRadius) : armyRadius;
                foreach (HexCoord hex in HexGridMath.HexesInRange(origin, radius))
                    fresh.Add(hex);
            }

            foreach (BuildingData building in BuildingRegistry.AllBuildings())
                if (building.Owner == player)
                {
                    footprint.Add(building.Hex);
                    foreach (HexCoord hex in HexGridMath.HexesInRange(building.Hex, buildingRadius))
                        fresh.Add(hex);
                }

            Visible[player] = fresh;

            if (!Visited.TryGetValue(player, out HashSet<HexCoord> visited))
            {
                visited = new HashSet<HexCoord>();
                Visited[player] = visited;
            }
            visited.UnionWith(footprint);

            if (!EverSeen.TryGetValue(player, out HashSet<HexCoord> everSeen))
            {
                everSeen = new HashSet<HexCoord>();
                EverSeen[player] = everSeen;
            }
            everSeen.UnionWith(fresh);

            VisibilityChanged?.Invoke(player);
        }

        public static void RecomputeFor(IEnumerable<PlayerSetupData> players)
        {
            if (players == null)
                return;
            foreach (PlayerSetupData player in players)
                RecomputeFor(player);
        }
    }
}
