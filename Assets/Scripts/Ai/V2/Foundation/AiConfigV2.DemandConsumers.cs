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
        //  no development facility). Free stock alone still does not create a demand.
        //  The old Defence demand stubs (GarrisonCombatPower + BaselineForceReadiness) were removed
        //  with the AGG-RAID Defence cleanup; there is no DesireAxis.Defence any more — Active
        //  Defence work, when built, lives inside the Aggression axis.
        // =======================================================================================
        // Severity at/above which a threatened asset counts as genuinely endangered. The old
        // Defence-axis DEMAND stubs that used to consume this are gone; it survives as a shared
        // THREAT fact (Economy escort safety reads it). Renamed from defenceSeverityTrigger — it
        // was never a Defence-lane number, just a generic threat-severity gate Defence happened to
        // consume too.
        public const float threatSeverityTrigger = 0.18f;
        // General combat-power-per-body normalizer (card/upgrade evaluation). Renamed from
        // defencePerBodyPowerEstimate: it was never a Defence-axis number.
        public const float combatPowerPerBodyEstimate = 6f;

        // Plain on/off switch for Base origination (0 disables it), not a count — DemandLayer.Economy
        // no longer caps how many extraction/collector/Base demands it emits per turn; that arbitration
        // belongs to MissionAdmissionPolicy.Capacity (already int.MaxValue for Economy) and the real
        // downstream owners (AxisBudgetLedger, MaterializationReservation, ResourceAllocator). The old
        // per-family `.Take(1)` here duplicated that admission one layer too early and silently dropped
        // legal candidates the allocator would have funded (2026-09-21 fix).
        public const int economyMaxExpansionBaseDemandsPerTurn = 1;

        // StrategicMaintenancePolicy's repair candidate (restored V1 RepairUnit task) — see its
        // own comment. Calibrated 2026-09-21 against AiDebug.log: at 1.5, minor damage (1 HP)
        // already outranked strong hero PlayMat picks, and severe damage beat every other tempo
        // candidate in the log by ~5x. Lowered to keep "repair beats replaying a badly damaged
        // unit" while minor repairs stay competitive rather than automatically dominant.
        public const float repairPowerValueWeight = 1.0f;
        public const int developmentMaxDemandsPerTurn = 1;     // one development-infrastructure demand at a time

    }
}
