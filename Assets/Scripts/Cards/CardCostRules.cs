using Game.Ai;
using Game.Economy;
using Game.Map;
using Game.Players;

namespace Game.Cards
{
    // The AI-side counterpart of CardData.EffectivePlay* — the cost of PLAYING one specific hand
    // CARD INSTANCE, correct for a Research/Production-created card (spec P0 §5): its ResourceCost
    // was already paid at Create, and its play-time AP is activationApCost, not apCost. For every
    // ordinary card (starting-deck / drawn / event-reward / returned-aircraft) these return
    // exactly the definition's own apCost/resourceCost, 1:1 — nothing about non-produced cards
    // changes.
    //
    // Use these wherever the AI already holds the CardData instance (a hand read). Paths that only
    // have a bare CardDefinition and no instance — BuildFacility's extraction-facility cards from
    // GameConfig — stay on the definition: there is no produced instance to consult.
    // Physical rule: what it costs to PLAY a specific hand-card instance (AP + ResourceCost),
    // correct for Research/Production-created cards. Extracted from the former Game.Ai.AiCardCost
    // (ARCH-01) — a thin, canonical wrapper over ArmyActions.EffectiveDeployApCost and
    // CardData.EffectivePlayResourceCost that both gameplay and the AI can share.
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

        // Reservation-aware affordability of this instance's play-time resource cost. A null
        // cost (Research/Production card) is always affordable — unlike
        // AiResourceReservation.CanAfford's own null → false, which is there to catch a cost that
        // was never wired up, not a card that genuinely costs nothing to play.
        public static bool CanAffordPlayResources(PlayerRoot root, PlayerSetupData player, CardData card)
        {
            ResourceCost cost = PlayResources(card);
            return cost == null || AiResourceReservation.CanAfford(root, player, cost);
        }
    }
}
