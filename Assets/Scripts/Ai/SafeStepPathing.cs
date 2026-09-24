using System.Collections.Generic;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Terrain;
using Game.Units;
using UnityEngine;

namespace Game.Ai
{
    // Layer-neutral, fog-honest routing shared by planning and execution. Route sequences
    // retain HexPathfinder's destination-specific straightness tie break; cost-only fields
    // below are used only where Economy needs a minimum cost, never a route/threat witness.
    public static class SafeStepPathing
    {
        // `projectedCurrentMovement`/`projectedMaxMovement` — ask the SAME question about a
        // hypothetical version of this army whose shared movement differs from the live one:
        // AI-01's raid assembly (a recruit slower than the host drags the whole formation down,
        // see ArmyData.ComputeCurrentMovement) and AI-03's equipment admission ("does this grant
        // unblock a step the mission's route actually needs"). Null — every pre-existing call
        // site — keeps the army's own live movement figures and behaviour unchanged.
        public static HexCoord? FindNextSafeStep(HexMap map, ArmyData army, HexCoord targetHex,
            int? projectedCurrentMovement = null, int? projectedMaxMovement = null)
        {
            if (map == null || army == null)
                return null;
            PlayerRouteCache cache = EnsureCacheState(map, army.Owner);
            // Execution still searches live with CurrentMovement and its own air/ground rule;
            // only the equivalent remembered blocker membership is shared with planning. A FULLY
            // hidden army sets nothing off on arrival (AiMapMemory.KnownGroundArrival), so it
            // may cross known armies/structures — only danger zones remain transit blocks for it.
            HashSet<HexCoord> blockers = Game.Map.StealthSystem.IsArmyFullyHidden(army)
                ? cache.HiddenBlockedHexes : cache.BlockedHexes;
            return AiTurnController.FindAffordableStep(map, army, targetHex,
                SafeRouteBlocker(null, blockers, targetHex, null),
                projectedCurrentMovement, projectedMaxMovement);
        }

        // Convenience for the common AI-01 shape: "the roster this assembly will produce".
        public static HexCoord? FindNextSafeStepForRoster(HexMap map, ArmyData army,
            HexCoord targetHex, IReadOnlyList<UnitData> projectedRoster) =>
            FindNextSafeStep(map, army, targetHex,
                projectedRoster == null ? (int?)null : ArmyData.ComputeCurrentMovement(projectedRoster),
                projectedRoster == null ? (int?)null : ArmyData.ComputeMaxMovement(projectedRoster));

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
            PlayerRouteCache cache = EnsureCacheState(map, owner);
            int minimum = int.MaxValue;
            for (int i = 0; i < baseHexes.Count; i++)
            {
                HexCoord home = baseHexes[i];
                if (!cache.BaseCostFields.TryGetValue(home, out Dictionary<HexCoord, int> field))
                {
                    if (cache.BaseCostFields.Count >= MaxCostFields)
                        cache.BaseCostFields.Clear();
                    field = HexPathfinder.FindCosts(map, new[] { home },
                        hex => cache.BlockedHexes.Contains(hex));
                    cache.BaseCostFields[home] = field;
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
            PlayerRouteCache cache = EnsureCacheState(map, owner);
            if (!cache.ReturnCostFields.TryGetValue(maxMovement, out ReturnCostField field)
                || !field.Bases.SetEquals(baseHexes))
            {
                if (cache.ReturnCostFields.Count >= MaxCostFields)
                    cache.ReturnCostFields.Clear();
                field = new ReturnCostField
                {
                    Bases = new HashSet<HexCoord>(baseHexes),
                    Costs = HexPathfinder.FindCosts(map, baseHexes,
                        hex => cache.BlockedHexes.Contains(hex), maxMovement, reverse: true)
                };
                cache.ReturnCostFields[maxMovement] = field;
            }
            return field.Costs.TryGetValue(from, out int cost) ? cost : int.MaxValue;
        }

        private const int MaxCachedRoutes = 512;
        private const int MaxCostFields = 32;
        private sealed class PlayerRouteCache
        {
            public readonly Dictionary<(HexCoord from, HexCoord target, int? maxMovement), HexPath> Routes = new Dictionary<(HexCoord, HexCoord, int?), HexPath>();
            public readonly Dictionary<HexCoord, Dictionary<HexCoord, int>> BaseCostFields = new Dictionary<HexCoord, Dictionary<HexCoord, int>>();
            public readonly Dictionary<int, ReturnCostField> ReturnCostFields = new Dictionary<int, ReturnCostField>();
            // Planning (owner-only APIs) is conservative and routes as a VISIBLE mover; only a
            // live, fully hidden army (FindNextSafeStep) uses HiddenBlockedHexes.
            public HashSet<HexCoord> BlockedHexes;
            public HashSet<HexCoord> HiddenBlockedHexes;
            public long MemoryVersion;
            public void ClearPathsAndFields() { Routes.Clear(); BaseCostFields.Clear(); ReturnCostFields.Clear(); }
        }
        private sealed class ReturnCostField { public HashSet<HexCoord> Bases; public Dictionary<HexCoord, int> Costs; }
        private static readonly Dictionary<PlayerSetupData, PlayerRouteCache> _playerCaches = new Dictionary<PlayerSetupData, PlayerRouteCache>();
        private static HexMap _cacheMap;
        private static int _cacheMapVersion = -1;
        // Transit blockers are derived from the ONE AI arrival rule (AiMapMemory.KnownGroundArrival)
        // — never a parallel list: a hex is crossed only if arriving there sets nothing off for
        // this kind of mover. Candidates are every hex this observer remembers holding an army or
        // a foreign building (memory only, never live BuildingRegistry: a hidden ownership change
        // cannot silently alter route policy), plus active scout-danger zones for every mover.
        // The route's explicit destination remains separately exempt in SafeRouteBlocker; the
        // mission's permission to fight/capture THAT endpoint is the execution gate's check.
        private static HashSet<HexCoord> CaptureMemoryBlockers(HexMap map, PlayerSetupData owner, bool moverFullyHidden)
        {
            var blocked = new HashSet<HexCoord>();
            var candidates = new HashSet<HexCoord>();
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(owner)) candidates.Add(sighting.Hex);
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownNeutralSightings(owner)) candidates.Add(sighting.Hex);
            foreach (AiMapMemory.KnownBuilding building in AiMapMemory.AllKnownBuildings(owner))
                if (building.Owner != owner) candidates.Add(building.Hex);
            foreach (HexCoord hex in candidates)
                if (AiMapMemory.KnownGroundArrival(owner, hex, moverFullyHidden).HasOutcome) blocked.Add(hex);
            foreach ((HexCoord center, int radius) in AiMapMemory.ScoutDangerZoneRanges(owner))
                foreach (HexCoord hex in HexGridMath.HexesInRange(center, radius)) blocked.Add(hex);
            return blocked;
        }
        private static PlayerRouteCache EnsureCacheState(HexMap map, PlayerSetupData owner)
        {
            if (map != _cacheMap || map.PathingVersion != _cacheMapVersion) { _playerCaches.Clear(); _cacheMap = map; _cacheMapVersion = map.PathingVersion; }
            long memoryVersion = AiMapMemory.RouteMemoryVersionFor(owner);
            if (!_playerCaches.TryGetValue(owner, out PlayerRouteCache cache))
            {
                cache = new PlayerRouteCache
                {
                    BlockedHexes = CaptureMemoryBlockers(map, owner, moverFullyHidden: false),
                    HiddenBlockedHexes = CaptureMemoryBlockers(map, owner, moverFullyHidden: true),
                    MemoryVersion = memoryVersion,
                };
                _playerCaches[owner] = cache;
            }
            else if (memoryVersion != cache.MemoryVersion)
            {
                HashSet<HexCoord> current = CaptureMemoryBlockers(map, owner, moverFullyHidden: false);
                if (!cache.BlockedHexes.SetEquals(current)) cache.ClearPathsAndFields();
                cache.BlockedHexes = current;
                cache.HiddenBlockedHexes = CaptureMemoryBlockers(map, owner, moverFullyHidden: true);
                cache.MemoryVersion = memoryVersion;
            }
            return cache;
        }
        private static HexPath GetRoute(HexMap map, PlayerSetupData owner, HexCoord from, HexCoord targetHex, int? maxMovement)
        {
            PlayerRouteCache cache = EnsureCacheState(map, owner);
            var key = (from, targetHex, maxMovement);
            if (cache.Routes.TryGetValue(key, out HexPath cached)) return cached;
            if (cache.Routes.Count >= MaxCachedRoutes) cache.Routes.Clear();
            HexPath computed = HexPathfinder.FindPath(map, from, targetHex, blockHex: SafeRouteBlocker(map, cache.BlockedHexes, targetHex, maxMovement));
            cache.Routes[key] = computed;
            return computed;
        }
        private static System.Func<HexCoord, bool> SafeRouteBlocker(HexMap map, HashSet<HexCoord> blocked, HexCoord targetHex, int? maxMovement)
        {
            return hex =>
            {
                if (!hex.Equals(targetHex) && blocked.Contains(hex)) return true;
                if (maxMovement.HasValue && map != null && map.TryGetTerrainAt(hex, out TerrainTypeEntry entry) && Mathf.Max(1, entry.moveCost) > maxMovement.Value) return true;
                return false;
            };
        }
    }
}
