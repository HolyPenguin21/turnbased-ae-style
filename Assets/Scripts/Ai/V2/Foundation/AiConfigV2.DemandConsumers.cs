namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Def/Eco/Dev demand consumers (DemandLayer minimal vertical slices) + Materialization garrison saturation modifiers — kept as one block, matching the original single SECTION banner.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  ECO / DEV DEMAND CONSUMERS  (DemandLayer minimal vertical slices)
        //  Deterministic thresholds, not combat simulation. ECO/DEV raise a demand only when a
        //  genuine structural gap exists (a positive-payback extraction site, acute starvation, or
        //  no development facility). Free stock alone still does not create a demand.
        //  The Defence demand stubs (GarrisonCombatPower + BaselineForceReadiness) were removed
        //  with the AGG-RAID Defence cleanup; DesireAxis.Defence is a reserved zero-weight axis.
        // =======================================================================================
        // Severity at/above which a threatened asset counts as genuinely endangered. The Defence
        // DEMAND stubs that used to consume this are gone; it survives as a shared THREAT fact
        // (Economy escort safety reads it). Renamed from defenceSeverityTrigger — it was never a
        // Defence-lane number, just a generic threat-severity gate Defence happened to consume too.
        public const float threatSeverityTrigger = 0.18f;
        // General combat-power-per-body normalizer (card/upgrade evaluation). Renamed from
        // defencePerBodyPowerEstimate: it was never a Defence-lane number.
        public const float combatPowerPerBodyEstimate = 6f;

        public const int economyMaxInfrastructureDemandsPerTurn = 1;
        public const int economyMaxExpansionBaseDemandsPerTurn = 1;
        public const int developmentMaxDemandsPerTurn = 1;     // one development-infrastructure demand at a time

    }
}
