namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Resource allocator.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  RESOURCE ALLOCATOR  (Strategy V2 build-order step 5)
        //  radar -> per-axis BudgetSlices of the shared pool -> many-to-many packing -> ordered
        //  TentativeAllocation. AP is the only live resource dimension; Energy / Human / Materials
        //  / Tech stay out of allocation until step 9.
        // =======================================================================================
        // AP held OUT of the whole turn's allocatable pool for the off-budget HousekeepingManager
        // (step 8). Renamed from allocatorManagerApReserve — same role, clearer owner. 0 for now —
        // reservation cleanup does not spend AP; raise this only when garrison-reorg with a real AP
        // cost lands in the HousekeepingManager stage. Strategic Manager (Phase A + Phase B) and
        // mission allocation must all leave this amount untouched.
        public const float housekeepingApReserve = 0f;
        // Hard bound for the step-6 pack -> provision -> re-pack loop. Step 5 only builds the
        // AllocationSession/retry seam and executes one pack per turn.
        public const int maxReallocIterations = 3;
        // Structural provisioning failure cooldown. Budget deferral never starts this cooldown.
        public const int allocatorRejectCooldownTurns = 2;
        // Shared tolerance for AP slice affordability / atomic draw / remainder comparisons.
        public const float allocatorSliceEpsilon = 0.01f;

    }
}
