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
    // File-split (mechanical, no behaviour change): the class body lives in the sibling
    // AiConfigV2.*.cs partial files, one per topic — see Docs/ai-v2-file-split-refactor-tasks.md
    // Task 2. This file keeps only the class declaration; frameLogEnabled (the one non-const
    // field) lives in AiConfigV2.Diagnostics.cs.
    public static partial class AiConfigV2
    {
    }
}
