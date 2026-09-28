namespace Game.Ai.V2
{
    // ===========================================================================================
    //  TASKSCORE CALIBRATION TABLE — every lever of the world-task score, in one place.
    // ===========================================================================================
    //  Value = Benefit - Cost - Risk - Opportunity (TaskSlotCategory). One TaskScore point is one
    //  AP-equivalent (taskScorePerApEquivalent), so every Benefit cap below reads as "worth at most
    //  this many AP". Calibrating cross-family comparability means moving the BENEFIT caps; the
    //  cost side is the one price table and the risk side is shared. Per-task logs print the same
    //  decomposition: [AI][V2][TaskScore] ... | benefit (...) | cost (...) | risk | opportunity.
    //
    //  Rows marked "shape" are not caps: they say how a raw fact reaches its cap (where the ramp
    //  saturates). Only move them when the raw fact's range is wrong, not to reweight a family.
    // ===========================================================================================
    public static partial class AiConfigV2
    {
        // ---- BENEFIT · Economy -----------------------------------------------------------------
        // Resource income a site adds: min(cap, gain x weight) + shortage bonus.
        public const float taskScoreEconomicPhysicalBenefitMax = 10f;
        public const float taskScoreEconomicPhysicalBenefitWeight = 5f;   // shape: points per unit of income
        public const float taskScoreEconomicDeficitBonusMax = 3f;
        public const float taskScoreEconomicDeficitFullGain = 1f;         // shape: income that earns the full bonus
        // How fast the build pays its resources back.
        public const float taskScorePaybackMax = 8f;
        public const float taskScorePaybackHorizonTurns = 8f;             // shape: payback worth zero at this horizon
        // Base-only facts.
        public const float taskScoreAirfieldMax = 8f;
        public const float taskScoreGlobalCardEffectMax = 6f;
        // This Base would open a resource cluster no owned base reaches. Deliberately below the
        // income terms: it justifies a Base existing at all, never outranks an income-positive one.
        public const float taskScoreEconomicExpansionMax = 6f;
        public const float economyBaseExpansionClusterFullCount = 2f;     // shape: new cluster hexes for the full value

        // ---- BENEFIT · Recon -------------------------------------------------------------------
        public const float taskScoreInfoGainMax = 10f;
        public const float taskScoreStalenessMax = 8f;                    // value of refreshing old intel
        public const float taskScoreContactRelevanceMax = 10f;

        // ---- BENEFIT · Military (Raid / Attack / ActiveDefence) ---------------------------------
        // The expected resource/card reward of completing a Raid. Constant per eligible Raid,
        // never derived from defender power (that is WinChance's) and never on return legs.
        public const float RaidReward = 8f;
        public const float taskScoreWinChanceMax = 12f;
        // Attack's stronghold readiness (assembly x deployment, each in [0,1]); Base/Citadel only.
        public const float taskScoreAttackReadinessMax = 5.4f;
        // Damage an ActiveDefence intercept keeps off the threatened asset.
        public const float taskScorePreventedDamageMax = 12f;

        // ---- BENEFIT · Development (Research / Production) -------------------------------------
        // The need-weighted force an output adds. Below WinChance: amplifying a fight is never
        // worth more than winning it.
        public const float taskScoreForceAmplificationMax = 8f;
        public const float taskScoreForceAmplificationFullBodies = 2f;    // shape: bodies of force for the full value

        // ---- BENEFIT · Positional (any family) -------------------------------------------------
        // What the target is and whether it is where the enemy is — shared by Recon, ActiveDefence
        // and Attack, so they live here rather than under one family.
        public const float taskScoreStrategicRelevanceMax = 6f;
        public const float taskScoreThreatDirectionMax = 4f;
        public const float taskScoreFrontProgressMax = 8f;
        public const float taskScoreCorridorAlignmentMax = 8f;
        public const float taskScoreTerrainDefenseMax = 4f;
        // Signed span, not a positive maximum: +3 at home, 0 at the midpoint, -3 far away.
        public const float taskScoreProximityMax = 6f;
        public const float taskScoreProximityFullFalloffDistance = 9f;    // shape: hexes to the far end

        // ---- COST · the ONE price table (ActionPrice), in AP-equivalents -----------------------
        // 1 AP = 1 AP-equivalent for every use (card play, Challenge, activation now or on a later
        // turn of a march). 1 H/E/M/T unit = actionPriceResourceAp x Scarcity(type).
        // Calibration (2026-09-27): a turn grants 8-16 AP but only ~4-5 H/E/M/T in total, and
        // both are spent to ~0 by turn end, so a resource unit is worth at least one AP; the
        // scarcity factor moves it between 0.2 (nothing else wants it) and 1.8 (the hand and
        // deck are short of it). Earlier prices disagreed: cards 1/3 AP, world tasks 1/2 AP.
        public const float actionPriceResourceAp = 1f;
        public const float actionPriceScarcityMin = 0.2f;
        public const float actionPriceScarcityMax = 1.8f;
        // The two value currencies as scales of that one table. A TaskScore point is one
        // AP-equivalent; the card score keeps 0.15 per AP.
        public const float taskScorePerApEquivalent = 1f;
        public const float cardScorePerApEquivalent = 0.15f;

        // ---- RISK ------------------------------------------------------------------------------
        public const float taskScoreThreatRiskMax = 8f;                   // threat at / near the task hex
        public const float taskScoreCitadelThreatRiskMax = 8f;            // home threat, starting Citadel
        public const float taskScoreBaseThreatRiskMax = 0f;               // home threat, other Bases (off)
        public const float taskScoreDetectionRiskMax = 8f;
        // Acting on an aged sighting of a mobile target — the same span the staleness value uses.
        public const float taskScoreIntelAgePenaltyMax = taskScoreStalenessMax;

        // ---- OPPORTUNITY -----------------------------------------------------------------------
        // No lever: the value of the task an actor is taken from (MissionIntent.DisplacementValue)
        // is already in TaskScore points.

        // ---- LIFECYCLE POLICY on the TaskScore scale (not part of Value) ------------------------
        // Phase-A/Phase-B Play-vs-Hold urgency: TaskScore.Value 5..12 maps to no..full urgency.
        // Provisional; revalidate against measured Play-vs-Hold choices.
        public const float taskScoreUrgencyRampLo = 5f;
        public const float taskScoreUrgencyRampHi = 12f;
        // Economy actor loan: the build must beat the donor task's own value by this margin.
        public const float taskScoreEconomyLoanHysteresisThreshold = 1.6f;
        // Initial Base admission uses positive net TaskScore; switching a committed Base to a
        // challenger requires beating it by this margin.
        public const float economyBaseSwitchHysteresisThreshold = 10f;
    }
}
