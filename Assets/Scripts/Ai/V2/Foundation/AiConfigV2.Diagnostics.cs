namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Diagnostics — frame log switch + mid-turn loop safety bounds.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  DIAGNOSTICS
        // =======================================================================================
        // AiFrameLog — readable per-block dump of the frozen turn frame (GAME STATE .. MISSION
        // CONTINUITY) into AiDebugLog. Mutable so it can be toggled from a console/inspector.
        // Disabled in the normal compact trace because WorldAnalysis already writes the canonical
        // turn snapshot. Enable for a focused human-readable dump; independent of the global
        // AiDebugLog.VerboseEnabled switch so this one explicit frame can be requested alone.
        public static bool frameLogEnabled = false;

        // =======================================================================================
        //  MID-TURN LOOP BOUNDS
        // =======================================================================================

        // Safety bounds, not policy knobs. One task step is at most one canonical state-mutating
        // gameplay operation (or one explicit no-op result) followed by settlement and observation.
        public const int maxMidTurnStepsPerTurn = 96;
        public const int maxMidTurnNoProgressCycles = 2;
        public const int maxMidTurnFallbackReturns = 1;

    }
}
