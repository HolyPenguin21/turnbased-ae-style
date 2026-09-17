namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Desire evaluators — cross-axis radar smoothing / placeholder scalars not specific to one axis (per-axis desire tunables live in AiConfigV2.Recon.cs / .Aggression.cs / .Development.cs).
    public static partial class AiConfigV2
    {
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
