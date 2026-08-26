using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai
{
    // Level 2 for AiTaskKind.AirRecon — target selection (which hex to fly toward for information)
    // lives here, same split VisitHexTask already follows for ground Разведка; AiScoutPlanner only
    // sequences calls into it. Deliberately simpler than VisitHexTask's own citadel-wave coverage
    // (AirRecon is a fallback, not Разведка's primary task, see AiTask.AirRecon's own comment) — no
    // wavefront bookkeeping, just "closest useful, reachable, forward-of-us hex".
    public static class AirReconTask
    {
        public static bool HasObservedEnemyAntiAir(PlayerSetupData actor) => AiMapMemory.HasObservedEnemyAntiAir(actor);

        public readonly struct ReconTarget
        {
            public readonly HexCoord Hex;
            public readonly AiAviationSupport.Sortie Sortie;
            public readonly float Score;
            public readonly string Reason;

            public ReconTarget(HexCoord hex, AiAviationSupport.Sortie sortie, float score, string reason)
            {
                Hex = hex;
                Sortie = sortie;
                Score = score;
                Reason = reason;
            }
        }

        // Scans a bounded ring around the launch/current hex (loosely sized off the fleet's own
        // round-trip range — AiAviationSupport.TryPlanSortie/TryPlanSortieFromStorage is still the
        // real feasibility gate, this radius only keeps the scan itself cheap), scores each
        // unexplored-or-stale, reachable hex by forward progress toward known enemy territory
        // (favoured over lateral wandering, per spec) then shorter safe distance, and returns the
        // single best. Never targets a known enemy army for damage the way AirStrikeTask does — a
        // discovered enemy along the way is a free opportunistic strike the shared resolver already
        // offers (AviationCombatPresenter.ResolveStep), not something this method goes looking for.
        public static ReconTarget? FindReconHex(PlayerSetupData actor, AirStrikeTask.LaunchCandidate candidate, HexMap map)
        {
            if (actor == null || map == null)
                return null;

            HexCoord start = candidate.AirfieldHex;
            HexCoord? enemyRef = FindEnemyReferenceHex(actor);
            int roundTripRange = candidate.ExistingArmy != null
                ? candidate.ExistingArmy.CurrentMovement
                : candidate.Aircraft.Min(AviationRules.EffectiveMoveMax);
            int searchRadius = Mathf.Max(2, roundTripRange / 2 + 1);

            ReconTarget? best = null;
            foreach (HexCoord hex in HexGridMath.HexesInRange(start, searchRadius))
            {
                if (!map.TryGetTerrainAt(hex, out _))
                    continue;
                bool everSeen = VisionSystem.HasEverSeen(actor, hex);
                bool visible = VisionSystem.IsVisible(actor, hex);
                if (everSeen && visible)
                    continue; // nothing to learn right now — currently visible AND already known

                AiAviationSupport.Sortie? sortie = candidate.ExistingArmy != null
                    ? AiAviationSupport.TryPlanSortie(candidate.ExistingArmy, hex, map, actor)
                    : AiAviationSupport.TryPlanSortieFromStorage(candidate.AirfieldHex, candidate.Aircraft, hex, map, actor);
                if (!sortie.HasValue)
                    continue;

                float forwardBonus = 0f;
                if (enemyRef.HasValue)
                {
                    int distToEnemy = HexGridMath.Distance(hex, enemyRef.Value);
                    int startDistToEnemy = HexGridMath.Distance(start, enemyRef.Value);
                    forwardBonus = Mathf.Max(0, startDistToEnemy - distToEnemy) * AiConfig.airReconForwardWeight;
                }
                float freshBonus = everSeen ? AiConfig.airReconForwardWeight * 0.5f : AiConfig.airReconForwardWeight;
                float score = AiConfig.airReconBaseWeight + forwardBonus + freshBonus
                    - sortie.Value.TotalCost * AiConfig.airReconDistancePenalty;

                if (best == null || score > best.Value.Score)
                    best = new ReconTarget(hex, sortie.Value, score,
                        $"recon flight toward ({hex.Q},{hex.R})" + (everSeen ? " — stale info" : " — unexplored"));
            }
            return best;
        }

        // Known enemy citadel if any (strongest, most stable directional reference); falls back to
        // any known non-neutral army sighting otherwise. Null (no bias, nearest unexplored hex wins
        // on distance alone) only once nothing about the enemy is known at all yet.
        private static HexCoord? FindEnemyReferenceHex(PlayerSetupData actor)
        {
            foreach (AiMapMemory.KnownBuilding building in AiMapMemory.AllKnownBuildings(actor))
                if (building.IsStartingCitadel && building.Owner != null && building.Owner != actor && !building.Owner.IsNeutral)
                    return building.Hex;
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(actor))
                if (sighting.Owner != null && sighting.Owner != actor && !sighting.Owner.IsNeutral)
                    return sighting.Hex;
            return null;
        }
    }
}
