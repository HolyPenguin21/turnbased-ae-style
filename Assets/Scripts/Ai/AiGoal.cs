using Game.HexGrid;

namespace Game.Ai
{
    // See the AI architecture design doc — a global goal is the top of the
    // goal -> task chain -> army model. Only Stage 1 (scoring/picking) exists so far; nothing
    // executes a goal yet.
    public enum AiGoalKind
    {
        DefendBorder,
        ExpandEconomy,
        DestroyEnemyCitadel,
        HuntExposedHero,
    }

    // One scored candidate for what an AI player could spend this turn on — produced by
    // AiGoalScorer. Description is a short human-readable reason, for debugging/logging only
    // (see GameTurnController) — not shown to the player anywhere yet.
    public class AiGoal
    {
        public AiGoalKind Kind;
        public float Score;
        public HexCoord? TargetHex;
        public string Description;
    }
}
