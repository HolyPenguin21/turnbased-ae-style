using System;
using System.Collections.Generic;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using UnityEngine;

namespace Game.Ai.V2
{
    // Terrain-aware, read-only route witness for Scout proposal ordering.
    //
    // WorldSnapshot intentionally freezes player/world state, but its current MapKnowledge payload
    // does not carry per-hex terrain movement cost. Terrain geometry itself is immutable after map
    // generation and is public information, so this helper reads ONLY HexMap terrain/path geometry;
    // every ownership/threat/visited/block decision still comes from the frozen snapshot. No live
    // army/enemy registry or vision query is used here.
    //
    // This is proposal VALUE, not AP pricing: ground travel spends movement points, never AP.
    // ScoutCostModel remains the source of activation/stealth AP requirements.
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
            if (snap?.Self?.Armies == null || target.Kind != ScoutTargetKind.Explore)
                return new Assessment(true, 0, 0, 0, 1, 0, 1f);

            HexMap map = ResolveMap();
            if (map == null)
            {
                // Geometry unavailable (headless/unit-test snapshot). Preserve old ordering rather
                // than inventing a cost; tests/sims without a scene remain deterministic.
                return new Assessment(true, 0, 0, 0, 1, 0, 1f);
            }

            Assessment best = Assessment.NoRoute;
            foreach (ArmySnapshot mover in ScoutMoverSelector.Eligible(snap, target, null))
            {
                Assessment a = EvaluatePair(snap, map, mover, target.FocusHex);
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

        private static Assessment EvaluatePair(WorldSnapshot snap, HexMap map, ArmySnapshot mover, HexCoord focus)
        {
            Func<HexCoord, bool> block = h => !h.Equals(focus)
                && snap.MapKnowledge?.ScoutHardBlockedHexes != null
                && snap.MapKnowledge.ScoutHardBlockedHexes.Contains(h);
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
            bool canFollowThrough = reachesThisTurn && remaining > 0
                && HasAffordableFreshNeighbor(snap, map, focus, remaining);
            int expectedVisits = reachesThisTurn ? (canFollowThrough ? 2 : 1) : 0;

            // Penalise only EXTRA terrain burden beyond plain hex distance; distance/proximity is
            // already represented in ReconObjective.BaseValue and must not be counted twice.
            int extraTerrain = Math.Max(0, movementCost - distance);
            float terrainFactor = 1f / (1f + 0.25f * extraTerrain);
            float tempoFactor = expectedVisits >= 2 ? 1.30f
                : expectedVisits == 1 ? 1f
                : 1f / Math.Max(1f, eta);
            float multiplier = Mathf.Clamp(terrainFactor * tempoFactor, 0.25f, 1.50f);

            return new Assessment(true, movementCost, distance, eta, expectedVisits, remaining, multiplier);
        }

        private static bool HasAffordableFreshNeighbor(WorldSnapshot snap, HexMap map, HexCoord focus, int movement)
        {
            foreach (HexCoord h in HexGridMath.Neighbors(focus))
            {
                if (!map.TryGetTerrainAt(h, out _))
                    continue;
                if (snap.MapKnowledge?.VisitedHexSet != null && snap.MapKnowledge.VisitedHexSet.Contains(h))
                    continue;
                if (snap.MapKnowledge?.ScoutHardBlockedHexes != null && snap.MapKnowledge.ScoutHardBlockedHexes.Contains(h))
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
