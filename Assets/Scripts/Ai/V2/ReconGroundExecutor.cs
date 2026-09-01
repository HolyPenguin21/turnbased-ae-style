using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  CONTINUOUS GROUND RECON EXECUTION
    // ===========================================================================================
    //  One provisioned Scout mission still owns AP/accounting for this turn, but a single focus
    //  hex no longer owns the actor's tactical movement. The provisioned objective seeds/refreshes
    //  a durable ReconAssignment; every actual move is ONE adjacent step selected from live state.
    //
    //  After each authoritative MoveArmyRoutine returns, vision/contact/stealth/event state has
    //  settled. We record discovery, mark assignment progress, then start the next iteration by
    //  running ReconReactionPolicy and ReconGroundStepPlanner again. No cached multi-hex route is
    //  followed after new information appears.
    // ===========================================================================================
    internal static class ReconGroundExecutor
    {
        public static IEnumerator Run(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, int apBefore,
            IReadOnlyList<ProvisionedMission> queue, int missionIndex)
        {
            ArmyData army = Resolve(player, pm.MoverArmyId);
            if (army == null || ctx?.Map == null)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                yield break;
            }

            result.StartHex = army.Hex;
            result.FinalHex = army.Hex;

            ReconMode requestedMode = pm.ScoutKind == ScoutTargetKind.Surveil
                ? ReconMode.Refresh
                : ReconMode.Explore;
            HexCoord strategicAnchor = pm.FocusHex;
            ReconAssignment assignment = ReconAssignmentRegistry.GetOrCreate(player, army.Id,
                army.Hex, strategicAnchor, requestedMode, ctx.TurnNumber);

            // A Required-stealth mission must enter before the first activation/move. Do not let
            // the new continuous loop weaken the existing AP/activation contract.
            if (pm.StealthApReserved)
            {
                if (!TryEnterRequiredStealth(root, army, out bool entered))
                {
                    result.StopReason = ExecutionStopReason.RequiredStealthUnavailable;
                    result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                    AiDebugLog.Write($"[AI][V2][Recon][Ground] [{pm.Mission?.AttemptId}] actor=#{army.Id} "
                        + "required stealth unavailable; stop before movement");
                    yield break;
                }
                result.EnteredStealth |= entered;
            }

            bool optionalStealthChecked = false;
            int iterations = 0;
            int maxIterations = Math.Max(2, army.CurrentMovement + 4); // + bounded reaction actions that do not move
            ExecutionStopReason stop = ExecutionStopReason.OutOfMovement;

            HashSet<int> knownEnemyIds = KnownIds(AiMapMemory.AllKnownEnemySightings(player));
            HashSet<int> knownNeutralIds = KnownIds(AiMapMemory.AllKnownNeutralSightings(player));

            // The mission-level objective may complete while the actor keeps performing its durable
            // Explore/Refresh assignment. This flag is ledger truth, not a reason to stop movement.
            RefreshObjectiveSatisfied(player, pm, result);

            while (true)
            {
                if (++iterations > maxIterations)
                {
                    stop = ExecutionStopReason.MoveRejected;
                    break;
                }

                army = Resolve(player, pm.MoverArmyId);
                if (army == null || army.Owner != player)
                {
                    stop = ExecutionStopReason.MoverLost;
                    ReconAssignmentRegistry.Retire(player, pm.MoverArmyId, "mover lost");
                    break;
                }
                if (!AiArmyRoles.IsSoloRecce(army))
                {
                    stop = ExecutionStopReason.MoverLost;
                    ReconAssignmentRegistry.Retire(player, pm.MoverArmyId, "actor no longer solo Recce");
                    break;
                }
                if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
                {
                    stop = ExecutionStopReason.BattleStarted;
                    break;
                }

                RefreshObjectiveSatisfied(player, pm, result);

                // Re-anchor the durable actor assignment from the newest strategic objective, but
                // do not recreate it and do not turn the anchor into a fixed route destination.
                assignment = ReconAssignmentRegistry.GetOrCreate(player, army.Id, army.Hex,
                    strategicAnchor, requestedMode, ctx.TurnNumber);

                ReconReactionDecision reaction = ReconReactionPolicy.Evaluate(
                    player, ctx.Map, army, assignment, ctx.TurnNumber);

                if (reaction.Action == ReconReactionAction.StopAndReplan)
                {
                    stop = ExecutionStopReason.TargetInvalidated;
                    break;
                }

                if (reaction.Action == ReconReactionAction.CaptureOpportunity)
                {
                    // Hidden entry has already happened. The reaction policy performed the first
                    // live safety/defender check; now decloak, refresh, and call the authoritative
                    // capture method which performs its own final defender check again.
                    ExitArmyStealth(army);
                    VisionSystem.RecomputeFor(player);
                    ReconReactionDecision afterDecloak = ReconReactionPolicy.Evaluate(
                        player, ctx.Map, army, assignment, ctx.TurnNumber);
                    if (afterDecloak.Action == ReconReactionAction.Flee
                        || afterDecloak.Action == ReconReactionAction.EvadeDetector
                        || afterDecloak.Action == ReconReactionAction.StopAndReplan)
                    {
                        AiDebugLog.Write($"[AI][V2][Recon][Capture] actor=#{army.Id} cancelled after decloak: {afterDecloak}");
                        reaction = afterDecloak;
                    }
                    else
                    {
                        BuildingData before = BuildingRegistry.FindAt(army.Hex);
                        PlayerSetupData previousOwner = before?.Owner;
                        BuildingRegistry.CaptureOrDestroyIfUndefended(army.Hex, player, ctx.HexSelection, army);
                        BuildingData after = BuildingRegistry.FindAt(army.Hex);
                        bool changed = before != after || (after != null && after.Owner != previousOwner);
                        if (changed)
                        {
                            ReconAssignmentRegistry.MarkProgress(player, army.Id, ctx.TurnNumber);
                            AiDebugLog.Write($"[AI][V2][Recon][Capture] actor=#{army.Id} resolved structure at "
                                + $"({army.Hex.Q},{army.Hex.R}); movement={army.CurrentMovement}");
                            RefreshObjectiveSatisfied(player, pm, result);
                            if (army.CurrentMovement <= 0)
                            {
                                stop = ExecutionStopReason.OutOfMovement;
                                break;
                            }
                            continue; // fresh live reaction + step selection after the world mutation
                        }

                        // Another authoritative condition blocked capture. Do not spin on the same
                        // opportunity; stop this provisioned attempt and let the next strategic pass
                        // rebuild from the now-refreshed world.
                        stop = ExecutionStopReason.TargetInvalidated;
                        break;
                    }
                }

                if (army.CurrentMovement <= 0)
                {
                    stop = ExecutionStopReason.OutOfMovement;
                    break;
                }

                HexCoord? next = null;
                string actionWhy = null;
                bool forceDecloakForAttack = false;

                switch (reaction.Action)
                {
                    case ReconReactionAction.Flee:
                        if (reaction.TargetHex.HasValue)
                            next = VisitHexTask.FindNextSafeStep(ctx.Map, army, reaction.TargetHex.Value);
                        actionWhy = "Flee";
                        break;

                    case ReconReactionAction.EvadeDetector:
                        if (reaction.TargetHex.HasValue)
                            next = VisitHexTask.FindNextSafeStep(ctx.Map, army, reaction.TargetHex.Value);
                        actionWhy = "EvadeDetector";
                        break;

                    case ReconReactionAction.AttackOpportunity:
                        if (reaction.TargetHex.HasValue)
                            next = reaction.TargetHex.Value;
                        actionWhy = "AttackOpportunity";
                        forceDecloakForAttack = true;
                        break;

                    case ReconReactionAction.Continue:
                    default:
                        ReconGroundStepPlanner.StepChoice? choice = ReconGroundStepPlanner.Pick(
                            player, ctx.Map, army, assignment, ctx.TurnNumber);
                        if (choice.HasValue)
                            next = choice.Value.Hex;
                        actionWhy = assignment.Mode.ToString();
                        break;
                }

                if (!next.HasValue)
                {
                    stop = reaction.Action == ReconReactionAction.Flee
                        ? ExecutionStopReason.NoSafeStep
                        : ExecutionStopReason.NoSafeStep;
                    break;
                }

                if (forceDecloakForAttack)
                {
                    ExitArmyStealth(army);
                    VisionSystem.RecomputeFor(player);
                    // Opportunity is one-step and live. Decloaking can expose new danger or remove
                    // the target; verify the same target is still contactable before committing.
                    ArmyData targetNow = BattleInitiator.FindEnemyAt(next.Value, player);
                    if (targetNow == null || !reaction.TargetArmyId.HasValue
                        || targetNow.Id != reaction.TargetArmyId.Value)
                    {
                        stop = ExecutionStopReason.TargetInvalidated;
                        break;
                    }
                }

                if (!optionalStealthChecked && !pm.StealthApReserved && !result.EnteredStealth
                    && !forceDecloakForAttack)
                {
                    optionalStealthChecked = true;
                    float mandatoryClaims = MandatoryApClaimsFrom(queue, missionIndex);
                    result.EnteredStealth |= MaybeEnterOptionalStealth(player, root, ctx, army, pm,
                        next.Value, assignment.StrategicAnchor, mandatoryClaims);
                }

                HexCoord beforeHex = army.Hex;
                var move = AiDecision.Move(army, next.Value,
                    $"V2 recon continuous — {actionWhy}; mode={assignment.Mode}; "
                    + $"anchor=({assignment.StrategicAnchor.Q},{assignment.StrategicAnchor.R})",
                    null, 0f, AiTaskCategory.Reconnaissance);
                var trace = new AiMoveExecutionTrace();
                yield return AiTurnController.MoveArmyRoutine(player, move, ctx, trace);
                result.EnteredStealth |= trace.EnteredStealthThisStep;

                army = Resolve(player, pm.MoverArmyId);
                HexCoord endHex = army != null ? army.Hex : trace.EndHex;
                bool moved = !endHex.Equals(beforeHex);
                if (moved)
                {
                    result.StepsMoved++;
                    ReconAssignmentRegistry.MarkProgress(player, pm.MoverArmyId, ctx.TurnNumber);
                }
                result.FinalHex = endHex;

                if (army == null)
                {
                    stop = ExecutionStopReason.MoverLost;
                    ReconAssignmentRegistry.Retire(player, pm.MoverArmyId, "mover lost during step");
                    break;
                }
                if (trace.BattleOccurred)
                {
                    stop = ExecutionStopReason.BattleStarted;
                    break;
                }
                if (trace.HexEventOccurred)
                {
                    stop = ExecutionStopReason.HexEventStarted;
                    break;
                }
                if (!moved)
                {
                    stop = ExecutionStopReason.MoveRejected;
                    break;
                }

                // VisionSystem/AiMapMemory have already settled inside the authoritative move path.
                // New contacts become strategic interrupts, but no longer force the actor to keep
                // walking toward a stale focus or stop merely because that focus was completed.
                HashSet<int> enemyNow = KnownIds(AiMapMemory.AllKnownEnemySightings(player));
                HashSet<int> neutralNow = KnownIds(AiMapMemory.AllKnownNeutralSightings(player));
                int[] newEnemyIds = enemyNow.Where(id => !knownEnemyIds.Contains(id)).ToArray();
                int[] newNeutralIds = neutralNow.Where(id => !knownNeutralIds.Contains(id)).ToArray();
                knownEnemyIds = enemyNow;
                knownNeutralIds = neutralNow;

                if (newEnemyIds.Length > 0)
                {
                    StrategicInterruptRegistry.MarkDiscovery(player, ctx.TurnNumber, newEnemyIds);
                    AiDebugLog.Write($"[AI][V2][Recon][Discovery] actor=#{army.Id} enemy=[{string.Join(",", newEnemyIds)}] "
                        + "— next action will be live reaction/replan");
                }
                if (newNeutralIds.Length > 0)
                {
                    StrategicInterruptRegistry.MarkDiscovery(player, ctx.TurnNumber, newNeutralIds);
                    AiDebugLog.Write($"[AI][V2][Recon][Discovery] actor=#{army.Id} neutral=[{string.Join(",", newNeutralIds)}] "
                        + "— next action will be live reaction/replan");
                }

                RefreshObjectiveSatisfied(player, pm, result);
            }

            result.FinalHex = Resolve(player, pm.MoverArmyId)?.Hex ?? result.FinalHex;
            result.StopReason = stop;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            AiDebugLog.Write($"[AI][V2][Recon][Ground] [{pm.Mission?.AttemptId}] {pm.Key} actor=#{pm.MoverArmyId} "
                + $"({result.StartHex.Q},{result.StartHex.R})→({result.FinalHex.Q},{result.FinalHex.R}) "
                + $"steps={result.StepsMoved} ap−{result.ApSpent.ToString("0.#", CultureInfo.InvariantCulture)} "
                + $"objective={(result.ReachedGoal ? "met" : "open")} stop={stop}");
        }

        private static void RefreshObjectiveSatisfied(PlayerSetupData player, ProvisionedMission pm,
            ExecutionResult result)
        {
            if (result.ReachedGoal)
                return;
            bool met = pm.ScoutKind == ScoutTargetKind.Surveil
                ? ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex,
                    pm.TrackedArmyId, pm.BaselineObservedTurn)
                : ScoutObjectiveEvaluator.IsExploreSatisfiedLive(player, pm.FocusHex);
            if (met)
            {
                result.ReachedGoal = true;
                AiDebugLog.Write($"[AI][V2][Recon][Objective] [{pm.Mission?.AttemptId}] {pm.Key} met; "
                    + "durable actor assignment continues while movement remains");
            }
        }

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            ArmyRegistry.AllForOwner(player).FirstOrDefault(a => a.Id == armyId);

        private static HashSet<int> KnownIds(IEnumerable<AiMapMemory.KnownEnemySighting> sightings)
        {
            var set = new HashSet<int>();
            foreach (AiMapMemory.KnownEnemySighting s in sightings)
                set.Add(s.ArmyId);
            return set;
        }

        private static void ExitArmyStealth(ArmyData army)
        {
            if (army == null)
                return;
            foreach (var member in army.Members.ToList())
                if (member.IsHidden)
                    StealthSystem.ExitStealth(member);
        }

        private static bool TryEnterRequiredStealth(PlayerRoot root, ArmyData army, out bool entered)
        {
            entered = false;
            if (army == null || army.Members.Count == 0)
                return false;
            if (army.Members.Any(m => m.IsHidden))
                return true;
            if (army.HasActivatedThisTurn)
                return false;
            var scout = army.Members[0];
            if (!StealthSystem.CanEnterStealth(scout))
                return false;
            if (root == null || !root.CanSpendActionPoints(army.ActivationApCost + 1))
                return false;
            root.SpendActionPoints(AiConfigV2.scoutOptionalStealthAp);
            StealthSystem.EnterStealth(scout);
            entered = true;
            return true;
        }

        private static float MandatoryApClaimsFrom(IReadOnlyList<ProvisionedMission> provisioned, int startIndex)
        {
            if (provisioned == null)
                return 0f;
            float total = 0f;
            for (int i = Mathf.Max(0, startIndex); i < provisioned.Count; i++)
                if (provisioned[i] != null)
                    total += Mathf.Max(0f, provisioned[i].ClaimedAp);
            return total;
        }

        private static bool MaybeEnterOptionalStealth(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ArmyData army, ProvisionedMission pm, HexCoord nextHex, HexCoord strategicAnchor, float mandatoryApClaims)
        {
            if (army == null || army.Members.Count == 0 || root == null)
                return false;
            if (army.Members.Any(m => m.IsHidden) || army.HasActivatedThisTurn)
                return false;
            var scout = army.Members[0];
            if (!StealthSystem.CanEnterStealth(scout))
                return false;

            int stealthAp = AiConfigV2.scoutOptionalStealthAp;
            if (stealthAp <= 0 || !root.CanSpendActionPoints(stealthAp))
                return false;

            AiHandData hand = AiHandRegistry.Peek(player);
            bool drawAvailable = hand != null && hand.HasFreeSlot && hand.HasCardsLeftToDraw;
            float knownRouteRisk = Mathf.Max(LegDetectionRisk(player, nextHex),
                LegDetectionRisk(player, strategicAnchor));

            var eval = ScoutOptionalStealthPolicy.Evaluate(new OptionalStealthInputs
            {
                LegDetectionRisk = knownRouteRisk,
                MoverAlreadyHidden = false,
                MoverIsStrategicBody = army.Members.Any(m => m.IsHero),
                ApRemaining = root.ActionPoints,
                StealthApCost = stealthAp,
                MandatoryApClaims = mandatoryApClaims,
                DrawAvailable = drawAvailable,
                DrawApCost = ctx != null ? ctx.DrawApCost : 0,
                DrawOpportunities = drawAvailable ? AiConfigV2.maxTerminalDrawsPerTurn : 0,
            });

            float slack = Mathf.Max(0f, root.ActionPoints - Mathf.Max(0f, mandatoryApClaims));
            AiDebugLog.Write($"[AI][V2][Recon][Stealth] [{pm.Mission?.AttemptId}] actor=#{army.Id} {eval.ToCompact()} "
                + $"ap={root.ActionPoints} mandatory={mandatoryApClaims.ToString("0.##", CultureInfo.InvariantCulture)} "
                + $"slack={slack.ToString("0.##", CultureInfo.InvariantCulture)} draw={(drawAvailable ? 1 : 0)}");

            if (eval.Decision != OptionalStealthDecision.Enter
                || slack + AiConfigV2.allocatorSliceEpsilon < stealthAp)
                return false;

            root.SpendActionPoints(stealthAp);
            StealthSystem.EnterStealth(scout);
            return true;
        }

        private static float LegDetectionRisk(PlayerSetupData player, HexCoord hex)
        {
            int r = AiConfigV2.frontierEnemyExposureRadius;
            int detectors = 0;
            foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                if (HexGridMath.Distance(s.Hex, hex) <= r && s.CanDetectStealthAt(hex))
                    detectors++;
            return Mathf.Clamp01(detectors / Mathf.Max(0.0001f, AiConfigV2.scoutDetectionRiskNorm));
        }
    }
}
