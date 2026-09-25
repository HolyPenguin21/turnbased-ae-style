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
            HexCoord? next = SafeStepPathing.FindNextSafeStep(ctx.Map, army, destination);
            if (!next.HasValue)
            {
                r.Blocked = ExecutionStopReason.NoSafeStep;
                r.NeedsReplan = true;
                yield break;
            }

            int armyId = army.Id;
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player,
                AiDecision.Move(army, next.Value, reason, 0f, AiGroundMoveAuthority.Transit),
                ctx, trace);
            r.Army = AiV2Util.ResolveArmy(player, armyId);
            r.EndHex = r.Army != null ? r.Army.Hex : trace.EndHex;
            r.Moved = !r.EndHex.Equals(r.StartHex);
            r.BattleOccurred = trace.BattleOccurred;
            r.HexEventOccurred = trace.HexEventOccurred;
        }

        // THE flight step of a ground fight's air support (Raid AirSupport, Attack AirSupport):
        // one step of the wing's strike sortie toward `targetHex` — create the sortie on the first
        // step, strike the defenders on arrival under `policy`, then fly back and land at
        // `landingHex`. The lane validates its own target first; this never re-plans.
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

            if (sortie.Outbound && wing.Hex.Equals(targetHex))
            {
                AviationCombatPresenter presenter = ctx.HexSelection?.AviationCombatPresenter;
                if (presenter == null)
                {
                    result.StopReason = ExecutionStopReason.TargetInvalidated;
                    yield break;
                }
                var strike = new AviationCombatPresenter.AirStrikeResult();
                wing.PendingAirStrikePolicy = policy;
                yield return presenter.ResolveAirStrikeAtCurrentHex(wing, wing.Hex,
                    wing.PendingAirStrikePolicy.Value, strike);
                wing.PendingAirStrikePolicy = null;
                wing.LastAirStrikeHex = wing.Hex;
                wing.LastAirStrikeAttacked = strike.Attacked;
                result.CombatChanged |= strike.Attacked;
                result.AirSupportStrikeSucceeded |= strike.Attacked;
                sortie.Outbound = false;
                sortie.TargetHex = sortie.LandingHex;
                result.ActualActorArmyId = wing.Id;
                result.StopReason = ExecutionStopReason.StepCompleted;
                yield break;
            }

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
                wing.PendingAirStrikePolicy = policy;
            var trace = new AiMoveExecutionTrace();
            yield return AiTurnController.MoveArmyRoutine(player, move, ctx, trace);
            wing.PendingAirStrikePolicy = null;
            ArmyData after = AiV2Util.ResolveArmy(player, pm.MoverArmyId);
            HexCoord final = after?.Hex ?? trace.EndHex;
            if (!final.Equals(before))
                result.StepsMoved++;
            result.FinalHex = final;
            result.ActualActorArmyId = pm.MoverArmyId;
            if (after != null && after.LastAirStrikeHex.HasValue
                && after.LastAirStrikeHex.Value.Equals(targetHex)
                && after.LastAirStrikeAttacked)
            {
                result.CombatChanged = true;
                result.AirSupportStrikeSucceeded = true;
                sortie.Outbound = false;
                sortie.TargetHex = sortie.LandingHex;
            }
            if (!sortie.Outbound && final.Equals(sortie.LandingHex))
            {
                AirSortieRegistry.Remove(player, sortie);
                result.ReachedGoal = true;
                result.DurableRoleContinues = true;
            }
            result.StopReason = ExecutionStopReason.StepCompleted;
        }
    }
}
