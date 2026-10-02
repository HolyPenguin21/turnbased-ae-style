using System;
using Game.Cards;
using Game.Economy;
using UnityEngine;

namespace Game.Ai.V2
{
    // "The card has been lying in hand while the bank piles up" — a growing, capped play bonus.
    // Pure formula (no snapshot, no registry): age comes from CardData.AcquiredTurn, the bank
    // reading from the caller's spendable function (StrategicSpendability.SpendableAmount, so
    // reserved resources never count as surplus).
    internal static class IdleCardPressure
    {
        // min(cap, rate x max(0, age - grace)) x saturation.
        public static float Bonus(int ageTurns, float saturation)
        {
            float aged = Mathf.Min(AiConfigV2.idleCardBonusCap,
                AiConfigV2.idleCardBonusPerTurn * Mathf.Max(0, ageTurns - AiConfigV2.idleCardGraceTurns));
            return aged * Mathf.Clamp01(saturation);
        }

        // [0..1]: how far the spendable bank exceeds the card's own cost. The tightest costed
        // resource decides (a card needing Tech is not "affordable surplus" because Human is
        // plentiful). No resource cost -> 1 (AP is priced by ResourceEfficiency, not here).
        // No bank reading -> 0: never push a play without knowing it is affordable.
        public static float Saturation(ResourceCost cost, Func<ResourceType, float> spendable)
        {
            if (spendable == null)
                return 0f;
            float sat = 1f;
            if (cost == null)
                return sat;
            foreach (ResourceType type in ResourceBundle.All)
            {
                int amount = cost.Get(type);
                if (amount <= 0)
                    continue;
                float headroom = spendable(type) - amount;
                sat = Mathf.Min(sat, Mathf.Clamp01(headroom / AiConfigV2.idleCardSaturationHeadroom));
            }
            return sat;
        }
    }
}
