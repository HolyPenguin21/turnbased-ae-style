using System.Collections.Generic;
using System.Linq;
using Game.Aviation;
using Game.Economy;
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

        // Breakdown/TargetName/KnownDefense/KnownAttack/KnownDefenders/Estimate (2026-08-26,
        // air-strike scoring rework, project owner's own report) — replaces the old bare BaseScore
        // float. AirStrikeTask never knows about active Raid tasks (see this class's own header
        // comment), so it still never composes a final Reason string itself; it only ever hands
        // back its own raw, UNCAPPED self-value breakdown (Breakdown.Total == old BaseScore's role)
        // plus everything AiAggressionPlanner needs to judge whether this target also helps an
        // active RaidWeakerArmy task. Estimate is the SAME AviationCombatEstimator.EstimateAirStrike
        // call Breakdown's own damage/kill terms were computed from — exposed so AiAggressionPlanner's
        // own raid-coordination step reuses it as the "after first strike" roster instead of paying
        // for an identical second Monte Carlo pass. BaseScore is deliberately never clamped to
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
            public readonly ScoreBreakdown Breakdown;
            public readonly AviationCombatEstimator.AirStrikeEstimate Estimate;
            public readonly string TargetName;
            public readonly float KnownDefense;
            public readonly float KnownAttack;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> KnownDefenders;

            public StrikeTarget(HexCoord hex, AiAviationSupport.Sortie? sortie, AiAviationSupport.MultiTurnSortie? multiTurn,
                ScoreBreakdown breakdown, AviationCombatEstimator.AirStrikeEstimate estimate, string targetName,
                float knownDefense, float knownAttack, IReadOnlyList<WorthIt.DefenderProfile> knownDefenders)
            {
                Hex = hex;
                Sortie = sortie;
                MultiTurn = multiTurn;
                Breakdown = breakdown;
                Estimate = estimate;
                TargetName = targetName;
                KnownDefense = knownDefense;
                KnownAttack = knownAttack;
                KnownDefenders = knownDefenders;
            }

            public HexCoord LandingHex => Sortie?.LandingHex ?? MultiTurn?.LandingHex ?? default;
            public int RequiredTurns => Sortie.HasValue ? 1 : (MultiTurn?.RequiredTurns ?? 1);
            public int RequiredUnlandedEnds => MultiTurn?.RequiredUnlandedEnds ?? 0;
            public float BaseScore => Breakdown.Total;
        }

        // Everything ScoreSelfValue's own "no meaningful effect" rejection used to log directly
        // (2026-08-26 P2 fix, "дедуплицировать лог отклонённых AirStrike") — now handed back to the
        // caller instead, so the log line itself can move up to FindTargets (the "candidate
        // collection" level) and be deduplicated there. Also doubles as the change-fingerprint
        // LogRejectionIfChanged compares against: a coarse read, same "good enough to detect real
        // change, not a full state diff" precision every other fingerprint in this codebase already
        // settles for (see AiTask.LastBattleEstimateLoggedTurn's own comment).
        private readonly struct RejectionDiagnostic
        {
            public readonly float DamageFraction;
            public readonly float KillAnyProbability;
            public readonly string UrgencyLabel;
            public readonly int AircraftCount;
            public readonly float AircraftAttackSum;

            public RejectionDiagnostic(float damageFraction, float killAnyProbability, string urgencyLabel,
                int aircraftCount, float aircraftAttackSum)
            {
                DamageFraction = damageFraction;
                KillAnyProbability = killAnyProbability;
                UrgencyLabel = urgencyLabel;
                AircraftCount = aircraftCount;
                AircraftAttackSum = aircraftAttackSum;
            }

            public int Fingerprint() => System.HashCode.Combine(Mathf.RoundToInt(DamageFraction * 1000f),
                Mathf.RoundToInt(KillAnyProbability * 1000f), UrgencyLabel, AircraftCount, Mathf.RoundToInt(AircraftAttackSum));
        }

        // Rejection-log dedup state (2026-08-26 P2 fix) — keyed by (player, air-group identity,
        // target hex, reason); a fresh LaunchCandidate/StrikeTarget pair is recomputed from scratch
        // every single Decide step (nothing here is a persistent task yet — FindTargets runs during
        // CANDIDATE SEARCH, before any AiTask exists), so unlike AiTask's own per-task fingerprint
        // fields (LastBattleEstimateLoggedTurn) there is no object to hang this state on; a static
        // dictionary is the only place left to remember "did we already say this, and did anything
        // actually change since". Same "at most once per turn, unless the fingerprint changed"
        // rule as that other precedent. Never explicitly cleared between games — a stale entry for
        // a player/hex/army-id combination that no longer exists is harmless dead weight (it just
        // sits unread forever), not a correctness risk, so there's no need for the ceremony of a
        // Clear() hook the way AiMapMemory's own per-game state needs one.
        private static readonly Dictionary<(PlayerSetupData Player, string GroupKey, HexCoord TargetHex, string Reason), (int Turn, int Fingerprint)>
            _rejectionLogState = new Dictionary<(PlayerSetupData, string, HexCoord, string), (int, int)>();

        private static void LogRejectionIfChanged(PlayerSetupData actor, int turnNumber, string groupKey, HexCoord targetHex,
            string reason, int fingerprint, string message)
        {
            var key = (actor, groupKey, targetHex, reason);
            if (_rejectionLogState.TryGetValue(key, out (int Turn, int Fingerprint) last)
                && last.Turn == turnNumber && last.Fingerprint == fingerprint)
                return;
            _rejectionLogState[key] = (turnNumber, fingerprint);
            AiDebugLog.Write(message);
        }

        // Stable identity for a LaunchCandidate's own dedup key — an already-formed air army has
        // its own real ArmyData.Id; a still-stored group has no single persistent object at all
        // (ScoreTarget can be called against a different subset of the same airfield's stock from
        // one step to the next), so the airfield hex itself is the closest stand-in identity —
        // good enough for THIS purpose (suppressing an unchanged repeat log), not meant to survive
        // the stored roster actually changing composition (AircraftCount/AircraftAttackSum in the
        // fingerprint above already catches that case).
        private static string GroupKey(LaunchCandidate candidate) =>
            candidate.ExistingArmy != null ? $"army:{candidate.ExistingArmy.Id}" : $"airfield:{candidate.AirfieldHex.Q},{candidate.AirfieldHex.R}";

        // Additive self-value breakdown for one strike candidate (2026-08-26 air-strike scoring
        // rework, project owner's own spec) — every term here is either a flat weight from AiConfig
        // or reads straight off AviationCombatEstimator's own Monte Carlo output; no second combat
        // model. Total deliberately excludes any raid-coordination bonus (AiAggressionPlanner's own
        // EvaluateRaidCoordination adds that separately, then applies airStrikeScoreCap exactly
        // once) — this is a target's STANDALONE tactical value, per spec section 4's "самостоятельная
        // тактическая ценность" (a strike must never require an active raid to be worth flying).
        public readonly struct ScoreBreakdown
        {
            public readonly float Base;
            public readonly float DamageFraction;
            public readonly float DamageValue;
            public readonly float KillAnyProbability;
            public readonly float KillValue;
            public readonly float UrgencyValue;
            public readonly bool IsCitadelUrgency;
            public readonly float RouteCost;
            public readonly float ResourceScarcityCost;
            // Energy forecast diagnostics (2026-08-26 P1 fix, "last Energy" planner/executor
            // parity) — the same energy-before/predicted-cost/energy-after numbers
            // ResourceScarcityPenalty derived ResourceScarcityCost from, kept alongside it purely
            // so BuildAirStrikeReason/debug logging can show the forecast, not just the resulting
            // penalty number. Always 0/0/0/false for a repeat strike or any other candidate
            // ResourceScarcityPenalty never ran for (no NEW energy spend to forecast there).
            public readonly float EnergyBefore;
            public readonly float PredictedEnergyCost;
            public readonly float EnergyAfter;
            public readonly bool EnergyAlreadyPaid;
            public readonly float Total;

            public ScoreBreakdown(float baseWeight, float damageFraction, float damageValue, float killAnyProbability,
                float killValue, float urgencyValue, bool isCitadelUrgency, float routeCost, float resourceScarcityCost,
                float energyBefore = 0f, float predictedEnergyCost = 0f, float energyAfter = 0f, bool energyAlreadyPaid = false)
            {
                Base = baseWeight;
                DamageFraction = damageFraction;
                DamageValue = damageValue;
                KillAnyProbability = killAnyProbability;
                KillValue = killValue;
                UrgencyValue = urgencyValue;
                IsCitadelUrgency = isCitadelUrgency;
                RouteCost = routeCost;
                ResourceScarcityCost = resourceScarcityCost;
                EnergyBefore = energyBefore;
                PredictedEnergyCost = predictedEnergyCost;
                EnergyAfter = energyAfter;
                EnergyAlreadyPaid = energyAlreadyPaid;
                Total = baseWeight + damageValue + killValue + urgencyValue - routeCost - resourceScarcityCost;
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
        // Every reachable known target with a complete sortie, not just the single best one — added
        // 2026-08-26 (AirStrike/Raid coordination spec) so AiAggressionPlanner can weigh a
        // coordination bonus against EVERY candidate's own BaseScore before picking a winner,
        // instead of only ever seeing whichever target happened to win on raw BaseScore alone (see
        // this method's own callers for why: a lower-BaseScore target that actually unlocks an
        // active raid must be able to outrank a higher-BaseScore one that doesn't).
        // `turnNumber` (2026-08-26 P1/P2 diagnostics fix) — only ever used to key the rejection-log
        // dedup below (LogRejectionIfChanged/_rejectionLogState); never read by any selection or
        // scoring decision, so passing it changes no AI behavior.
        public static IEnumerable<StrikeTarget> FindTargets(PlayerSetupData actor, PlayerRoot root, LaunchCandidate candidate, HexMap map,
            int turnNumber)
        {
            if (actor == null || map == null)
                yield break;

            string groupKey = GroupKey(candidate);

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

            // "No known targets at all" (2026-08-26 P2 fix, "разделить причины отсутствия
            // кандидата AirStrike") — its own distinct diagnostic category, checked once here
            // rather than falling through to the generic post-loop summary below with a zero
            // known-target count baked in (same information, just a clearer single-purpose line).
            if (targetHexes.Count == 0)
            {
                LogRejectionIfChanged(actor, turnNumber, groupKey, default, "NoKnownTargets", turnNumber,
                    $"[AI] {actor.Nickname}: AirStrike unavailable from ({candidate.AirfieldHex.Q},{candidate.AirfieldHex.R}) — no known targets.");
                yield break;
            }

            int blockedByAa = 0, noRoute = 0, ineffective = 0, accepted = 0;
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
                    {
                        // No structured reason comes back from TryPlanSortie/TryPlanMultiTurnSortie
                        // itself (both just return null on any failure — range, landing capacity, OR
                        // the known-AA hard filter, all folded together). KnownAaExposureAt(hex) is
                        // the one honest signal still available from here without reworking that
                        // planning API: it can't tell whether AA blocked THIS route specifically,
                        // only whether the target hex itself is known to carry AA — a reasonable
                        // proxy in practice (an AA-carrying garrison is usually what a strike route
                        // fails to safely reach), not a precise trace of the real rejection.
                        if (AiAviationSupport.KnownAaExposureAt(actor, hex) > 0)
                            blockedByAa++;
                        else
                            noRoute++;
                        continue;
                    }
                }

                targetInfo.TryGetValue(hex, out var known);
                string name = string.IsNullOrEmpty(known.Name) ? $"target at ({hex.Q},{hex.R})" : known.Name;
                var scored = ScoreTarget(actor, root, candidate, hex, sortie, multiTurn, known.Defense, known.Attack, known.Defenders, name,
                    out RejectionDiagnostic? rejection);
                if (!scored.HasValue)
                {
                    ineffective++;
                    if (rejection.HasValue)
                        LogRejectionIfChanged(actor, turnNumber, groupKey, hex, "Ineffective", rejection.Value.Fingerprint(),
                            $"[AI] {actor.Nickname}: AirStrike rejected: no meaningful effect — target {name}, "
                            + $"expectedDamage={rejection.Value.DamageFraction:P2}, killAny={rejection.Value.KillAnyProbability:P2}, "
                            + $"urgency={rejection.Value.UrgencyLabel}.");
                    continue;
                }
                accepted++;
                (ScoreBreakdown breakdown, AviationCombatEstimator.AirStrikeEstimate estimate) = scored.Value;
                yield return new StrikeTarget(hex, sortie, multiTurn, breakdown, estimate, name, known.Defense, known.Attack, known.Defenders);
            }

            // Aggregate "nothing usable this step" diagnostic (2026-08-26 P2 fix) — replaces the old
            // single blanket "no reachable known target with a complete sortie" line
            // (AiAggressionPlanner's own caller used to log this itself whenever it saw zero yielded
            // targets); this version actually breaks the known-target pool down by why each one
            // didn't pan out, instead of a single generic phrase that read the same whether nothing
            // was known, everything was AA-blocked, or every target was simply not worth hitting.
            if (accepted == 0)
            {
                int fingerprint = System.HashCode.Combine(targetHexes.Count, blockedByAa, noRoute, ineffective);
                LogRejectionIfChanged(actor, turnNumber, groupKey, default, "Summary", fingerprint,
                    $"[AI] {actor.Nickname}: AirStrike unavailable from ({candidate.AirfieldHex.Q},{candidate.AirfieldHex.R}): "
                    + $"{targetHexes.Count} known target(s) — {blockedByAa} blocked by AA, {noRoute} has no complete sortie, "
                    + $"{ineffective} rejected as ineffective.");
            }
        }

        // Compatibility wrapper — the single best-BaseScore target, same selection this method
        // always made before FindTargets existed. AiAggressionPlanner no longer calls this (it needs
        // every candidate to weigh its own coordination bonus — see FindTargets' own comment); kept
        // for any other/future caller that only ever wants "the one best target" with no raid
        // awareness. Deliberately NOT `FindTargets(...).OrderByDescending(...).FirstOrDefault()`
        // directly — StrikeTarget is a struct, so FirstOrDefault() on an empty sequence would
        // silently hand back a bogus all-default StrikeTarget instead of a real "no target" null.
        public static StrikeTarget? FindTarget(PlayerSetupData actor, PlayerRoot root, LaunchCandidate candidate, HexMap map, int turnNumber)
        {
            List<StrikeTarget> targets = FindTargets(actor, root, candidate, map, turnNumber).ToList();
            return targets.Count > 0 ? targets.OrderByDescending(t => t.BaseScore).First() : (StrikeTarget?)null;
        }

        // Additive self-value scoring (2026-08-26 air-strike scoring rework, project owner's own
        // spec — see ScoreBreakdown's own comment for the full term list). Every army-vs-army number
        // here comes from ONE AviationCombatEstimator.EstimateAirStrike Monte Carlo pass, reused for
        // both the damage and kill terms and returned to the caller (so AiAggressionPlanner's own
        // raid-coordination step never re-simulates the same first strike). Known-AA route exposure
        // is still not a term here (2026-08-26, project owner's own earlier follow-up spec item 1 —
        // "не трактовать ПВО как простой штраф в score"): `sortie` only ever reaches this method
        // already AA-free, since AiAviationSupport.PlanSortieCore (behind TryPlanSortie/
        // TryPlanSortieFromStorage, see FindTargets above) now hard-filters every candidate landing
        // by known-AA exposure itself. Not clamped to airStrikeScoreCap here — see
        // StrikeTarget.BaseScore's own comment for why the cap only ever applies once, in
        // AiAggressionPlanner, after any coordination bonus is added.
        private static (ScoreBreakdown Breakdown, AviationCombatEstimator.AirStrikeEstimate Estimate)? ScoreTarget(
            PlayerSetupData actor, PlayerRoot root, LaunchCandidate candidate, HexCoord targetHex,
            AiAviationSupport.Sortie? sortie, AiAviationSupport.MultiTurnSortie? multiTurn,
            float targetDefense, float targetAttack, IReadOnlyList<WorthIt.DefenderProfile> targetDefenders, string targetName,
            out RejectionDiagnostic? rejection)
        {
            var selfValueResult = ScoreSelfValue(actor, candidate.Aircraft, targetHex, targetDefense, targetAttack,
                targetDefenders, targetName, out rejection);
            if (!selfValueResult.HasValue)
                return null;
            (ScoreBreakdown selfValue, AviationCombatEstimator.AirStrikeEstimate estimate) = selfValueResult.Value;

            int totalCost = sortie?.TotalCost ?? multiTurn?.TotalRouteCost ?? 0;
            float routeCost = totalCost * AiConfig.airStrikeDistancePenalty;
            int apEnergyCost = candidate.Aircraft.Sum(u => u.ActivationApCost + u.LaunchEnergyCost);
            routeCost += apEnergyCost * AiConfig.airStrikeApCostPenalty;
            if (multiTurn.HasValue)
            {
                routeCost += Mathf.Max(0, multiTurn.Value.RequiredTurns - 1) * AiConfig.airStrikeExtraTurnPenalty;
                routeCost += multiTurn.Value.RequiredUnlandedEnds * AiConfig.airStrikeUnlandedEndPenalty;
            }

            EnergyForecast energyForecast = ResourceScarcityPenalty(root, actor, candidate);

            var breakdown = new ScoreBreakdown(selfValue.Base, selfValue.DamageFraction, selfValue.DamageValue,
                selfValue.KillAnyProbability, selfValue.KillValue, selfValue.UrgencyValue, selfValue.IsCitadelUrgency,
                routeCost, energyForecast.Penalty, energyForecast.EnergyBefore, energyForecast.PredictedCost,
                energyForecast.EnergyAfter, energyForecast.AlreadyPaid);
            return (breakdown, estimate);
        }

        // Repeat-strike self-value (2026-08-26 rework, spec section 7) — a helicopter already
        // sitting on the target hex, deciding whether to hold for one more strike
        // (AiAggressionPlanner.TryContinueLoiterAtTarget), scored through the exact same
        // base+damage+kill(+urgency) terms ScoreTarget uses for a fresh candidate — just against the
        // REAL ground-truth roster still standing on the hex (AviationCombatPresenter.
        // FindAirStrikeTargetsAt) instead of a remembered sighting, and with no route/AP/resource
        // cost at all (the army is already there — nothing left to fly or launch). Replaces the old
        // flat airStrikeRepeatScore constant: a repeat against a real, still-dangerous remnant
        // naturally scores in the normal AirStrike band; a repeat against an already-thinned
        // remnant naturally falls toward airStrikeBaseWeight, per spec's own "естественная оценка
        // текущего состава и HP цели". Nullable for the same reason ScoreTarget is — the P1
        // "no meaningful effect" gate (see ScoreSelfValue) applies here too, so a repeat with no
        // real expected damage/kill left against the ground-truth remnant is rejected the same way
        // a fresh candidate would be, never allowed through just because the army is already there.
        public static (ScoreBreakdown Breakdown, AviationCombatEstimator.AirStrikeEstimate Estimate)? ScoreRepeatStrike(
            PlayerSetupData actor, IReadOnlyList<UnitData> aircraft, HexCoord targetHex,
            IReadOnlyList<WorthIt.DefenderProfile> targetDefenders, string targetName = null)
        {
            float targetDefense = targetDefenders?.Sum(d => d.Defense) ?? 0f;
            float targetAttack = targetDefenders?.Sum(d => d.Attack) ?? 0f;
            // Rejection reason discarded — AiAggressionPlanner.TryContinueLoiterAtTarget's own
            // caller already logs a single unconditional "repeat AirStrike cancelled" line for a
            // null result here, and that task transitions out of the loiter phase the same step
            // (never calls back in for the same still-unchanged rejection), so this path was never
            // the dedup problem FindTargets' own per-step re-scan was.
            return ScoreSelfValue(actor, aircraft, targetHex, targetDefense, targetAttack, targetDefenders, targetName, out _);
        }

        // Shared damage/kill/urgency core (spec sections 1, 2, 5) behind both ScoreTarget (a fresh
        // candidate, plus its own route/resource cost) and ScoreRepeatStrike (an already-landed
        // repeat, no route/resource cost at all) — one Monte Carlo pass
        // (AviationCombatEstimator.EstimateAirStrike), read into the same weighted terms either way,
        // so the two paths can never quietly diverge on what "expected outcome" means.
        //
        // Returns null — reject the candidate outright, before it ever becomes a scored StrikeTarget
        // or competes on Total — when the SAME forecast neither of ScoreTarget/ScoreRepeatStrike ever
        // recomputes shows no meaningful expected damage AND no meaningful chance to kill anything
        // (2026-08-26 P1 fix, "исключить авиаудары с нулевой ожидаемой эффективностью"; see
        // AiConfig.airStrikeMinExpectedDamageFraction/airStrikeMinKillProbability's own comment).
        // Deliberately checked BEFORE urgencyValue is even added to the breakdown — a target this
        // forecast says the AI cannot meaningfully hurt does not "defend" the base/citadel just
        // because it happens to be the live threat Defence is reacting to.
        private static (ScoreBreakdown Breakdown, AviationCombatEstimator.AirStrikeEstimate Estimate)? ScoreSelfValue(
            PlayerSetupData actor, IReadOnlyList<UnitData> aircraft, HexCoord targetHex,
            float targetDefense, float targetAttack, IReadOnlyList<WorthIt.DefenderProfile> targetDefenders,
            string targetName, out RejectionDiagnostic? rejection)
        {
            rejection = null;
            AviationCombatEstimator.AirStrikeEstimate estimate =
                AviationCombatEstimator.EstimateAirStrike(aircraft, targetDefense, targetAttack, targetDefenders);

            // damageFraction (spec section 1) — only computable against a real per-unit roster
            // (targetDefenders); an aggregate-sum-only sighting has no per-unit HP to divide by, so
            // this term reads 0 for it, same "no per-unit data, no estimate" convention
            // RaidWeakerArmyTask.EstimateAgainst's own aggregate fallback already follows.
            float targetTotalHp = targetDefenders != null ? targetDefenders.Sum(d => Mathf.Max(0f, d.HitPoints)) : 0f;
            float damageFraction = targetTotalHp > 0.01f ? Mathf.Clamp01(estimate.ExpectedDamage / targetTotalHp) : 0f;

            if (damageFraction < AiConfig.airStrikeMinExpectedDamageFraction
                && estimate.KillAnyProbability < AiConfig.airStrikeMinKillProbability)
            {
                bool wouldBeCitadelUrgency = false;
                bool wouldBeUrgent = actor != null
                    && AiDefencePlanner.IsUrgentAirStrikeTarget(actor, targetHex, out wouldBeCitadelUrgency);
                string urgencyLabel = !wouldBeUrgent ? "none" : wouldBeCitadelUrgency ? "citadel" : "base";
                // No direct log here any more (2026-08-26 P2 fix, "дедуплицировать лог отклонённых
                // AirStrike") — this is a pure scoring method, called every step for every known
                // target; handed back to the caller instead (FindTargets/ScoreRepeatStrike's own
                // caller) so the actual log line can live at the candidate-collection level, deduped.
                rejection = new RejectionDiagnostic(damageFraction, estimate.KillAnyProbability, urgencyLabel,
                    aircraft?.Count ?? 0, aircraft?.Sum(u => u.Attack) ?? 0f);
                return null;
            }

            float damageValue = damageFraction * AiConfig.airStrikeDamageFractionWeight;

            // Kill value (spec section 2) — bounded by construction: KillAnyProbability/WipeProbability
            // are themselves in [0,1], so this term can never exceed
            // airStrikeKillAnyWeight + airStrikeExpectedKillWeight*rosterSize + airStrikeWipeBonus.
            float killValue = estimate.KillAnyProbability * AiConfig.airStrikeKillAnyWeight
                + estimate.ExpectedKillCount * AiConfig.airStrikeExpectedKillWeight
                + estimate.WipeProbability * AiConfig.airStrikeWipeBonus;

            float urgencyValue = 0f;
            bool isCitadelUrgency = false;
            if (actor != null && AiDefencePlanner.IsUrgentAirStrikeTarget(actor, targetHex, out isCitadelUrgency))
                urgencyValue = isCitadelUrgency ? AiConfig.airStrikeUrgencyCitadelBonus : AiConfig.airStrikeUrgencyBaseBonus;

            var breakdown = new ScoreBreakdown(AiConfig.airStrikeBaseWeight, damageFraction, damageValue,
                estimate.KillAnyProbability, killValue, urgencyValue, isCitadelUrgency, 0f, 0f);
            return (breakdown, estimate);
        }

        // Energy forecast for launching THIS candidate (2026-08-26 P1 fix, "корректно учитывать
        // расход последней Energy для сформированной авиагруппы") — reuses the exact same
        // ArmyData.HasActivatedThisTurn rule the real executor (HexSelectionController.Movement's
        // own MoveArmy cost computation: `army.HasActivatedThisTurn ? 0 : army.ActivationEnergyCost`)
        // already charges by, instead of assuming ExistingArmy != null means free. A still-STORED
        // group (ExistingArmy == null) has never activated by definition — its own first move,
        // this same launch, always pays the full cost. A formed, landed, untasked group pays it too
        // UNLESS it already activated (and so already paid) earlier this same turn, in which case
        // this launch is its second move and costs nothing further — same "already activated, free"
        // rule every other AI spend check keys off ArmyData.HasActivatedThisTurn for.
        private readonly struct EnergyForecast
        {
            public readonly float Penalty;
            public readonly float EnergyBefore;
            public readonly float PredictedCost;
            public readonly float EnergyAfter;
            public readonly bool AlreadyPaid;

            public EnergyForecast(float penalty, float energyBefore, float predictedCost, float energyAfter, bool alreadyPaid)
            {
                Penalty = penalty;
                EnergyBefore = energyBefore;
                PredictedCost = predictedCost;
                EnergyAfter = energyAfter;
                AlreadyPaid = alreadyPaid;
            }
        }

        // Resource scarcity (spec section 6) — extra penalty when launching THIS candidate would
        // leave zero AiResourceReservation-visible Energy free for any OTHER AI spend this same
        // step. Read through AiResourceReservation.Available, never root.GetResource directly —
        // same "reserved resources are never free" rule AiAviationSupport.CanAffordLaunch's own
        // comment already states for this exact Energy check.
        private static EnergyForecast ResourceScarcityPenalty(PlayerRoot root, PlayerSetupData actor, LaunchCandidate candidate)
        {
            if (root == null || actor == null)
                return new EnergyForecast(0f, 0f, 0f, 0f, false);

            float energyBefore = AiResourceReservation.Available(root, actor, ResourceType.Energy);
            bool alreadyPaid = candidate.ExistingArmy != null && candidate.ExistingArmy.HasActivatedThisTurn;
            if (alreadyPaid)
                return new EnergyForecast(0f, energyBefore, 0f, energyBefore, true);

            float energyCost = candidate.Aircraft.Sum(u => u.LaunchEnergyCost);
            if (energyCost <= 0)
                return new EnergyForecast(0f, energyBefore, 0f, energyBefore, false);

            float energyAfter = energyBefore - energyCost;
            float penalty = energyAfter <= 0.01f ? AiConfig.airStrikeLastEnergyPenalty : 0f;
            return new EnergyForecast(penalty, energyBefore, energyCost, energyAfter, false);
        }
    }
}
