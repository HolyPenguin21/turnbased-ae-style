namespace Game.Ai.V2
{
    public static partial class AiConfigV2
    {
        // Unified world-task scale. These are semantic-slot constants; no task-family multipliers
        // belong here. First-pass calibration is intentionally shared by Economy/Recon/Aggression.
        public const float taskScoreEconomicDeficitBonusMax = 3f;
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
        // Full signed span, not a positive-only maximum: +3 to -3 across 12 hexes.
        public const float taskScoreProximityMax = 6f;
        public const float taskScoreProximityFullFalloffDistance = 12f;
        public const float taskScoreTerrainDefenseMax = 4f;
        public const float taskScoreMilitaryTargetMax = 12f;
        public const float taskScoreWinChanceMax = 12f;
        // Base-only: this candidate would open a resource cluster not already reachable from an
        // owned base. Deliberately smaller than the physical-income terms (10/8) — it justifies a
        // Base existing at all when direct income is not yet needed, it must not outrank a site
        // that is ALSO immediately income-positive.
        public const float taskScoreEconomicExpansionMax = 6f;
        // Development-only: a CardUpgrade that improves the outcome against EVERY known threat
        // (EquipmentMatchupFit = 1) reaches the shared taskScoreUrgencyRampLo..Hi band (5..12) by
        // itself; one that turns no known fight carries no urgency, only its card score.
        public const float taskScoreUpgradeMatchupMax = 10f;
        // The expected resource/card reward of completing a Raid. Constant per eligible Raid,
        // never derived from defender power and never applied to Recon/Economy or Raid return legs.
        public const float RaidReward = 8f;

        // Physical costs use the same conversion for every world task. They intentionally do not
        // reuse Economy's historical 4/AP, 1.5/resource or 0.8/travel tuning.
        public const float taskScoreCardPriceApWeight = 2f;
        public const float taskScoreCardPriceResourceWeight = 1f;
        // A mover's once-per-turn re-activation fee (ArmyData.HasActivatedThisTurn resets every
        // turn — see TaskScoreEvaluator.DeliveryFromEta) is the SAME real AP whether it's Raid's
        // mover marching to a target or Economy's builder marching to a site: it's never itself a
        // played card's own AP cost, so it's priced at half the shared card-price rate. One shared
        // constant so both axes charge the identical real fact at the identical rate.
        public const float taskScoreReactivationApWeight = 1f;
        public const float taskScoreThreatRiskMax = 8f;
        public const float taskScoreDetectionRiskMax = 8f;

        // Phase-A/Phase-B Play-vs-Hold urgency is lifecycle policy, not an intrinsic TaskScore slot,
        // but every migrated demand family (world-map AND Development, since its UpgradeMatchupValue
        // migration) feeds it with TaskScore.Value. Keep one shared conversion band instead of
        // resurrecting per-axis multipliers. The 5..12 band is a provisional policy calibration, NOT
        // a conversion from a retired Economy deficit weight. Revalidate against measured
        // Play-vs-Hold choices.
        public const float taskScoreUrgencyRampLo = 5f;
        public const float taskScoreUrgencyRampHi = 12f;

        // Economy actor-loan policy consumes a migrated world-task value, so its policy offsets
        // must live on the same scale instead of reusing the retired Economy site score directly.
        // Continuation loss and hysteresis are provisional policy offsets to calibrate
        // from actual interruptions; they are not derived from the current deficit bonus (+3).
        // They are not intrinsic TaskScore slots. Legacy constants remain for unmigrated paths.
        public const float taskScoreEconomyLoanContinuationLoss = 4f;
        public const float taskScoreEconomyLoanHysteresisThreshold = 1.6f;
    }
}
