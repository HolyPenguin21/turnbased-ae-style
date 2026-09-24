namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Eco/Dev demand consumers (DemandLayer minimal vertical slices) + Materialization garrison saturation modifiers — kept as one block, matching the original single SECTION banner.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  ECO / DEV DEMAND CONSUMERS  (DemandLayer minimal vertical slices)
        //  Deterministic thresholds, not combat simulation. ECO/DEV raise a demand only when a
        //  genuine structural gap exists (a positive-payback extraction site, acute starvation, or
        //  no development facility). Free stock alone does not create a demand. There is no
        //  DesireAxis.Defence: ActiveDefence work lives inside the Aggression axis.
        // =======================================================================================
        // Severity at/above which a threatened asset counts as genuinely endangered. A shared
        // THREAT fact (Economy escort safety reads it), not a Defence-lane number.
        public const float threatSeverityTrigger = 0.18f;
        // General combat-power-per-body normalizer (card/upgrade evaluation). Renamed from
        // defencePerBodyPowerEstimate: it was never a Defence-axis number.
        public const float combatPowerPerBodyEstimate = 6f;

        // Plain on/off switch for Base origination (0 disables it), not a count —
        // DemandLayer.Economy does not cap how many extraction/collector/Base demands it emits per
        // turn; that arbitration belongs to MissionAdmissionPolicy.Capacity (int.MaxValue for
        // Economy) and the downstream owners (ApBudgetLedger, MaterializationReservation,
        // ResourceAllocator). A per-family cap here would duplicate that admission one layer too
        // early and drop legal candidates the allocator would fund.
        public const int economyMaxExpansionBaseDemandsPerTurn = 1;

        // StrategicMaintenancePolicy's repair candidate — see its own comment. Calibrated against
        // AiDebug.log: at 1.5, minor damage (1 HP) outranked strong hero PlayMat picks and severe
        // damage beat every other tempo candidate by ~5x. 1.0 keeps "repair beats replaying a badly
        // damaged unit" while minor repairs stay competitive rather than automatically dominant.
        public const float repairPowerValueWeight = 1.0f;
        public const int developmentMaxDemandsPerTurn = 1;     // one development-infrastructure demand at a time

    }
}
