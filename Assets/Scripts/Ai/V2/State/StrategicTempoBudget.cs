using System.Collections.Generic;
using Game.Players;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  STRATEGIC TEMPO BUDGET  (AI-MGR-02 §P0.4)
    // ===========================================================================================
    //  ONE authoritative per-(player, turn) budget for every end-of-turn tempo action. All the
    //  hard caps are enforced against THIS, not against locals inside a single UseSurplus() call,
    //  so re-entering the arbiter (main Phase B, bounded reaction round, reaction follow-up,
    //  Housekeeping tempo re-run) can never buy more actions than the per-turn limit.
    //
    //    maxEndOfTurnTempoActionsPerTurn  <- TotalTempoActionsUsed   (every executed tempo action)
    //    maxSurplusActionsPerTurn         <- SurplusCardActionsUsed   (materialization + non-combat plays)
    //    maxTerminalDrawsPerTurn          <- DrawActionsUsed
    //    maxGenerationActionsPerTurn      <- GenerationAttemptsUsed   (Research/Production Challenges)
    //
    //  Turn-keyed: a stale entry from a previous turn reads as an empty budget, so no explicit
    //  reset is required.
    // ===========================================================================================
    internal sealed class StrategicTempoBudget
    {
        public int Turn = -1;
        public int TotalTempoActionsUsed;
        public int SurplusCardActionsUsed;
        public int DrawActionsUsed;
        public int GenerationAttemptsUsed;

        private static readonly Dictionary<PlayerSetupData, StrategicTempoBudget> ByPlayer =
            new Dictionary<PlayerSetupData, StrategicTempoBudget>();

        // The live budget for this player/turn (created empty on first read of a new turn).
        public static StrategicTempoBudget For(PlayerSetupData player, int turn)
        {
            if (player == null)
                return new StrategicTempoBudget { Turn = turn };
            if (!ByPlayer.TryGetValue(player, out StrategicTempoBudget b) || b.Turn != turn)
                ByPlayer[player] = b = new StrategicTempoBudget { Turn = turn };
            return b;
        }

        public bool TotalCapHit => TotalTempoActionsUsed >= AiConfigV2.maxEndOfTurnTempoActionsPerTurn;
        public bool CardCapHit => SurplusCardActionsUsed >= AiConfigV2.maxSurplusActionsPerTurn;
        public bool DrawCapHit => DrawActionsUsed >= AiConfigV2.maxTerminalDrawsPerTurn;
        public bool GenerationCapHit => GenerationAttemptsUsed >= AiConfigV2.maxGenerationActionsPerTurn;

        public void RecordAction(bool card, bool draw, bool generationAttempt)
        {
            TotalTempoActionsUsed++;
            if (card) SurplusCardActionsUsed++;
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
