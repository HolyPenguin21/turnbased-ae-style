using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using UnityEngine;

namespace Game.Ai.V2
{
    // Terrain-aware, read-only route witness for ground Scout proposal ordering. Explore and
    // generic Refresh use it; Surveil has separate observation-vantage semantics.
    internal static class ScoutRouteCostEvaluator
    {
        internal readonly struct Assessment
        {
            public readonly bool HasRoute;
            public readonly int MovementCost;
            public readonly int HexDistance;
            public readonly int EtaTurns;
            public readonly int ExpectedVisitsThisTurn;
            public readonly int RemainingMovementAtFocus;
            public readonly float AdmissionMultiplier;

            public Assessment(bool hasRoute, int movementCost, int hexDistance, int etaTurns,
                int expectedVisitsThisTurn, int remainingMovementAtFocus, float admissionMultiplier)
            {
                HasRoute = hasRoute;
                MovementCost = movementCost;
                HexDistance = hexDistance;
                EtaTurns = etaTurns;
                ExpectedVisitsThisTurn = expectedVisitsThisTurn;
                RemainingMovementAtFocus = remainingMovementAtFocus;
                AdmissionMultiplier = admissionMultiplier;
            }

            public static Assessment NoRoute => new Assessment(false, int.MaxValue, int.MaxValue,
                int.MaxValue, 0, 0, 0f);
        }

        private static HexMap _cachedMap;

        public static Assessment Evaluate(WorldSnapshot snap, ScoutMissionTarget target)
        {
            bool ground = ReconScoutKinds.IsGround(target.Kind);
            if (snap?.Self?.Armies == null || !ground)
                return new Assessment(true, 0, 0, 0, 1, 0, 1f);

            HexMap map = ResolveMap();
            if (map == null)
            {
                return new Assessment(true, 0, 0, 0, 1, 0, 1f);
            }

            Assessment best = Assessment.NoRoute;
            foreach (ArmySnapshot mover in ScoutMoverSelector.Eligible(snap, target, null))
            {
                Assessment a = EvaluatePair(snap, map, mover, target.FocusHex, target.Kind);
                if (!a.HasRoute)
                    continue;
                if (!best.HasRoute
                    || a.ExpectedVisitsThisTurn > best.ExpectedVisitsThisTurn
                    || (a.ExpectedVisitsThisTurn == best.ExpectedVisitsThisTurn && a.EtaTurns < best.EtaTurns)
                    || (a.ExpectedVisitsThisTurn == best.ExpectedVisitsThisTurn && a.EtaTurns == best.EtaTurns
                        && a.MovementCost < best.MovementCost))
                    best = a;
            }
            return best;
        }

        private static Assessment EvaluatePair(WorldSnapshot snap, HexMap map, ArmySnapshot mover,
            HexCoord focus, ScoutTargetKind kind)
        {
            // Spec §19 — a stealth-capable mover's route may pass through neutral-occupied hexes.
            bool stealthCapable = mover != null && (mover.IsHidden || mover.StealthLevel > 0 || mover.CanEnterStealth);
            Func<HexCoord, bool> block = h => !h.Equals(focus)
                && snap.MapKnowledge != null
                && snap.MapKnowledge.IsBlockedForScout(h, stealthCapable);
            HexPath path = HexPathfinder.FindPath(map, mover.Hex, focus, blockHex: block);
            if (path == null || path.Hexes.Count < 2)
                return Assessment.NoRoute;

            int movementCost = 0;
            for (int i = 1; i < path.Hexes.Count; i++)
                movementCost += GroundMoveCost(map, path.Hexes[i]);

            int distance = path.Hexes.Count - 1;
            int budget = Math.Max(1, mover.MaxMovement);
            int eta = mover.CurrentMovement >= movementCost
                ? 1
                : 1 + CeilDiv(Math.Max(0, movementCost - mover.CurrentMovement), budget);

            int remaining = Math.Max(0, mover.CurrentMovement - movementCost);
            bool reachesThisTurn = mover.CurrentMovement >= movementCost;
            bool explore = ReconScoutKinds.IsExplore(kind);
            bool canFollowThrough = explore && reachesThisTurn && remaining > 0
                && HasAffordableFreshNeighbor(snap, map, focus, remaining);
            // Refresh can continue tactically after satisfying its anchor, but the proposal owns one
            // stale-info objective. Do not count hypothetical second Refresh completions here.
            int expectedVisits = reachesThisTurn ? (canFollowThrough ? 2 : 1) : 0;

            int extraTerrain = Math.Max(0, movementCost - distance);
            float terrainFactor = 1f / (1f + 0.25f * extraTerrain);
            float tempoFactor = expectedVisits >= 2 ? 1.30f
                : expectedVisits == 1 ? 1f
                : 1f / Math.Max(1f, eta);

            float retraceFactor = RetraceFactor(snap, mover, path, kind);
            float multiplier = Mathf.Clamp(terrainFactor * tempoFactor * retraceFactor, 0.20f, 1.50f);

            return new Assessment(true, movementCost, distance, eta, expectedVisits, remaining, multiplier);
        }

        private static float RetraceFactor(WorldSnapshot snap, ArmySnapshot mover, HexPath path,
            ScoutTargetKind kind)
        {
            if (mover?.Owner == null || path?.Hexes == null || path.Hexes.Count < 2)
                return 1f;

            var stepHexes = new List<HexCoord>(path.Hexes.Count - 1);
            for (int i = 1; i < path.Hexes.Count; i++)
                stepHexes.Add(path.Hexes[i]);

            float factor = 1f;
            if (ScoutTrailRegistry.IsImmediateReversal(mover.Owner, mover.ArmyId, stepHexes[0]))
                factor *= AiConfigV2.scoutImmediateReversalFactor;

            int trailHits = ScoutTrailRegistry.RecentTrailHits(mover.Owner, mover.ArmyId, stepHexes);
            if (trailHits > 0)
                factor *= 1f / (1f + AiConfigV2.scoutRecentTrailPenaltyPerHex * trailHits);

            // Only Explore treats ordinary already-visited ground as low-information re-treading.
            // Refresh deliberately revisits old ground, so applying scoutExploredRouteFloor here
            // would suppress exactly the route class Refresh is supposed to use.
            if (ReconScoutKinds.IsExplore(kind))
            {
                ISet<HexCoord> visited = snap.MapKnowledge?.VisitedHexSet;
                if (visited != null && stepHexes.Count > 0)
                {
                    int visitedHits = 0;
                    foreach (HexCoord h in stepHexes)
                        if (visited.Contains(h))
                            visitedHits++;
                    float visitedFrac = (float)visitedHits / stepHexes.Count;
                    factor *= Mathf.Lerp(1f, AiConfigV2.scoutExploredRouteFloor, visitedFrac);
                }
            }

            return factor;
        }

        private static bool HasAffordableFreshNeighbor(WorldSnapshot snap, HexMap map, HexCoord focus, int movement)
        {
            foreach (HexCoord h in HexGridMath.Neighbors(focus))
            {
                if (!map.TryGetTerrainAt(h, out _))
                    continue;
                if (snap.MapKnowledge?.VisitedHexSet != null && snap.MapKnowledge.VisitedHexSet.Contains(h))
                    continue;
                if (snap.MapKnowledge != null && snap.MapKnowledge.IsBlockedForScout(h, stealthCapable: false))
                    continue;
                if (GroundMoveCost(map, h) <= movement)
                    return true;
            }
            return false;
        }

        private static int GroundMoveCost(HexMap map, HexCoord h)
        {
            if (!map.TryGetTerrainAt(h, out var terrain) || terrain == null)
                return 1;
            return Math.Max(1, terrain.moveCost);
        }

        private static HexMap ResolveMap()
        {
            if (_cachedMap != null)
                return _cachedMap;
#pragma warning disable CS0618
            _cachedMap = UnityEngine.Object.FindObjectOfType<HexMap>();
#pragma warning restore CS0618
            return _cachedMap;
        }

        private static int CeilDiv(int a, int b) => b <= 0 ? a : (a + b - 1) / b;
    }
}
