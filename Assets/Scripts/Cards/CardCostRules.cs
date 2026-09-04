using Game.Economy;
using Game.Map;

namespace Game.Cards
{
    // Physical rule: what it costs to PLAY a specific hand-card instance — AP and ResourceCost,
    // correct for a Research/Production-created card (its ResourceCost was already paid at Create,
    // its play-time AP is activationApCost not apCost). A thin canonical wrapper over
    // ArmyActions.EffectiveDeployApCost and CardData.EffectivePlayResourceCost that both gameplay
    // and the AI share. Was Game.Ai.AiCardCost before ARCH-01.
    //
    // No affordability check lives here: "can the player physically pay" is PlayerRoot's job, and
    // "can the player pay once strategic AI reservations are netted out" is an AI concern
    // (AiResourceReservation.CanAffordCardPlay) — neither belongs in Game.Cards.
    public static class CardCostRules
    {
        // Play-time AP. Delegates to ArmyActions.EffectiveDeployApCost(CardData), which already
        // folds in RapidReaction (0 AP) and ResearchProductionCreated (activationApCost).
        public static int PlayAp(CardData card) => ArmyActions.EffectiveDeployApCost(card);

        // Play-time ResourceCost of this instance — null for a Research/Production card (already
        // paid at Create), the definition's own resourceCost otherwise. Callers treat null as
        // "nothing to check, nothing to charge".
        public static ResourceCost PlayResources(CardData card) => card?.EffectivePlayResourceCost;

        // One resource-type amount of the play-time cost — 0 for a Research/Production card.
        public static int PlayResource(CardData card, ResourceType type)
        {
            ResourceCost cost = PlayResources(card);
            return cost == null ? 0 : cost.Get(type);
        }
    }
}
