namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Development — desire sub-block (rawDev / production support / DevelopmentOpportunityEvaluator EV model).
    public static partial class AiConfigV2
    {
        // Development desire (radar). rawDev = surplusRamp * quality * gain, where
        //   quality = max(readyOfferingQuality, latentTargetPressure).
        // The facility+hero prerequisite is NO LONGER a desire gate — a latent appetite keeps the
        // axis warm so DemandLayer can STAGE the missing prerequisite (build the facility, move a
        // Research/Production hero onto it) the way Recon bootstraps a scout. `surplus` still gates
        // hard: a Challenge stakes real H/E/M/T with a probabilistic return. First-pass, tune vs log.
        public const float devDesireGain = 1.0f;
        public const float devSurplusRampLo = 0.15f;  // below this SurplusFraction -> ~no appetite
        public const float devSurplusRampHi = 0.60f;  // at/above -> full surplus term
        public const float devWeightSuccessChance = 0.6f;
        public const float devWeightTargets = 0.4f;
        public const float devTargetRampLo = 0f;
        public const float devTargetRampHi = 4f;       // 4+ upgradeable targets -> full targets term
        // Ceiling on the LATENT appetite (no live offering yet — facility unbuilt or unstaffed).
        // Held below a fully execution-ready setup so a facility+hero+offerings state still outranks
        // a bare "I have units worth improving" appetite. Only applies when DevPathViable.
        public const float devLatentPotential = 0.5f;

        // Production is an amplifier: Economy creates the spendable runway and Attack/Defence
        // provide the reason to mint. Optional Production stays weak below the readiness ramp;
        // a concrete high-value demand may lift it only to the emergency floor, never erase cost.
        public const float productionSupportReadinessLo = 0.25f;
        public const float productionSupportReadinessHi = 0.75f;
        public const float productionSupportMin = 0.30f;
        public const float productionSupportMax = 1.15f;
        public const float productionSupportEmergencyFloor = 0.70f;

        // Development OPPORTUNITY EV model (DevelopmentOpportunityEvaluator). Tuned against
        // AiDebug.log 2026-09-07: with the old values (apValue 3, margin 0.5, full alt-cost) a
        // p=0.77 offering scored EV = 0.77*G - ~8 - 3, so equipment upgrades (single-item G ~2..10)
        // could never clear the margin and Enumerate returned 0 objectives every turn.
        public const float devEvToBaseValue = 2.5f;    // EV (AiPower units) -> 0..100 BaseValue
        public const float devEvMargin = 0.05f;        // keep an opportunity only if EV exceeds this
        public const float devApValue = 1f;            // value of 1 AP, for the EV apCost term
        // Equipment persists across turns/battles, while Challenge + attach costs are paid once.
        // Applied only to the equipment power delta at Development's EV boundary; AiPower remains
        // a canonical current-force scalar and is not inflated globally.
        public const float devEquipmentPersistenceMultiplier = 3f;
        // The "I could play a fresh Unit with the same resources" alternative is a SOFT opportunity
        // cost, not a 1:1 trade (the unit is usually still played a later turn) — weight it down.
        public const float devAlternativeWeight = 0.5f;
        public const float devEquipGainFraction = 0.25f; // on-map unit FALLBACK when OriginatingCard is null: equipment adds ~this * UnitPower
        public const float devImportanceRaidMatch = 1.5f; // recipient sits on a hex an Aggression objective targets
        public const float devImportanceField = 1.0f;
        public const float devImportanceGarrison = 0.5f;
        public const float devImportanceHandCard = 0.9f;

    }
}
