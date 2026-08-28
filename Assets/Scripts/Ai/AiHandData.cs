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

        // Shared hand capacity (spec P0 §10) — set from CardHandUI.MaxHandSize via AiTurnContext
        // (AiHandRegistry can't reach the scene, so the value is pushed in on construction — see
        // AiHandRegistry.GetOrCreate / AiTurnController.RunTurn). Defaults to CardHandUI's own
        // default of 10 so a hand created before that push still caps somewhere sane. This is a
        // real data invariant now (2026-08-28 P1, project owner's own report — Vashti twice ended a
        // turn with hand=11): DrawOne itself physically refuses to overflow it, so Hand.Count >
        // Capacity is unreachable through any AI draw/deploy path, not just the Development planner's
        // own HasFreeSlot pre-check. Private setter — pushed in only via the constructor or
        // SetCapacity, never assigned field-style from outside.
        public int Capacity { get; private set; } = 10;

        public bool HasFreeSlot => Hand.Count < Capacity;

        // Consumed (RemoveAt), not cycled — mirrors CardHandUI's _remainingDeck: every card in
        // the deck is one-time-use for the whole game.
        private readonly List<CardDefinition> _remainingDeck = new List<CardDefinition>();

        public bool HasCardsLeftToDraw => _remainingDeck.Count > 0;
        // Cards not yet drawn this game — AiTurnController.LogHand's own turn-begin log line
        // (2026-08-24, project owner's own ask: surface unused-AP-relevant deck/hand state that
        // was otherwise invisible outside the Inspector).
        public int RemainingDeckCount => _remainingDeck.Count;

        public AiHandData(StartingDeckCatalog deckCatalog, Faction faction, int startingHandSize, int capacity = 10)
        {
            Capacity = Mathf.Max(0, capacity);

            if (deckCatalog != null)
                _remainingDeck.AddRange(deckCatalog.BuildDeckPool(faction));

            for (int i = 0; i < startingHandSize; i++)
                if (DrawOne() == null)
                    break;
        }

        public void SetCapacity(int capacity) => Capacity = Mathf.Max(0, capacity);

        // Same PopRandomCard + AddCard pairing CardHandUI's own OnDrawClicked uses, minus
        // the AP check (the caller's job — see AiTurnController) and minus any UI animation.
        // Null once the deck's empty, same "nothing left to draw" outcome CardHandUI has too.
        public CardData DrawOne()
        {
            // Checked BEFORE pulling anything off the deck — a full hand must not consume a
            // one-time-use deck card it then has nowhere to put (spec P1 §"Execution rechecks
            // invariant"). This is the layer that actually makes Hand.Count > Capacity unreachable;
            // the planner-side HasFreeSlot checks only keep a doomed candidate from being proposed.
            if (_remainingDeck.Count == 0 || !HasFreeSlot)
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

        // `capacity` is optional so the many callers that only need to read an existing hand don't
        // have to thread the scene's MaxHandSize through — they pass null and leave whatever
        // capacity the hand was first created with. AiTurnController.RunTurn is the one caller that
        // does pass it, on the first turn that creates the hand and every turn after (SetCapacity
        // below), so the cap always tracks CardHandUI.MaxHandSize even if it changes mid-game.
        public static AiHandData GetOrCreate(PlayerSetupData player, StartingDeckCatalog deckCatalog,
            int startingHandSize, int? capacity = null)
        {
            if (player == null)
                return null;
            if (!ByPlayer.TryGetValue(player, out AiHandData hand))
            {
                hand = new AiHandData(deckCatalog, player.Faction, startingHandSize, capacity ?? 10);
                ByPlayer[player] = hand;
            }
            else if (capacity.HasValue)
            {
                hand.SetCapacity(capacity.Value);
            }
            return hand;
        }
    }
}
