using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using UnityEngine;

namespace Game.Ai
{
    // Layer-neutral, fog-honest routing shared by planning and execution. Route sequences
    // retain HexPathfinder's destination-specific straightness tie break; cost-only fields
    // below are used only where Economy needs a minimum cost, never a route/threat witness.
    public static class SafeStepPathing
    {
        public static HexCoord? FindNextSafeStep(HexMap map, ArmyData army, HexCoord targetHex)
        {
            if (map == null || army == null)
                return null;
            EnsureCacheState(map, army.Owner);
            // Execution still searches live with CurrentMovement and its own air/ground rule;
            // only the equivalent remembered blocker membership is shared with planning.
            return AiTurnController.FindAffordableStep(map, army, targetHex,
                SafeRouteBlocker(army, targetHex));
        }

        public static int FindSafePathCost(HexMap map, ArmyData army, HexCoord targetHex)
        {
            if (map == null || army == null)
                return int.MaxValue;
            return FindSafePathCost(map, army.Owner, army.Hex, targetHex, army.MaxMovement);
        }

        // Projected legs (including Economy's return journeys) use the same blocker and
        // maxMovement policy as executed ground movement.
        public static int FindSafePathCost(HexMap map, PlayerSetupData owner,
            HexCoord from, HexCoord targetHex, int? maxMovement = null)
        {
            if (map == null || owner == null)
                return int.MaxValue;
            return GetRoute(map, owner, from, targetHex, maxMovement)?.TotalCost ?? int.MaxValue;
        }

        public static HexPath FindSafePath(HexMap map, PlayerSetupData owner,
            HexCoord from, HexCoord targetHex, int? maxMovement = null)
        {
            if (map == null || owner == null)
                return null;
            HexPath cached = GetRoute(map, owner, from, targetHex, maxMovement);
            // HexPath.Hexes is mutable. Never expose the cached witness to a caller that may
            // edit it and silently change subsequent paths and cost-only reads.
            return cached == null ? null : new HexPath(new List<HexCoord>(cached.Hexes), cached.TotalCost);
        }

        // Economy's PreparationTravelCost is min(base -> target), for many target hexes but
        // fixed base positions. One forward cost field per base is built lazily and reused
        // across candidates, refreshes and turns while map and remembered blockers stay equal.
        // A blocked TARGET is terminal, not transit, matching FindSafePathCost's exemption.
        public static int FindSafeBasePreparationCost(HexMap map, PlayerSetupData owner,
            IReadOnlyList<HexCoord> baseHexes, HexCoord target)
        {
            if (map == null || owner == null || baseHexes == null || baseHexes.Count == 0)
                return int.MaxValue;
            EnsureCacheState(map, owner);
            int minimum = int.MaxValue;
            for (int i = 0; i < baseHexes.Count; i++)
            {
                HexCoord home = baseHexes[i];
                if (!_baseCostFields.TryGetValue(home, out Dictionary<HexCoord, int> field))
                {
                    if (_baseCostFields.Count >= MaxCostFields)
                        _baseCostFields.Clear();
                    field = HexPathfinder.FindCosts(map, new[] { home },
                        hex => _cachedMemoryBlockers.Contains(hex));
                    _baseCostFields[home] = field;
                }
                if (field.TryGetValue(target, out int cost) && cost < minimum)
                    minimum = cost;
            }
            return minimum;
        }

        // Return travel is target -> nearest base, NOT base -> target: entering-hex terrain
        // makes the two directions asymmetric. One reverse multi-source cost field gives the
        // exact minimum for every target at a specific mover MaxMovement. A blocked start can
        // leave its hex, a blocked base is destination-exempt, and blocked intermediates cannot
        // be crossed, exactly as in FindSafePathCost.
        public static int FindNearestBaseReturnCost(HexMap map, PlayerSetupData owner,
            HexCoord from, IReadOnlyList<HexCoord> baseHexes, int maxMovement)
        {
            if (map == null || owner == null || baseHexes == null || baseHexes.Count == 0)
                return int.MaxValue;
            EnsureCacheState(map, owner);
            if (!_returnCostFields.TryGetValue(maxMovement, out ReturnCostField field)
                || !field.Bases.SetEquals(baseHexes))
            {
                if (_returnCostFields.Count >= MaxCostFields)
                    _returnCostFields.Clear();
                field = new ReturnCostField
                {
                    Bases = new HashSet<HexCoord>(baseHexes),
                    Costs = HexPathfinder.FindCosts(map, baseHexes,
                        hex => _cachedMemoryBlockers.Contains(hex), maxMovement, reverse: true)
                };
                _returnCostFields[maxMovement] = field;
            }
            return field.Costs.TryGetValue(from, out int cost) ? cost : int.MaxValue;
        }

        private const int MaxCachedRoutes = 512;
        private const int MaxCostFields = 32;
        private static readonly Dictionary<(PlayerSetupData owner, HexCoord from, HexCoord target, int? maxMovement), HexPath>
            _routeCache = new Dictionary<(PlayerSetupData, HexCoord, HexCoord, int?), HexPath>();
        private static readonly Dictionary<HexCoord, Dictionary<HexCoord, int>> _baseCostFields =
            new Dictionary<HexCoord, Dictionary<HexCoord, int>>();
        private sealed class ReturnCostField
        {
            public HashSet<HexCoord> Bases;
            public Dictionary<HexCoord, int> Costs;
        }
        private static readonly Dictionary<int, ReturnCostField> _returnCostFields =
            new Dictionary<int, ReturnCostField>();
        private static HexMap _cacheMap;
        private static int _cacheMapVersion = -1;
        private static PlayerSetupData _cacheOwner;
        private static int _cacheMemoryVersion = -1;
        private static HashSet<HexCoord> _cachedMemoryBlockers;

        // AiMapMemory's version is intentionally coarse; resource/building observations can
        // bump it without changing a route. Compare the complete blocker set before clearing
        // anything, including the newly unblocked cells OUTSIDE a previously cached path.
        private static HashSet<HexCoord> CaptureMemoryBlockers(HexMap map, PlayerSetupData owner)
        {
            var blocked = new HashSet<HexCoord>();
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(owner))
                blocked.Add(sighting.Hex);
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownNeutralSightings(owner))
                blocked.Add(sighting.Hex);
            foreach (HexCoord hex in map.AllCoords)
                if (AiMapMemory.IsScoutDangerous(owner, hex))
                    blocked.Add(hex);
            return blocked;
        }

        private static void ClearCachedPathsAndFields()
        {
            _routeCache.Clear();
            _baseCostFields.Clear();
            _returnCostFields.Clear();
        }

        private static void EnsureCacheState(HexMap map, PlayerSetupData owner)
        {
            int memoryVersion = AiMapMemory.RouteMemoryVersion;
            if (map != _cacheMap || map.PathingVersion != _cacheMapVersion || owner != _cacheOwner)
            {
                ClearCachedPathsAndFields();
                _cacheMap = map;
                _cacheMapVersion = map.PathingVersion;
                _cacheOwner = owner;
                _cachedMemoryBlockers = CaptureMemoryBlockers(map, owner);
                _cacheMemoryVersion = memoryVersion;
            }
            else if (memoryVersion != _cacheMemoryVersion)
            {
                HashSet<HexCoord> current = CaptureMemoryBlockers(map, owner);
                if (_cachedMemoryBlockers == null || !_cachedMemoryBlockers.SetEquals(current))
                    ClearCachedPathsAndFields();
                _cachedMemoryBlockers = current;
                _cacheMemoryVersion = memoryVersion;
            }
        }

        private static HexPath GetRoute(HexMap map, PlayerSetupData owner,
            HexCoord from, HexCoord targetHex, int? maxMovement)
        {
            EnsureCacheState(map, owner);
            var key = (owner, from, targetHex, maxMovement);
            if (_routeCache.TryGetValue(key, out HexPath cached))
                return cached;
            if (_routeCache.Count >= MaxCachedRoutes)
                _routeCache.Clear();
            HexPath computed = HexPathfinder.FindPath(map, from, targetHex,
                blockHex: SafeRouteBlocker(map, owner, targetHex, maxMovement));
            _routeCache[key] = computed;
            return computed;
        }

        private static System.Func<HexCoord, bool> SafeRouteBlocker(
            ArmyData army, HexCoord targetHex) =>
            SafeRouteBlocker(null, army.Owner, targetHex, null);

        private static System.Func<HexCoord, bool> SafeRouteBlocker(
            HexMap map, PlayerSetupData owner, HexCoord targetHex, int? maxMovement)
        {
            // Every caller has already validated this owner/map via EnsureCacheState. Capture
            // the immutable set so each expanded hex is one O(1) lookup instead of scanning
            // EnemySightings (keyed by ArmyId) and all scout-danger zones repeatedly.
            HashSet<HexCoord> blocked = _cachedMemoryBlockers;
            return hex =>
            {
                if (!hex.Equals(targetHex) && blocked.Contains(hex))
                    return true;
                if (maxMovement.HasValue && map != null
                    && map.TryGetTerrainAt(hex, out TerrainTypeEntry entry)
                    && Mathf.Max(1, entry.moveCost) > maxMovement.Value)
                    return true;
                return false;
            };
        }
    }
}
