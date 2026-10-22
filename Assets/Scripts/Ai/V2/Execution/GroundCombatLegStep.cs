using System.Linq;
using System.Collections;
using System.Collections.Generic;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // What one non-capturing transit step physically did. Each lane executor maps it onto its own
    // ExecutionResult semantics (arrival, completion callbacks, which flags it publishes).
    internal sealed class GroundLegStepResult
    {
        // Set when no move was attempted: OutOfMovement, or NoSafeStep (then NeedsReplan).
        public ExecutionStopReason? Blocked;
        public bool NeedsReplan;
        public HexCoord StartHex;
        public HexCoord EndHex;
        public bool Moved;
        public bool BattleOccurred;
        public bool HexEventOccurred;
        // The mover re-resolved after the move; null when it was lost.
        public ArmyData Army;
    }

    // ===========================================================================================
    //  S4 — THE ONE NON-CAPTURING GROUND TRANSIT STEP of every ground-combat lifecycle leg:
    //  Raid Return / SupportReturn / RecoveryReturn, ActiveDefence Return, Attack SupportReturn /
    //  RecoveryReturn, and the Raid / Attack Reinforcement and Gather convoys. Exactly one safe step
    //  toward `destination` under Transit authority — never a capture, never a re-plan.
    // ===========================================================================================
    internal static class GroundCombatLegStep
    {
        // Keep the physical attack receipt even when a SingleUse wing has already been
        // removed from ArmyRegistry. Destruction by AA alone has no such receipt.
        internal static bool RecordAirStrikeReceipt(ArmyData wing, HexCoord target, ExecutionResult result)
        {
            bool attacked = wing?.LastAirStrikeHex.HasValue == true
                && wing.LastAirStrikeHex.Value.Equals(target) && wing.LastAirStrikeAttacked;
            result.CombatChanged |= attacked;
            result.AirSupportStrikeSucceeded |= attacked;
            return attacked;
        }

        // One step of a lifecycle leg walking to its already-fixed destination (a return home, a
        // recovery base) — never re-picks it. `arrived` reports the arrival, also when the mover
        // already stands there. A battle on the way is a combat change whatever the lane.
        internal static IEnumerator WalkTo(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, ArmyData army, HexCoord destination,
            string reason, System.Action arrived)
        {
            pm.ExecutionHex = destination;
            if (army.Hex.Equals(destination))
            {
                arrived();
                yield break;
            }
            // A temporarily blocked first step is a retry-next-turn, not a reason to drop the leg.
            var leg = new GroundLegStepResult();
            yield return Transit(player, ctx, army, destination, reason, leg, result);
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
            result.CombatChanged |= leg.BattleOccurred;
            // A battle or event that leaves the mover standing on its destination is an arrival:
            // the next pass finds it home and retires the leg (provisioning: TargetSatisfied), so a
            // completion owed on arrival (an ActiveDefence relief delivery) must happen now.
            if ((leg.BattleOccurred || leg.HexEventOccurred) && army != null && army.Hex.Equals(destination))
            {
                arrived();
                yield break;
            }
            if (StopAfterMove(result, army, moved, leg.BattleOccurred, leg.HexEventOccurred))
                yield break;
            if (army.Hex.Equals(destination))
            {
                arrived();
                yield break;
            }
            result.StopReason = army.CurrentMovement > 0
                ? ExecutionStopReason.StepCompleted
                : ExecutionStopReason.OutOfMovement;
        }

        // One transit step of a reinforcement convoy toward its rendezvous. The handoff is a
        // SEPARATE step: this never moves and hands off in the same one.
        internal static IEnumerator ConvoyStep(PlayerSetupData player, AiTurnContext ctx,
            ExecutionResult result, ArmyData support, HexCoord rendezvous, string reason)
        {
            var leg = new GroundLegStepResult();
            yield return Transit(player, ctx, support, rendezvous, reason, leg, result);
            if (leg.Blocked.HasValue)
            {
                result.StopReason = leg.Blocked.Value;
                result.NeedsReplan = leg.NeedsReplan;
                yield break;
            }

            bool moved = leg.Moved;
            if (moved) result.StepsMoved++;
            result.FinalHex = leg.EndHex;
            result.OperationStarted |= moved;
            result.CombatChanged |= leg.BattleOccurred;
            if (StopAfterMove(result, leg.Army, moved, leg.BattleOccurred, leg.HexEventOccurred))
                yield break;
            result.StopReason = ExecutionStopReason.StepCompleted;
        }

        // The settled facts of one ground move a lane routed itself (its own authority and target):
        // the re-resolved mover (null when lost), its position and step count, and the physical
        // start of the operation — a battle or an ordinary hex event starts it as surely as a step.
        internal static ArmyData RecordMove(PlayerSetupData player, ProvisionedMission pm,
            ExecutionResult result, HexCoord before, AiMoveExecutionTrace trace, out bool moved)
        {
            ArmyData army = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
            HexCoord endHex = army != null ? army.Hex : trace.EndHex;
            moved = !endHex.Equals(before);
            WorldDeltaLifecycle.RecordExecutionMutation(result, moved || trace.BattleOccurred
                || trace.EnteredStealthThisStep);
            if (moved)
                result.StepsMoved++;
            result.FinalHex = endHex;
            bool operationStarted = moved || trace.BattleOccurred || trace.HexEventOccurred;
            result.OperationStarted |= operationStarted;
            if (operationStarted)
                result.ActualActorArmyId = pm.MoverArmyId;
            return army;
        }

        // The shared end of a ground step after its move: a battle or a hex event ends the step for
        // a fresh observation, a lost mover asks for a re-plan, a move the rules refused stops it.
        // False when the mover stands, moved and may go on.
        internal static bool StopAfterMove(ExecutionResult result, ArmyData army, bool moved,
            bool battle, bool hexEvent)
        {
            if (battle || hexEvent)
            {
                result.NeedsReplan |= army == null;
                result.StopReason = army == null ? ExecutionStopReason.MoverLost
                    : battle ? ExecutionStopReason.BattleStarted : ExecutionStopReason.HexEventStarted;
                return true;
            }
            if (army == null)
            {
                // Lost en route: Continuity's next pass detects the loss and cleans up.
                result.StopForReplan(ExecutionStopReason.MoverLost);
                return true;
            }
            if (!moved)
            {
                result.StopReason = ExecutionStopReason.MoveRejected;
                return true;
            }
            return false;
        }

        internal static IEnumerator Transit(PlayerSetupData player, AiTurnContext ctx,
            ArmyData army, HexCoord destination, string reason, GroundLegStepResult r,
            ExecutionResult execution = null)
        {
            r.StartHex = army.Hex;
            r.EndHex = army.Hex;
            r.Army = army;
            if (army.CurrentMovement <= 0)
            {
                r.Blocked = ExecutionStopReason.OutOfMovement;
                yield break;
            }
            HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, destination,
                profile: SafeRouteProfile.Combat);
            if (!next.HasValue)
            {
                r.Blocked = ExecutionStopReason.NoSafeStep;
                r.NeedsReplan = true;
                yield break;
            }

            int armyId = army.Id;
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player,
                AiDecision.Move(army, next.Value, reason, 0f, AiGroundMoveAuthority.TransitCapture),
                ctx, trace);
            r.Army = AiV2Util.ResolveArmy(player, armyId);
            r.EndHex = r.Army != null ? r.Army.Hex : trace.EndHex;
            r.Moved = !r.EndHex.Equals(r.StartHex);
            r.BattleOccurred = trace.BattleOccurred;
            r.HexEventOccurred = trace.HexEventOccurred;
            WorldDeltaLifecycle.RecordExecutionMutation(execution, r.Moved || trace.BattleOccurred
                || trace.EnteredStealthThisStep);
        }

        // THE flight cycle of a ground-combat task's air support (Raid / Attack / ActiveDefence):
        //   approach -> strike -> hold or return -> (next turn) strike again -> ... -> land.
        // One call is one step. Away from the target it flies one leg toward `targetHex` (the
        // lane's honest current target hex — a moving ActiveDefence threat is followed); the
        // terminal step strikes on arrival under `policy`. At the target it strikes with every
        // aircraft that still has its strike this turn (free during the paid sortie). After a
        // strike it re-checks the live facts: targets still on the hex for this policy AND the wing
        // can end this turn here and still land in endurance (CanEndTurnHereAndRecover — "stop now,
        // come back later", never a route that spends this turn's remaining MP) -> HOLD for the
        // next turn's strike; otherwise the series is over and the same sortie flies home now
        // (generic aviation recovery). The lane validates its own task first; this never re-plans.
        // THE one place a ground-combat task's air support forms its wing from stored aircraft
        // (Attack / Raid / ActiveDefence). Provisioning bound an own airfield and the exact
        // aircraft GroundCombatAirSupport.StoredSupportGroup named (pm.AircraftRuntimeIds); the
        // shared AviationWingPreparation forms them into a wing (free — the launch is paid by the
        // first flight step) and the formed wing becomes the leg's actor (ActualActorArmyId), so
        // Continuity binds the real wing. A formed wing that never takes off lands back in storage
        // at turn end (AviationTurnLifecycle). Any other mover is returned unchanged.
        // How many adjacent steps a lane's executor may run for this mover this call: its current
        // movement, or — a stored launch not yet formed — the movement of the aircraft it launches.
        internal static int StepBudget(ArmyData mover, ProvisionedMission pm)
        {
            if (mover != null && AviationRules.IsAirfield(mover) && pm?.AircraftRuntimeIds != null)
                return mover.Members.Where(u => u != null && pm.AircraftRuntimeIds.Contains(u.RuntimeId))
                    .Select(AviationRules.EffectiveMoveCurrent).DefaultIfEmpty(0).Min();
            return mover?.CurrentMovement ?? 0;
        }

        internal static ArmyData MaterializeSupportWing(PlayerSetupData player, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, ArmyData mover)
        {
            if (mover == null || pm == null || !AviationRules.IsAirfield(mover))
                return mover;
            if (!GroundCombatAirSupport.IsStoredSource(player, mover, pm.AircraftRuntimeIds))
                return null;
            List<UnitData> aircraft = pm.AircraftRuntimeIds
                .Select(id => mover.Members.First(u => u != null && u.RuntimeId == id)).ToList();
            // No ownership view here: the formation never repurposes an existing army (new wing).
            if (!AviationWingPreparation.TryForm(player, ctx, mover, aircraft, null,
                    out ArmyData wing, out string how))
            {
                AiDebugLog.Write($"[AI][V2][AirSupport] stored launch from airfield #{mover.Id} failed - {how}");
                return null;
            }
            if (pm.RaidAirSupportArmyId == mover.Id)
                pm.RaidAirSupportArmyId = wing.Id;
            pm.MoverArmyId = wing.Id;
            pm.AircraftRuntimeIds = null;
            result.ActualActorArmyId = wing.Id;
            WorldDeltaLifecycle.RecordExecutionMutation(result, true);
            AiDebugLog.Write($"[AI][V2][AirSupport] formed wing #{wing.Id} from airfield #{mover.Id} "
                + $"({how}): [{string.Join(",", aircraft.Select(u => u.Name))}]");
            return wing;
        }

        internal static IEnumerator AirStrikeSortie(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, ArmyData wing,
            HexCoord targetHex, HexCoord landingHex, AirStrikePolicy policy, string label,
            string flyReason)
        {
            AirSortie sortie = AirSortieRegistry.ForArmy(player, wing);
            bool starting = sortie == null;
            bool reconHandoff = sortie?.Kind == AirSortieKind.Recon
                && policy.Kind == AirStrikePolicyKind.DefenceSupport
                && GroundCombatAirSupport.CanYieldReconSortie(player, wing, ctx.TurnNumber);
            if (reconHandoff)
            {
                // Provisioning proved the exact defence service and landing. Change the
                // existing physical record atomically; retain member SortieLaunchPaid flags.
                sortie.Kind = AirSortieKind.Strike;
                sortie.TargetHex = targetHex;
                sortie.LandingHex = landingHex;
                sortie.Outbound = true;
                sortie.HeldTurn = null;
                WorldDeltaLifecycle.RecordExecutionMutation(result, true);
            }
            if (sortie == null)
            {
                sortie = new AirSortie
                {
                    Kind = AirSortieKind.Strike, Army = wing,
                    TargetHex = targetHex,
                    LandingHex = landingHex,
                    Outbound = true,
                };
                AirSortieRegistry.Add(player, sortie);
            }
            if (reconHandoff || starting)
            {
                // A funded combat sortie now owns the wing. Drop Recon's coverage/heading
                // bookkeeping only here, after handoff, never during read-only nomination.
                ReconAirSortieRegistry.Retire(player, wing.Id);
                ReconPatrolStateRegistry.Retire(player, wing.Id, "combat support handoff");
            }
            if (sortie.Kind != AirSortieKind.Strike)
            {
                result.StopReason = ExecutionStopReason.TargetInvalidated;
                yield break;
            }
            sortie.StrikePolicy = policy;
            if (GroundCombatAirSupport.TargetKnownEmpty(player, ctx.TurnNumber, targetHex, policy))
            {
                GroundCombatAirSupport.SendHome(player, sortie, "nothing left to strike");
                yield return FlyHome(player, root, ctx, pm, result, sortie);
                yield break;
            }
            sortie.TargetHex = targetHex;
            sortie.Outbound = true;
            result.ActualActorArmyId = wing.Id;

            bool atTarget = wing.Hex.Equals(targetHex);
            bool attacked = false;
            if (atTarget)
            {
                AviationCombatPresenter presenter = ctx.HexSelection?.AviationCombatPresenter;
                if (presenter != null && AviationActions.CanStrikeAtCurrentHex(wing, policy))
                {
                    var strike = new AviationCombatPresenter.AirStrikeResult();
                    yield return AviationActions.ResolveStationaryStrike(presenter, wing, policy, strike);
                    wing.LastAirStrikeHex = wing.Hex;
                    wing.LastAirStrikeAttacked = strike.Attacked;
                    attacked = strike.Attacked;
                    result.CombatChanged |= strike.Attacked;
                    result.AirSupportStrikeSucceeded |= strike.Attacked;
                    WorldDeltaLifecycle.RecordExecutionMutation(result, strike.Attacked);
                }
            }
            else
            {
                AiDecision move = AiAirSortiePlanner.ContinueSortie(player, root, ctx, sortie,
                    label, flyReason, 0f);
                if (move == null)
                {
                    result.StopReason = wing.CurrentMovement <= 0
                        ? ExecutionStopReason.OutOfMovement : ExecutionStopReason.NoSafeStep;
                    yield break;
                }
                HexCoord before = wing.Hex;
                bool enteringTarget = sortie.Outbound && move.TargetHex.Equals(targetHex);
                if (enteringTarget)
                {
                    wing.LastAirStrikeHex = null;
                    wing.LastAirStrikeAttacked = false;
                    move.AirStrikePolicy = policy;
                }
                var trace = new AiMoveExecutionTrace();
                yield return AiTurnController.MoveArmyRoutine(player, move, ctx, trace);
                ArmyData moved = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
                if (moved != null)
                    moved.PendingAirStrikePolicy = null;
                HexCoord final = moved?.Hex ?? trace.EndHex;
                WorldDeltaLifecycle.RecordExecutionMutation(result, !final.Equals(before));
                if (!final.Equals(before))
                    result.StepsMoved++;
                result.FinalHex = final;
                attacked = enteringTarget && final.Equals(targetHex)
                    && RecordAirStrikeReceipt(moved ?? wing, targetHex, result);
                if (moved == null || !AviationRules.IsValidAirArmy(moved))
                {
                    AirSortieRegistry.Remove(player, sortie);
                    result.StopReason = ExecutionStopReason.MoverLost;
                    yield break;
                }
                wing = moved;
                // ContinueSortie turns the sortie home when no safe round trip is left.
                if (sortie.Kind != AirSortieKind.Strike || !sortie.Outbound)
                {
                    GroundCombatAirSupport.SendHome(player, sortie, "lost its safe round trip");
                    yield return FlyHome(player, root, ctx, pm, result, sortie);
                    yield break;
                }
                if (!final.Equals(targetHex))
                {
                    result.StopReason = wing.CurrentMovement > 0
                        ? ExecutionStopReason.StepCompleted : ExecutionStopReason.OutOfMovement;
                    yield break;
                }
            }
            // Over the target: keep striking on later turns, or end the series and go home.
            bool targetsRemain = AviationCombatPresenter.FindAirStrikeTargetsAt(wing.Hex, player,
                policy.ExactTargetArmyId).Sum(t => t.Members.Count) > policy.MinimumSurvivors;
            bool canHold = targetsRemain
                && AiAirSortiePlanner.CanEndTurnHereAndRecover(wing, ctx.Map, player);
            if (canHold)
            {
                sortie.HeldTurn = ctx.TurnNumber;
                result.FinalHex = wing.Hex;
                result.DurableRoleContinues = true;
                result.StopReason = ExecutionStopReason.OutOfMovement;
                AiDebugLog.Write($"[AI][V2][AirSupport] {label} wing #{wing.Id} holds over "
                    + $"({wing.Hex.Q},{wing.Hex.R}) attacked={(attacked ? 1 : 0)} "
                    + $"safeEnds={AviationRange.SafeUnlandedEndsRemaining(wing)} - strikes again next turn");
                yield break;
            }
            GroundCombatAirSupport.SendHome(player, sortie, targetsRemain
                ? "must return to stay inside endurance" : "target hex has nothing left to strike");
            yield return FlyHome(player, root, ctx, pm, result, sortie);
        }

        // The series is over: the remaining flight is the generic aviation recovery obligation,
        // allowed to spend the wing's remaining MP this very turn (essential for
        // TurnsWithoutRefuel = 0). The task releases the wing (its sortie is no longer Strike).
        private static IEnumerator FlyHome(PlayerSetupData player, PlayerRoot root, AiTurnContext ctx,
            ProvisionedMission pm, ExecutionResult result, AirSortie sortie)
        {
            ArmyData wing = sortie.Army;
            if (wing != null)
                yield return AviationRebaseExecutor.ExecuteContinuation(
                    player, root, ctx, wing, _ => { }, result: result);
            ArmyData after = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
            if (after != null)
                result.FinalHex = after.Hex;
            else
            {
                result.FinalHex = wing != null ? wing.Hex : result.FinalHex;
                AirSortieRegistry.Remove(player, sortie);
                result.StopReason = ExecutionStopReason.MoverLost;
                yield break;
            }
            result.DurableRoleContinues = true;
            result.StopReason = ExecutionStopReason.StepCompleted;
        }
    }
}


