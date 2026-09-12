namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Mission continuity.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  MISSION CONTINUITY  (Strategy V2 build-order step 7)
        //  Two separated concerns:
        //    Intent  — "I still want to finish THIS objective / keep tracking THIS army". Durable,
        //              outlives any single MissionProposal. Drives retarget hysteresis for every
        //              recon mission.
        //    Commitment — a funding POLICY on an Intent: "do not drop this from the budget over a
        //              small Radar wobble". Soft for a far Surveil that has actually started
        //              moving; Hard (raid) lands in step 9.
        //  Invariants held here: Intent != Proposal, Intent != Commitment, Progress != AP spent,
        //  post-execution observation != strategic policy.
        // =======================================================================================
        // Retarget hysteresis. A fresh candidate only displaces the hex an in-flight intent is
        // already heading for if it beats it by this margin on LocalAdmissionScore. Applied via
        // MissionAdmissionPolicy.AdmissionRank at BOTH pruning points — the MissionLayer beam and
        // the allocator's K-cut (step 7.1) — one knob, one formula, no separate incumbent bonus
        // (that stacked to ~1.5x and became a hard lock). Progress-aware margins are a later pass.
        public const float commitmentRetargetMargin = 0.20f;
        // Absolute emergency cap on how long a single intent may persist without completing —
        // safety net only. The real mechanism (deadline = first-executed ETA + slack) is a later
        // step; the ETA is unknown at intent creation because a Surveil proposal has no vantage yet.
        public const int commitmentMaxTurns = 6;
        // Consecutive turns an intent may make NO forward progress (no step, no stealth entry, no
        // productive stop) before it is retired and its key put on the allocator reject cooldown.
        public const int commitmentStallTurns = 2;

    }
}
