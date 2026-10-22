using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  STRATEGIC TEMPO BUDGET
    // ===========================================================================================
    //  ONE authoritative per-(player, turn) budget for every end-of-turn tempo action. All the
    //  hard caps are enforced against THIS, not against locals inside a single UseSurplus() call,
    //  so re-entering the arbiter (main Phase B, bounded reaction round, reaction follow-up,
    //  Housekeeping tempo re-run) can never buy more actions than the per-turn limit.
    //
    //    maxEndOfTurnTempoActionsPerTurn  <- TotalTempoActionsUsed   (every executed tempo action)
    //    maxTerminalDrawsPerTurn          <- DrawActionsUsed
    //    maxGenerationActionsPerTurn      <- GenerationAttemptsUsed   (Research/Production Challenges)
    //
    //  Turn-keyed: a stale entry from a previous turn reads as an empty budget, so no explicit
    //  reset is required.
    // ===========================================================================================
    internal sealed class StrategicTempoBudget
    {
        public int TotalTempoActionsUsed;
        public int DrawActionsUsed;
        public int GenerationAttemptsUsed;

        internal static void EndTurn(PlayerSetupData player, int turn)
        {
            ByPlayer.Remove(player, turn);
        }

        private static readonly TurnScopedByPlayer<StrategicTempoBudget> ByPlayer =
            new TurnScopedByPlayer<StrategicTempoBudget>(() => new StrategicTempoBudget());

        // Match-start reset (MatchStateInventory.ResetForNewMatch).
        internal static void ClearAll() => ByPlayer.Clear();

        // The live budget for this player/turn (created empty on first read of a new turn).
        public static StrategicTempoBudget For(PlayerSetupData player, int turn)
        {
            if (player == null)
                return new StrategicTempoBudget();
            return ByPlayer.GetOrReset(player, turn);
        }

        public bool TotalCapHit => TotalTempoActionsUsed >= AiConfigV2.maxEndOfTurnTempoActionsPerTurn;
        public bool DrawCapHit => DrawActionsUsed >= AiConfigV2.maxTerminalDrawsPerTurn;
        public bool GenerationCapHit => GenerationAttemptsUsed >= AiConfigV2.maxGenerationActionsPerTurn;

        public void RecordAction(bool draw, bool generationAttempt)
        {
            TotalTempoActionsUsed++;
            if (draw) DrawActionsUsed++;
            if (generationAttempt) GenerationAttemptsUsed++;
        }

        // Phase A (FulfillDemands) generation attempts also debit the shared generation count so
        // the tempo arbiter's remaining allowance is Phase-A-aware.
        public static void RecordGenerationAttempt(PlayerSetupData player, int turn)
        {
            if (player == null) return;
            For(player, turn).GenerationAttemptsUsed++;
        }

        public static int GenerationUsed(PlayerSetupData player, int turn) =>
            For(player, turn).GenerationAttemptsUsed;
    }
}
