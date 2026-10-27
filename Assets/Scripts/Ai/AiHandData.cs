using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Players;
using UnityEngine;

namespace Game.Ai
{
    // AI's match-owned hand uses the same deck/capacity rules as the human model. Its only
    // specialization is the owner-turn stamp used by AI scoring.
    public class AiHandData : PlayerHandData
    {
        // Keep the existing AI fixture API; gameplay mutations use AddCard/RemoveCard so the
        // observer notification and mutation version continue to be issued at one boundary.
        public new List<CardData> Hand => HandCards;

        public AiHandData(StartingDeckCatalog deckCatalog, Faction faction, int startingHandSize, int capacity = 10,
            string aiDeckName = null, string grantedLeaderKey = null)
            : base(deckCatalog?.BuildAiDeckPool(faction, aiDeckName, grantedLeaderKey), startingHandSize,
                capacity, reserveRapidHero: true) { }

        protected override void OnAcquired(CardData card) => card.AcquiredTurn = CurrentTurn;

        public static int AgeInTurns(CardData card, int turnNumber) =>
            card == null || card.AcquiredTurn < 0 ? 0 : Mathf.Max(0, turnNumber - card.AcquiredTurn);

        public new static AiHandData FromState(HandState state, Func<string, CardDefinition> resolve)
        {
            var hand = new AiHandData(null, Faction.None, 0);
            hand.LoadState(state, resolve);
            return hand;
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

        // Rebind a restored model without invoking GetOrCreate (which would deal a new hand).
        public static void RegisterRestored(PlayerSetupData player, AiHandData hand)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (hand == null) throw new ArgumentNullException(nameof(hand));
            if (ByPlayer.TryGetValue(player, out var old)) OwnerByHand.Remove(old);
            ByPlayer[player] = hand;
            OwnerByHand[hand] = player;
        }

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
                hand = new AiHandData(deckCatalog, player.Faction, startingHandSize, capacity ?? 10, player.AiDeckName, player.AiGrantedLeaderKey);
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
