using System.Collections.Generic;
using Game.Map;
using UnityEngine;

namespace Game.Cards
{
    // Single source of truth for "remove and return one random card from a one-time-use deck
    // pool, without replacement" — CardHandUI's own starting-hand draw / OnDrawClicked and
    // AiHandData.DrawOne used to each carry an independent copy of this exact algorithm (same
    // Random.Range + RemoveAt pair, on two different List<CardDefinition> fields). Capacity
    // checks and the resulting AddCard/AP-spend calls still live on each caller — those genuinely
    // differ (CardHandUI shows a UI hint on a full hand, AiHandData just returns null) — only the
    // draw-from-deck mechanic itself is shared here, so it can't silently drift between the human
    // and AI paths.
    public static class DeckDraw
    {
        // Reserve one cheapest mobile ground scout before the random draws, then insert it
        // into a random slot. Human and AI opening deals use this in every match mode. All cards
        // come from the selected deck and are consumed once; scout-free decks still draw normally.
        // AI-only extra (reserveRapidHero): after the scout, one Hero with RapidReaction from the
        // same deck is also reserved (random among such heroes) while a slot remains. Humans draw fairly.
        public static List<CardDefinition> DrawStartingHand(List<CardDefinition> remainingDeck, int count,
            bool reserveRapidHero = false)
        {
            var hand = new List<CardDefinition>();
            if (remainingDeck == null || count <= 0 || remainingDeck.Count == 0)
                return hand;

            count = System.Math.Min(count, remainingDeck.Count);
            int scoutIndex = -1, bestAp = int.MaxValue, ties = 0;
            long bestResources = long.MaxValue;
            for (int i = 0; i < remainingDeck.Count; i++)
            {
                CardDefinition candidate = remainingDeck[i];
                if (candidate == null || candidate.cardType != CardType.Unit || candidate.isAviation
                    || candidate.moveMax <= 0 || AbilityParams.GetBestRecceRadius(candidate.grantedAbilities) <= 0)
                    continue;

                // Deck copies are ordinary, unattached cards; use the definition overload
                // without issuing temporary CardData runtime identities during selection.
                int ap = ArmyActions.EffectiveDeployApCost(candidate);
                ResourceCost cost = candidate.resourceCost;
                long resources = cost == null ? 0 : (long)cost.human + cost.energy + cost.materials + cost.tech;
                if (ap < bestAp || (ap == bestAp && resources < bestResources))
                {
                    bestAp = ap;
                    bestResources = resources;
                    scoutIndex = i;
                    ties = 1;
                }
                else if (ap == bestAp && resources == bestResources && Random.Range(0, ++ties) == 0)
                    scoutIndex = i;
            }

            CardDefinition scout = null;
            if (scoutIndex >= 0)
            {
                scout = remainingDeck[scoutIndex];
                remainingDeck.RemoveAt(scoutIndex);
            }
            int randomCount = count - (scout != null ? 1 : 0);
            CardDefinition rapidHero = null;
            if (reserveRapidHero && randomCount > 0)
            {
                int heroes = 0, heroIndex = -1;
                for (int i = 0; i < remainingDeck.Count; i++)
                {
                    CardDefinition c = remainingDeck[i];
                    if (c != null && c.cardType == CardType.Hero && c.grantedAbilities != null
                        && c.grantedAbilities.Contains(UnitAbilities.RapidReaction)
                        && Random.Range(0, ++heroes) == 0)
                        heroIndex = i;
                }
                if (heroIndex >= 0)
                {
                    rapidHero = remainingDeck[heroIndex];
                    remainingDeck.RemoveAt(heroIndex);
                    randomCount--;
                }
            }
            for (int i = 0; i < randomCount; i++)
            {
                CardDefinition card = PopRandom(remainingDeck);
                if (card == null) break;
                hand.Add(card);
            }
            if (rapidHero != null)
                hand.Insert(Random.Range(0, hand.Count + 1), rapidHero);
            if (scout != null)
                hand.Insert(Random.Range(0, hand.Count + 1), scout);
            return hand;
        }

        public static CardDefinition PopRandom(List<CardDefinition> remainingDeck)
        {
            if (remainingDeck == null || remainingDeck.Count == 0)
                return null;
            int index = Random.Range(0, remainingDeck.Count);
            CardDefinition card = remainingDeck[index];
            remainingDeck.RemoveAt(index);
            return card;
        }
    }
}
