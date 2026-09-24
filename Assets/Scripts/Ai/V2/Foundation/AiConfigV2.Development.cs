namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Development — desire sub-block (rawDev / investment window / DevelopmentOpportunityEvaluator EV model).
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

        // Investment window (DevelopmentInvestmentGate). Laboratory / Factory are a late resource
        // sink that must not compete with the main deck: a spend is allowed only when EVERY resource
        // it consumes has kept its headroom (spendable / 2x income) at or above the threshold for
        // this many consecutive turns. Resources it does not consume never matter. The threshold is
        // the radar's own "full surplus" point, so the radar and the gate describe the same economy.
        public const float devInvestmentSurplusThreshold = devSurplusRampHi;
        public const int devInvestmentSurplusTurns = 2;

        // Development OPPORTUNITY EV model (DevelopmentOpportunityEvaluator). One investment EV per
        // opportunity: expected output value minus Challenge and prerequisite costs.
        public const float devEvToBaseValue = 2.5f;    // EV (AiPower units) -> 0..100 BaseValue
        public const float devEvMargin = 0.05f;        // keep an opportunity only if EV exceeds this
        public const float devApValue = 1f;            // value of 1 AP, for the EV apCost term
        // Equipment persists across turns/battles, while Challenge + attach costs are paid once.
        // Applied only to the equipment power delta at Development's EV boundary; AiPower remains
        // a canonical current-force scalar and is not inflated globally.
        public const float devEquipmentPersistenceMultiplier = 3f;
    }
}
