using System.Collections.Generic;
using System.Linq;
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
            bool allowHostileStructureCapture = false,
            int? projectedCurrentMovement = null, int? projectedMaxMovement = null)
        {
            if (map == null || army == null)
                return null;
            PlayerRouteCache cache = EnsureCacheState(map, army.Owner);
            // Execution still searches live with CurrentMovement and its own air/ground rule;
            // only the equivalent remembered blocker membership is shared with planning.
            return AiTurnController.FindAffordableStep(map, army, targetHex,
                SafeRouteBlocker(null, cache.BlockedHexes, cache.HostileStructureHexes,
                    cache.CapturableStructureHexes, targetHex, null, allowHostileStructureCapture),
                projectedCurrentMovement, projectedMaxMovement);
        }

        // Convenience for the common AI-01 shape: "the roster this assembly will produce".
        public static HexCoord? FindNextSafeStepForRoster(HexMap map, ArmyData army,
            HexCoord targetHex, IReadOnlyList<UnitData> projectedRoster,
            bool allowHostileStructureCapture = false) =>
            FindNextSafeStep(map, army, targetHex, allowHostileStructureCapture,
                projectedRoster == null ? (int?)null : ArmyData.ComputeCurrentMovement(projectedRoster),
                projectedRoster == null ? (int?)null : ArmyData.ComputeMaxMovement(projectedRoster));

        public static int FindSafePathCost(HexMap map, ArmyData army, HexCoord targetHex,
            bool allowHostileStructureCapture = false)
        {
            if (map == null || army == null)
                return int.MaxValue;
            return FindSafePathCost(map, army.Owner, army.Hex, targetHex, army.MaxMovement,
                allowHostileStructureCapture);
        }

        // Projected legs (including Economy's return journeys) use the same blocker and
        // maxMovement policy as executed ground movement.
        public static int FindSafePathCost(HexMap map, PlayerSetupData owner,
            HexCoord from, HexCoord targetHex, int? maxMovement = null,
            bool allowHostileStructureCapture = false)
        {
            if (map == null || owner == null)
                return int.MaxValue;
            return GetRoute(map, owner, from, targetHex, maxMovement,
                allowHostileStructureCapture)?.TotalCost ?? int.MaxValue;
        }

        public static HexPath FindSafePath(HexMap map, PlayerSetupData owner,
            HexCoord from, HexCoord targetHex, int? maxMovement = null,
            bool allowHostileStructureCapture = false)
        {
            if (map == null || owner == null)
                return null;
            HexPath cached = GetRoute(map, owner, from, targetHex, maxMovement,
                allowHostileStructureCapture);
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
                        hex => cache.BlockedHexes.Contains(hex)
                            || cache.HostileStructureHexes.Contains(hex));
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
                        hex => cache.BlockedHexes.Contains(hex)
                            || cache.HostileStructureHexes.Contains(hex), maxMovement, reverse: true)
                };
                cache.ReturnCostFields[maxMovement] = field;
            }
            return field.Costs.TryGetValue(from, out int cost) ? cost : int.MaxValue;
        }

        private const int MaxCachedRoutes = 512;
        private const int MaxCostFields = 32;

        // A player switch must not discard the other AI's stationary-base fields. Every owner
        // keeps its own bounded route/cost caches and last-observed blockers. The global memory
        // revision remains a cheap dirty signal; only the selected owner's blockers are compared.
        private sealed class PlayerRouteCache
        {
            public readonly Dictionary<(HexCoord from, HexCoord target, int? maxMovement, bool allowCapture), HexPath>
                Routes = new Dictionary<(HexCoord, HexCoord, int?, bool), HexPath>();
            public readonly Dictionary<HexCoord, Dictionary<HexCoord, int>> BaseCostFields =
                new Dictionary<HexCoord, Dictionary<HexCoord, int>>();
            public readonly Dictionary<int, ReturnCostField> ReturnCostFields =
                new Dictionary<int, ReturnCostField>();
            public HashSet<HexCoord> BlockedHexes;
            public HashSet<HexCoord> HostileStructureHexes;
            // FIX-07 — the subset of HostileStructureHexes this player KNOWS is standing
            // undefended (AiMapMemory.KnownUndefendedForeignStructureAt). Only these may be
            // entered by a mover whose accepted plan permits taking a structure it walks onto;
            // a defended one, or one whose defence we do not know, stays blocked for everyone.
            // Guarded-event knowledge also affects this set: its changes need not bump the
            // narrower RouteMemoryVersion, so observe the owner's existing KnowledgeVersion too.
            public HashSet<HexCoord> CapturableStructureHexes;
            public int MemoryVersion;
            public int KnowledgeVersion;

            public void ClearPathsAndFields()
            {
                Routes.Clear();
                BaseCostFields.Clear();
                ReturnCostFields.Clear();
            }
        }

        private sealed class ReturnCostField
        {
            public HashSet<HexCoord> Bases;
            public Dictionary<HexCoord, int> Costs;
        }

        private static readonly Dictionary<PlayerSetupData, PlayerRouteCache> _playerCaches =
            new Dictionary<PlayerSetupData, PlayerRouteCache>();
        private static HexMap _cacheMap;
        private static int _cacheMapVersion = -1;

        // AiMapMemory's version is intentionally coarse; resource/building observations can
        // bump it without changing a route. Compare the complete blocker set before clearing
        // anything, including newly unblocked cells OUTSIDE a previously cached path.
        private static HashSet<HexCoord> CaptureMemoryBlockers(HexMap map, PlayerSetupData owner)
        {
            var blocked = new HashSet<HexCoord>();
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(owner))
                blocked.Add(sighting.Hex);
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownNeutralSightings(owner))
                blocked.Add(sighting.Hex);
            // Expand only the (few, small) zones themselves instead of testing every hex on the
            // map against every zone — this ran once per rebuild, and a rebuild used to happen
            // on nearly every AiMapMemory write.
            foreach ((HexCoord center, int radius) in AiMapMemory.ScoutDangerZoneRanges(owner))
                foreach (HexCoord hex in HexGridMath.HexesInRange(center, radius))
                    blocked.Add(hex);
            return blocked;
        }

        internal static HashSet<HexCoord> KnownForeignStructureHexes(
            PlayerSetupData owner, IEnumerable<AiMapMemory.KnownBuilding> buildings) =>
            new HashSet<HexCoord>((buildings ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                .Where(b => b.Owner != null && b.Owner != owner)
                .Select(b => b.Hex));

        // FIX-07 — same set, narrowed to the ones knowledge says nobody is holding. The rule
        // itself lives in AiMapMemory; this only materialises it once per memory revision so the
        // blocker below stays an O(1) lookup.
        internal static HashSet<HexCoord> CapturableForeignStructureHexes(
            PlayerSetupData owner, IEnumerable<AiMapMemory.KnownBuilding> buildings) =>
            new HashSet<HexCoord>((buildings ?? System.Array.Empty<AiMapMemory.KnownBuilding>())
                .Where(b => b.Owner != null && b.Owner != owner
                    && AiMapMemory.KnownUndefendedForeignStructureAt(owner, b.Hex))
                .Select(b => b.Hex));

        private static PlayerRouteCache EnsureCacheState(HexMap map, PlayerSetupData owner)
        {
            // Map identity and terrain revisions affect every owner's routes, unlike a single
            // player's remembered hostiles and scout-danger zones.
            if (map != _cacheMap || map.PathingVersion != _cacheMapVersion)
            {
                _playerCaches.Clear();
                _cacheMap = map;
                _cacheMapVersion = map.PathingVersion;
            }

            int memoryVersion = AiMapMemory.RouteMemoryVersion;
            int knowledgeVersion = AiMapMemory.KnowledgeVersionFor(owner);
            if (!_playerCaches.TryGetValue(owner, out PlayerRouteCache cache))
            {
                cache = new PlayerRouteCache
                {
                    BlockedHexes = CaptureMemoryBlockers(map, owner),
                    HostileStructureHexes = KnownForeignStructureHexes(
                        owner, AiMapMemory.AllKnownBuildings(owner)),
                    CapturableStructureHexes = CapturableForeignStructureHexes(
                        owner, AiMapMemory.AllKnownBuildings(owner)),
                    MemoryVersion = memoryVersion,
                    KnowledgeVersion = knowledgeVersion
                };
                _playerCaches[owner] = cache;
            }
            else if (memoryVersion != cache.MemoryVersion
                || knowledgeVersion != cache.KnowledgeVersion)
            {
                // A guard appeared/disappeared during an ordinary visible-hex observation:
                // KnownUndefendedForeignStructureAt changed even if armies and buildings did not.
                // Only capturability depends on that broader fact. Avoid rebuilding the expensive
                // enemy/danger blocker set on every unrelated resource-only observation.
                bool routeFactsChanged = memoryVersion != cache.MemoryVersion;
                HashSet<HexCoord> current = routeFactsChanged
                    ? CaptureMemoryBlockers(map, owner) : cache.BlockedHexes;
                HashSet<HexCoord> hostileStructures = routeFactsChanged
                    ? KnownForeignStructureHexes(owner, AiMapMemory.AllKnownBuildings(owner))
                    : cache.HostileStructureHexes;
                HashSet<HexCoord> capturableStructures = CapturableForeignStructureHexes(
                    owner, AiMapMemory.AllKnownBuildings(owner));
                if (!cache.BlockedHexes.SetEquals(current)
                    || !cache.HostileStructureHexes.SetEquals(hostileStructures)
                    || cache.CapturableStructureHexes == null
                    || !cache.CapturableStructureHexes.SetEquals(capturableStructures))
                    cache.ClearPathsAndFields();
                cache.BlockedHexes = current;
                cache.HostileStructureHexes = hostileStructures;
                cache.CapturableStructureHexes = capturableStructures;
                cache.MemoryVersion = memoryVersion;
                cache.KnowledgeVersion = knowledgeVersion;
            }
            return cache;
        }

        private static HexPath GetRoute(HexMap map, PlayerSetupData owner,
            HexCoord from, HexCoord targetHex, int? maxMovement,
            bool allowHostileStructureCapture)
        {
            PlayerRouteCache cache = EnsureCacheState(map, owner);
            var key = (from, targetHex, maxMovement, allowHostileStructureCapture);
            if (cache.Routes.TryGetValue(key, out HexPath cached))
                return cached;
            if (cache.Routes.Count >= MaxCachedRoutes)
                cache.Routes.Clear();
            HexPath computed = HexPathfinder.FindPath(map, from, targetHex,
                blockHex: SafeRouteBlocker(map, cache.BlockedHexes,
                    cache.HostileStructureHexes, cache.CapturableStructureHexes,
                    targetHex, maxMovement, allowHostileStructureCapture));
            cache.Routes[key] = computed;
            return computed;
        }

        private static System.Func<HexCoord, bool> SafeRouteBlocker(
            HexMap map, HashSet<HexCoord> blocked, HashSet<HexCoord> hostileStructures,
            HashSet<HexCoord> capturableStructures,
            HexCoord targetHex, int? maxMovement, bool allowHostileStructureCapture)
        {
            // Capture this owner's set, not a mutable global active-owner reference. Each
            // expanded hex is one O(1) lookup instead of a scan of sightings/danger zones.
            return hex =>
            {
                // Only an explicitly requested destination may be entered under a capture
                // permission. A foreign structure along the route is never free transit: taking
                // it would be an unrelated state-changing action, even when known undefended.
                // Unknown/defended structures remain blocked, including at the destination.
                if (hostileStructures != null && hostileStructures.Contains(hex)
                    && (!allowHostileStructureCapture || !hex.Equals(targetHex)
                        || capturableStructures == null || !capturableStructures.Contains(hex)))
                    return true;
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
