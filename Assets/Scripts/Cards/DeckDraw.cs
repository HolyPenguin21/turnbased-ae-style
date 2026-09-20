using System.Collections.Generic;
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
