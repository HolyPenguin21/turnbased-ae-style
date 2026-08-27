using System;
using System.Collections.Generic;
using Game.Aviation;
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
    // RecomputeFor's own `footprint` set vs. `fresh`). This class itself does not remember
    // changing content: AiMapMemory owns decision-making memory, while HumanVisualMemory owns
    // the deliberately narrow player-facing exceptions (enemy armies through the current turn,
    // stationary buildings until corrected). The bare fact "an army of mine has been here" is
    // remembered forever, per player, and drives Game.Map.HexCoordLabel. Terrain itself is never
    // gated by any of this — only content, per the owner's "map is always visible" spec.
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
        // Fires only when the viewer's visible/visited state actually changed. World-content
        // changes on an already-visible hex use VisibleContentChanged below instead, so an army
        // walking through a large already-revealed area does not rebuild the whole fog/UI stack
        // once per animation step.
        public static event Action<PlayerSetupData> VisibilityChanged;
        public static event Action<PlayerSetupData, HexCoord> VisibleContentChanged;

        public static PlayerSetupData CurrentViewer { get; set; }

        // Dev-only override — see GameTurnController.debugRevealFogOfWar's own comment. Touches
        // only the three CurrentViewer-facing read paths directly below, never the underlying
        // per-player Visible/Visited/EverSeen sets themselves and never IsVisible/IsVisited/
        // HasEverSeen taking an explicit `player` argument — those are what AiMapMemory and every
        // other per-player AI read directly (see this class's own header comment), so this can
        // only ever change what gets RENDERED to whichever human is CurrentViewer, never what any
        // player (AI included) actually knows. Not reset by Clear() — a dev preference outlives
        // any one game session, same as any other Inspector-set debug toggle.
        public static bool DebugRevealAll { get; set; }

        public static void Configure(GameConfig config)
        {
            _config = config;
        }

        public static void Clear()
        {
            Visible.Clear();
            Visited.Clear();
            EverSeen.Clear();
            HumanVisualMemory.Clear();
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
            return DebugRevealAll || CurrentViewer == null || IsVisible(CurrentViewer, hex);
        }

        // Same fail-open rule as IsVisibleToCurrentViewer. The one deliberate exception to "content
        // has no memory" above: a hex's resource yield is terrain-derived and doesn't change on its
        // own, so once an army has actually stood on it, that yield keeps being shown to that player
        // even after their army moves off and the hex drops out of Visible — unlike armies/buildings/
        // ownership, which still re-hide the instant vision leaves (see MapResourceDisplay,
        // HexSelectionController.SelectHex).
        public static bool IsVisitedByCurrentViewer(HexCoord hex)
        {
            return DebugRevealAll || CurrentViewer == null || IsVisited(CurrentViewer, hex);
        }

        // Same fail-open rule again. The looser tier below IsVisitedByCurrentViewer: a hex merely
        // seen from a neighbor's vision radius, never physically stood on, still reveals which
        // resource types are there (see MapResourceDisplay/HexInfoPanelUI's "?" amount display).
        public static bool HasEverSeenByCurrentViewer(HexCoord hex)
        {
            return DebugRevealAll || CurrentViewer == null || HasEverSeen(CurrentViewer, hex);
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
                // Aircraft reveal every crossed hex but never "visit" it: only their visibility
                // is remembered, preserving the ground exploration distinction in FOW.
                if (!AviationRules.IsAirArmy(army))
                    footprint.Add(origin);
                // A Recce-tagged member (r1sX) widens this army's own vision by its radius
                // step beyond the flat GameConfig.armyVisionRadius default — the number comes
                // from the tag itself now (see Game.Cards.AbilityParams), max across members,
                // never summed. Detection strength (the sX part) is a separate concern handled
                // by Game.Map.StealthSystem, not here.
                int radius = armyRadius + Game.Cards.AbilityParams.GetBestRecceRadius(army);
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

            bool visibilityChanged = !Visible.TryGetValue(player, out HashSet<HexCoord> previous) || !previous.SetEquals(fresh);
            Visible[player] = fresh;

            if (!Visited.TryGetValue(player, out HashSet<HexCoord> visited))
            {
                visited = new HashSet<HexCoord>();
                Visited[player] = visited;
            }
            bool visitedChanged = !footprint.IsSubsetOf(visited);
            visited.UnionWith(footprint);

            if (!EverSeen.TryGetValue(player, out HashSet<HexCoord> everSeen))
            {
                everSeen = new HashSet<HexCoord>();
                EverSeen[player] = everSeen;
            }
            everSeen.UnionWith(fresh);

            if (visibilityChanged || visitedChanged)
                VisibilityChanged?.Invoke(player);
        }

        public static void RecomputeFor(IEnumerable<PlayerSetupData> players)
        {
            if (players == null)
                return;
            foreach (PlayerSetupData player in players)
                RecomputeFor(player);
        }

        // RecomputeFor above only ever recomputes and fires for the mover/builder's own owner
        // (see ArmyRegistry.Register/Unregister, BuildingRegistry.Register/Unregister/
        // CaptureOrDestroy) — a bystander who already has `hex` inside their own Visible set
        // otherwise never finds out its content just changed (an army arrived/left/moved through)
        // until their OWN vision happens to recompute for an unrelated reason. That starved
        // AiMapMemory's EnemySightings snapshot (its only refresh hook is this same event) of any
        // update when an enemy walked next to a stationary AI army — the AI kept deciding off a
        // stale "no threat nearby" memory (see the project owner's own "разведчик не отступает"
        // report). Deliberately does NOT touch that player's own Visible/Visited/EverSeen — their
        // vision radius didn't move, only what's sitting on `hex` did, so re-snapshotting content
        // is all any subscriber (AiMapMemory chief among them) actually needs.
        public static void NotifyContentChanged(HexCoord hex)
        {
            foreach (KeyValuePair<PlayerSetupData, HashSet<HexCoord>> entry in new List<KeyValuePair<PlayerSetupData, HashSet<HexCoord>>>(Visible))
                if (entry.Value.Contains(hex))
                    VisibleContentChanged?.Invoke(entry.Key, hex);
        }
    }
}
