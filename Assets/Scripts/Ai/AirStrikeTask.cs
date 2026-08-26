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

        // BaseScore/TargetName/KnownDefense/KnownAttack/KnownDefenders (2026-08-26, AirStrike/Raid
        // coordination spec, project owner's own report) — replaces the old Score/Reason pair.
        // AirStrikeTask never knows about active Raid tasks (see this class's own header comment),
        // so it no longer composes a final Reason string itself; it only ever hands back its own
        // raw, UNCAPPED base score plus everything AiAggressionPlanner needs to judge whether this
        // target also helps an active RaidWeakerArmy task (KnownDefense/KnownAttack/KnownDefenders
        // feed AviationCombatEstimator.EstimateAirStrike directly, same three numbers ScoreTarget
        // itself already read off the same sighting). BaseScore is deliberately never clamped to
        // airStrikeScoreCap here any more — see that constant's own comment: the cap now applies
        // exactly once, in AiAggressionPlanner, AFTER any coordination bonus is added, so a
        // genuinely raid-supporting strike can still win against a marginally-higher-BaseScore
        // ordinary target instead of the bonus being wasted on an already-clamped number.
        public readonly struct StrikeTarget
        {
            public readonly HexCoord Hex;
            // Exactly one of Sortie/MultiTurn is ever set (2026-08-26 multi-turn aviation spec) —
            // Sortie for an ordinary same-turn round trip, MultiTurn for a helicopter-style route
            // spanning several turns, only ever considered once Sortie itself came back null (see
            // FindTargets — same-turn always wins when both exist, per spec point 10's "если
            // доступен однодневный план — использовать его как сейчас").
            public readonly AiAviationSupport.Sortie? Sortie;
            public readonly AiAviationSupport.MultiTurnSortie? MultiTurn;
            public readonly float BaseScore;
            public readonly string TargetName;
            public readonly float KnownDefense;
            public readonly float KnownAttack;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> KnownDefenders;

            public StrikeTarget(HexCoord hex, AiAviationSupport.Sortie? sortie, AiAviationSupport.MultiTurnSortie? multiTurn,
                float baseScore, string targetName, float knownDefense, float knownAttack,
                IReadOnlyList<WorthIt.DefenderProfile> knownDefenders)
            {
                Hex = hex;
                Sortie = sortie;
                MultiTurn = multiTurn;
                BaseScore = baseScore;
                TargetName = targetName;
                KnownDefense = knownDefense;
                KnownAttack = knownAttack;
                KnownDefenders = knownDefenders;
            }

            public HexCoord LandingHex => Sortie?.LandingHex ?? MultiTurn?.LandingHex ?? default;
            public int RequiredTurns => Sortie.HasValue ? 1 : (MultiTurn?.RequiredTurns ?? 1);
            public int RequiredUnlandedEnds => MultiTurn?.RequiredUnlandedEnds ?? 0;
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
        // Every reachable known target with a complete sortie, not just the single best one — added
        // 2026-08-26 (AirStrike/Raid coordination spec) so AiAggressionPlanner can weigh a
        // coordination bonus against EVERY candidate's own BaseScore before picking a winner,
        // instead of only ever seeing whichever target happened to win on raw BaseScore alone (see
        // this method's own callers for why: a lower-BaseScore target that actually unlocks an
        // active raid must be able to outrank a higher-BaseScore one that doesn't).
        public static IEnumerable<StrikeTarget> FindTargets(PlayerSetupData actor, LaunchCandidate candidate, HexMap map)
        {
            if (actor == null || map == null)
                yield break;

            var targetHexes = new HashSet<HexCoord>();
            var targetInfo = new Dictionary<HexCoord, (float Defense, float Attack, string Name, IReadOnlyList<WorthIt.DefenderProfile> Defenders)>();
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownEnemySightings(actor))
            {
                if (sighting.Owner == null || sighting.Owner == actor)
                    continue;
                targetHexes.Add(sighting.Hex);
                targetInfo[sighting.Hex] = (sighting.DefenseSum, sighting.AttackSum, sighting.Name, sighting.Defenders);
            }
            foreach (AiMapMemory.KnownEnemySighting sighting in AiMapMemory.AllKnownNeutralSightings(actor))
            {
                targetHexes.Add(sighting.Hex);
                targetInfo[sighting.Hex] = (sighting.DefenseSum, sighting.AttackSum, sighting.Name, sighting.Defenders);
            }

            foreach (HexCoord hex in targetHexes)
            {
                AiAviationSupport.Sortie? sortie = candidate.ExistingArmy != null
                    ? AiAviationSupport.TryPlanSortie(candidate.ExistingArmy, hex, map, actor)
                    : AiAviationSupport.TryPlanSortieFromStorage(candidate.AirfieldHex, candidate.Aircraft, hex, map, actor);

                // No same-turn round trip — before dropping this target, check whether the group's
                // own SafeUnlandedEndsRemaining margin (always 0 for a plane, see that method's own
                // comment) allows a proven-safe multi-turn route instead (2026-08-26 multi-turn
                // aviation spec, point 10). AiAggressionPlanner still decides whether to actually
                // START one — this only ever hands back a technically-possible option, never
                // touches strategic state itself (this class's own header comment).
                AiAviationSupport.MultiTurnSortie? multiTurn = null;
                if (!sortie.HasValue)
                {
                    multiTurn = candidate.ExistingArmy != null
                        ? AiAviationSupport.TryPlanMultiTurnSortie(candidate.ExistingArmy, hex, map, actor)
                        : AiAviationSupport.TryPlanMultiTurnSortieFromStorage(candidate.AirfieldHex, candidate.Aircraft, hex, map, actor);
                    if (!multiTurn.HasValue)
                        continue;
                }

                targetInfo.TryGetValue(hex, out var known);
                float baseScore = ScoreTarget(candidate, sortie, multiTurn, known.Defense, known.Attack);
                string name = string.IsNullOrEmpty(known.Name) ? $"target at ({hex.Q},{hex.R})" : known.Name;
                yield return new StrikeTarget(hex, sortie, multiTurn, baseScore, name, known.Defense, known.Attack, known.Defenders);
            }
        }

        // Compatibility wrapper — the single best-BaseScore target, same selection this method
        // always made before FindTargets existed. AiAggressionPlanner no longer calls this (it needs
        // every candidate to weigh its own coordination bonus — see FindTargets' own comment); kept
        // for any other/future caller that only ever wants "the one best target" with no raid
        // awareness. Deliberately NOT `FindTargets(...).OrderByDescending(...).FirstOrDefault()`
        // directly — StrikeTarget is a struct, so FirstOrDefault() on an empty sequence would
        // silently hand back a bogus all-default StrikeTarget instead of a real "no target" null.
        public static StrikeTarget? FindTarget(PlayerSetupData actor, LaunchCandidate candidate, HexMap map)
        {
            List<StrikeTarget> targets = FindTargets(actor, candidate, map).ToList();
            return targets.Count > 0 ? targets.OrderByDescending(t => t.BaseScore).First() : (StrikeTarget?)null;
        }

        // Ranking order per spec: target value/defence worth striking, expected damage × ready
        // aircraft count, shorter total sortie distance, lower AP/energy cost. Known AA route
        // exposure is no longer a term here (2026-08-26, project owner's own follow-up spec item 1
        // — "не трактовать ПВО как простой штраф в score"): `sortie` only ever reaches this method
        // already AA-free, since AiAviationSupport.PlanSortieCore (behind TryPlanSortie/
        // TryPlanSortieFromStorage, see FindTargets above) now hard-filters every candidate landing
        // by known-AA exposure itself, the same rule AirRecon's own FindReconHex already applied.
        // No longer clamped to airStrikeScoreCap here (2026-08-26, AirStrike/Raid coordination spec)
        // — see StrikeTarget.BaseScore's own comment for why the cap moved to AiAggressionPlanner.
        // multiTurn (2026-08-26 multi-turn aviation spec, point 10) — every extra turn before the
        // strike actually lands costs airStrikeExtraTurnPenalty, and every intermediate safe-
        // unlanded-end the route spends costs airStrikeUnlandedEndPenalty, on top of the ordinary
        // distance/AP terms below — deliberately small relative to airStrikeBaseWeight/
        // airStrikeTargetValueWeight so a genuinely valuable multi-turn strike can still win, per
        // spec's own "не делать штраф настолько большим, чтобы вертолётная механика фактически
        // никогда не использовалась".
        private static float ScoreTarget(LaunchCandidate candidate, AiAviationSupport.Sortie? sortie,
            AiAviationSupport.MultiTurnSortie? multiTurn, float targetDefense, float targetAttack)
        {
            float score = AiConfig.airStrikeBaseWeight;
            float targetValue = targetDefense + targetAttack;
            score += Mathf.Sqrt(Mathf.Max(0f, targetValue)) * AiConfig.airStrikeTargetValueWeight;
            score += candidate.Aircraft.Count * AiConfig.airStrikeTargetValueWeight * 0.5f;
            int totalCost = sortie?.TotalCost ?? multiTurn?.TotalRouteCost ?? 0;
            score -= totalCost * AiConfig.airStrikeDistancePenalty;
            int apEnergyCost = candidate.Aircraft.Sum(u => u.ActivationApCost + u.LaunchEnergyCost);
            score -= apEnergyCost * AiConfig.airStrikeApCostPenalty;
            if (multiTurn.HasValue)
            {
                score -= Mathf.Max(0, multiTurn.Value.RequiredTurns - 1) * AiConfig.airStrikeExtraTurnPenalty;
                score -= multiTurn.Value.RequiredUnlandedEnds * AiConfig.airStrikeUnlandedEndPenalty;
            }
            return score;
        }
    }
}
