namespace Game.Ai.V2
{
    public static partial class AiConfigV2
    {
        // Unified world-task scale. These are semantic-slot constants; no task-family multipliers
        // belong here. First-pass calibration is intentionally shared by Economy/Recon/Aggression.
        public const float taskScoreEconomicDeficitBonusMax = 12f;
        public const float taskScoreEconomicDeficitFullGain = 1f;
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
        public const float taskScoreDeliveryApWeight = taskScoreCardPriceApWeight; // One real AP = one price for card or delivery.
        public const float taskScoreTravelWeight = 0.5f;
        public const float taskScoreThreatRiskMax = 8f;
        public const float taskScoreDetectionRiskMax = 8f;

        // Phase-A/Phase-B Play-vs-Hold urgency is lifecycle policy, not an intrinsic TaskScore slot,
        // but migrated world-map demands feed it with TaskScore.Value. Keep one shared conversion
        // band for every migrated world family instead of resurrecting Recon/Raid/Economy-specific
        // multipliers. The legacy urgency band was 25..60; the already-established common migration
        // bridge maps the old 60-point Economy deficit term to canonical 12 (x0.2), hence 5..12.
        // Development remains on its pre-migration 25..60 band until that non-world family migrates.
        public const float taskScoreUrgencyRampLo = 5f;
        public const float taskScoreUrgencyRampHi = 12f;

        // Economy actor-loan policy consumes a migrated world-task value, so its policy offsets
        // must live on the same scale instead of reusing the retired Economy site score directly.
        // The old extraction score's dominant deficit term topped out at 60, while the canonical
        // TaskScore deficit bonus tops out at 12: preserving the former relative policy therefore
        // maps continuation loss 20 -> 4 and hysteresis 8 -> 1.6. These are policy offsets, not
        // intrinsic TaskScore slots, and legacy constants remain available to non-migrated paths.
        public const float taskScoreEconomyLoanContinuationLoss = 4f;
        public const float taskScoreEconomyLoanHysteresisThreshold = 1.6f;
    }
}
