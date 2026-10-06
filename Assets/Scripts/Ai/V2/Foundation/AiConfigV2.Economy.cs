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
        // Enemy-Citadel perimeter; also the zero-cost distance for soft own-home crowding.
        public const int economyBaseMinSpacing = 3;
        public const float economyBaseMaxDefenseModifier = 2f;
        // Base expansion value, cluster count and switch hysteresis: AiConfigV2.TaskScore.cs.

        public const float economyHomeHeroAssignmentApPenalty = 1.5f;

        public const int mobileCollectionMinMarginalYield = 1;
        // WorldAnalysis.KnownThreatsAffectingEconomyRoute: a known enemy (other player) army this
        // close to any route hex, or to the site itself, must be answered by an escort.
        public const int economyRouteThreatRadius = 1;
        public const int economySiteThreatRadius = 2;
        public const float mobileCollectionMinSafeRetreatMargin = 0f;
        public const int mobileCollectionMinCommitmentTurns = 1;
        public const float economySecurityAbsWeight = 0.5f;
        public const float economySecurityRelWeight = 0.3f;
        public const float economySecurityBottleneckWeight = 0.2f;
    }
}
