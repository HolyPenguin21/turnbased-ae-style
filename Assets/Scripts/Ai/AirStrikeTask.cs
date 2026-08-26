using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using UnityEngine;

namespace Game.Ai
{
    // Level 2 for AiTaskKind.AirStrike — composition eligibility, concrete target-finding/scoring
    // live here, same split every other Level-2 task class already follows (see RaidWeakerArmyTask/
    // VisitHexTask's own class comments); AiAggressionPlanner only sequences calls into it and
    // turns the results into AiDecision/AiTask.
    //
    // Never reads or plays a hand card, never recruits — a sortie's whole roster is whatever's
    // already stored at an owned airfield or already airborne (see FindLaunchCandidates); Management
    // owns every aviation card (see AiManagementPlanner.IsAviationCard/FindAviationPlacement).
    public static class AirStrikeTask
    {
        // One launchable group — either aircraft still sitting in an airfield's own stored
        // container (ExistingArmy null), or an existing, currently-untasked air army already
        // sitting over an owned airfield (ExistingArmy set — no launch step needed, only a fresh
        // sortie assignment). Never a mobile air army mid-flight — that one is already task-owned,
        // or (landed, untasked, NOT at an owned airfield — shouldn't normally happen since every
        // sortie always lands at one) simply not eligible until it gets home.
        public readonly struct LaunchCandidate
        {
            public readonly HexCoord AirfieldHex;
            public readonly ArmyData ExistingArmy;
            public readonly IReadOnlyList<UnitData> Aircraft;

            public LaunchCandidate(HexCoord airfieldHex, ArmyData existingArmy, IReadOnlyList<UnitData> aircraft)
            {
                AirfieldHex = airfieldHex;
                ExistingArmy = existingArmy;
                Aircraft = aircraft;
            }
        }

        public static IEnumerable<LaunchCandidate> FindLaunchCandidates(PlayerSetupData player, AiResourcePool pool)
        {
            foreach (HexCoord hex in AiAviationSupport.OwnedAirfieldHexes(player))
            {
                ArmyData stored = AviationRules.FindAirfieldAt(hex, player);
                if (stored != null && stored.Members.Count >= AiConfig.aviationLaunchMinReadyAircraft)
                    yield return new LaunchCandidate(hex, null, stored.Members.ToList());
            }
            foreach (ArmyData army in pool.AvailableArmies())
            {
                if (!AviationRules.IsAirArmy(army) || !AviationRules.IsOwnedAirfieldAt(army.Hex, player))
                    continue;
                if (army.Members.Count >= AiConfig.aviationLaunchMinReadyAircraft)
                    yield return new LaunchCandidate(army.Hex, army, army.Members.ToList());
            }
        }

        public readonly struct StrikeTarget
        {
            public readonly HexCoord Hex;
            public readonly AiAviationSupport.Sortie Sortie;
            public readonly float Score;
            public readonly string Reason;

            public StrikeTarget(HexCoord hex, AiAviationSupport.Sortie sortie, float score, string reason)
            {
                Hex = hex;
                Sortie = sortie;
                Score = score;
                Reason = reason;
            }
        }

        // Scans the same two AiMapMemory sources RaidWeakerArmyTask.FindTarget already uses
        // (known enemy army sightings + known enemy-owned buildings/garrisons) — restricted to real
        // ENEMY owners only (neutrals are never an AirStrike target, unlike RaidWeakerArmyTask's own
        // "Раздел 5" scope — the spec's own wording is "known enemy armies/garrisons", not
        // neutral clean-up). Stored aircraft sitting in an enemy airfield never surface here at all
        // — BattleInitiator.IsEngageable (which AiMapMemory's own sighting recorder already gates
        // on) excludes IsAirfield armies entirely, so this needs no separate filter for that case.
        // A candidate with no complete sortie (AiAviationSupport.TryPlanSortie/
        // TryPlanSortieFromStorage returns null) is dropped before scoring — never assumed
        // reachable just because it's known.
        public static StrikeTarget? FindTarget(PlayerSetupData actor, LaunchCandidate candidate, HexMap map)
        {
            if (actor == null || map == null)
                return null;

            var enemyHexes = new HashSet<HexCoord>();
            var enemyDefense = new Dictionary<HexCoord, (float Defense, float Attack, string Name)>();
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(actor))
            {
                if (sighting.Owner == null || sighting.Owner == actor || sighting.Owner.IsNeutral)
                    continue;
                enemyHexes.Add(sighting.Hex);
                enemyDefense[sighting.Hex] = (sighting.DefenseSum, sighting.AttackSum, sighting.Name);
            }
            foreach (AiMapMemory.KnownBuilding building in AiMapMemory.AllKnownBuildings(actor))
            {
                if (building.Owner == null || building.Owner == actor || building.Owner.IsNeutral || building.IsStartingCitadel)
                    continue;
                enemyHexes.Add(building.Hex);
            }

            StrikeTarget? best = null;
            foreach (HexCoord hex in enemyHexes)
            {
                AiAviationSupport.Sortie? sortie = candidate.ExistingArmy != null
                    ? AiAviationSupport.TryPlanSortie(candidate.ExistingArmy, hex, map, actor)
                    : AiAviationSupport.TryPlanSortieFromStorage(candidate.AirfieldHex, candidate.Aircraft, hex, map, actor);
                if (!sortie.HasValue)
                    continue;

                enemyDefense.TryGetValue(hex, out (float Defense, float Attack, string Name) known);
                float score = ScoreTarget(actor, candidate, sortie.Value, known.Defense, known.Attack, map);
                string name = string.IsNullOrEmpty(known.Name) ? $"target at ({hex.Q},{hex.R})" : known.Name;
                if (best == null || score > best.Value.Score)
                    best = new StrikeTarget(hex, sortie.Value, score,
                        $"air strike on {name} — {candidate.Aircraft.Count} aircraft ready");
            }
            return best;
        }

        // Ranking order per spec: target value/defence worth striking, expected damage × ready
        // aircraft count, lower known AA exposure along the route, shorter total sortie distance,
        // lower AP/energy cost. Clamped to airStrikeScoreCap so this tier can never cross a ground
        // raid's own tactical combat/execute score (see that constant's own comment).
        private static float ScoreTarget(PlayerSetupData actor, LaunchCandidate candidate, AiAviationSupport.Sortie sortie,
            float targetDefense, float targetAttack, HexMap map)
        {
            float score = AiConfig.airStrikeBaseWeight;
            float targetValue = targetDefense + targetAttack;
            score += Mathf.Sqrt(Mathf.Max(0f, targetValue)) * AiConfig.airStrikeTargetValueWeight;
            score += candidate.Aircraft.Count * AiConfig.airStrikeTargetValueWeight * 0.5f;
            score -= KnownAaExposure(actor, sortie.OutboundPath) * AiConfig.airStrikeAaExposurePenalty;
            score -= sortie.TotalCost * AiConfig.airStrikeDistancePenalty;
            int apEnergyCost = candidate.Aircraft.Sum(u => u.ActivationApCost + u.LaunchEnergyCost);
            score -= apEnergyCost * AiConfig.airStrikeApCostPenalty;
            return Mathf.Min(score, AiConfig.airStrikeScoreCap);
        }

        // Coarse route-risk read — every known-AA-tagged enemy sighting (AiMapMemory.
        // KnownEnemySighting.HasAntiAir, see that field's own comment) within raidThreatRadius of
        // ANY hex the outbound leg crosses. Deliberately approximate (no per-unit AA radius is kept
        // in memory, only the bool flag) — good enough to rank routes relative to each other, never
        // meant as an exact prediction of what will actually react (that stays AntiAirRules' own
        // live, honest-fog job at execution time).
        private static int KnownAaExposure(PlayerSetupData actor, HexPath outbound)
        {
            if (outbound == null)
                return 0;
            int exposure = 0;
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(actor))
            {
                if (!sighting.HasAntiAir)
                    continue;
                if (outbound.Hexes.Any(hex => HexGridMath.Distance(hex, sighting.Hex) <= AiConfig.raidThreatRadius))
                    exposure++;
            }
            return exposure;
        }
    }
}
