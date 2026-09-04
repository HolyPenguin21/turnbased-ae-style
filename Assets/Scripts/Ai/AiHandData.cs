using System;
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

        // Fired after every successful mutation of Hand, from whichever single entry point
        // actually changed it (AddCard/RemoveCard below, including DrawOne's own draw) — the one
        // thing CardHandUI's AI hand debug view (see its own OnDebugHandChanged) needs to stay
        // live across an AI turn without any executor having to know a debug view might be
        // watching (DEBUG-UI-01: every V2 card-execution path already goes through one of these
        // two methods as "the executor owns the hand boundary", so this is the one place a
        // refresh notification can live without duplicating it into recruit/equipment/generated/
        // etc separately).
        public event Action HandChanged;

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

        // The still-drawable draw pool itself, not just its count — read by the Strategy V2
        // WorldAnalysis scan (Game.Ai.V2) to forecast the player's whole-game military/economic
        // potential from the cards they can still get to. Draw order is random (see DrawOne), so
        // this is an honest multiset the AI knows, never a peek at what comes next. Read-only
        // view — the deck is still mutated only through DrawOne.
        public IReadOnlyList<CardDefinition> RemainingDeck => _remainingDeck;

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

        // The only two places Hand is ever added to/removed from outside DrawOne — every V2
        // card-execution path (deploy, base/facility build, aviation, equipment attach,
        // Research/Production mint) and the non-draw hex-event grant path (HexSelectionController.
        // GrantCard) call these instead of touching the list directly, so HandChanged always fires.
        public void AddCard(CardData card)
        {
            Hand.Add(card);
            HandChanged?.Invoke();
        }

        public bool RemoveCard(CardData card)
        {
            bool removed = Hand.Remove(card);
            if (removed)
                HandChanged?.Invoke();
            return removed;
        }

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

            int poolIndex = UnityEngine.Random.Range(0, _remainingDeck.Count);
            CardDefinition definition = _remainingDeck[poolIndex];
            _remainingDeck.RemoveAt(poolIndex);

            var card = new CardData(definition);
            AddCard(card);
            return card;
        }
    }

    // Per-(AI)player registry of AiHandData — same "created once, kept for the game's lifetime"
    // pattern as PlayerRootRegistry.
    public static class AiHandRegistry
    {
        private static readonly Dictionary<PlayerSetupData, AiHandData> ByPlayer = new Dictionary<PlayerSetupData, AiHandData>();
        // Reverse lookup is intentionally registry-owned rather than stored on AiHandData: the hand
        // remains plain card/deck data, while low-level draw execution can still identify which AI
        // player's strategic interrupt must be raised when a boundary draw changes the hand.
        private static readonly Dictionary<AiHandData, PlayerSetupData> OwnerByHand = new Dictionary<AiHandData, PlayerSetupData>();

        public static void Clear()
        {
            ByPlayer.Clear();
            OwnerByHand.Clear();
        }

        // Non-creating read — for callers (e.g. the pre-turn Initiative capacity analysis) that
        // may look at an AI hand if one already exists but must NOT bring one into being just to
        // read it (that would draw cards / consume RNG out of turn). Null when this player has
        // never had a hand built yet.
        public static AiHandData Peek(PlayerSetupData player) =>
            player != null && ByPlayer.TryGetValue(player, out AiHandData hand) ? hand : null;

        public static bool TryGetOwner(AiHandData hand, out PlayerSetupData player)
        {
            player = null;
            return hand != null && OwnerByHand.TryGetValue(hand, out player) && player != null;
        }

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
                OwnerByHand[hand] = player;
            }
            else if (capacity.HasValue)
            {
                hand.SetCapacity(capacity.Value);
            }
            // Defensive repair for test/setup code that may have survived a registry reset in an
            // unusual order: any returned hand must always have a reverse owner mapping.
            OwnerByHand[hand] = player;
            return hand;
        }
    }
}
