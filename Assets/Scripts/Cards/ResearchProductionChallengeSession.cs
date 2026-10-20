using Game.Units;

namespace Game.Cards
{
    // One paid attempt's mutable tokens and tactic state. Fate belongs to the existing attempt loop.
    public sealed class ResearchProductionChallengeSession
    {
        private readonly UnitData _hero;
        public bool[] Tokens { get; set; }
        public int BaseRequired { get; }
        public int SuccessModifier { get; private set; }
        public int DifficultyModifier { get; private set; }
        public bool TacticUsed { get; private set; }
        public int Successes => System.Math.Max(0, ResearchProductionSystem.CountHits(Tokens) + SuccessModifier);
        public int Required => System.Math.Max(0, BaseRequired + DifficultyModifier);
        public bool Success => Successes >= Required;

        public ResearchProductionChallengeSession(UnitData hero, CardDefinition card)
        {
            _hero = hero;
            BaseRequired = ResearchProductionSystem.RequiredSuccesses(card);
        }

        private TacticHand Hand => _hero?.Owner?.Tactics;

        public string TacticUnavailableReason(CardData card)
            => TacticRules.SideUnavailableReason(Hand, card, Tokens != null, TacticUsed)
                ?? TacticRules.UnavailableReason(card.Definition, Required);

        public bool CanPlay(CardData card) => TacticUnavailableReason(card) == null;

        public bool HasTactic => TacticRules.HasPlayable(Hand, CanPlay);

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
