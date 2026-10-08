using System.Collections.Generic;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;

namespace Game.Ai.V2
{
    // The ONE intrinsic card score of a Research/Production preparation step. It is the shared
    // StrategicCardEvaluator machinery, not a second calculator:
    //   · Facility — ScoreNonCombat(Facility): infrastructure value - the current price;
    //   · Operator card — ScoreForDemand: the hero/unit card's own utility at its deployment place
    //     (garrison capacity, ...) + the registry's fixed Researcher/Assembler value - the price;
    //   · Generated operator — the same hero score for the card the Challenge would mint, its
    //     benefit discounted by the success chance (generation risk), the Challenge paid in full,
    //     and the later deployment AP excluded (that is the next, separate action).
    // The price is always the CURRENT step only: placement + a capacity upgrade needed today, or
    // the card's play/activation AP. Both the early admission (DevelopmentOpportunityEvaluator) and
    // the Phase A infrastructure lane call these, so a step can never be priced two ways.
    internal static class DevelopmentPreparationScorer
    {
        internal static float Facility(CardData card, int stageAp, ResourceCost stageCost,
            WorldSnapshot snap, CapabilityInventory inv, AiHandData hand,
            System.Func<ResourceType, float> spendableResource, PlayerSetupData player) =>
            StrategicCardEvaluator.ScoreNonCombat(NonCombatRole.Facility, card, snap, inv, hand,
                bestEquipmentUpgrade: 0f, actualApCost: stageAp, actualResourceCost: stageCost,
                spendableResource: spendableResource, player: player).NetScore;

        internal static float HandOperator(CardData card, int ordinal, HexCoord hex,
            ResearchProductionMode mode, ArmyData garrison, WorldSnapshot snap, CapabilityInventory inv,
            System.Func<ResourceType, float> spendableResource, PlayerSetupData player)
        {
            if (card?.Definition == null || garrison == null)
                return float.NegativeInfinity;
            IReadOnlyList<string> abilities = MaterializationChainMatching.EffectiveAbilities(
                card.Definition, card.Equipment, card.Mutator);
            AxisDemand demand = OperatorDemand(hex, mode);
            MaterializationPlan plan = MaterializationPlanFactory.MakeExistingPlan(
                MaterializationChainKind.Direct, demand, card, ordinal, null, -1,
                new PlacementOption(hex, DeploymentKind.Garrison, garrison), abilities);
            return StrategicCardEvaluator.ScoreForDemand(plan, demand, plan.ExpectedTraits, inv,
                card.Definition.moveMax, hasCompetingHeroDemand: false, snap,
                spendableResource: spendableResource, player: player).NetScore;
        }

        internal static float GeneratedOperator(GenerationStep source, HexCoord hex,
            ResearchProductionMode mode, ArmyData garrison, WorldSnapshot snap, CapabilityInventory inv,
            System.Func<ResourceType, float> spendableResource, PlayerSetupData player)
        {
            if (source?.CardDef == null || garrison == null)
                return float.NegativeInfinity;
            IReadOnlyList<string> abilities = MaterializationChainMatching.EffectiveAbilities(
                source.CardDef, null);
            AxisDemand demand = OperatorDemand(hex, mode);
            MaterializationPlan plan = MaterializationPlanFactory.MakeGeneratedPlan(
                MaterializationChainKind.GenerateDeploy, demand, source, null, -1, false,
                new PlacementOption(hex, DeploymentKind.Garrison, garrison), abilities);
            float chain = StrategicCardEvaluator.ScoreForDemand(plan, demand, plan.ExpectedTraits, inv,
                source.CardDef.moveMax, hasCompetingHeroDemand: false, snap,
                spendableResource: spendableResource, player: player).NetScore;
            // The minted card is deployed by a later action that pays its own activation AP.
            float deferredDeployAp = CardCostRules.PlayAp(
                new CardData(source.CardDef) { ResearchProductionCreated = true });
            return chain + ActionPrice.ToCardScore(ActionPrice.Ap(deferredDeployAp));
        }

        private static AxisDemand OperatorDemand(HexCoord hex, ResearchProductionMode mode) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Development,
            Capability = CapabilityKind.DevelopmentOperator,
            DesiredAmount = 1,
            TargetHex = hex,
            DevelopmentOperatorMode = mode,
        };
    }
}
