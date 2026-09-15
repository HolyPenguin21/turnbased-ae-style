namespace Game.Ai.V2
{
    public static partial class AiConfigV2
    {
        // Unified world-task scale. These are semantic-slot constants; no task-family multipliers
        // belong here. First-pass calibration is intentionally shared by Economy/Recon/Aggression.
        public const float taskScoreEconomicDeficitBonusMax = 12f;
        public const float taskScoreEconomicPhysicalBenefitMax = 10f;
        public const float taskScoreEconomicPhysicalBenefitWeight = 5f;
        public const float taskScorePaybackMax = 8f;
        public const float taskScorePaybackHorizonTurns = 8f;

        public const float taskScoreAirfieldMax = 8f;
        public const float taskScoreGlobalCardEffectMax = 6f;
        public const float taskScoreInfoGainMax = 10f;
        public const float taskScoreStalenessMax = 8f;
        public const float taskScoreStrategicRelevanceMax = 6f;
        public const float taskScoreThreatDirectionMax = 4f;
        public const float taskScoreContactRelevanceMax = 10f;
        public const float taskScoreFrontProgressMax = 8f;
        public const float taskScoreCorridorAlignmentMax = 8f;
        public const float taskScoreProximityMax = 6f;
        public const float taskScoreProximityFullFalloffDistance = 12f;
        public const float taskScoreTerrainDefenseMax = 4f;
        public const float taskScoreMilitaryTargetMax = 12f;
        public const float taskScoreWinChanceMax = 12f;

        // Physical costs use the same conversion for every world task. They intentionally do not
        // reuse Economy's historical 4/AP, 1.5/resource or 0.8/travel tuning.
        public const float taskScoreCardPriceApWeight = 2f;
        public const float taskScoreCardPriceResourceWeight = 1f;
        public const float taskScoreDeliveryApWeight = 1f;
        public const float taskScoreTravelWeight = 0.5f;
        public const float taskScoreThreatRiskMax = 8f;
        public const float taskScoreDetectionRiskMax = 8f;
    }
}
