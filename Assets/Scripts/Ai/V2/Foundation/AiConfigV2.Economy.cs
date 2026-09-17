namespace Game.Ai.V2
{
    // Economy policy and standing. World-task value conversion lives in TaskScoreEvaluator.
    public static partial class AiConfigV2
    {
        // The spending horizon and measured shortage of the actual hand/deck determine demand.
        public const float economyDeckNeedHorizonTurns = 8f;
        public const float economyHandPaydownHorizonTurns = 2f;
        public const float economyOperationalPaydownHorizonTurns = 1f;
        public const float economyDeckNeedDiscount = 0.35f;
        public const float economyRunwayHorizonTurns = 3f;
        public const float economyBottleneckReferenceTurns = 10f;
        public const float economyIncomeGapWeight = 0.30f;
        public const float economyRelativeGapWeight = 0.20f;
        public const float economyRunwayGapWeight = 0.25f;
        public const float economyOperationalPressureWeight = 0.15f;
        public const float economyStarvationWeight = 0.10f;
        public const float economyDesireMaxWeight = 0.65f;
        public const float economyDesireMeanWeight = 0.35f;
        public const float economyLatentMultiplier = 0.25f;

        // Structural/legal Base and Extraction rules, not legacy score contributions.
        public const float economyExtractionMaxPaybackTurns = 8f;
        public const int economyBaseFoundScanRadius = 3;
        public const int economyBaseMinSpacing = 3;
        public const float economyBaseMaxDefenseModifier = 2f;

        // Initial Base admission uses positive net TaskScore; commitment switching keeps hysteresis.
        public const float economyBaseSwitchHysteresisThreshold = 10f;
        public const float economySameTurnCompletionBonus = 8f;
        public const float economyAdmissionCompletionCostWeight = 1f;

        // Borrowing an already committed actor is a real loss of continuity.
        public const float economyLoanHysteresisThreshold = taskScoreEconomyLoanHysteresisThreshold;
        public const float economyLoanContinuationLoss = taskScoreEconomyLoanContinuationLoss;
        public const float economyHomeHeroAssignmentApPenalty = 1.5f;

        public const float economySecurityAbsWeight = 0.5f;
        public const float economySecurityRelWeight = 0.3f;
        public const float economySecurityBottleneckWeight = 0.2f;
    }
}
