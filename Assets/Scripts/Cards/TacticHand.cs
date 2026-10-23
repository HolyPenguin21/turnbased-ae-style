using System;
using System.Collections.Generic;

namespace Game.Cards
{
    public enum TacticEffect { None, AddSuccess, SuppressSuccess, AddAndSuppressSuccess }

    // Match-owned inventory, shared by map and battle presentations. Never uses main-hand capacity.
    public sealed class TacticHand
    {
        private readonly List<CardData> _cards = new List<CardData>();
        public IReadOnlyList<CardData> Cards => _cards;
        public int Version { get; private set; }
        public event Action Changed;

        public bool Add(CardData card)
        {
            if (!TacticRules.IsTactic(card?.Definition) || _cards.Contains(card)) return false;
            _cards.Add(card);
            Version++;
            Changed?.Invoke();
            return true;
        }

        public void Clear()
        {
            if (_cards.Count == 0) return;
            _cards.Clear();
            Version++;
            Changed?.Invoke();
        }

        public bool Contains(CardData card) => card != null && _cards.Contains(card);

        internal bool Consume(CardData card)
        {
            if (!_cards.Remove(card)) return false;
            Version++;
            Changed?.Invoke();
            return true;
        }
    }

    public static class TacticRules
    {
        public static bool IsTactic(CardDefinition card) => card != null && card.cardType == CardType.Tactic
            && card.tacticAmount > 0 && (card.tacticEffect == TacticEffect.AddSuccess
                || card.tacticEffect == TacticEffect.SuppressSuccess
                || card.tacticEffect == TacticEffect.AddAndSuppressSuccess);

        // The checks both challenge kinds (battle duel, Research/Production) share: a card is
        // playable once per side per challenge, only after the tokens are on the table, and only
        // from the side's own match inventory. Effect-specific checks follow in UnavailableReason.
        public static string SideUnavailableReason(TacticHand hand, CardData card, bool tokensReady, bool sideUsed)
        {
            if (card == null) return "Select a tactical card.";
            if (!tokensReady) return "Wait for the token roll to finish.";
            if (sideUsed) return "Tactical card already used in this challenge.";
            if (hand?.Contains(card) != true) return "This tactical card is not in your hand.";
            return null;
        }

        public static bool HasPlayable(TacticHand hand, Func<CardData, bool> canPlay)
        {
            if (hand == null) return false;
            foreach (var card in hand.Cards) if (canPlay(card)) return true;
            return false;
        }

        public static string UnavailableReason(CardDefinition card, int opposingSuccesses)
        {
            if (!IsTactic(card)) return "This card has no tactical effect.";
            if (card.tacticEffect == TacticEffect.SuppressSuccess && opposingSuccesses <= 0)
                return "No opposing successes to remove.";
            return null;
        }

        public static bool CanApply(CardDefinition card, int opposingSuccesses) =>
            UnavailableReason(card, opposingSuccesses) == null;

        public static string Description(CardDefinition card)
        {
            if (!IsTactic(card)) return string.Empty;
            string successes = card.tacticAmount == 1 ? "success" : "successes";
            string add = $"+{card.tacticAmount} guaranteed {successes}";
            string suppress = $"-{card.tacticAmount} opposing {successes}";
            return card.tacticEffect == TacticEffect.AddSuccess ? add
                : card.tacticEffect == TacticEffect.SuppressSuccess ? suppress : $"{suppress} / {add}";
        }

        // Suppression removes only successes present at play time. Fate never rerolls a suppressed
        // success: modifiers are separate from the original rolled tokens.
        public static void Apply(CardDefinition card, bool defenderSide, int opposingSuccesses,
            ref int attackerModifier, ref int defenderModifier)
        {
            int amount = card.tacticAmount;
            if (card.tacticEffect == TacticEffect.SuppressSuccess
                || card.tacticEffect == TacticEffect.AddAndSuppressSuccess)
            {
                int removed = Math.Min(amount, Math.Max(0, opposingSuccesses));
                if (defenderSide) attackerModifier -= removed;
                else defenderModifier -= removed;
            }
            if (card.tacticEffect == TacticEffect.AddSuccess
                || card.tacticEffect == TacticEffect.AddAndSuppressSuccess)
            {
                if (defenderSide) defenderModifier += amount;
                else attackerModifier += amount;
            }
        }
    }
}
