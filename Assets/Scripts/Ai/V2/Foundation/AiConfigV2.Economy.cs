namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Economy standing.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  ECONOMY STANDING
        // =======================================================================================
        // How many turns the AI is allowed to take to afford one "typical" wanted card when
        // sizing DeckResourceNeed into a per-turn target income (variant (a): derive the absolute
        // floor from what the deck actually costs, not a fixed 2/2/2/2).
        public const float economyDeckNeedHorizonTurns = 8f;
        public const float economyHandPaydownHorizonTurns = 2f;
        public const float economyOperationalPaydownHorizonTurns = 1f;
        public const float economyDeckNeedDiscount = 0.35f;
        public const float economyRunwayHorizonTurns = 3f;
        public const float economyIncomeGapWeight = 0.30f;
        public const float economyRelativeGapWeight = 0.20f;
        public const float economyRunwayGapWeight = 0.25f;
        public const float economyOperationalPressureWeight = 0.15f;
        public const float economyStarvationWeight = 0.10f;
        public const float economyDesireMaxWeight = 0.65f;
        public const float economyDesireMeanWeight = 0.35f;
        public const float economyLatentMultiplier = 0.25f;
        public const float economySiteDeficitValue = 60f;
        public const float economySiteIncomeGainValue = 10f;
        // A protected extraction hex may beat a modest raw-yield advantage farther outside the
        // support radius; resource priority is still decided separately in DemandLayer.
        public const float economySiteBaseSynergyValue = 14f;
        public const float economySiteClusterValue = 6f;
        public const float economySiteTravelPenalty = 2.5f;
        public const float economySiteThreatPenalty = 18f;
        public const float economySiteHeroOpportunityPenalty = 0.35f;
        public const float economyBaseCapacityValue = 12f;
        public const float economyBaseHexYieldValue = 10f;
        public const float economyBaseClusterValue = 8f;
        public const float economyBaseNetworkExpansionValue = 6f;
        public const float economyBaseInfrastructurePressureValue = 10f;
        public const float economyBaseAirfieldValue = 8f;
        public const float economyBaseLogisticsValue = 5f;
        public const float economyBaseForwardProgressValue = 8f;
        public const float economyBaseCorridorAlignmentValue = 8f;
        public const float economyBaseGlobalEffectValue = 6f;
        // Symmetric to economyBaseHexYieldValue: a Base converting an already-productive owned
        // extraction site destroys real, currently-collected income, not potential yield.
        public const float economyBaseExtractionLossPenalty = 10f;
        public const float economyBuildResourcePenalty = 1.5f;
        public const float economyBuildApPenalty = 4f;
        public const float economyExtractionMaxPaybackTurns = 8f;
        public const float economyExtractionPaybackValue = 8f;
        public const float economyBaseDemandMinValue = 12f;
        public const int economyResourceClusterRadius = 2;
        public const int economyBaseFoundScanRadius = 3;
        public const int economyBaseMinSpacing = 3;
        public const float economyBaseUrgencyPerDeferredTurn = 12f;
        public const float economySameTurnCompletionBonus = 8f;
        public const float economyAdmissionCompletionCostWeight = 1f;
        public const float economyLoanHysteresisThreshold = 8f;
        public const float economyLoanContinuationLoss = 20f;
        // Blend weights for EconomicSecurity = w_abs*AbsFloor + w_rel*relTerm + w_bot*(1-Bottleneck).
        public const float economySecurityAbsWeight = 0.5f;
        public const float economySecurityRelWeight = 0.3f;
        public const float economySecurityBottleneckWeight = 0.2f;

    }
}
