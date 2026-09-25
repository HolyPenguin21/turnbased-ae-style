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
    // One provisioned Scout mission still owns AP/accounting for this turn, but a single focus
    // hex no longer owns the actor's tactical movement. The provisioned objective seeds/refreshes
    // a durable ReconPatrolState; every actual move is ONE adjacent step selected from live state.
    //
    // After each authoritative MoveArmyRoutine returns, vision/contact/stealth/event state has
    // settled. We record discovery, mark assignment progress, then start the next iteration by
    // running ReconReactionPolicy and ReconGroundStepPlanner again. No cached multi-hex route is
    // followed after new information appears.
    // ===========================================================================================
    internal static class ReconGroundExecutor
    {
        internal sealed class StepControl
        {
            public bool CanContinue;
            public bool CommandAttempted;
            public ExecutionStopReason StopReason;

            public void Reset()
            {
                CanContinue = false;
                CommandAttempted = false;
                StopReason = ExecutionStopReason.StepCompleted;
            }
        }

        private sealed class StepRuntime
        {
            public bool OptionalStealthChecked;
        }

        private sealed class PreparedStep
        {
            public ReconMode RequestedMode;
            public HexCoord StrategicAnchor;
        }

        // Compatibility adapter for the current flag-off pipeline. It preserves the old bounded
        // multi-step behaviour, but every iteration now goes through the same one-command core the
        // mid-turn loop will call.
        public static IEnumerator Run(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, int apBefore,
            IReadOnlyList<ProvisionedMission> queue, int missionIndex, WorldSnapshot snapshot = null)
        {
            if (!TryPrepare(player, root, ctx, pm, result, queue, missionIndex, snapshot,
                    recordBatchAudit: true, out PreparedStep prepared))
            {
                SummarizeIfLast(player, ctx, queue, missionIndex);
                yield break;
            }

            ArmyData initialArmy = Resolve(player, pm.MoverArmyId);
            int iterations = 0;
            int maxIterations = Math.Max(2, (initialArmy?.CurrentMovement ?? 0) + 4);
            var runtime = new StepRuntime();
            var control = new StepControl();
            ExecutionStopReason stop = ExecutionStopReason.OutOfMovement;

            while (true)
            {
                if (++iterations > maxIterations)
                {
                    stop = ExecutionStopReason.MoveRejected;
                    break;
                }

                control.Reset();
                yield return RunPreparedStep(player, root, ctx, pm, result, queue, missionIndex,
                    snapshot, prepared, runtime, control);
                stop = control.StopReason;
                if (!control.CanContinue)
                    break;
            }

            Finish(player, root, ctx, pm, result, apBefore, stop, queue, missionIndex,
                summarize: true);
        }

        // One admitted ground-recon task step. It executes at most one canonical capture operation
        // OR one adjacent MoveArmyRoutine command. It never selects a different mission.
        public static IEnumerator RunStep(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, int apBefore,
            IReadOnlyList<ProvisionedMission> queue, int missionIndex, WorldSnapshot snapshot,
            StepControl control)
        {
            control ??= new StepControl();
            control.Reset();

            if (!TryPrepare(player, root, ctx, pm, result, queue, missionIndex, snapshot,
                    recordBatchAudit: false, out PreparedStep prepared))
            {
                control.StopReason = result.StopReason;
                Finish(player, root, ctx, pm, result, apBefore, result.StopReason,
                    queue, missionIndex, summarize: false);
                yield break;
            }

            yield return RunPreparedStep(player, root, ctx, pm, result, queue, missionIndex,
                snapshot, prepared, new StepRuntime(), control);
            Finish(player, root, ctx, pm, result, apBefore, control.StopReason,
                queue, missionIndex, summarize: false);
        }

        private static bool TryPrepare(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, IReadOnlyList<ProvisionedMission> queue,
            int missionIndex, WorldSnapshot snapshot, bool recordBatchAudit,
            out PreparedStep prepared)
        {
            prepared = null;
            ArmyData army = Resolve(player, pm?.MoverArmyId ?? -1);
            if (army == null || ctx?.Map == null || pm == null || result == null)
            {
                if (result != null)
                {
                    result.StopReason = ExecutionStopReason.MoverLost;
                    result.ApSpent = 0f;
                }
                return false;
            }

            ReconAcceptanceAudit.BeginTurn(player, ctx.TurnNumber);

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
                return false;
            }

            // Admission must precede BOTH durable patrol creation and required-stealth AP spend.
            // RunPreparedStep repeats these same domain checks after each awaited movement: a
            // valid scout can still be lost/recomposed or a battle can start between steps.
            if (!AiArmyRoles.IsSoloRecce(army))
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.ApSpent = 0f;
                ReconPatrolStateRegistry.Retire(player, army.Id, "actor no longer solo Recce");
                return false;
            }
            if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
            {
                result.StopReason = ExecutionStopReason.BattleStarted;
                result.ApSpent = 0f;
                if (result.StepsMoved == 0 && !result.EnteredStealth)
                    result.BlockedBeforeMovement = true;
                return false;
            }

            prepared = new PreparedStep
            {
                RequestedMode = ReconScoutKinds.IsExplore(pm.ScoutKind)
                    ? ReconMode.Explore
                    : ReconMode.Refresh,
                // The hex the scout works FROM: the vantage of a Surveil or of a vantage Refresh
                // (Recon audit B2 — ExecutionHex != FocusHex), otherwise the focus itself.
                StrategicAnchor = ReconScoutKinds.IsSurveil(pm.ScoutKind)
                    || (ReconScoutKinds.IsRefresh(pm.ScoutKind) && !pm.ExecutionHex.Equals(pm.FocusHex))
                    ? pm.ExecutionHex
                    : pm.FocusHex,
            };

            ReconPatrolStateRegistry.GetOrCreate(player, army.Id, army.Hex,
                prepared.StrategicAnchor, prepared.RequestedMode, ctx.TurnNumber);

            // Required stealth is preparatory state in the same admitted task transaction. The
            // operation is idempotent when the actor is already hidden and can never be charged
            // twice after activation.
            if (pm.StealthApReserved)
            {
                if (!TryEnterRequiredStealth(root, army, out bool entered))
                {
                    result.StopReason = ExecutionStopReason.RequiredStealthUnavailable;
                    AiDebugLog.Write($"[AI][V2][Recon][Ground] [{pm.Mission?.AttemptId}] actor=#{army.Id} "
                        + "required stealth unavailable; stop before movement");
                    return false;
                }
                result.EnteredStealth |= entered;
            }

            RefreshObjectiveSatisfied(player, pm, result);
            return true;
        }

        private static IEnumerator RunPreparedStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result,
            IReadOnlyList<ProvisionedMission> queue, int missionIndex, WorldSnapshot snapshot,
            PreparedStep prepared, StepRuntime runtime, StepControl control)
        {
            ArmyData army = Resolve(player, pm.MoverArmyId);
            if (army == null || army.Owner != player)
            {
                control.StopReason = ExecutionStopReason.MoverLost;
                ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId, "mover lost");
                yield break;
            }
            if (!AiArmyRoles.IsSoloRecce(army))
            {
                control.StopReason = ExecutionStopReason.MoverLost;
                ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId, "actor no longer solo Recce");
                yield break;
            }
            if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
            {
                control.StopReason = ExecutionStopReason.BattleStarted;
                if (result.StepsMoved == 0 && !result.EnteredStealth)
                    result.BlockedBeforeMovement = true;
                yield break;
            }

            RefreshObjectiveSatisfied(player, pm, result);
            ReconPatrolState assignment = ReconPatrolStateRegistry.GetOrCreate(player, army.Id,
                army.Hex, prepared.StrategicAnchor, prepared.RequestedMode, ctx.TurnNumber);

            ReconReactionDecision reaction = ReconReactionPolicy.Evaluate(
                player, ctx.Map, army, assignment);
            if (reaction.Action == ReconReactionAction.StopAndReplan)
            {
                control.StopReason = ExecutionStopReason.TargetInvalidated;
                yield break;
            }

            if (army.CurrentMovement <= 0)
            {
                control.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }

            HexCoord? next = null;
            string actionWhy = null;
            bool forceDecloakForAttack = false;
            bool sabotage = false;
            switch (reaction.Action)
            {
                case ReconReactionAction.Flee:
                    if (reaction.TargetHex.HasValue)
                        next = reaction.TargetHex.Value;
                    actionWhy = "Flee";
                    break;
                case ReconReactionAction.EvadeDetector:
                    if (reaction.TargetHex.HasValue)
                        next = reaction.TargetHex.Value;
                    actionWhy = "EvadeDetector";
                    break;
                case ReconReactionAction.AttackOpportunity:
                    if (reaction.TargetHex.HasValue)
                        next = reaction.TargetHex.Value;
                    actionWhy = "AttackOpportunity";
                    forceDecloakForAttack = true;
                    break;
                case ReconReactionAction.SabotageStructure:
                    if (reaction.TargetHex.HasValue)
                        next = reaction.TargetHex.Value;
                    actionWhy = "SabotageStructure";
                    sabotage = true;
                    break;
                case ReconReactionAction.Continue:
                default:
                    ReconGroundStepPlanner.StepChoice? choice = ReconGroundStepPlanner.Pick(
                        player, ctx.Map, army, assignment, ctx.TurnNumber, snapshot);
                    if (choice.HasValue)
                        next = choice.Value.Hex;
                    actionWhy = assignment.Mode.ToString();
                    break;
            }

            if (!next.HasValue)
            {
                control.StopReason = ExecutionStopReason.NoSafeStep;
                yield break;
            }

            // Tactical policies choose the desired destination; the shared ground-routing
            // owner decides the executable first step for every Recon action shape.
            next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, next.Value);
            if (!next.HasValue)
            {
                control.StopReason = ExecutionStopReason.NoSafeStep;
                yield break;
            }

            if (forceDecloakForAttack)
            {
                result.StealthChanged |= ExitArmyStealth(army);
                VisionSystem.RecomputeFor(player);
                AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
                ArmyData targetNow = BattleInitiator.FindEnemyAt(next.Value, army);
                if (targetNow == null || !reaction.TargetArmyId.HasValue
                    || targetNow.Id != reaction.TargetArmyId.Value)
                {
                    control.StopReason = ExecutionStopReason.TargetInvalidated;
                    yield break;
                }
            }

            if (sabotage)
            {
                // A fully hidden mover takes no action on arrival, so the takeover needs a visible
                // scout. Leaving stealth is free; re-check the one arrival rule on the settled
                // post-decloak state before committing the step.
                result.StealthChanged |= ExitArmyStealth(army);
                VisionSystem.RecomputeFor(player);
                AiReconIntelMemory.ObserveCurrentVisibility(player, ctx.TurnNumber);
                if (!reaction.TargetHex.HasValue || !next.Value.Equals(reaction.TargetHex.Value)
                    || !AiMapMemory.KnownUndefendedForeignStructureAt(player, next.Value))
                {
                    control.StopReason = ExecutionStopReason.TargetInvalidated;
                    yield break;
                }
            }

            if (!runtime.OptionalStealthChecked && !pm.StealthApReserved
                && !result.EnteredStealth && !forceDecloakForAttack && !sabotage)
            {
                runtime.OptionalStealthChecked = true;
                float mandatoryClaims = MandatoryApClaimsFrom(queue, missionIndex);
                result.EnteredStealth |= MaybeEnterOptionalStealth(player, root, ctx, army, pm,
                    next.Value, assignment.StrategicAnchor, mandatoryClaims);
            }

            HashSet<int> knownEnemyIds = KnownIds(AiMapMemory.AllKnownEnemySightings(player));
            HashSet<int> knownNeutralIds = KnownIds(AiMapMemory.AllKnownNeutralSightings(player));
            HexCoord beforeHex = army.Hex;
            ReconAcceptanceAudit.RecordDecision(player, ctx.TurnNumber, army.Id,
                beforeHex, next.Value, actionWhy);
            var move = AiDecision.Move(army, next.Value,
                $"V2 recon continuous — {actionWhy}; mission={ReconScoutKinds.Name(pm.ScoutKind)}; "
                + $"mode={assignment.Mode}; anchor=({assignment.StrategicAnchor.Q},{assignment.StrategicAnchor.R})", 0f,
                forceDecloakForAttack ? AiGroundMoveAuthority.Combat
                    : sabotage ? GroundMoveAuthorityPolicy.ForStructureAssaultStep(next.Value, reaction.TargetHex.Value)
                    : AiGroundMoveAuthority.Transit);
            // Required/optional stealth and visible opportunistic attacks were resolved above.
            // Re-entering stealth in the shared mover would cancel the intended combat and could
            // also spend AP that Recon deliberately reserved for other missions.
            move.AllowAutomaticStealth = false;
            var trace = new AiMoveExecutionTrace();
            control.CommandAttempted = true;
            yield return AiTurnController.MoveArmyRoutine(player, move, ctx, trace);
            result.EnteredStealth |= trace.EnteredStealthThisStep;

            army = Resolve(player, pm.MoverArmyId);
            HexCoord endHex = army != null ? army.Hex : trace.EndHex;
            bool moved = !endHex.Equals(beforeHex);
            if (moved)
            {
                result.StepsMoved++;
                ReconPatrolStateRegistry.MarkProgress(player, pm.MoverArmyId, ctx.TurnNumber);
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
                control.StopReason = ExecutionStopReason.MoverLost;
                ReconPatrolStateRegistry.Retire(player, pm.MoverArmyId, "mover lost during step");
                yield break;
            }
            if (trace.BattleOccurred)
            {
                control.StopReason = ExecutionStopReason.BattleStarted;
                yield break;
            }
            if (trace.HexEventOccurred)
            {
                control.StopReason = ExecutionStopReason.HexEventStarted;
                yield break;
            }
            if (!moved)
            {
                control.StopReason = ExecutionStopReason.MoveRejected;
                yield break;
            }

            HashSet<int> enemyNow = KnownIds(AiMapMemory.AllKnownEnemySightings(player));
            HashSet<int> neutralNow = KnownIds(AiMapMemory.AllKnownNeutralSightings(player));
            int[] newEnemyIds = enemyNow.Where(id => !knownEnemyIds.Contains(id)).ToArray();
            int[] newNeutralIds = neutralNow.Where(id => !knownNeutralIds.Contains(id)).ToArray();

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
            control.CanContinue = army.CurrentMovement > 0;
            control.StopReason = control.CanContinue
                ? ExecutionStopReason.StepCompleted
                : ExecutionStopReason.OutOfMovement;
        }

        private static void Finish(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, int apBefore, ExecutionStopReason stop,
            IReadOnlyList<ProvisionedMission> queue, int missionIndex, bool summarize)
        {
            if (result == null) return;
            result.FinalHex = Resolve(player, pm?.MoverArmyId ?? -1)?.Hex ?? result.FinalHex;
            result.StopReason = stop;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            AiDebugLog.Write($"[AI][V2][Recon][Ground] [{pm?.Mission?.AttemptId}] {pm?.Key} actor=#{pm?.MoverArmyId ?? -1} "
                + $"kind={(pm != null ? ReconScoutKinds.Name(pm.ScoutKind) : "-")} "
                + $"({result.StartHex.Q},{result.StartHex.R})→({result.FinalHex.Q},{result.FinalHex.R}) "
                + $"steps={result.StepsMoved} ap−{result.ApSpent.ToString("0.#", CultureInfo.InvariantCulture)} "
                + $"objective={(result.ReachedGoal ? "met" : "open")} stop={stop}");
            if (summarize)
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

            bool met = ScoutObjectiveEvaluator.IsSatisfiedLive(player, pm.ScoutKind, pm.FocusHex,
                pm.TrackedArmyId, pm.BaselineObservedTurn);

            if (met)
            {
                result.ReachedGoal = true;
                // Spec §1 — for a ground Explore/Refresh actor this is a satisfied WAYPOINT, not a
                // finished role: the durable ReconPatrolState persists and the MissionIntent should
                // be re-focused next turn, not retired. Surveil completion is a genuine done.
                result.DurableRoleContinues = !ReconScoutKinds.IsSurveil(pm.ScoutKind)
                    && AiArmyRoles.IsSoloRecce(Resolve(player, pm.MoverArmyId));
                AiDebugLog.Write($"[AI][V2][Recon][Objective] [{pm.Mission?.AttemptId}] {pm.Key} "
                    + $"kind={ReconScoutKinds.Name(pm.ScoutKind)} met; "
                    + $"durableRoleContinues={(result.DurableRoleContinues ? 1 : 0)}");
            }
        }

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            AiV2Util.ResolveArmy(player, armyId);

        private static HashSet<int> KnownIds(IEnumerable<AiMapMemory.KnownEnemySighting> sightings)
        {
            var set = new HashSet<int>();
            foreach (AiMapMemory.KnownEnemySighting s in sightings)
                set.Add(s.ArmyId);
            return set;
        }

        private static bool ExitArmyStealth(ArmyData army)
        {
            if (army == null)
                return false;
            bool changed = false;
            foreach (var member in army.Members.ToList())
                if (member.IsHidden)
                {
                    StealthSystem.ExitStealth(member);
                    changed = true;
                }
            return changed;
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
            if (root == null || !StealthSystem.TryEnterStealth(
                    scout, root, army.ActivationApCost))
                return false;
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
            int stealthAp = StealthSystem.EnterStealthApCost;
            if (!StealthSystem.CanPayToEnterStealth(scout, root))
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

            return StealthSystem.TryEnterStealth(scout, root);
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
            bool hidden = StealthSystem.IsArmyFullyHidden(army);
            if (hidden)
                return; // already hidden — this policy is only asked before entering stealth

            // A hex only a hidden mover may enter — exactly what the one arrival rule refuses a
            // VISIBLE mover (a known army to fight, a known undefended structure to take over).
            bool OccupiedByOther(HexCoord h) =>
                AiMapMemory.KnownGroundArrival(player, h, moverFullyHidden: false).HasOutcome;

            if (OccupiedByOther(nextHex) || OccupiedByOther(anchor))
                access = 1f;

            int occupiedNearby = 0;
            foreach (HexCoord h in HexGridMath.HexesInRange(nextHex, 2))
                if (!h.Equals(nextHex) && OccupiedByOther(h))
                    occupiedNearby++;
            shorten = Mathf.Clamp01(occupiedNearby / 4f);
        }

        private static float LegDetectionRisk(PlayerSetupData player, HexCoord hex) =>
            ScoutRiskModel.DetectorRisk(AiMapMemory.AllKnownEnemySightings(player), hex);
    }
}
