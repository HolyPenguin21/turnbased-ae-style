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
    //  Called only after authoritative movement/vision/stealth state has settled. It never reads
    //  TrueWorld and never emits a multi-step plan. One decision is valid for one immediate action;
    //  after that action the executor must call this policy again on the new live state.
    //
    //  Priority is safety-first:
    //    1) a scout personally exposed to a stronger known enemy -> Flee;
    //    2) a still-hidden scout inside a known detector envelope -> EvadeDetector;
    //    3) a hidden scout standing on a now-confirmed undefended hostile structure -> Capture;
    //    4) an adjacent visible weak solo Recce that is overwhelmingly beatable -> opportunistic attack;
    //    5) otherwise Continue.
    //
    //  This keeps "discovery" separate from "reaction". AiMapMemory/VisionSystem tell us what is
    //  now known; this class decides what Recon does with that knowledge.
    // ===========================================================================================
    public enum ReconReactionAction
    {
        Continue,
        Flee,
        EvadeDetector,
        AttackOpportunity,
        CaptureOpportunity,
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
        // Opportunistic combat is intentionally much stricter than normal Raid admission. Recon
        // is not a combat lane: only an overwhelmingly favourable fight against another solo Recce
        // is worth breaking scouting tempo for.
        private const float AttackOpportunityWinChance = 0.80f;
        private const float StrongEnemyFleeWinChance = 0.50f;

        public static ReconReactionDecision Evaluate(PlayerSetupData player, HexMap map, ArmyData army,
            ReconAssignment assignment, int turn)
        {
            if (player == null || map == null || army == null || assignment == null)
                return new ReconReactionDecision(ReconReactionAction.StopAndReplan, null, null, 0f,
                    "missing live recon state");
            if (!AiArmyRoles.IsSoloRecce(army))
                return new ReconReactionDecision(ReconReactionAction.StopAndReplan, null, null, 0f,
                    "actor is no longer solo Recce");

            bool inStealth = IsArmyInStealth(army);

            // A remembered non-neutral army only becomes a Flee trigger when THIS scout is exposed
            // to that owner (ordinary visible scout, or a hidden scout personally detected by it)
            // and the actual WorthIt comparison says Recon is not favoured. A still-hidden scout
            // does not magically know that an enemy has detected it: ArmyFullyHiddenFrom is exactly
            // the authoritative per-observer visibility predicate.
            ReconReactionDecision? flee = FindStrongExposedThreat(player, map, army, inStealth);
            if (flee.HasValue)
                return Log(army, assignment, flee.Value);

            // If we are still genuinely hidden but a known Recce source can challenge stealth from
            // here, prefer one immediate lower-risk adjacent step. This is an evasion reaction,
            // not a new strategic assignment and not a cached path.
            if (inStealth && CurrentDetectorRisk(player, army.Hex) > 0f)
            {
                HexCoord? evade = PickLowerDetectorRiskStep(player, map, army, turn);
                if (evade.HasValue)
                    return Log(army, assignment, new ReconReactionDecision(
                        ReconReactionAction.EvadeDetector, evade, null, 0f,
                        "known detector envelope; lower-risk adjacent step exists"));
            }

            // Hidden-entry facility/base sequence. The scout has already arrived without capturing
            // because BuildingRegistry correctly rejects invisible attackers. Only CURRENTLY visible
            // structure state is read here. Re-check exposed danger above happened first, and the
            // defender scan below is live/observer-aware. Executor may now decloak and call the one
            // authoritative CaptureOrDestroyIfUndefended method; that method checks defenders again.
            if (inStealth && IsSafeCaptureOpportunity(player, army))
            {
                return Log(army, assignment, new ReconReactionDecision(
                    ReconReactionAction.CaptureOpportunity, army.Hex, null, 0f,
                    "hidden entry confirmed an undefended hostile structure"));
            }

            // Recon may opportunistically remove another scout, but never invent a combat target
            // from memory. Target must be CURRENTLY visible/contactable, adjacent, solo Recce and
            // overwhelmingly favourable through the shared WorthIt estimator.
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
                if (sighting.Owner == null || sighting.Owner.IsNeutral)
                    continue;
                if (inStealth && StealthSystem.ArmyFullyHiddenFrom(army, sighting.Owner))
                    continue;

                float hexBonus = WorthIt.HexDefenseBonus(sighting.Hex, map);
                float win = sighting.Defenders != null && sighting.Defenders.Count > 0
                    ? WorthIt.WinChance(army, sighting.Defenders, hexBonus)
                    : WorthIt.WinChance(army, sighting.DefenseSum + hexBonus, sighting.AttackSum);
                if (win < worstWin)
                {
                    worstWin = win;
                    worst = sighting;
                }
            }

            if (!worst.HasValue || worstWin >= StrongEnemyFleeWinChance)
                return null;

            HexCoord home = AiTurnController.NearestOwnGarrisonHex(player, army.Hex);
            return new ReconReactionDecision(ReconReactionAction.Flee, home, worst.Value.ArmyId,
                worstWin, $"exposed to stronger known enemy near ({worst.Value.Hex.Q},{worst.Value.Hex.R})");
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
                ArmyData target = BattleInitiator.FindEnemyAt(h, player);
                if (target == null || target.Owner == null || target.Owner.IsNeutral
                    || !AiArmyRoles.IsSoloRecce(target))
                    continue;

                List<Game.Units.UnitData> visible = StealthSystem.TargetableMembersFor(target, player).ToList();
                float enemyAttack = WorthIt.AttackSum(visible);
                float enemyDefense = WorthIt.DefenseSum(visible) + WorthIt.HexDefenseBonus(h, map);
                float win = WorthIt.WinChance(army, enemyDefense, enemyAttack);
                if (win < bestWin)
                    continue;

                // If the scout is in stealth, it cannot initiate contact until it voluntarily
                // decloaks. The executor owns that one authoritative state transition immediately
                // before the attack move; this policy only says the opportunity is good enough.
                bestWin = win;
                best = new ReconReactionDecision(ReconReactionAction.AttackOpportunity,
                    h, target.Id, win, "adjacent visible solo Recce is overwhelmingly beatable");
            }
            return best;
        }

        private static bool IsSafeCaptureOpportunity(PlayerSetupData player, ArmyData army)
        {
            if (!VisionSystem.IsVisible(player, army.Hex))
                return false;
            BuildingData building = BuildingRegistry.FindAt(army.Hex);
            if (building == null || building.Owner == null || building.Owner == player)
                return false;

            foreach (ArmyData resident in ArmyRegistry.AllAt(army.Hex))
            {
                if (resident == army || resident.Owner != building.Owner)
                    continue;
                if (BattleInitiator.IsEngageable(resident, player))
                    return false;
            }
            return true;
        }

        private static HexCoord? PickLowerDetectorRiskStep(PlayerSetupData player, HexMap map,
            ArmyData army, int turn)
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
                    || AiMapMemory.IsScoutDangerous(player, h)
                    || ScoutExecutionSafety.VantageBlockedNow(player, h, turn))
                    continue;
                if (AiMapMemory.KnownEnemySightingAt(player, h).HasValue)
                    continue;
                if (VisionSystem.IsVisible(player, h) && BattleInitiator.FindEnemyAt(h, player) != null)
                    continue;

                float risk = CurrentDetectorRisk(player, h);
                if (risk > bestRisk + 0.0001f)
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

        private static bool IsArmyInStealth(ArmyData army) =>
            army != null && army.Members.Count > 0 && army.Members.All(m => m.IsHidden);

        private static ReconReactionDecision Log(ArmyData army, ReconAssignment assignment,
            ReconReactionDecision decision)
        {
            AiDebugLog.Write($"[AI][V2][Recon][Reaction] actor=#{army.Id} mode={assignment.Mode} {decision}");
            return decision;
        }
    }
}
