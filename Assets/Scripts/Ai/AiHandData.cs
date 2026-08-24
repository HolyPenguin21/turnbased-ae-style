using System.Collections.Generic;
using Game.Cards;
using Game.Players;
using UnityEngine;

namespace Game.Ai
{
    // Headless mirror of CardHandUI's own deck/hand state (see its own Awake/PopRandomCard)
    // for a non-human player — each player resolves their own deck from the shared
    // StartingDeckCatalog by their own Faction, same as the human does, just plain data with no
    // MonoBehaviour/UI attached. One instance per AI player, created lazily the first time that
    // player's turn actually needs to look at its hand (see AiHandRegistry.GetOrCreate) — an AI
    // player that's already been eliminated, or never gets a turn, never spends the memory on one.
    public class AiHandData
    {
        public readonly List<CardData> Hand = new List<CardData>();

        // Consumed (RemoveAt), not cycled — mirrors CardHandUI's _remainingDeck: every card in
        // the deck is one-time-use for the whole game.
        private readonly List<CardDefinition> _remainingDeck = new List<CardDefinition>();

        public bool HasCardsLeftToDraw => _remainingDeck.Count > 0;
        // Cards not yet drawn this game — AiTurnController.LogHand's own turn-begin log line
        // (2026-08-24, project owner's own ask: surface unused-AP-relevant deck/hand state that
        // was otherwise invisible outside the Inspector).
        public int RemainingDeckCount => _remainingDeck.Count;

        public AiHandData(StartingDeckCatalog deckCatalog, Faction faction, int startingHandSize)
        {
            if (deckCatalog != null)
                _remainingDeck.AddRange(deckCatalog.BuildDeckPool(faction));

            for (int i = 0; i < startingHandSize; i++)
                if (DrawOne() == null)
                    break;
        }

        // Same PopRandomCard + AddCard pairing CardHandUI's own OnDrawClicked uses, minus
        // the AP check (the caller's job — see AiTurnController) and minus any UI animation.
        // Null once the deck's empty, same "nothing left to draw" outcome CardHandUI has too.
        public CardData DrawOne()
        {
            if (_remainingDeck.Count == 0)
                return null;

            int poolIndex = Random.Range(0, _remainingDeck.Count);
            CardDefinition definition = _remainingDeck[poolIndex];
            _remainingDeck.RemoveAt(poolIndex);

            var card = new CardData(definition);
            Hand.Add(card);
            return card;
        }
    }

    // Per-(AI)player registry of AiHandData — same "created once, kept for the game's lifetime"
    // pattern as PlayerRootRegistry.
    public static class AiHandRegistry
    {
        private static readonly Dictionary<PlayerSetupData, AiHandData> ByPlayer = new Dictionary<PlayerSetupData, AiHandData>();

        public static void Clear() => ByPlayer.Clear();

        public static AiHandData GetOrCreate(PlayerSetupData player, StartingDeckCatalog deckCatalog, int startingHandSize)
        {
            if (player == null)
                return null;
            if (!ByPlayer.TryGetValue(player, out AiHandData hand))
            {
                hand = new AiHandData(deckCatalog, player.Faction, startingHandSize);
                ByPlayer[player] = hand;
            }
            return hand;
        }
    }
}
