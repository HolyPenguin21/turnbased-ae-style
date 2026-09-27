using System;
using Game.Cards;
using Game.Economy;
using UnityEngine;

namespace Game.Ai.V2
{
    // ===========================================================================================
    //  ACTION PRICE — the ONE price table of AI V2
    // ===========================================================================================
    //  Everything an action spends is priced here in AP-equivalents, and only here:
    //    · 1 AP = 1 AP-equivalent, whatever it pays for — a card play, a Challenge, an army's
    //      activation now or on a later turn of its march. AP is fungible; there is no discount
    //      for "only moving".
    //    · 1 unit of H/E/M/T = actionPriceResourceAp AP-equivalents x Scarcity(type): pending
    //      hand/deck demand for that resource against stock + income over the economy horizon.
    //  The two value currencies are scales of this ONE table, never a second price:
    //    · TaskScore (world tasks): taskScorePerApEquivalent points per AP-equivalent;
    //    · card score (StrategicCardEvaluator): cardScorePerApEquivalent per AP-equivalent.
    //  A price always reaches a score through one of those two scales.
    // ===========================================================================================
    internal static class ActionPrice
    {
        internal static float Ap(float ap) => Mathf.Max(0f, ap);

        // Recurring activation of a multi-turn march: the army's per-turn activation, paid again
        // on each turn after the first (ArmyData.HasActivatedThisTurn resets every turn).
        internal static float RecurringAp(float perTurnActivationAp, float etaTurns) =>
            Mathf.Max(0f, perTurnActivationAp) * Mathf.Max(0f, etaTurns - 1f);

        // [0.2 .. 1.8] — how scarce this resource is for the rest of the hand and deck. 1 when
        // nothing is known (no snapshot): the neutral price.
        internal static float Scarcity(ResourceType type, WorldSnapshot snap,
            Func<ResourceType, float> spendableResource = null)
        {
            if (snap?.Self == null)
                return 1f;
            float demand = PendingCardResourceDemand(snap, type);
            float availableNow = spendableResource != null
                ? Mathf.Max(0f, spendableResource(type))
                : snap.Self.Stockpile.Get(type);
            float supply = availableNow + snap.Self.PerTurnIncome.Get(type)
                * Mathf.Max(1f, AiConfigV2.economyDeckNeedHorizonTurns);
            float pressure = demand <= 0.0001f ? 0f
                : demand / Mathf.Max(0.0001f, demand + supply);
            return Mathf.Lerp(AiConfigV2.actionPriceScarcityMin, AiConfigV2.actionPriceScarcityMax,
                Mathf.Clamp01(pressure));
        }

        // AP-equivalents of a resource bundle, each type at its own scarcity.
        internal static float Resources(Func<ResourceType, float> amount, WorldSnapshot snap = null,
            Func<ResourceType, float> spendableResource = null)
        {
            if (amount == null)
                return 0f;
            float total = 0f;
            foreach (ResourceType type in ResourceBundle.All)
            {
                float units = Mathf.Max(0f, amount(type));
                if (units > 0f)
                    total += units * AiConfigV2.actionPriceResourceAp
                        * Scarcity(type, snap, spendableResource);
            }
            return total;
        }

        internal static float Resources(ResourceCost cost, WorldSnapshot snap = null,
            Func<ResourceType, float> spendableResource = null) =>
            cost == null ? 0f : Resources(t => cost.Get(t), snap, spendableResource);

        internal static float Resources(ResourceVector cost, WorldSnapshot snap = null) =>
            Resources(t => t == ResourceType.Human ? cost.Human
                : t == ResourceType.Energy ? cost.Energy
                : t == ResourceType.Materials ? cost.Materials : cost.Tech, snap);

        // The two currencies — the only conversion of a price into score units.
        internal static float ToTaskScore(float apEquivalents) =>
            Mathf.Max(0f, apEquivalents) * AiConfigV2.taskScorePerApEquivalent;

        internal static float ToCardScore(float apEquivalents) =>
            Mathf.Max(0f, apEquivalents) * AiConfigV2.cardScorePerApEquivalent;

        // Inverse, for a planner that must rank an already-scored loss against AP.
        internal static float FromTaskScore(float scoreUnits) =>
            Mathf.Max(0f, scoreUnits) / Mathf.Max(0.0001f, AiConfigV2.taskScorePerApEquivalent);

        internal static float PendingCardResourceDemand(WorldSnapshot snap, ResourceType type)
        {
            float demand = 0f;
            if (snap?.Self?.Hand != null)
                foreach (CardData card in snap.Self.Hand)
                    demand += card?.EffectivePlayResourceCost?.Get(type) ?? 0;
            if (snap?.Self?.Deck != null)
                foreach (CardDefinition card in snap.Self.Deck)
                    demand += card?.resourceCost?.Get(type) ?? 0;
            return demand;
        }
    }
}
