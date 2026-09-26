using System.Collections;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ATK §26/§67 — EXECUTION OF ONE ATTACK STEP.
    //
    //  Exactly ONE atomic physical step per call, exactly like every other V2 executor. There is no
    //  internal `while (army.CurrentMovement > 0)` loop re-planning on its own (§67): a step mutates
    //  the world, the settled-step loop takes a fresh snapshot, typed invalidation re-admits the
    //  Aggression lane, and the NEXT step is decided against that fresh world.
    //
    //  Movement authority (§26/§27) comes from the one policy owner: every approach step is plain
    //  Transit and only the terminal step INTO the target may seek a takeover, so an operation can
    //  never incidentally capture some other structure it walks across.
    //
    //  A peer executor file beside ReconGroundExecutor / ReconAirExecutor — the established shape
    //  for a lane's own step semantics, not a new architectural layer.
    // ===========================================================================================
    internal static class AttackExecutor
    {
        internal static IEnumerator RunStep(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, WorldSnapshot snapshot)
        {
            ArmyData army = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
            if (army == null || army.Owner != player || army.Members.Count == 0)
            {
                result.StopReason = ExecutionStopReason.MoverLost;
                result.NeedsReplan = true;
                yield break;
            }

            AttackMissionTarget target = pm.AttackTarget;
            switch (target.Phase)
            {
                case AttackMissionPhase.RecoveryReturn:
                case AttackMissionPhase.SupportReturn:
                case AttackMissionPhase.GatherReturn:
                    yield return RunWalkHomeStep(player, ctx, pm, result, army, target);
                    yield break;
                case AttackMissionPhase.Reinforcement:
                case AttackMissionPhase.Gather:
                    yield return RunReinforcementStep(player, ctx, pm, result, army, snapshot);
                    yield break;
                case AttackMissionPhase.AirSupport:
                    if (!AviationRules.IsValidAirArmy(army) || !target.AirSupportLandingHex.HasValue)
                    {
                        result.StopReason = ExecutionStopReason.TargetInvalidated;
                        result.NeedsReplan = true;
                        yield break;
                    }
                    // The one flight step of ground-fight air support; a plain strike on every
                    // defender of the site (the ground assault takes the structure).
                    yield return GroundCombatLegStep.AirStrikeSortie(player, root, ctx, pm, result,
                        army, target.Target.Hex, target.AirSupportLandingHex.Value,
                        AirStrikePolicy.Standard, "AttackSupport",
                        "flies toward the attack site");
                    yield break;
            }

            yield return RunAssaultStep(player, ctx, pm, result, army, target, snapshot);
        }

        // §24/§26 — march on the target and, on the terminal step, take it.
        private static IEnumerator RunAssaultStep(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, ArmyData army,
            AttackMissionTarget target, WorldSnapshot snapshot)
        {
            // §25 — honest re-check against the CURRENT snapshot before spending anything. A target
            // that became ours (however it happened) is a satisfied objective, not a failed step.
            AttackObjectiveEvaluator.AttackTargetStatus status =
                AttackObjectiveEvaluator.EvaluateTarget(snapshot, target.Target);
            if (status == AttackObjectiveEvaluator.AttackTargetStatus.Captured)
            {
                result.ReachedGoal = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
                yield break;
            }
            if (status == AttackObjectiveEvaluator.AttackTargetStatus.Invalidated)
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                result.NeedsReplan = true;
                yield break;
            }

            HexCoord targetHex = target.Target.Hex;
            pm.ExecutionHex = targetHex;

            if (army.CurrentMovement <= 0)
            {
                result.StopReason = ExecutionStopReason.OutOfMovement;
                yield break;
            }

            // §9/§10 — the ONE tactical decision this step is allowed to take: a weak enemy field
            // army on the way may be destroyed first. This changes only where THIS step walks; the
            // strategic target above is untouched and no intent, proposal or demand is produced.
            AttackTacticalStrike strike = AttackTacticalOpportunity.Select(player, ctx.Map,
                snapshot, army, target, ctx.TurnNumber);
            HexCoord waypoint = strike.HasValue ? strike.Hex : targetHex;

            HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, waypoint);
            if (!next.HasValue)
            {
                // §27 — a route blocked by another known hostile structure simply has no safe step
                // here. That is what makes "capture the Base in the way first" fall out of routing
                // and scoring instead of being a scripted rule.
                result.StopReason = ExecutionStopReason.NoSafeStep;
                result.NeedsReplan = true;
                yield break;
            }

            HexCoord before = army.Hex;
            // A side strike may fight but must NEVER take a structure: only the terminal step into
            // the Attack's own target carries capture authority (§26/§27). The eligibility gate has
            // already excluded a candidate standing on any known hostile structure
            // (Base/Citadel/Facility — AttackObjectiveEvaluator.IsKnownHostileAttackSite), so the
            // contact step is a plain field battle.
            AiGroundMoveAuthority authority = strike.HasValue
                ? GroundMoveAuthorityPolicy.ForTacticalStrikeStep(next.Value, waypoint)
                : GroundMoveAuthorityPolicy.ForStructureAssaultStep(next.Value, targetHex);
            var decision = AiDecision.Move(army, next.Value, strike.HasValue
                ? $"V2 attack — tactical strike on enemy #{strike.EnemyArmyId} en route to "
                    + target.Target.DiagnosticLabel
                : $"V2 attack — assault {target.Target.DiagnosticLabel}", 0f, authority);
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, decision, ctx, trace);

            army = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
            HexCoord endHex = army != null ? army.Hex : trace.EndHex;
            bool moved = !endHex.Equals(before);
            if (moved)
                result.StepsMoved++;
            result.FinalHex = endHex;

            // Same facts as Raid's assault step: an ordinary hex event on the way is a physical
            // start of the operation and ends this step for a fresh observation.
            bool operationStarted = moved || trace.BattleOccurred || trace.HexEventOccurred;
            result.OperationStarted |= operationStarted;
            if (operationStarted)
                result.ActualActorArmyId = pm.MoverArmyId;

            if (trace.BattleOccurred)
            {
                result.CombatChanged = true;
                // §16/§17 — the diversion was actually spent. Record it as an execution FACT so
                // Continuity stamps the operation's once-per-turn marker; this Attack cannot divert
                // again this turn even if a second weak army shows up. Continuation is deliberately
                // NOT asserted here: the settled-step loop takes a fresh snapshot and re-evaluates
                // this intent, which may legitimately turn into Reinforcement or Recovery.
                if (strike.HasValue && next.Value.Equals(waypoint))
                    result.AttackOpportunisticStrike = true;
                result.StopReason = ExecutionStopReason.BattleStarted;
                yield break;
            }
            if (trace.HexEventOccurred)
            {
                result.StopReason = ExecutionStopReason.HexEventStarted;
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

            if (endHex.Equals(targetHex))
            {
                // §8/§61 — the structure changed hands. Publishing the infrastructure fact is what
                // lets the SAME turn's typed invalidation wake Recon, Aggression, Economy and
                // Development against the new topology instead of waiting for the next turn.
                result.InfrastructureChanged = true;
                result.ReachedGoal = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
                yield break;
            }

            result.StopReason = army.CurrentMovement > 0
                ? ExecutionStopReason.StepCompleted
                : ExecutionStopReason.OutOfMovement;
        }

        // §47 — walk the mover to the base Continuity already chose. Never re-picks a destination.
        private static IEnumerator RunWalkHomeStep(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, ArmyData army, AttackMissionTarget target)
        {
            HexCoord home = target.DestinationHex;
            pm.ExecutionHex = home;

            if (army.Hex.Equals(home))
            {
                result.ReachedGoal = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
                yield break;
            }
            var leg = new GroundLegStepResult();
            yield return GroundCombatLegStep.Transit(player, ctx, army, home,
                $"V2 attack — {target.Phase} to ({home.Q},{home.R})", leg);
            if (leg.Blocked.HasValue)
            {
                result.StopReason = leg.Blocked.Value;
                result.NeedsReplan = leg.NeedsReplan;
                yield break;
            }

            army = leg.Army;
            bool moved = leg.Moved;
            if (moved)
            {
                result.StepsMoved++;
                result.ActualActorArmyId = pm.MoverArmyId;
            }
            result.FinalHex = leg.EndHex;
            result.OperationStarted |= moved;

            if (leg.BattleOccurred)
            {
                result.CombatChanged = true;
                result.StopReason = ExecutionStopReason.BattleStarted;
                yield break;
            }
            if (leg.HexEventOccurred)
            {
                result.StopReason = ExecutionStopReason.HexEventStarted;
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
            if (army.Hex.Equals(home))
            {
                result.ReachedGoal = true;
                result.StopReason = ExecutionStopReason.ReachedGoal;
                yield break;
            }
            result.StopReason = army.CurrentMovement > 0
                ? ExecutionStopReason.StepCompleted
                : ExecutionStopReason.OutOfMovement;
        }

        // §46 — either exactly ONE transit step of the support army, or (once it stands on the
        // primary's hex) exactly ONE atomic roster handoff with no movement in the same step. The
        // handoff itself is the shared TaskExecutor primitive; Attack does not get its own.
        private static IEnumerator RunReinforcementStep(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, ArmyData support, WorldSnapshot snapshot)
        {
            AttackMissionTarget target = pm.AttackTarget;
            ArmyData primary = target.PrimaryArmyId.HasValue
                ? AiV2Util.ResolveArmy(player, target.PrimaryArmyId.Value) : null;
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
                    $"V2 attack — {target.Phase.ToString().ToLowerInvariant()} convoy to primary #{primary.Id}",
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
                    result.CombatChanged = true;
                    result.StopReason = ExecutionStopReason.BattleStarted;
                    yield break;
                }
                if (leg.HexEventOccurred)
                {
                    result.StopReason = ExecutionStopReason.HexEventStarted;
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

            result.ReinforcementHandoffAttempted = true;
            // The site's fight decides whether the support's hero should take command of the fist
            // (GroundCombatReinforcement.CommandHandover — the gather projection's same rule).
            bool handoffOk = TaskExecutor.ApplyReinforcementHandoff(player, ctx, pm, support, primary,
                out int transferred, out bool wasSwap, out string displacedUnitName, out string detail,
                AttackObjectiveEvaluator.KnownSiteOpposition(snapshot, target.Target.Hex),
                AttackObjectiveEvaluator.KnownSiteDefenceBonus(snapshot, ctx.Map, target.Target.Hex));
            AiDebugLog.Write($"[AI][V2] exec [{AiV2Trace.FormatCorrelation(pm.Mission)}] {pm.Key} — attack "
                + $"{target.Phase.ToString().ToLowerInvariant()} handoff support #{support.Id} -> primary #{primary.Id}: "
                + $"{(handoffOk ? "OK" : "REJECTED")} moved={transferred} swap={(wasSwap ? 1 : 0)} "
                + $"{(wasSwap ? $"displaced={displacedUnitName} " : "")}{detail}");

            if (transferred > 0)
            {
                // A delivered body is a physical start of the operation (a fresh Gather whose first
                // step is a same-hex handoff must still create its durable intent, §70).
                result.OperationStarted = true;
                result.CombatChanged = true;
                // The primary's readiness genuinely changed: bump and publish so the SAME turn's
                // bounded cycle re-checks this Attack instead of waiting a turn.
                V2StateVersion.Bump();
                StrategicInterruptRegistry.Mark(player, ctx.TurnNumber,
                    StrategicInvalidationReason.Actor | StrategicInvalidationReason.Capability,
                    actorIds: new[] { primary.Id, support.Id });
            }

            result.ReachedGoal = handoffOk;
            result.StopReason = handoffOk
                ? ExecutionStopReason.ReachedGoal
                : ExecutionStopReason.MoveRejected;
        }
    }
}
