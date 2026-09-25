using System.Collections;
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
    }
}
