using System.Collections.Generic;
using Game.Cards;
using Game.Economy;

namespace Game.Ai.V2.Initiative
{
    // The single "how much Human/Energy/Materials/Tech does the rest of this player's game still
    // want" formula, so the Initiative planner values a resource against exactly the same
    // remaining-deck appetite concept the V2 economy read uses (WorldAnalysis.AccumulateCardCosts
    // — deliberately mirrored here, same card-type filter and the same per-type resourceCost
    // sum, not a second unrelated formula).
    //
    // Amounts are returned as an int[4] indexed in ResourceType order
    // (Human, Energy, Materials, Tech).
    public static class InitiativeDeckDemand
    {
        public static readonly ResourceType[] Types =
            { ResourceType.Human, ResourceType.Energy, ResourceType.Materials, ResourceType.Tech };

        // A card counts toward remaining demand only if it actually costs resources to play and
        // is a permanent build (Unit / Hero / Facility / Base) — the same set WorldAnalysis uses.
        public static bool CountsTowardDemand(CardDefinition d)
        {
            if (d == null || d.resourceCost == null)
                return false;
            return d.cardType == CardType.Unit || d.cardType == CardType.Hero
                || d.cardType == CardType.Facility || d.cardType == CardType.Base;
        }

        public static void Accumulate(IEnumerable<CardDefinition> defs, int[] into)
        {
            if (defs == null || into == null)
                return;
            foreach (CardDefinition d in defs)
            {
                if (!CountsTowardDemand(d))
                    continue;
                for (int i = 0; i < Types.Length && i < into.Length; i++)
                    into[i] += d.resourceCost.Get(Types[i]);
            }
        }

        public static int[] Of(IEnumerable<CardDefinition> defs)
        {
            var acc = new int[Types.Length];
            Accumulate(defs, acc);
            return acc;
        }
    }
}
