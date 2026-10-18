using Game.Units;

namespace Game.Cards
{
    // One paid attempt's mutable dice and tactic state. Fate belongs to the existing attempt loop.
    public sealed class ResearchProductionChallengeSession
    {
        private readonly UnitData _hero;
        public bool[] Dice { get; set; }
        public int BaseRequired { get; }
        public int SuccessModifier { get; private set; }
        public int DifficultyModifier { get; private set; }
        public bool TacticUsed { get; private set; }
        public int Successes => System.Math.Max(0, ResearchProductionSystem.CountHits(Dice) + SuccessModifier);
        public int Required => System.Math.Max(0, BaseRequired + DifficultyModifier);
        public bool Success => Successes >= Required;

        public ResearchProductionChallengeSession(UnitData hero, CardDefinition card)
        {
            _hero = hero;
            BaseRequired = ResearchProductionSystem.RequiredSuccesses(card);
        }

        public string TacticUnavailableReason(CardData card)
        {
            if (card == null) return "Select a tactical card first.";
            if (Dice == null) return "Wait for the dice roll to finish.";
            if (TacticUsed) return "You have already used a tactical card in this challenge.";
            if (_hero?.Owner?.Tactics.Contains(card) != true) return "This tactical card is not in your hand.";
            return TacticRules.UnavailableReason(card.Definition, Required);
        }

        public bool CanPlay(CardData card) => TacticUnavailableReason(card) == null;

        public bool HasTactic
        {
            get
            {
                var hand = _hero?.Owner?.Tactics;
                if (hand == null) return false;
                foreach (var card in hand.Cards) if (CanPlay(card)) return true;
                return false;
            }
        }

        public bool TryPlay(CardData card)
            => TryPlay(card, out _);

        public bool TryPlay(CardData card, out string reason)
        {
            reason = TacticUnavailableReason(card);
            if (reason != null) return false;
            int a = SuccessModifier, d = DifficultyModifier;
            TacticRules.Apply(card.Definition, false, Required, ref a, ref d);
            SuccessModifier = a; DifficultyModifier = d;
            TacticUsed = true;
            _hero.Owner.Tactics.Consume(card);
            return true;
        }
    }
}
