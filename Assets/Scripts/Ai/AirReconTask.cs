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
        // real feasibility gate, this radius only keeps the scan itself cheap; it's also what
        // already picks the landing airfield as part of that same feasibility check — a forward
        // base naturally wins whenever it yields a cheaper total round trip than a rearward one, so
        // no separate relocation task is ever needed just to get a recon flight based somewhere more
        // convenient), scores each unexplored-or-stale, reachable hex by forward progress toward
        // known enemy territory (favoured over lateral wandering, per spec — see
        // EnemyConcentrationForwardBonus for how that direction is now derived) then shorter safe
        // distance, then known AA route risk, and returns the single best. Never targets a known
        // enemy army for damage the way AirStrikeTask does — a discovered enemy along the way is a
        // free opportunistic strike the shared resolver already offers (AviationCombatPresenter.
        // ResolveStep), not something this method goes looking for.
        public static ReconTarget? FindReconHex(PlayerSetupData actor, AirStrikeTask.LaunchCandidate candidate, HexMap map)
        {
            if (actor == null || map == null)
                return null;

            HexCoord start = candidate.AirfieldHex;
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

                // Route-level AA safety — rewritten 2026-08-26 (project owner's own spec item 4,
                // "Единая жёсткая безопасность маршрута по ПВО") from a ranked-down scoring penalty
                // into a genuine hard filter: a candidate whose planned round trip (either leg)
                // carries ANY known-AA exposure (AiAviationSupport.KnownAaExposure — already scoped
                // to hexes the route itself actually crosses, never "AA anywhere on the map") is
                // dropped outright, never merely scored lower. Replaces the old global "any AA seen
                // ANYWHERE suppresses AirRecon entirely" gate (removed from AiScoutPlanner.
                // TryStartAirReconCandidates, see that method's own comment) with the precise rule
                // the spec actually asked for — AA off THIS route no longer blocks anything, AA ON
                // it always does. Every surviving candidate below is therefore always zero-exposure,
                // so there is deliberately no separate penalty term left in the score formula for
                // it any more (see AiConfig.airReconForwardLandingWeight's own comment on what
                // replaced airReconAaExposurePenalty).
                if (AiAviationSupport.KnownAaExposure(actor, sortie.Value.OutboundPath)
                    + AiAviationSupport.KnownAaExposure(actor, sortie.Value.ReturnPath) > 0)
                    continue;

                float forwardBonus = EnemyConcentrationForwardBonus(actor, start, hex) * AiConfig.airReconForwardWeight;
                float freshBonus = everSeen ? AiConfig.airReconForwardWeight * 0.5f : AiConfig.airReconForwardWeight;

                // Forward-landing bonus (2026-08-26, project owner's own spec item 3 — "разведка
                // должна естественно садиться на передовой базе"). Without this, a short 2-hex hop
                // that returns to the SAME airfield it launched from always scored best on distance
                // alone (sortie.TotalCost is smallest for a round trip that never really goes
                // anywhere), even when a longer flight that lands at a different, more forward base
                // would reveal more and leave the fleet better based for next time. Rewards exactly
                // that: zero unless the sortie's own chosen landing hex is BOTH different from the
                // launch airfield AND genuinely closer to the nearest known enemy reference
                // (AiAviationSupport.NearestKnownEnemyDistance, shared with TryReplan/
                // TryPlanSortiePreferForwardLanding's own tie-break so "more forward" always means
                // the same thing everywhere) — scaled by how many hexes closer, so a modest edge
                // earns a modest nudge and a genuinely valuable relocation can outweigh the plain
                // distance penalty of the extra flight it costs.
                float forwardLandingBonus = 0f;
                if (!sortie.Value.LandingHex.Equals(start))
                {
                    int startForward = AiAviationSupport.NearestKnownEnemyDistance(actor, start);
                    int landingForward = AiAviationSupport.NearestKnownEnemyDistance(actor, sortie.Value.LandingHex);
                    if (landingForward < startForward)
                        forwardLandingBonus = (startForward - landingForward) * AiConfig.airReconForwardLandingWeight;
                }

                float score = AiConfig.airReconBaseWeight + forwardBonus + freshBonus + forwardLandingBonus
                    - sortie.Value.TotalCost * AiConfig.airReconDistancePenalty;

                if (best == null || score > best.Value.Score)
                    best = new ReconTarget(hex, sortie.Value, score,
                        $"recon flight toward ({hex.Q},{hex.R})" + (everSeen ? " — stale info" : " — unexplored"));
            }
            return best;
        }

        // Directional bias toward enemy territory, rewritten 2026-08-26 (project owner's own spec
        // point 2 — "направление должен строиться не только от известной цитадели, но и от
        // скоплений известных вражеских армий, ближайшие и более сильные... больший directional
        // bonus") to blend EVERY known reference instead of picking a single one (the old
        // FindEnemyReferenceHex: citadel if known, else the first enemy sighting found, full stop).
        // No separate multi-army spatial clustering model exists anywhere else in this codebase —
        // RaidWeakerArmyTask's own known-target pool already treats each AiMapMemory.
        // KnownEnemySighting as its own independent entry rather than grouping several into one
        // "concentration" — so this reuses that same per-sighting granularity: each sighting IS its
        // own concentration, weighted by its own remembered strength (DefenseSum+AttackSum) divided
        // by (1 + its own distance from the launch hex), so a near, strong army pulls the direction
        // far harder than a distant, weak one, exactly per spec. The enemy citadel (if known) is
        // folded in as one more weighted reference, at a flat airReconCitadelWeight strength — it
        // carries no combat-strength numbers of its own to weigh by, but stays the single most
        // stable, always-relevant directional anchor the old code already treated it as.
        // Returns a weighted-AVERAGE forward-progress amount (never negative) — averaging (not
        // summing) keeps the result on the same rough scale FindReconHex's own airReconForwardWeight
        // multiplier already expected from the old single-reference version, regardless of how many
        // enemies happen to be known right now; a hex advancing toward the weighted centroid of
        // every known reference scores highest, one retreating from all of them scores zero.
        private static float EnemyConcentrationForwardBonus(PlayerSetupData actor, HexCoord start, HexCoord candidate)
        {
            float weightedProgress = 0f;
            float totalWeight = 0f;

            void Accumulate(HexCoord reference, float weight)
            {
                if (weight <= 0f)
                    return;
                int startDist = HexGridMath.Distance(start, reference);
                int candidateDist = HexGridMath.Distance(candidate, reference);
                weightedProgress += weight * Mathf.Max(0, startDist - candidateDist);
                totalWeight += weight;
            }

            foreach (AiMapMemory.KnownBuilding building in AiMapMemory.AllKnownBuildings(actor))
                if (building.IsStartingCitadel && building.Owner != null && building.Owner != actor && !building.Owner.IsNeutral)
                    Accumulate(building.Hex, AiConfig.airReconCitadelWeight);

            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(actor))
            {
                if (sighting.Owner == null || sighting.Owner == actor)
                    continue;
                float strength = Mathf.Max(0f, sighting.DefenseSum + sighting.AttackSum);
                Accumulate(sighting.Hex, strength / (1 + HexGridMath.Distance(start, sighting.Hex)));
            }

            return totalWeight > 0f ? weightedProgress / totalWeight : 0f;
        }
    }
}
