using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Cards
{
    // Match-owned cards and one-use draw pool. CardHandUI only projects this model; AI uses
    // the same draw/capacity boundary and specializes acquisition stamping.
    public class PlayerHandData
    {
        protected readonly List<CardData> HandCards = new List<CardData>();
        private readonly List<CardDefinition> remainingDeck = new List<CardDefinition>();
        public IReadOnlyList<CardData> Hand { get; }
        public IReadOnlyList<CardDefinition> RemainingDeck { get; }
        public int Capacity { get; private set; }
        public bool HasFreeSlot => HandCards.Count < Capacity;
        public bool HasCardsLeftToDraw => remainingDeck.Count > 0;
        public int RemainingDeckCount => remainingDeck.Count;
        public int MutationVersion { get; private set; }
        public int CurrentTurn { get; private set; }
        public event Action HandChanged;

        public PlayerHandData(IEnumerable<CardDefinition> deck, int startingHandSize, int capacity = 10,
            bool reserveRapidHero = false)
        {
            Hand = HandCards.AsReadOnly();
            RemainingDeck = remainingDeck.AsReadOnly();
            Capacity = Mathf.Max(0, capacity);
            if (deck != null) remainingDeck.AddRange(deck);
            foreach (var definition in DeckDraw.DrawStartingHand(remainingDeck,
                Math.Min(startingHandSize, Capacity), reserveRapidHero))
                AddCard(new CardData(definition));
        }

        public void SetCurrentTurn(int turn) => CurrentTurn = Mathf.Max(0, turn);

        public void SetCapacity(int capacity)
        {
            int next = Mathf.Max(0, capacity);
            if (next == Capacity) return;
            Capacity = next;
            NotifyChanged();
        }

        public void AddCard(CardData card) => TryAddCard(card);

        // allowOverflow is the explicit human Library reward boundary. Prepaid production is
        // the shared exception; ordinary draws, rewards and returning aircraft remain capped.
        public bool TryAddCard(CardData card, bool allowOverflow = false)
        {
            if (card?.Definition == null || HandCards.Contains(card)
                || (!allowOverflow && !card.ResearchProductionCreated && !HasFreeSlot)) return false;
            OnAcquired(card);
            HandCards.Add(card);
            NotifyChanged();
            return true;
        }

        protected virtual void OnAcquired(CardData card) { }

        public bool RemoveCard(CardData card)
        {
            if (!HandCards.Remove(card)) return false;
            NotifyChanged();
            return true;
        }

        public bool MoveCard(CardData card, int index)
        {
            int old = HandCards.IndexOf(card);
            if (old < 0) return false;
            index = Mathf.Clamp(index, 0, HandCards.Count - 1);
            if (old == index) return false;
            HandCards.RemoveAt(old);
            HandCards.Insert(index, card);
            NotifyChanged();
            return true;
        }

        public CardData DrawOne()
        {
            if (!HasCardsLeftToDraw || !HasFreeSlot) return null;
            CardDefinition definition = DeckDraw.PopRandom(remainingDeck);
            if (definition == null) return null;
            var card = new CardData(definition);
            AddCard(card);
            return card;
        }

        private void NotifyChanged()
        {
            MutationVersion++;
            HandChanged?.Invoke();
        }

        public HandState CaptureState()
        {
            var state = new HandState { capacity = Capacity, currentTurn = CurrentTurn, mutationVersion = MutationVersion };
            foreach (var card in HandCards) state.cards.Add(CardState.Capture(card));
            foreach (var definition in remainingDeck) state.remainingDeckKeys.Add(CardState.Key(definition));
            return state;
        }

        public static PlayerHandData FromState(HandState state, Func<string, CardDefinition> resolve)
        {
            var hand = new PlayerHandData(null, 0);
            hand.LoadState(state, resolve);
            return hand;
        }
        // Canonical graph import retains exact card instances shared with AI evidence.
        internal void RestoreCanonical(HandState state, Func<int, CardData> resolveCard, Func<string, CardDefinition> resolve)
        {
            if (state == null || state.capacity < 0 || state.currentTurn < 0 || state.mutationVersion < 0
                || state.cards == null || state.remainingDeckKeys == null)
                throw new InvalidOperationException("Invalid saved hand state.");
            var ids = new HashSet<int>();
            var cards = new List<CardData>();
            foreach (var saved in state.cards)
            {
                if (saved == null || !ids.Add(saved.runtimeId)) throw new InvalidOperationException("Duplicate saved hand card.");
                var card = resolveCard(saved.runtimeId);
                if (card == null) throw new InvalidOperationException("Missing canonical hand card.");
                cards.Add(card);
            }
            var deck = new List<CardDefinition>();
            foreach (var key in state.remainingDeckKeys) deck.Add(CardState.Resolve(key, resolve));
            HandCards.Clear(); HandCards.AddRange(cards);
            remainingDeck.Clear(); remainingDeck.AddRange(deck);
            Capacity = state.capacity; CurrentTurn = state.currentTurn; MutationVersion = state.mutationVersion;
        }

        // Populate directly: restore must never draw or invoke OnAcquired/AddCard, which would
        // cap a legitimate overflowing hand or overwrite AI AcquiredTurn.
        protected void LoadState(HandState state, Func<string, CardDefinition> resolve)
        {
            if (state == null || state.capacity < 0 || state.currentTurn < 0 || state.mutationVersion < 0
                || state.cards == null || state.remainingDeckKeys == null)
                throw new InvalidOperationException("Invalid saved hand state.");
            var cards = new List<CardData>();
            var ids = new HashSet<int>();
            foreach (var saved in state.cards)
            {
                if (saved == null || !ids.Add(saved.runtimeId))
                    throw new InvalidOperationException("Duplicate or missing saved card identity.");
                cards.Add(saved.Restore(resolve));
            }
            var deck = new List<CardDefinition>();
            foreach (var key in state.remainingDeckKeys) deck.Add(CardState.Resolve(key, resolve));
            HandCards.Clear(); HandCards.AddRange(cards);
            remainingDeck.Clear(); remainingDeck.AddRange(deck);
            Capacity = state.capacity;
            CurrentTurn = state.currentTurn;
            MutationVersion = state.mutationVersion;
        }
    }
}
