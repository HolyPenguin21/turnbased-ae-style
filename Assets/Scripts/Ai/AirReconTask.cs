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

                float forwardBonus = EnemyConcentrationForwardBonus(actor, start, hex) * AiConfig.airReconForwardWeight;
                float freshBonus = everSeen ? AiConfig.airReconForwardWeight * 0.5f : AiConfig.airReconForwardWeight;
                // Known-AA route risk (AiAviationSupport.KnownAaExposure, shared with AirStrikeTask.
                // ScoreTarget — 2026-08-26, project owner's own spec point 2 "учитывать ПВО при
                // выборе вылета"). Same "ranked down, never a hard block" shape AirStrike already
                // uses for the identical concern: a risky-but-only-reachable hex still wins if
                // nothing safer scores as well, but a safe hex of otherwise-comparable info value
                // always outranks a risky one, and at truly EQUAL informativeness (spec's own tie-
                // break clause) the lower-exposure route wins outright — the penalty weight
                // (airReconAaExposurePenalty) is deliberately sized close to forwardBonus/freshBonus
                // themselves so it dominates near-ties without being able to out-vote a genuinely
                // much more informative target the way airStrikeAaExposurePenalty is sized relative
                // to airStrikeTargetValueWeight for the sibling task.
                float aaExposurePenalty = AiAviationSupport.KnownAaExposure(actor, sortie.Value.OutboundPath)
                    * AiConfig.airReconAaExposurePenalty;
                float score = AiConfig.airReconBaseWeight + forwardBonus + freshBonus
                    - sortie.Value.TotalCost * AiConfig.airReconDistancePenalty
                    - aaExposurePenalty;

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
