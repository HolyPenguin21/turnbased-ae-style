namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Def/Eco/Dev demand consumers (DemandLayer minimal vertical slices) + Materialization garrison saturation modifiers — kept as one block, matching the original single SECTION banner.
    public static partial class AiConfigV2
    {
        // =======================================================================================
        //  DEF / ECO / DEV DEMAND CONSUMERS  (DemandLayer minimal vertical slices)
        //  Deterministic thresholds, not combat simulation. A DEF demand is raised ONLY when a
        //  Citadel/Base is under real threat AND its committed defence is below requirement (see
        //  the saturation gate in DemandLayer.DefenceDemands); ECO/DEV only when a genuine
        //  structural gap exists (a positive-payback extraction site, acute starvation, or no
        //  development facility). Free stock alone still does not create a demand.
        // =======================================================================================
        public const float defenceSeverityTrigger = 0.18f;    // AssetThreatSnapshot.Severity at/above this raises DEF
        public const float defencePerBodyPowerEstimate = 6f;   // ~power one garrison body adds, for sizing DesiredAmount
        public const int defenceMaxBodiesPerAsset = 2;         // cap on bodies requested for one asset in one turn
        public const int defenceMaxDemandsPerTurn = 2;         // anti-spam cap across all threatened assets
        public const float defenceReserveMargin = 1.15f;       // requiredDefence = threateningPower * this

        public const int economyMaxInfrastructureDemandsPerTurn = 1;
        public const int economyMaxExpansionBaseDemandsPerTurn = 1;
        public const int developmentMaxDemandsPerTurn = 1;     // one development-infrastructure demand at a time

        // Garrison saturation / composition-diversity modifier in MaterializationCandidateBuilder
        // .ScorePlanA — applied ONLY to a GarrisonCombatPower demand landing in an existing
        // garrison / defensive army. Deterministic; keeps one destination from absorbing every
        // defensive card once it already covers the threat.
        public const float garrisonSaturatedPenalty = 6f;          // destination already meets RequiredCapabilityPower
        public const float garrisonCrowdingPenaltyPerMember = 0.35f; // grows with the destination's current member count
        public const float garrisonDuplicateTypePenalty = 0.6f;    // the card's primary type already dominates the destination

    }
}
