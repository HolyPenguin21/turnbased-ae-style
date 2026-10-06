using System.Linq;
using System.Collections;
using Game.Aviation;
using Game.HexGrid;
using Game.Map;
using Game.Players;

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
        internal static IEnumerator Transit(PlayerSetupData player, AiTurnContext ctx,
            ArmyData army, HexCoord destination, string reason, GroundLegStepResult r)
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
        internal static IEnumerator AirStrikeSortie(PlayerSetupData player, PlayerRoot root,
            AiTurnContext ctx, ProvisionedMission pm, ExecutionResult result, ArmyData wing,
            HexCoord targetHex, HexCoord landingHex, AirStrikePolicy policy, string label,
            string flyReason)
        {
            AirSortie sortie = AirSortieRegistry.ForArmy(player, wing);
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
                if (!final.Equals(before))
                    result.StepsMoved++;
                result.FinalHex = final;
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
                attacked = wing.LastAirStrikeHex.HasValue && wing.LastAirStrikeHex.Value.Equals(targetHex)
                    && wing.LastAirStrikeAttacked;
                result.CombatChanged |= attacked;
                result.AirSupportStrikeSucceeded |= attacked;
            }
            if (attacked)
                WorldDeltaLifecycle.CommitMutation();

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
                    + $"safeEnds={AviationRange.SafeUnlandedEndsRemaining(wing)} — strikes again next turn");
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
                yield return AviationRebasePlanner.ExecuteContinuation(
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


