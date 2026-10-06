using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Game.Cards;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using Game.Aviation;
using UnityEngine;

namespace Game.Ai.V2
{
    internal static class RaidExecutor
    {
        // Compatibility adapter for the current Full/Aggression batch path. The target is
        // revalidated before every adjacent move, but the adapter keeps executing steps until the
        // same terminal conditions as the previous loop.
        internal static IEnumerator RunRaid(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, int apBefore, WorldSnapshot snapshot)
        {
            ArmyData initialArmy = Resolve(player, pm.MoverArmyId);
            int maxIterations = (initialArmy?.CurrentMovement ?? 0) + 1;
            int iterations = 0;
            ExecutionStopReason stop = ExecutionStopReason.OutOfMovement;

            while (true)
            {
                if (++iterations > maxIterations)
                {
                    stop = ExecutionStopReason.MoveRejected;
                    break;
                }

                yield return RunRaidStepCore(player, root, ctx, pm, result, snapshot);
                stop = result.StopReason;
                if (stop != ExecutionStopReason.StepCompleted)
                    break;
            }

            FinishRaid(player, root, pm, result, apBefore, stop);
        }

        // One admitted Raid task step. It executes at most one adjacent MoveArmyRoutine command.
        // Objective identity stays fixed, while its last honestly-known hex and route are resolved
        // again immediately before the command.
        internal static IEnumerator RunRaidStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, int apBefore,
            WorldSnapshot snapshot = null)
        {
            yield return RunRaidStepCore(player, root, ctx, pm, result, snapshot);
            FinishRaid(player, root, pm, result, apBefore, result.StopReason);
        }

        private static IEnumerator RunRaidStepCore(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, WorldSnapshot snapshot)
        {
            ArmyData army = Resolve(player, pm?.MoverArmyId ?? -1);
            if (army == null || army.Owner != player)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                yield break;
            }
            if (ctx?.Map == null)
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = true;
                yield break;
            }
            if (ctx.HexSelection != null && ctx.HexSelection.IsBattleActive)
            {
                result.StopReason = ExecutionStopReason.BattleStarted;
                yield break;
            }

            // Four atomic legs. Execution never picks a different
            // target, a different base, re-scores anything, or creates a replacement mission: it
            // carries out exactly the plan Provisioning pinned onto `pm`.
            if (pm.RaidPhase == RaidMissionPhase.Return || pm.RaidPhase == RaidMissionPhase.SupportReturn
                || pm.RaidPhase == RaidMissionPhase.RecoveryReturn)
            {
                yield return RunRaidReturnStep(player, root, ctx, pm, result, army, snapshot);
                yield break;
            }
            if (pm.RaidPhase == RaidMissionPhase.AirSupport)
            {
                yield return RunRaidAirSupportStep(player, root, ctx, pm, result, army);
                yield break;
            }
            if (pm.RaidPhase == RaidMissionPhase.Reinforcement)
            {
                yield return RunRaidReinforcementStep(player, root, ctx, pm, result, army, snapshot);
                yield break;
            }

            if (RaidObjectiveEvaluator.IsObjectiveSatisfiedLive(player, pm.RaidTarget))
            {
                result.ReachedGoal = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
                yield break;
            }

            HexCoord targetHex;
            bool targetIsNeutral;
            if (pm.RaidTarget.Kind == RaidTargetKind.EventGuard)
            {
                // Event guards have no ArmyId until spawned — never queried in ArmyRegistry before
                // the trigger, never spawned here. The stable hex is the whole identity.
                if (!HexEventRegistry.HasActiveEvent(pm.RaidTarget.Hex))
                {
                    result.StopReason = ExecutionStopReason.TargetInvalidated;
                    result.NeedsReplan = true;
                    yield break;
                }
                targetHex = pm.RaidTarget.Hex;
                targetIsNeutral = true;
            }
            else
            {
                AiMapMemory.KnownEnemySighting? target = RaidObjectiveEvaluator.FindSightingLive(player, pm.RaidTarget.ArmyId);
                if (!target.HasValue)
                {
                    result.StopReason = ExecutionStopReason.TargetInvalidated;
                    result.NeedsReplan = true;
                    yield break;
                }

                targetHex = target.Value.Hex;
                targetIsNeutral = target.Value.Owner != null && target.Value.Owner.IsNeutral;

                // Defensive re-check only; RaidObjectiveEvaluator.IsNeutralRaidTarget
                // is the ONE canonical neutrality decision, already applied by Provisioning before
                // this step was ever scheduled. A target that flips to a non-neutral owner between
                // provisioning and this execution step (e.g. another AI player claimed it mid-turn)
                // must not be attacked — Raid targets neutrals only.
                if (!RaidObjectiveEvaluator.IsNeutralRaidTarget(target.Value.Owner))
                {
                    result.StopReason = ExecutionStopReason.TargetInvalidated;
                    result.NeedsReplan = true;
                    yield break;
                }
            }
            pm.ExecutionHex = targetHex;
            pm.RaidLastKnownHex = targetHex;
            pm.RaidTargetIsNeutral = targetIsNeutral;

            if (army.Hex.Equals(targetHex))
            {
                // Already standing on the target hex with no battle active (checked above). For an
                // event guard this can happen with no fresh move about to fire ResolveEventExplore
                // on its own (e.g. a previously blocked step) — explicitly (re-)trigger it through
                // the one thin gameplay entry point; the domain flow (spawn/battle/reward) is
                // unchanged either way. A physical neutral army resolves through the ordinary
                // contact/battle systems exactly as before.
                if (pm.RaidTarget.Kind == RaidTargetKind.EventGuard && ctx.HexSelection != null)
                    ctx.HexSelection.TriggerAiEventExplore(army, targetHex);
                result.StopReason = ExecutionStopReason.EnemyDiscovered;
                yield break;
            }
            if (army.CurrentMovement <= 0)
            {
                result.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }

            HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, targetHex,
                profile: SafeRouteProfile.Combat);
            if (!next.HasValue)
            {
                result.StopReason = ExecutionStopReason.NoSafeStep;
                result.NeedsReplan = true;
                yield break;
            }

            HexCoord before = army.Hex;
            // 2026-10-02 (project owner) — CombatAndCapture: the route no longer detours around a
            // known undefended foreign structure, and the game rule destroys/captures whatever
            // undefended structure a mover lands on, so a raid that crosses one is not forbidden.
            var decision = AiDecision.Move(army, next.Value,
                $"V2 raid — strike {pm.RaidTarget.DiagnosticLabel} at ({targetHex.Q},{targetHex.R})", 0f,
                AiGroundMoveAuthority.CombatAndCapture);
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

            army = Resolve(player, pm.MoverArmyId);
            HexCoord endHex = army != null ? army.Hex : trace.EndHex;
            bool moved = !endHex.Equals(before);
            if (moved)
                result.StepsMoved++;
            result.FinalHex = endHex;

            bool operationStarted = moved || trace.BattleOccurred || trace.HexEventOccurred;
            result.OperationStarted |= operationStarted;
            if (operationStarted)
                result.ActualActorArmyId = pm.MoverArmyId;

            if (trace.BattleOccurred)
            {
                result.NeedsReplan |= army == null;
                result.StopReason = army == null ? ExecutionStopReason.MoverLost : ExecutionStopReason.BattleStarted;
                yield break;
            }
            if (trace.HexEventOccurred)
            {
                result.NeedsReplan |= army == null;
                result.StopReason = army == null ? ExecutionStopReason.MoverLost : ExecutionStopReason.HexEventStarted;
                yield break;
            }
            if (army == null)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                yield break;
            }
            if (!moved)
            {
                result.StopReason = ExecutionStopReason.MoveRejected;
                yield break;
            }

            result.StopReason = army.CurrentMovement > 0
                ? ExecutionStopReason.StepCompleted
                : ExecutionStopReason.OutOfMovement;
        }

        private static IEnumerator RunRaidAirSupportStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, ArmyData wing)
        {
            if (!AviationRules.IsValidAirArmy(wing)
                || pm.RaidTarget.Kind != RaidTargetKind.NeutralArmy
                || !pm.RaidAirSupportLandingHex.HasValue)
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = true;
                yield break;
            }
            yield return GroundCombatLegStep.AirStrikeSortie(player, root, ctx, pm, result, wing,
                pm.RaidLastKnownHex, pm.RaidAirSupportLandingHex.Value,
                AirStrikePolicy.RaidSupport(pm.RaidTarget.ArmyId),
                "RaidSupport", "flies toward exact raid target");
        }

        // =====================================================================================
        //  RETURN leg: at most ONE step of the mover (primary for
        //  Return, support for SupportReturn) toward the already-chosen base. The base is never
        //  re-selected here. On SupportReturn arrival, the support's completion is handed straight
        //  to Continuity (mirrors CompleteRaidReinforcement's direct-call pattern) and the step is
        //  reported as a ProductiveStop (DurableRoleContinues), never a Completed objective — the
        //  durable Raid campaign must not be retired just because this one leg finished.
        // =====================================================================================
        private static IEnumerator RunRaidReturnStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, ArmyData army,
            WorldSnapshot snapshot)
        {
            bool isSupportLeg = pm.RaidPhase == RaidMissionPhase.SupportReturn;
            bool isRecoveryLeg = pm.RaidPhase == RaidMissionPhase.RecoveryReturn;
            void ReportArrived()
            {
                if (isSupportLeg)
                {
                    MissionContinuityLayer.CompleteRaidSupportReturn(player, snapshot,
                        pm.RaidPrimaryArmyId ?? pm.MoverArmyId, $"support #{pm.MoverArmyId} arrived home");
                    result.DurableRoleContinues = true;
                }
                else if (isRecoveryLeg)
                {
                    MissionContinuityLayer.CompleteRaidRecoveryReturn(player, snapshot,
                        pm.RaidPrimaryArmyId ?? pm.MoverArmyId,
                        $"primary #{pm.MoverArmyId} arrived at recovery base");
                    result.DurableRoleContinues = true;
                }
                result.ReachedGoal = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
            }

            HexCoord home = pm.RaidDestinationHex;
            pm.ExecutionHex = home;
            if (army.Hex.Equals(home))
            {
                ReportArrived();
                yield break;
            }
            // A temporarily blocked first step is a retry-next-turn, not a reason to drop the leg.
            var leg = new GroundLegStepResult();
            yield return GroundCombatLegStep.Transit(player, ctx, army, home,
                $"V2 raid — {(isSupportLeg ? "support " : isRecoveryLeg ? "recovery " : "")}return to base ({home.Q},{home.R})",
                leg);
            if (leg.Blocked.HasValue)
            {
                result.StopReason = leg.Blocked.Value;
                result.NeedsReplan = leg.NeedsReplan;
                yield break;
            }

            army = leg.Army;
            bool moved = leg.Moved;
            if (moved) result.StepsMoved++;
            result.FinalHex = leg.EndHex;
            result.OperationStarted |= moved;
            if (moved) result.ActualActorArmyId = pm.MoverArmyId;

            if (leg.BattleOccurred)
            {
                result.NeedsReplan |= army == null;
                result.StopReason = army == null ? ExecutionStopReason.MoverLost : ExecutionStopReason.BattleStarted;
                yield break;
            }

            if (leg.HexEventOccurred)
            {
                result.NeedsReplan |= army == null;
                result.StopReason = army == null ? ExecutionStopReason.MoverLost : ExecutionStopReason.HexEventStarted;
                yield break;
            }
            if (army == null)
            {
                // Support/primary lost en route — release its claim and let the Raid continue from
                // whatever state remains; ResolveActive's next pass detects the loss and cleans up.
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                yield break;
            }
            if (!moved) { result.StopReason = ExecutionStopReason.MoveRejected; yield break; }
            if (army.Hex.Equals(home))
            {
                ReportArrived();
                yield break;
            }
            result.StopReason = army.CurrentMovement > 0
                ? ExecutionStopReason.StepCompleted
                : ExecutionStopReason.OutOfMovement;
        }

        // =====================================================================================
        //  REINFORCEMENT leg. Either exactly ONE transit step of the SUPPORT army,
        //  or (once it stands on the primary's hex) exactly ONE atomic transfer/swap transaction
        //  with NO movement in the same step. The primary never moves in this leg.
        // =====================================================================================
        private static IEnumerator RunRaidReinforcementStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, ArmyData support,
            WorldSnapshot snapshot)
        {
            ArmyData primary = pm.RaidPrimaryArmyId.HasValue ? Resolve(player, pm.RaidPrimaryArmyId.Value) : null;
            if (primary == null || primary.Owner != player || primary.Members.Count == 0)
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = true;
                yield break;
            }

            HexCoord rendezvous = primary.Hex;
            pm.ExecutionHex = rendezvous;

            if (!support.Hex.Equals(rendezvous))
            {
                var leg = new GroundLegStepResult();
                yield return GroundCombatLegStep.Transit(player, ctx, support, rendezvous,
                    $"V2 raid — reinforcement convoy to primary #{primary.Id} at ({rendezvous.Q},{rendezvous.R})",
                    leg);
                if (leg.Blocked.HasValue)
                {
                    result.StopReason = leg.Blocked.Value;
                    result.NeedsReplan = leg.NeedsReplan;
                    yield break;
                }

                support = leg.Army;
                bool moved = leg.Moved;
                if (moved) result.StepsMoved++;
                result.FinalHex = leg.EndHex;
                result.OperationStarted |= moved;

                if (leg.BattleOccurred)
                {
                    result.NeedsReplan |= support == null;
                    result.StopReason = support == null ? ExecutionStopReason.MoverLost : ExecutionStopReason.BattleStarted;
                    yield break;
                }

                if (leg.HexEventOccurred)
                {
                    result.NeedsReplan |= support == null;
                    result.StopReason = support == null ? ExecutionStopReason.MoverLost : ExecutionStopReason.HexEventStarted;
                    yield break;
                }
                if (support == null)
                {
                    result.StopReason = ExecutionStopReason.MoverLost;
                    result.NeedsReplan = true;
                    yield break;
                }
                if (!moved) { result.StopReason = ExecutionStopReason.MoveRejected; yield break; }
                // A transfer is a SEPARATE step: never move and hand off in the same one.
                result.StopReason = ExecutionStopReason.StepCompleted;
                yield break;
            }

            // ---- the atomic handoff transaction ------------------------------------------
            result.ReinforcementHandoffAttempted = true;
            bool handoffOk = GroundCombatReinforcementTransaction.ApplyReinforcementHandoff(player, ctx, pm, support, primary,
                out int transferred, out bool wasSwap, out string displacedUnitName, out string detail);
            AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm.Mission)}] {pm.Key} — raid "
                + $"reinforcement handoff support #{support.Id} -> primary #{primary.Id}: "
                + $"{(handoffOk ? "OK" : "REJECTED")} moved={transferred} swap={(wasSwap ? 1 : 0)} "
                + $"{(wasSwap ? $"displaced={displacedUnitName} " : "")}{detail}");

            if (transferred > 0)
            {
                result.CombatChanged = true;
                // §10 — bump the version and publish an Actor|Capability invalidation so the next
                // bounded cycle re-checks this Raid's readiness in the SAME turn.
                WorldDeltaLifecycle.CommitMutation();
                WorldDeltaLifecycle.Publish(player, ctx.TurnNumber,
                    StrategicInvalidationReason.Actor | StrategicInvalidationReason.Capability,
                    actorIds: new[] { primary.Id, support.Id });
            }

            // A full/full swap displaced a primary member into support:
            // the whole support army now walks itself home instead of rejoining the fight. Hand
            // this off to Continuity right here, same direct-call pattern as CompleteRaidReinforcement
            // below, and skip the ordinary Assault-verification/CompleteRaidReinforcement path for
            // this turn — SupportReturn's own arrival re-evaluates the primary later.
            if (wasSwap)
            {
                MissionContinuityLayer.BeginRaidSupportReturn(player, snapshot, primary.Id, support.Id,
                    $"displaced={displacedUnitName}");
                result.ReachedGoal = true;
                result.DurableRoleContinues = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
                yield break;
            }

            // §9/§10 — the roster is re-verified against the shared estimator, and ONLY a verified
            // roster returns the operation to Assault. Continuity owns that state transition.
            bool verified = GroundCombatFeasibility.Clears(
                primary.Members.Select(WorthIt.FromLiveUnit).ToList(),
                WorthIt.SideCommander.Of(primary.Commander), AiV2Util.KnownPrimaryOppositionLive(player, pm.RaidTarget),
                AiConfigV2.raidMinViableWinChance, 0f, out float win, out bool cover);
            MissionContinuityLayer.CompleteRaidReinforcement(player, primary.Id, verified,
                $"win={win.ToString("0.00", CultureInfo.InvariantCulture)} cover={(cover ? 1 : 0)} "
                + $"transferred={transferred}");

            // The operation advanced either because a transfer actually happened, OR because the
            // roster already verified without needing one (CompleteRaidReinforcement just flipped
            // ri.Phase back to Assault above in that case too) — basing this solely on handoffOk
            // under-reports a genuinely productive step (e.g. support had nothing to spare but the
            // primary already clears the target on its own) as an unproductive one.
            bool operationAdvanced = handoffOk || verified;
            result.ReachedGoal = operationAdvanced;
            // The RENDEZVOUS is satisfied, the RAID is not: the durable operation continues (back
            // into Assault once the roster re-cleared). DurableRoleContinues makes the ledger
            // classify this as a ProductiveStop instead of retiring the intent.
            result.DurableRoleContinues = operationAdvanced;
            result.StopReason = operationAdvanced
                ? ExecutionStopReason.ReachedGoal
                : ExecutionStopReason.MoveRejected;
        }

        private static void FinishRaid(PlayerSetupData player, PlayerRoot root,
            ProvisionedMission pm, ExecutionResult result, int apBefore,
            ExecutionStopReason stop)
        {
            result.FinalHex = Resolve(player, pm?.MoverArmyId ?? -1)?.Hex ?? result.FinalHex;
            result.StopReason = stop;
            result.ApSpent = Mathf.Max(0f, apBefore - (root != null ? root.ActionPoints : apBefore));
            AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm?.Mission)}] {pm?.Key} — raid "
                + $"({result.StartHex.Q},{result.StartHex.R})→({result.FinalHex.Q},{result.FinalHex.R}) "
                + $"steps {result.StepsMoved} ap −{result.ApSpent.ToString("0.#", CultureInfo.InvariantCulture)} "
                + $"stop {stop}" + (!result.ReachedGoal ? ""
                    : pm?.Mission?.Target is RaidMissionTarget rt && rt.Phase != RaidMissionPhase.Assault
                        ? " (arrived at destination)" : " (target gone)"));
        }

        private static ArmyData Resolve(PlayerSetupData player, int armyId) =>
            AiV2Util.ResolveArmy(player, armyId);
    }
}

