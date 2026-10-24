namespace Game.Ai.V2
{
    // Flat tunables for the Strategy V2 pipeline (Game.Ai.V2), kept out of the 1600-line V1
    // AiConfig so the two never have to be read together and a V2 constant is never mistaken for
    // a shipping one. Everything here feeds the WorldAnalysis scan (build-order step 2) and the
    // evaluators/allocator built on top of it. All values are first-pass and meant to be tuned
    // against real AiDebug.log runs, exactly like V1's own tuning passes.
    //
    // NOTHING here changes V1 behaviour — the whole namespace is dead code until
    // AiConfig.aiStrategyV2Enabled is set.
    //
    // File-split (mechanical, no behaviour change): per-layer tunables live in the sibling
    // AiConfigV2.*.cs partial files, one per topic — see Docs/ai-v2-file-split-refactor-tasks.md
    // Task 2. This file keeps the pipeline-wide ones: diagnostics (frameLogEnabled, the one
    // non-const field), mid-turn loop bounds, the allocator and cross-axis desire smoothing.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  DIAGNOSTICS
        // =======================================================================================
        // AiFrameLog — readable per-block dump of the frozen turn frame (GAME STATE .. MISSION
        // CONTINUITY) into AiDebugLog. Mutable so it can be toggled from a console/inspector.
        // Disabled in the normal compact trace because WorldAnalysis already writes the canonical
        // turn snapshot. Enable for a focused human-readable dump.
        public static bool frameLogEnabled = false;

        // =======================================================================================
        //  MID-TURN LOOP BOUNDS
        // =======================================================================================

        // Safety bounds, not policy knobs. One task step is at most one canonical state-mutating
        // gameplay operation (or one explicit no-op result) followed by settlement and observation.
        public const int maxMidTurnStepsPerTurn = 96;
        public const int maxMidTurnNoProgressCycles = 2;

        // =======================================================================================
        //  RESOURCE ALLOCATOR  (Strategy V2 build-order step 5)
        //  radar -> per-axis BudgetSlices of the shared pool -> many-to-many packing -> ordered
        //  TentativeAllocation. AP is the only live resource dimension; Energy / Human / Materials
        //  / Tech stay out of allocation until step 9.
        // Hard bound for the step-6 pack -> provision -> re-pack loop. Step 5 only builds the
        // AllocationSession/retry seam and executes one pack per turn.
        public const int maxReallocIterations = 3;
        // Structural provisioning failure cooldown. Budget deferral never starts this cooldown.
        public const int allocatorRejectCooldownTurns = 2;
        // Shared tolerance for AP slice affordability / atomic draw / remainder comparisons.
        public const float allocatorSliceEpsilon = 0.01f;

        // =======================================================================================
        //  DESIRE EVALUATORS  (Strategy V2 build-order step 3)
        //  One response-curve evaluator per axis; each raw desire is Σ weighted contributions in
        //  [0..1], normalised to the simplex exactly once in Radar.Normalize. All four axes
        //  (Recon / Aggression / Economy / Development) have real evaluators.
        // =======================================================================================

        // ---- smoothing / placeholders / out-of-simplex scalars ---------------------------
        public const float desireSmoothing = 0.40f;          // weight on the previous smoothed value

        // Radar model #2 (proportional) — see RadarValueScale in DesireEvaluators.cs.
        // EffectiveValue = BaseValue * axisCount * weight. No floor, no ceiling: this is a pure
        // normalisation constant (axisCount), not a tunable knob, so there is nothing to configure
        // here any more.
    }
}
