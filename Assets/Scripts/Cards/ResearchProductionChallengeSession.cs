using System;
using Game.Combat;
using Game.Units;

namespace Game.Cards
{
    // One attempt owns its tokens, modifiers and temporary Fate. It never writes the live hero.
    public sealed class ResearchProductionChallengeSession : IDisposable
    {
        private readonly UnitData _hero;
        private readonly Func<int, bool[]> _rollTokens;
        private bool[] _tokens;
        private bool _initialized;
        private ResearchProductionSystem.ChallengeOutcome _result;
        public bool[] Tokens
        {
            get => _tokens == null ? null : (bool[])_tokens.Clone();
            set
            {
                if (_initialized || IsTerminal) throw new InvalidOperationException("The initial roll is already set.");
                _tokens = value == null ? Array.Empty<bool>() : (bool[])value.Clone();
                _initialized = true;
            }
        }
        public int TokenPoolSize { get; }
        public int BaseRequired { get; }
        public int FateAvailable { get; }
        public int FateRemaining { get; private set; }
        public int FateSpent => FateAvailable - FateRemaining;
        public int SuccessModifier { get; private set; }
        public int DifficultyModifier { get; private set; }
        public bool TacticUsed { get; private set; }
        public bool IsAccepted { get; private set; }
        public bool IsCancelled { get; private set; }
        public bool IsTerminal => IsAccepted || IsCancelled;
        public int Successes => Math.Max(0, ResearchProductionSystem.CountHits(_tokens) + SuccessModifier);
        public int Required => Math.Max(0, BaseRequired + DifficultyModifier);
        public bool Success => Successes >= Required;
        public ResearchProductionSystem.ChallengeOutcome Result => IsAccepted ? _result
            : throw new InvalidOperationException("The challenge has not been accepted.");

        public ResearchProductionChallengeSession(UnitData hero, CardDefinition card,
            Func<int, bool[]> rollTokens = null)
        {
            _hero = hero;
            BaseRequired = ResearchProductionSystem.RequiredSuccesses(card);
            TokenPoolSize = ResearchProductionSystem.TokenPoolSizeFor(hero);
            FateRemaining = FateAvailable = Math.Max(0, hero?.Fate ?? 0);
            _rollTokens = rollTokens ?? ChallengeResolver.RollTokens;
        }

        public void Roll()
        {
            if (_initialized || IsTerminal) return;
            try { Tokens = _rollTokens(TokenPoolSize); }
            catch { Cancel(); throw; }
        }

        public bool CanSpend => CanSpendWithModifiers(SuccessModifier, DifficultyModifier);
        public bool CanSpendWithModifiers(int successModifier, int difficultyModifier)
            => !IsTerminal && _initialized && ResearchProductionSystem.CanSpendFate(_tokens,
                FateRemaining, Math.Max(0, BaseRequired + difficultyModifier) - successModifier);

        public bool TrySpend(out int index, out bool appended)
            => TrySpendWithModifiers(SuccessModifier, DifficultyModifier, out index, out appended);
        public bool TrySpendWithModifiers(int successModifier, int difficultyModifier, out int index, out bool appended)
        {
            index = -1; appended = false;
            if (!CanSpendWithModifiers(successModifier, difficultyModifier)) return false;
            try
            {
                bool hit = _rollTokens(1)[0];
                index = Array.IndexOf(_tokens, false);
                if (index < 0)
                {
                    appended = true; index = _tokens.Length;
                    Array.Resize(ref _tokens, index + 1);
                }
                _tokens[index] = hit;
                FateRemaining--;
                return true;
            }
            catch { Cancel(); throw; }
        }

        public ResearchProductionSystem.ChallengeOutcome Accept()
        {
            if (IsAccepted) return _result;
            if (IsCancelled || !_initialized) throw new InvalidOperationException("The challenge cannot be accepted.");
            _result = new ResearchProductionSystem.ChallengeOutcome { Success = Success, Successes = Successes,
                Required = Required, FateSpent = FateSpent, FateAvailable = FateAvailable };
            IsAccepted = true;
            return _result;
        }
        public void Cancel() { if (!IsAccepted) IsCancelled = true; }
        public void Dispose() => Cancel();
        private TacticHand Hand => _hero?.Owner?.Tactics;
        public string TacticUnavailableReason(CardData card)
            => IsTerminal ? "This challenge has ended."
                : TacticRules.SideUnavailableReason(Hand, card, _initialized, TacticUsed)
                    ?? TacticRules.UnavailableReason(card.Definition, Required);
        public bool CanPlay(CardData card) => TacticUnavailableReason(card) == null;
        public bool HasTactic => TacticRules.HasPlayable(Hand, CanPlay);
        public bool TryPlay(CardData card) => TryPlay(card, out _);
        public bool TryPlay(CardData card, out string reason)
        {
            reason = TacticUnavailableReason(card);
            if (reason != null) return false;
            int a = SuccessModifier, d = DifficultyModifier;
            TacticRules.Apply(card.Definition, false, Required, ref a, ref d);
            SuccessModifier = a; DifficultyModifier = d;
            TacticUsed = true;
            try { Hand.Consume(card); }
            catch { Cancel(); throw; }
            return true;
        }
    }
}
