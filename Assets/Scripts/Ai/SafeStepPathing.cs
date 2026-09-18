using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using UnityEngine;

namespace Game.Ai
{
    // Layer-neutral scout-movement support (not V1, not V2 — both call it). Answers: given an
    // army heading for a target, what is the next hex it should step to while routing AROUND
    // remembered (fog-of-war-honest) enemy/neutral sightings and still-cooling scout-danger
    // hexes? Extracted verbatim from the former Game.Ai.VisitHexTask (ARCH-01, 2026-09-04).
    //
    // Only a REMEMBERED army on the path is blocked (AiMapMemory.KnownEnemySightingAt — honest,
    // fog-of-war-respecting, enemy AND neutral alike), never fog itself: the whole point of a
    // scout is to walk into unseen ground. `targetHex` is exempt regardless — the caller already
    // refuses to pick a destination with a known sighting on or near it; this only guards the
    // hexes along the WAY. Null when no such route exists yet — treat as "nothing to do this
    // step", not a reason to abandon the target.
    public static class SafeStepPathing
    {
        public static HexCoord? FindNextSafeStep(HexMap map, ArmyData army, HexCoord targetHex)
        {
            if (map == null || army == null)
                return null;
            // Routed through the shared AiTurnController.FindAffordableStep — this path (blocked
            // around known sightings) can differ from an unblocked one, so THIS is the path whose
            // first step must be checked against army.CurrentMovement.
            return AiTurnController.FindAffordableStep(map, army, targetHex,
                SafeRouteBlocker(army, targetHex));
        }

        // Canonical cost of the same fog-honest route FindNextSafeStep executes. Analysis freezes
        // this witness into Economy opportunities; Provisioning re-runs it live immediately before
        // binding. Keeping the blocker here prevents planning/execution from drifting.
        public static int FindSafePathCost(HexMap map, ArmyData army, HexCoord targetHex)
        {
            if (map == null || army == null)
                return int.MaxValue;
            return FindSafePathCost(map, army.Owner, army.Hex, targetHex, army.MaxMovement);
        }

        // Same canonical blocker for a projected leg whose mover is not physically standing at
        // `from` yet (Economy uses it for the post-build return leg). This keeps outbound and
        // return costing on the exact route policy execution already uses.
        //
        // `maxMovement` — when given, hard-blocks any hex whose entry cost exceeds it, the same
        // way a known sighting is hard-blocked: such a hex is impassable for this mover no
        // matter how many turns it waits, so the search itself must route around it instead of
        // returning the globally-cheapest route (which may run straight through it) for the
        // caller to reject only after the fact (see WorldAnalysis's own per-hex MaxMovement
        // check, bd283fb — this generalises that guard into the search).
        public static int FindSafePathCost(HexMap map, PlayerSetupData owner,
            HexCoord from, HexCoord targetHex, int? maxMovement = null)
        {
            if (map == null || owner == null)
                return int.MaxValue;
            return GetRoute(map, owner, from, targetHex, maxMovement)?.TotalCost ?? int.MaxValue;
        }

        // Same canonical route as FindSafePathCost, but returns the actual hex sequence so a
        // caller can check per-hex terrain cost against a specific mover's MaxMovement — a
        // finite TotalCost only proves a route exists over however many turns it takes; it says
        // nothing about whether any single hex on it costs more to enter than the mover can ever
        // have in one turn (impassable for that mover regardless of turns banked). Passing
        // `maxMovement` makes the search itself honour that instead of leaving it to the caller.
        public static HexPath FindSafePath(HexMap map, PlayerSetupData owner,
            HexCoord from, HexCoord targetHex, int? maxMovement = null)
        {
            if (map == null || owner == null)
                return null;
            HexPath cached = GetRoute(map, owner, from, targetHex, maxMovement);
            // HexPath.Hexes is a mutable List behind a readonly reference. Never let a caller
            // alter the shared witness used by another consumer (including the cost-only API).
            return cached == null ? null : new HexPath(new List<HexCoord>(cached.Hexes), cached.TotalCost);
        }

        // AiMapMemory.RouteMemoryVersion is deliberately coarse: observing a resource or a
        // building bumps it even when no route blocker changed. Keep a snapshot of the actual
        // remembered blocker HEXES for this player. When the version changes, compare sets before
        // discarding expensive routes; this also catches a newly cleared blocker that could
        // permit a shorter route OUTSIDE the old path (checking only old path hexes would not).
        // Restrict to one owner/map at a time to avoid retaining stale snapshots for other players.
        // Bounded capacity prevents growth across turns when those inputs legitimately stay stable.
        private const int MaxCachedRoutes = 512;
        private static readonly Dictionary<(PlayerSetupData owner, HexCoord from, HexCoord target, int? maxMovement), HexPath>
            _routeCache = new Dictionary<(PlayerSetupData, HexCoord, HexCoord, int?), HexPath>();
        private static HexMap _cacheMap;
        private static int _cacheMapVersion = -1;
        private static PlayerSetupData _cacheOwner;
        private static int _cacheMemoryVersion = -1;
        private static HashSet<HexCoord> _cachedMemoryBlockers;

        private static HashSet<HexCoord> CaptureMemoryBlockers(HexMap map, PlayerSetupData owner)
        {
            var blocked = new HashSet<HexCoord>();
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(owner))
                blocked.Add(sighting.Hex);
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownNeutralSightings(owner))
                blocked.Add(sighting.Hex);
            // ScoutDangerZones has no public enumeration API. Check the actual map cells using
            // its canonical predicate rather than duplicating zone geometry or accessing internals.
            foreach (HexCoord hex in map.AllCoords)
                if (AiMapMemory.IsScoutDangerous(owner, hex))
                    blocked.Add(hex);
            return blocked;
        }

        private static HexPath GetRoute(HexMap map, PlayerSetupData owner,
            HexCoord from, HexCoord targetHex, int? maxMovement)
        {
            int memoryVersion = AiMapMemory.RouteMemoryVersion;
            if (map != _cacheMap || map.PathingVersion != _cacheMapVersion || owner != _cacheOwner)
            {
                _routeCache.Clear();
                _cacheMap = map;
                _cacheMapVersion = map.PathingVersion;
                _cacheOwner = owner;
                _cachedMemoryBlockers = CaptureMemoryBlockers(map, owner);
                _cacheMemoryVersion = memoryVersion;
            }
            else if (memoryVersion != _cacheMemoryVersion)
            {
                HashSet<HexCoord> currentBlockers = CaptureMemoryBlockers(map, owner);
                if (_cachedMemoryBlockers == null || !_cachedMemoryBlockers.SetEquals(currentBlockers))
                    _routeCache.Clear();
                _cachedMemoryBlockers = currentBlockers;
                _cacheMemoryVersion = memoryVersion;
            }

            var key = (owner, from, targetHex, maxMovement);
            if (_routeCache.TryGetValue(key, out HexPath cached))
                return cached;
            // A missing path (null) is a valid cached result, but only for this exact key and
            // unchanged topology/blocker set. Limit entries even if no turn-boundary mutation occurs.
            if (_routeCache.Count >= MaxCachedRoutes)
                _routeCache.Clear();
            HexPath computed = HexPathfinder.FindPath(map, from, targetHex,
                blockHex: SafeRouteBlocker(map, owner, targetHex, maxMovement));
            _routeCache[key] = computed;
            return computed;
        }

        // No map/maxMovement here — FindNextSafeStep's own caller, AiTurnController.
        // FindAffordableStep, already hard-blocks any hex over this army's MaxMovement itself.
        private static System.Func<HexCoord, bool> SafeRouteBlocker(
            ArmyData army, HexCoord targetHex) =>
            SafeRouteBlocker(null, army.Owner, targetHex, null);

        private static System.Func<HexCoord, bool> SafeRouteBlocker(
            HexMap map, PlayerSetupData owner, HexCoord targetHex, int? maxMovement) => hex =>
        {
            if (!hex.Equals(targetHex)
                && (AiMapMemory.KnownEnemySightingAt(owner, hex).HasValue
                    || AiMapMemory.IsScoutDangerous(owner, hex)))
                return true;
            if (maxMovement.HasValue && map != null
                && map.TryGetTerrainAt(hex, out TerrainTypeEntry entry)
                && Mathf.Max(1, entry.moveCost) > maxMovement.Value)
                return true;
            return false;
        };
    }
}
