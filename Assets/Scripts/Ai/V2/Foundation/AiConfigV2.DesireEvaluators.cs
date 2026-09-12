namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Desire evaluators — cross-axis radar smoothing / placeholder scalars not specific to one axis (per-axis desire tunables live in AiConfigV2.Recon.cs / .Aggression.cs / .Development.cs).
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  DESIRE EVALUATORS  (Strategy V2 build-order step 3)
        //  One response-curve evaluator per axis; each raw desire is Σ weighted contributions in
        //  [0..1], normalised to the simplex exactly once in Radar.Normalize. Recon + Aggression
        //  are the two live axes; Defence/Economy/Development return a flat placeholder until
        //  their own evaluators land in later build-order steps.
        // =======================================================================================

        // ---- smoothing / placeholders / out-of-simplex scalars ---------------------------
        public const float desireSmoothing = 0.40f;          // weight on the previous smoothed value
        // (removed) desirePlaceholderInactive — DEF/ECO/DEV raw desire is now honestly 0 until each
        // axis gets a real evaluator; a placeholder radar weight would mis-scale EffectiveValue.

        // Radar model #1a — the radar's ONLY effect on decisions is scaling objective/mission VALUE:
        //   EffectiveValue = BaseValue * (floor + (1-floor) * min(1, weight * axisCount))
        // A cold axis (weight -> 0) scales down to this floor; an axis at or above the even split
        // (weight >= 1/axisCount) scales at 1.0. First-pass value — tune against real AiDebug.log.
        public const float radarScaleFloor = 0.35f;
    }
}
