namespace Game.Ai.V2
{
    // Part of AiConfigV2 (file-split Task 2, see Docs/ai-v2-file-split-refactor-tasks.md).
    // Development — desire sub-block (rawDev / investment window / DevelopmentOpportunityEvaluator EV model).
    public static partial class AiConfigV2
    {
        // Development desire (radar): rawDev = surplusRamp x JustifiedForceNeed x feasibility,
        // never above the need itself (Production amplifies an Attack/Defence need, it never
        // creates one — ForceNeedModel). feasibility = max(ready best success chance, latent path).
        // The facility+hero prerequisite is NOT a desire gate — a latent appetite keeps the axis
        // warm so DemandLayer can STAGE the missing prerequisite. `surplus` still gates hard: a
        // Challenge stakes real H/E/M/T with a probabilistic return. First-pass, tune vs log.
        public const float devSurplusRampLo = 0.15f;  // below this SurplusFraction -> ~no appetite
        public const float devSurplusRampHi = 0.60f;  // at/above -> full surplus term
        // Feasibility of the LATENT path (no live offering yet — facility unbuilt or unstaffed).
        // Held below a fully execution-ready setup so a staffed facility with a likely Challenge
        // still outranks a bare path. Only applies when DevPathViable.
        public const float devLatentPotential = 0.5f;

        // Investment window (DevelopmentInvestmentGate). Laboratory / Factory are a late resource
        // sink that must not compete with the main deck: a spend is allowed only when EVERY resource
        // it consumes has kept its headroom (spendable / 2x income) at or above the threshold for
        // this many consecutive turns. Resources it does not consume never matter. The threshold is
        // the radar's own "full surplus" point, so the radar and the gate describe the same economy.
        public const float devInvestmentSurplusThreshold = devSurplusRampHi;
        public const int devInvestmentSurplusTurns = 2;

        // Development OPPORTUNITY value (DevelopmentOpportunityEvaluator). A PREPARE must pass two
        // gates, one per currency: the card-currency investment EV (is the card chain worth its
        // cards) above devEvMargin, and a positive world-task TaskScore (ForceAmplification minus
        // the operator walk), on which it then competes in the allocator. No EV -> world-value
        // conversion exists.
        public const float devEvMargin = 0.05f;        // keep an opportunity only if card EV exceeds this
        // In the card-currency EV, prerequisite AP (facility / operator / hero travel) is priced
        // by ActionPrice in card units; equipment output value is
        // StrategicCardEvaluator.EquipmentUpgradeValue's (equipmentUpgradePersistence).
    }
}
