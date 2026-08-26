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

        // Scans known ARMY/garrison sightings (AiMapMemory.KnownEnemySighting) — both non-neutral
        // enemy sightings AND neutral ones (AllKnownNeutralSightings, added 2026-08-26 — project
        // owner's own spec point 1, same "known sighting" source RaidWeakerArmyTask.FindTarget
        // already merges in for its own ground-raid target pool). Unlike RaidWeakerArmyTask's own
        // "Раздел 5" scope, a bare AllKnownBuildings hex with no known sighting (enemy OR neutral)
        // is never added here: a strike needs a real, known composition to plan against, and ground
        // can capture/clear an empty building for free next turn anyway, so burning a sortie's
        // energy on one that turns out empty (project owner's report, 2026-08-26 turn 24 —
        // Hollowmen struck (5,3), flew home, and the ground army then found only an empty resource
        // building there) is pure waste. Stored aircraft sitting in an airfield never surface here
        // at all — BattleInitiator.IsEngageable (which AiMapMemory's own sighting recorder already
        // gates on) excludes IsAirfield armies entirely, so this needs no separate filter for that
        // case.
        // A candidate with no complete sortie (AiAviationSupport.TryPlanSortie/
        // TryPlanSortieFromStorage returns null) is dropped before scoring — never assumed
        // reachable just because it's known. TryPlanSortie/TryPlanSortieFromStorage are exactly
        // where the "full safe round trip" requirement (start airfield -> target -> any owned
        // airfield with a free landing slot) already lives, so a neutral target with no such route
        // is dropped the same way an unreachable enemy one already was — no separate check needed
        // here for that half of point 1's spec.
        public static StrikeTarget? FindTarget(PlayerSetupData actor, LaunchCandidate candidate, HexMap map)
        {
            if (actor == null || map == null)
                return null;

            var targetHexes = new HashSet<HexCoord>();
            var targetInfo = new Dictionary<HexCoord, (float Defense, float Attack, string Name)>();
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(actor))
            {
                if (sighting.Owner == null || sighting.Owner == actor)
                    continue;
                targetHexes.Add(sighting.Hex);
                targetInfo[sighting.Hex] = (sighting.DefenseSum, sighting.AttackSum, sighting.Name);
            }
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownNeutralSightings(actor))
            {
                targetHexes.Add(sighting.Hex);
                targetInfo[sighting.Hex] = (sighting.DefenseSum, sighting.AttackSum, sighting.Name);
            }

            StrikeTarget? best = null;
            foreach (HexCoord hex in targetHexes)
            {
                AiAviationSupport.Sortie? sortie = candidate.ExistingArmy != null
                    ? AiAviationSupport.TryPlanSortie(candidate.ExistingArmy, hex, map, actor)
                    : AiAviationSupport.TryPlanSortieFromStorage(candidate.AirfieldHex, candidate.Aircraft, hex, map, actor);
                if (!sortie.HasValue)
                    continue;

                targetInfo.TryGetValue(hex, out (float Defense, float Attack, string Name) known);
                float score = ScoreTarget(actor, candidate, sortie.Value, known.Defense, known.Attack, map);
                string name = string.IsNullOrEmpty(known.Name) ? $"target at ({hex.Q},{hex.R})" : known.Name;
                if (best == null || score > best.Value.Score)
                    best = new StrikeTarget(hex, sortie.Value, score,
                        $"air strike on {name} — {candidate.Aircraft.Count} aircraft ready");
            }
            return best;
        }

        // Ranking order per spec: target value/defence worth striking, expected damage × ready
        // aircraft count, shorter total sortie distance, lower AP/energy cost. Known AA route
        // exposure is no longer a term here (2026-08-26, project owner's own follow-up spec item 1
        // — "не трактовать ПВО как простой штраф в score"): `sortie` only ever reaches this method
        // already AA-free, since AiAviationSupport.PlanSortieCore (behind TryPlanSortie/
        // TryPlanSortieFromStorage, see FindTarget above) now hard-filters every candidate landing
        // by known-AA exposure itself, the same rule AirRecon's own FindReconHex already applied.
        // Clamped to airStrikeScoreCap so this tier can never cross a ground raid's own tactical
        // combat/execute score (see that constant's own comment).
        private static float ScoreTarget(PlayerSetupData actor, LaunchCandidate candidate, AiAviationSupport.Sortie sortie,
            float targetDefense, float targetAttack, HexMap map)
        {
            float score = AiConfig.airStrikeBaseWeight;
            float targetValue = targetDefense + targetAttack;
            score += Mathf.Sqrt(Mathf.Max(0f, targetValue)) * AiConfig.airStrikeTargetValueWeight;
            score += candidate.Aircraft.Count * AiConfig.airStrikeTargetValueWeight * 0.5f;
            score -= sortie.TotalCost * AiConfig.airStrikeDistancePenalty;
            int apEnergyCost = candidate.Aircraft.Sum(u => u.ActivationApCost + u.LaunchEnergyCost);
            score -= apEnergyCost * AiConfig.airStrikeApCostPenalty;
            return Mathf.Min(score, AiConfig.airStrikeScoreCap);
        }
    }
}
