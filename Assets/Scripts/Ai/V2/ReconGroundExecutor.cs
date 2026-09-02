using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Combat;
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
            IReadOnlyList<ProvisionedMission> queue, int missionIndex, WorldSnapshot snapshot = null)
        {
            ArmyData army = Resolve(player, pm.MoverArmyId);
            if (army == null || ctx?.Map == null)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
                yield break;
            }

            // Spec §25 — strategic scores for the score-based Explore<->Refresh mode hysteresis in
            // ReconAssignmentRegistry. Same raw signals the strategic layer's sub-pressures use.
            float exploreScore = snapshot?.MapKnowledge?.ExplorableUnknownFrac ?? 0f;
            float refreshScore = ReconIntelSnapshotRegistry.StalePressure(snapshot);

            ReconAcceptanceAudit.BeginTurn(player, ctx.TurnNumber);
            if (missionIndex == 0)
                ReconAcceptanceAudit.RecordThreeScoutBatch(player, ctx.TurnNumber, queue);

            result.StartHex = army.Hex;
            result.FinalHex = army.Hex;

            if (!ReconScoutKinds.IsExplore(pm.ScoutKind)
                && !ReconScoutKinds.IsRefresh(pm.ScoutKind)
                && !ReconScoutKinds.IsSurveil(pm.ScoutKind))
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.ApSpent = 0f;
                AiDebugLog.Write($"[AI][V2][Recon][Ground] [{pm.Mission?.AttemptId}] actor=#{army.Id} "
                    + $"unknown Scout kind {(int)pm.ScoutKind}; fail closed before movement");
                SummarizeIfLast(player, ctx, queue, missionIndex);
                yield break;
            }

            ReconMode requestedMode = ReconScoutKinds.IsExplore(pm.ScoutKind)
                ? ReconMode.Explore
                : ReconMode.Refresh;
            // Surveil's FocusHex is the enemy/contact being observed. Provisioning already chose a
            // safe observation vantage in ExecutionHex; use that as the strategic heading so the
            // continuous ground planner does not undo the vantage decision by walking at the enemy.
            HexCoord strategicAnchor = ReconScoutKinds.IsSurveil(pm.ScoutKind)
                ? pm.ExecutionHex
                : pm.FocusHex;
            ReconAssignment assignment = ReconAssignmentRegistry.GetOrCreate(player, army.Id,
                army.Hex, strategicAnchor, requestedMode, ctx.TurnNumber, exploreScore, refreshScore);

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
                    SummarizeIfLast(player, ctx, queue, missionIndex);
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
                    strategicAnchor, requestedMode, ctx.TurnNumber, exploreScore, refreshScore);

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
                    bool captureStartedHidden = army.Members.Count > 0 && army.Members.All(m => m.IsHidden);
                    ExitArmyStealth(army);
                    VisionSystem.RecomputeFor(player);
                    AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
                    ReconReactionDecision afterDecloak = ReconReactionPolicy.Evaluate(
                        player, ctx.Map, army, assignment, ctx.TurnNumber);
                    if (afterDecloak.Action == ReconReactionAction.Flee
                        || afterDecloak.Action == ReconReactionAction.EvadeDetector
                        || afterDecloak.Action == ReconReactionAction.StopAndReplan)
                    {
                        AiDebugLog.Write($"[AI][V2][Recon][Capture] actor=#{army.Id} cancelled after decloak: {afterDecloak}");
                        ReconAcceptanceAudit.RecordHiddenFacilityCancel(player, ctx.TurnNumber, army.Id,
                            army.Hex, captureStartedHidden, afterDecloak.Action);
                        reaction = afterDecloak;
                    }
                    else
                    {
                        BuildingData before = BuildingRegistry.FindAt(army.Hex);
                        PlayerSetupData previousOwner = before?.Owner;
                        BuildingRegistry.CaptureOrDestroyIfUndefended(army.Hex, player, ctx.HexSelection, army);
                        BuildingData after = BuildingRegistry.FindAt(army.Hex);
                        bool changed = before != after || (after != null && after.Owner != previousOwner);
                        ReconAcceptanceAudit.RecordHiddenFacilityCapture(player, ctx.TurnNumber, army.Id,
                            army.Hex, captureStartedHidden, changed);
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
                    stop = ExecutionStopReason.NoSafeStep;
                    break;
                }

                if (forceDecloakForAttack)
                {
                    ExitArmyStealth(army);
                    VisionSystem.RecomputeFor(player);
                    AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
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
                ReconAcceptanceAudit.RecordDecision(player, ctx.TurnNumber, army.Id,
                    beforeHex, next.Value, actionWhy);
                var move = AiDecision.Move(army, next.Value,
                    $"V2 recon continuous — {actionWhy}; mission={ReconScoutKinds.Name(pm.ScoutKind)}; "
                    + $"mode={assignment.Mode}; anchor=({assignment.StrategicAnchor.Q},{assignment.StrategicAnchor.R})",
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
                    // Keep the tactical IntelAge sidecar explicitly current at the authoritative
                    // transition boundary. This is idempotent with VisionSystem callbacks and does
                    // NOT mutate the frozen strategic snapshot or ground Visited state.
                    AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
                    ReconAcceptanceAudit.RecordStep(player, ctx.TurnNumber, pm.MoverArmyId,
                        beforeHex, endHex);
                }
                result.FinalHex = endHex;

                if (forceDecloakForAttack && reaction.TargetArmyId.HasValue)
                    ReconAcceptanceAudit.RecordWeakRecceAttack(player, ctx.TurnNumber, pm.MoverArmyId,
                        reaction.TargetArmyId.Value, trace.BattleOccurred, reaction.WinChance);

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
                + $"kind={ReconScoutKinds.Name(pm.ScoutKind)} "
                + $"({result.StartHex.Q},{result.StartHex.R})→({result.FinalHex.Q},{result.FinalHex.R}) "
                + $"steps={result.StepsMoved} ap−{result.ApSpent.ToString("0.#", CultureInfo.InvariantCulture)} "
                + $"objective={(result.ReachedGoal ? "met" : "open")} stop={stop}");
            SummarizeIfLast(player, ctx, queue, missionIndex);
        }

        private static void SummarizeIfLast(PlayerSetupData player, AiTurnContext ctx,
            IReadOnlyList<ProvisionedMission> queue, int missionIndex)
        {
            if (ctx != null && queue != null && missionIndex >= queue.Count - 1)
                ReconAcceptanceAudit.Summarize(player, ctx.TurnNumber);
        }

        private static void RefreshObjectiveSatisfied(PlayerSetupData player, ProvisionedMission pm,
            ExecutionResult result)
        {
            if (result.ReachedGoal)
                return;

            bool met;
            if (ReconScoutKinds.IsSurveil(pm.ScoutKind))
            {
                met = ScoutObjectiveEvaluator.IsSurveilSatisfiedLive(player, pm.FocusHex,
                    pm.TrackedArmyId, pm.BaselineObservedTurn);
            }
            else if (ReconScoutKinds.IsRefresh(pm.ScoutKind))
            {
                met = ScoutObjectiveEvaluator.IsRefreshSatisfiedLive(player, pm.FocusHex);
            }
            else if (ReconScoutKinds.IsExplore(pm.ScoutKind))
            {
                met = ScoutObjectiveEvaluator.IsExploreSatisfiedLive(player, pm.FocusHex);
            }
            else
            {
                met = false;
            }

            if (met)
            {
                result.ReachedGoal = true;
                // Spec §1 — for a ground Explore/Refresh actor this is a satisfied WAYPOINT, not a
                // finished role: the durable ReconAssignment persists and the MissionIntent should
                // be re-focused next turn, not retired. Surveil completion is a genuine done.
                result.DurableRoleContinues = !ReconScoutKinds.IsSurveil(pm.ScoutKind)
                    && AiArmyRoles.IsSoloRecce(Resolve(player, pm.MoverArmyId));
                AiDebugLog.Write($"[AI][V2][Recon][Objective] [{pm.Mission?.AttemptId}] {pm.Key} "
                    + $"kind={ReconScoutKinds.Name(pm.ScoutKind)} met; "
                    + $"durableRoleContinues={(result.DurableRoleContinues ? 1 : 0)}");
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
            int stealthAp = AiConfigV2.scoutOptionalStealthAp;
            if (root == null || !root.CanSpendActionPoints(army.ActivationApCost + stealthAp))
                return false;
            root.SpendActionPoints(stealthAp);
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

            RouteTopologyBenefits(player, army, nextHex, strategicAnchor,
                out float routeAccess, out float routeShorten);

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
                RouteAccessBenefit = routeAccess,
                RouteShorteningBenefit = routeShorten,
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

        // Spec §12 — stealth as route topology. RouteAccessBenefit is 1 when a known non-own army
        // sits on the immediate next step (or the anchor) so a visible mover would be forced to
        // engage while a hidden one can pass; RouteShorteningBenefit rises with the density of
        // known occupied hexes around the next step (a hidden corridor through a cluster). Honest
        // memory only — never a TrueWorld read.
        private static void RouteTopologyBenefits(PlayerSetupData player, ArmyData army, HexCoord nextHex,
            HexCoord anchor, out float access, out float shorten)
        {
            access = 0f;
            shorten = 0f;
            bool hidden = army.Members.Count > 0 && army.Members.All(m => m.IsHidden);
            if (hidden)
                return; // already hidden — this policy is only asked before entering stealth

            bool OccupiedByOther(HexCoord h)
            {
                foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownEnemySightings(player))
                    if (s.Hex.Equals(h)) return true;
                foreach (AiMapMemory.KnownEnemySighting s in AiMapMemory.AllKnownNeutralSightings(player))
                    if (s.Hex.Equals(h)) return true;
                return false;
            }

            if (OccupiedByOther(nextHex) || OccupiedByOther(anchor))
                access = 1f;

            int occupiedNearby = 0;
            foreach (HexCoord h in HexGridMath.HexesInRange(nextHex, 2))
                if (!h.Equals(nextHex) && OccupiedByOther(h))
                    occupiedNearby++;
            shorten = Mathf.Clamp01(occupiedNearby / 4f);
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
