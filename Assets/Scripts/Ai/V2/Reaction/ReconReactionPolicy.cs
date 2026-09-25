using System;
using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  LIVE GROUND RECON REACTIONS
    // ===========================================================================================
    // Called only after authoritative movement/vision/stealth state has settled. It never reads
    // TrueWorld and never emits a multi-step plan. One decision is valid for one immediate action;
    // after that action the executor must call this policy again on the new live state.
    // ===========================================================================================
    public enum ReconReactionAction
    {
        Continue,
        Flee,
        EvadeDetector,
        AttackOpportunity,
        // Step onto an adjacent, currently visible, undefended enemy structure: a Facility is
        // destroyed (the owner loses its output), a Base is taken over. Requires leaving stealth —
        // a fully hidden mover takes no action on arrival (BuildingRegistry.CaptureOrDestroyIfUndefended).
        SabotageStructure,
        StopAndReplan,
    }

    public readonly struct ReconReactionDecision
    {
        public readonly ReconReactionAction Action;
        public readonly HexCoord? TargetHex;
        public readonly int? TargetArmyId;
        public readonly float WinChance;
        public readonly string Reason;

        public ReconReactionDecision(ReconReactionAction action, HexCoord? targetHex,
            int? targetArmyId, float winChance, string reason)
        {
            Action = action;
            TargetHex = targetHex;
            TargetArmyId = targetArmyId;
            WinChance = winChance;
            Reason = reason ?? string.Empty;
        }

        public static ReconReactionDecision ContinueDecision(string reason = "no live reaction") =>
            new ReconReactionDecision(ReconReactionAction.Continue, null, null, 0f, reason);

        public override string ToString() =>
            $"action={Action} target={(TargetHex.HasValue ? $"({TargetHex.Value.Q},{TargetHex.Value.R})" : "none")} "
            + $"army={(TargetArmyId.HasValue ? $"#{TargetArmyId.Value}" : "none")} "
            + $"win={WinChance:0.00} reason={Reason}";
    }

    internal static class ReconReactionPolicy
    {
        private const float AttackOpportunityWinChance = AiConfigV2.scoutReactionAttackWinChance;
        private const float StrongEnemyFleeWinChance = AiConfigV2.scoutReactionFleeWinChance;

        public static ReconReactionDecision Evaluate(PlayerSetupData player, HexMap map, ArmyData army,
            ReconPatrolState assignment)
        {
            if (player == null || map == null || army == null || assignment == null)
                return new ReconReactionDecision(ReconReactionAction.StopAndReplan, null, null, 0f,
                    "missing live recon state");
            if (!AiArmyRoles.IsSoloRecce(army))
                return new ReconReactionDecision(ReconReactionAction.StopAndReplan, null, null, 0f,
                    "actor is no longer solo Recce");

            bool inStealth = StealthSystem.IsArmyFullyHidden(army);

            ReconReactionDecision? flee = FindStrongExposedThreat(player, map, army, inStealth);
            if (flee.HasValue)
                return Log(army, assignment, flee.Value);

            if (inStealth && CurrentDetectorRisk(player, army.Hex) > 0f)
            {
                HexCoord? evade = PickLowerDetectorRiskStep(player, map, army);
                if (evade.HasValue)
                    return Log(army, assignment, new ReconReactionDecision(
                        ReconReactionAction.EvadeDetector, evade, null, 0f,
                        "known detector envelope; lower-risk adjacent step exists"));
            }

            ReconReactionDecision? sabotage = FindUndefendedStructureOpportunity(player, map, army);
            if (sabotage.HasValue)
                return Log(army, assignment, sabotage.Value);

            ReconReactionDecision? attack = FindWeakScoutOpportunity(player, map, army);
            if (attack.HasValue)
                return Log(army, assignment, attack.Value);

            return Log(army, assignment, ReconReactionDecision.ContinueDecision());
        }

        private static ReconReactionDecision? FindStrongExposedThreat(PlayerSetupData player, HexMap map,
            ArmyData army, bool inStealth)
        {
            float worstWin = 1f;
            AiMapMemory.KnownEnemySighting? worst = null;
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { army.Hex }, AiConfig.scoutFleeRadius))
            {
                // A building-bound garrison cannot leave its structure to catch the scout
                // (AiMapMemory keeps it permanently since audit F1); it is detection risk only,
                // which DetectorRisk still counts.
                if (sighting.Owner == null || sighting.Owner.IsNeutral || sighting.IsGarrison)
                    continue;
                // Own stealth state only — whether this enemy has DETECTED the scout is unknown to
                // its owner (stealth design), so it is never read here.
                if (inStealth && StealthSystem.IsArmyFullyHidden(army))
                    continue;

                // Do NOT call WorthIt.HexDefenseBonus on a stale/non-visible enemy position: that
                // helper includes live BuildingRegistry and would leak a structure change through
                // fog. Terrain is immutable/public and safe; structural defence is added only when
                // the hex is currently visible to this player.
                float hexBonus = HonestHexDefenseBonus(player, map, sighting.Hex);
                float win = WorthIt.WinChance(army, sighting.Defenders, hexBonus, sighting.Commander);
                if (win < worstWin)
                {
                    worstWin = win;
                    worst = sighting;
                }
            }

            if (!worst.HasValue || worstWin >= StrongEnemyFleeWinChance)
                return null;

            HexCoord fleeTo = PickFleeTarget(player, map, army, worst.Value.Hex);
            return new ReconReactionDecision(ReconReactionAction.Flee, fleeTo, worst.Value.ArmyId,
                worstWin, $"exposed to stronger known enemy near ({worst.Value.Hex.Q},{worst.Value.Hex.R})");
        }

        // §14 — a real flee-destination evaluator. The nearest owned garrison is only the fallback
        // and the friendly-approach term; the chosen hex also maximises distance from the threat,
        // keeps recon useful, and avoids known detectors / hostile zones / backtracking. Running
        // home to the citadel is no longer the automatic answer to any strong sighting.
        private static HexCoord PickFleeTarget(PlayerSetupData player, HexMap map, ArmyData army,
            HexCoord threatHex)
        {
            HexCoord fallback = AiTurnController.NearestOwnGarrisonHex(player, army.Hex);
            int radius = Math.Max(2, AiConfig.scoutFleeRadius);
            HexCoord best = fallback;
            float bestScore = float.NegativeInfinity;

            foreach (HexCoord h in HexGridMath.HexesInRange(army.Hex, radius))
            {
                if (h.Equals(army.Hex) || !map.TryGetTerrainAt(h, out _))
                    continue;
                // The one Recon step rule, for the scout as it is now (a flee never enters stealth).
                if (ScoutExecutionSafety.StepBlocked(player, army, h))
                    continue;

                float fromThreat = HexGridMath.Distance(h, threatHex);
                float toFriendly = HexGridMath.Distance(h, fallback);
                float detector = CurrentDetectorRisk(player, h);
                int freshNeighbors = 0;
                foreach (HexCoord n in HexGridMath.Neighbors(h))
                    if (map.TryGetTerrainAt(n, out _) && !VisionSystem.IsVisited(player, n))
                        freshNeighbors++;
                float futureRecon = freshNeighbors / 6f;
                int backtrack = ScoutTrailRegistry.RecentTrailHits(player, army.Id, new[] { h });

                float score = AiConfigV2.scoutFleeThreatDistWeight * fromThreat
                    + AiConfigV2.scoutFleeFriendlyApproachWeight * (1f / (1f + toFriendly))
                    + AiConfigV2.scoutFleeFutureReconWeight * futureRecon
                    - AiConfigV2.scoutFleeDetectorWeight * detector
                    - AiConfigV2.scoutFleeBacktrackWeight * backtrack;
                // The executor asks the shared SafeStepPathing owner for Flee's first step. Its
                // target evaluator must ask that SAME owner before selecting this candidate, or
                // an unreachable hex wins here and execution reports NoSafeStep while a lower-
                // scoring legal escape was available. Probe only candidates capable of beating
                // the current best; this is an emergency reaction, not a full-map routing sweep.
                if (score > bestScore
                    && SafeStepPathing.FindNextSafeStep(map, army, h) != null)
                {
                    bestScore = score;
                    best = h;
                }
            }
            return best;
        }

        private static ReconReactionDecision? FindWeakScoutOpportunity(PlayerSetupData player, HexMap map,
            ArmyData army)
        {
            if (!BattleInitiator.IsCombatCapable(army))
                return null;

            ReconReactionDecision? best = null;
            float bestWin = AttackOpportunityWinChance;
            foreach (HexCoord h in HexGridMath.Neighbors(army.Hex))
            {
                if (!VisionSystem.IsVisible(player, h))
                    continue;
                ArmyData target = BattleInitiator.FindEnemyAt(h, army);
                if (target == null || target.Owner == null || target.Owner.IsNeutral
                    || !AiArmyRoles.IsSoloRecce(target))
                    continue;

                List<Game.Units.UnitData> visible = StealthSystem.TargetableMembersFor(target, player).ToList();
                if (visible.Count == 0)
                    continue;
                List<WorthIt.DefenderProfile> profiles = visible.Select(WorthIt.FromLiveUnit).ToList();
                // CURRENTLY visible target, so the full live hex bonus is honest here.
                float hexBonus = WorthIt.HexDefenseBonus(h, map);

                // §17 — every safety gate, not just a raw win chance. The scout must be able to
                // damage every defender, a win must not routinely leave it critically wounded, and
                // the hex it would end on must not sit under a second known threat it cannot beat.
                if (!WorthIt.CanDamageAll(army, profiles, hexBonus))
                    continue;
                WorthIt.BattleEstimate est = WorthIt.Estimate(army, profiles, hexBonus,
                    visible.Contains(target.Commander) ? WorthIt.SideCommander.Of(target.Commander) : default);
                if (est.WinChance < bestWin)
                    continue;
                if (est.CriticalAfterBattleChance > AiConfigV2.scoutReactionAttackMaxCriticalAfter)
                    continue;
                if (PostCombatPositionUnsafe(player, map, army, h, target.Id))
                    continue;

                bestWin = est.WinChance;
                best = new ReconReactionDecision(ReconReactionAction.AttackOpportunity, h, target.Id,
                    est.WinChance, "adjacent solo Recce: beatable, damage-complete, post-combat safe");
            }
            return best;
        }

        // A scout passing an enemy structure nobody defends should not walk by it. Honest, live
        // observation only: the hex must be VISIBLE now and pass the one arrival rule
        // (AiMapMemory.KnownUndefendedForeignStructureAt), the one step must be affordable, and the
        // hex it would end on must not sit under a known enemy it cannot survive — the same
        // post-action safety gate the weak-Recce attack uses. A Base/Citadel outranks a Facility.
        private static ReconReactionDecision? FindUndefendedStructureOpportunity(PlayerSetupData player,
            HexMap map, ArmyData army)
        {
            HexCoord? best = null;
            bool bestIsStronghold = false;
            foreach (HexCoord h in HexGridMath.Neighbors(army.Hex)
                .OrderBy(x => x.Q).ThenBy(x => x.R))
            {
                if (!VisionSystem.IsVisible(player, h) || !map.TryGetTerrainAt(h, out var terrain))
                    continue;
                AiMapMemory.KnownBuilding? building = AiMapMemory.KnownBuildingAt(player, h);
                if (!building.HasValue || building.Value.Owner == null
                    || building.Value.Owner == player || building.Value.Owner.IsNeutral)
                    continue;
                if (!AiMapMemory.KnownUndefendedForeignStructureAt(player, h))
                    continue;
                int cost = terrain != null ? Math.Max(1, terrain.moveCost) : 1;
                if (cost > army.CurrentMovement)
                    continue;
                if (PostCombatPositionUnsafe(player, map, army, h, -1))
                    continue;
                bool stronghold = building.Value.IsBase || building.Value.IsStartingCitadel;
                if (best.HasValue && (bestIsStronghold || !stronghold))
                    continue;
                best = h;
                bestIsStronghold = stronghold;
            }
            if (!best.HasValue)
                return null;
            return new ReconReactionDecision(ReconReactionAction.SabotageStructure, best, null, 1f,
                bestIsStronghold ? "adjacent undefended enemy Base: take it over"
                    : "adjacent undefended enemy Facility: destroy it");
        }

        // §17 acceptable post-combat position — would any OTHER known non-neutral enemy within
        // flee radius of `hex` be able to beat this army once it is standing there? Honest memory
        // only; the target being attacked is excluded.
        private static bool PostCombatPositionUnsafe(PlayerSetupData player, HexMap map, ArmyData army,
            HexCoord hex, int excludeArmyId)
        {
            foreach (AiMapMemory.KnownEnemySighting s in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { hex }, AiConfig.scoutFleeRadius))
            {
                // A garrison cannot move out to punish the post-combat position (audit F1).
                if (s.ArmyId == excludeArmyId || s.Owner == null || s.Owner.IsNeutral || s.IsGarrison)
                    continue;
                float hexBonus = HonestHexDefenseBonus(player, map, s.Hex);
                float win = WorthIt.WinChance(army, s.Defenders, hexBonus, s.Commander);
                if (win < StrongEnemyFleeWinChance)
                    return true;
            }
            return false;
        }

        private static HexCoord? PickLowerDetectorRiskStep(PlayerSetupData player, HexMap map,
            ArmyData army)
        {
            float current = CurrentDetectorRisk(player, army.Hex);
            HexCoord? bestHex = null;
            float bestRisk = current;
            int bestCost = int.MaxValue;

            foreach (HexCoord h in HexGridMath.Neighbors(army.Hex))
            {
                if (!map.TryGetTerrainAt(h, out var terrain))
                    continue;
                int cost = terrain != null ? Math.Max(1, terrain.moveCost) : 1;
                if (cost > army.CurrentMovement
                    || ScoutExecutionSafety.StepBlocked(player, army, h))
                    continue;

                float risk = CurrentDetectorRisk(player, h);
                if (risk > bestRisk + 0.0001f)
                    continue;
                // EvadeDetector does not own capture: the same default noncapturing safe-route
                // rule must witness the direct adjacent step before it becomes a reaction.
                // Otherwise VantageBlockedNow can admit an undefended visible foreign building
                // for a non-required-stealth mission, only for execution to reject the move.
                if (SafeStepPathing.FindNextSafeStep(map, army, h) == null)
                    continue;
                if (risk < bestRisk - 0.0001f || cost < bestCost
                    || (Math.Abs(risk - bestRisk) < 0.0001f && cost == bestCost
                        && (!bestHex.HasValue || h.Q < bestHex.Value.Q
                            || (h.Q == bestHex.Value.Q && h.R < bestHex.Value.R))))
                {
                    bestRisk = risk;
                    bestCost = cost;
                    bestHex = h;
                }
            }

            return bestHex.HasValue && bestRisk < current ? bestHex : null;
        }

        private static float CurrentDetectorRisk(PlayerSetupData player, HexCoord h)
        {
            int detectors = 0;
            foreach (AiMapMemory.KnownEnemySighting sighting in
                     AiMapMemory.KnownEnemySightingsNear(player, new[] { h }, AiConfigV2.frontierEnemyExposureRadius))
            {
                if (sighting.Owner == null || sighting.Owner.IsNeutral)
                    continue;
                if (sighting.CanDetectStealthAt(h))
                    detectors++;
            }
            return Mathf.Clamp01(detectors / Math.Max(1f, AiConfigV2.scoutDetectionRiskNorm));
        }

        // Fog-honest variant of WorthIt.HexDefenseBonus. AiMapMemory stores the observed defense
        // in KnownBuilding and owns this question for the whole AI, so a remembered Base's
        // last-observed Defense is used for a fogged hex instead of dropping to terrain.
        private static float HonestHexDefenseBonus(PlayerSetupData player, HexMap map, HexCoord hex) =>
            AiMapMemory.KnownHexDefenseBonus(player, map, hex);

        private static ReconReactionDecision Log(ArmyData army, ReconPatrolState assignment,
            ReconReactionDecision decision)
        {
            AiDebugLog.Write($"[AI][V2][Recon][Reaction] actor=#{army.Id} mode={assignment.Mode} {decision}");
            return decision;
        }
    }
}
