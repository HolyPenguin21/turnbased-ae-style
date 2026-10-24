using System;
using Game.Cards;
using Game.Units;

namespace Game.Combat
{
    public enum BattleChallengeMode
    {
        GroundCombat,
        CaptureKill,
    }

    // Presentation-neutral mutable state for one tokens/Fate challenge. The popup may animate and
    // ask a human what to do, but tokens, remaining Fate and rerolls live here.
    public sealed class BattleChallengeSession
    {
        private readonly UnitData _attacker;
        private readonly UnitData _defender;
        private readonly AbilityMagnitudes _magnitudes;
        private readonly bool _defenderIsRetreating;
        private readonly Func<int, bool[]> _rollTokens;
        private FateDuelOrder _fateOrder;
        private bool? _decisionSide;
        private bool _attackerTacticUsed, _defenderTacticUsed;
        public int AttackerModifier { get; private set; }
        public int DefenderModifier { get; private set; }
        public bool TacticsCritical { get; }

        public BattleChallengeMode Mode { get; }
        public int AttackerPoolSize { get; }
        public int DefenderPoolSize { get; }
        public bool[] AttackerTokens { get; private set; }
        public bool[] DefenderTokens { get; private set; }
        public int AttackerFateRemaining { get; private set; }
        public int DefenderFateRemaining { get; private set; }
        public bool LastSpendHit { get; private set; }

        public BattleChallengeSession(BattleChallengeMode mode,
            UnitData attacker, UnitData defender,
            int attackerPoolSize, int defenderPoolSize,
            int attackerFate, int defenderFate,
            AbilityMagnitudes magnitudes, bool defenderIsRetreating = false,
            Func<int, bool[]> rollTokens = null, bool tacticsCritical = false)
        {
            Mode = mode;
            _attacker = attacker;
            _defender = defender;
            AttackerPoolSize = Math.Max(0, attackerPoolSize);
            DefenderPoolSize = Math.Max(0, defenderPoolSize);
            AttackerFateRemaining = Math.Max(0, attackerFate);
            DefenderFateRemaining = Math.Max(0, defenderFate);
            _magnitudes = magnitudes;
            _defenderIsRetreating = defenderIsRetreating;
            _rollTokens = rollTokens ?? ChallengeResolver.RollTokens;
            TacticsCritical = tacticsCritical;
        }

        public void Roll()
        {
            AttackerTokens = _rollTokens(AttackerPoolSize) ?? Array.Empty<bool>();
            DefenderTokens = _rollTokens(DefenderPoolSize) ?? Array.Empty<bool>();
            LastSpendHit = false;
        }

        // Headless presentation runs the same Fate/tactic state machine as the popup.
        public void ResolveAiDecisions()
        {
            while (TryNextFateTurn(out bool defenderTurn))
            {
                bool spent = false;
                while (ShouldAiSpend(defenderTurn) && TrySpend(defenderTurn, out _, out bool hit))
                {
                    spent = true;
                    if (!defenderTurn && !hit) break;
                }
                var tactic = ChooseAiTactic(defenderTurn);
                if (tactic != null && TryPlayTactic(defenderTurn, tactic)) spent = true;
                ReportFateTurn(spent);
            }
        }

        public bool HasAnyFate => AttackerFateRemaining > 0 || DefenderFateRemaining > 0;
        public bool HasAnyDecision => HasAnyFate || HasTactic(false) || HasTactic(true);
        public ChallengeResult CurrentResult => new ChallengeResult(
            AttackerTokens ?? Array.Empty<bool>(), DefenderTokens ?? Array.Empty<bool>(),
            AttackerModifier, DefenderModifier);

        private TacticHand HandOf(bool defenderSide) => (defenderSide ? _defender : _attacker)?.Owner?.Tactics;

        public string TacticUnavailableReason(bool defenderSide, CardData card)
        {
            string reason = TacticRules.SideUnavailableReason(HandOf(defenderSide), card,
                AttackerTokens != null && DefenderTokens != null, defenderSide ? _defenderTacticUsed : _attackerTacticUsed);
            if (reason != null) return reason;
            var result = CurrentResult;
            return TacticRules.UnavailableReason(card.Definition,
                defenderSide ? result.AttackerSuccesses : result.DefenderSuccesses);
        }

        public bool CanPlayTactic(bool defenderSide, CardData card) => TacticUnavailableReason(defenderSide, card) == null;

        public bool HasTactic(bool defenderSide) => TacticRules.HasPlayable(HandOf(defenderSide), c => CanPlayTactic(defenderSide, c));

        public bool TryPlayTactic(bool defenderSide, CardData card)
            => TryPlayTactic(defenderSide, card, out _);

        public bool TryPlayTactic(bool defenderSide, CardData card, out string reason)
        {
            reason = _decisionSide != defenderSide ? "Wait for your challenge turn; the defender acts first."
                : TacticUnavailableReason(defenderSide, card);
            if (reason != null) return false;
            var result = CurrentResult;
            int a = AttackerModifier, d = DefenderModifier;
            TacticRules.Apply(card.Definition, defenderSide,
                defenderSide ? result.AttackerSuccesses : result.DefenderSuccesses, ref a, ref d);
            if (defenderSide) _defenderTacticUsed = true; else _attackerTacticUsed = true;
            AttackerModifier = a; DefenderModifier = d;
            HandOf(defenderSide).Consume(card);
            return true;
        }

        // Scarce cards are considered only in an admitted critical encounter, after the existing
        // Fate policy finishes. Compare real ability-modified damage, not just tokens differences.
        public CardData ChooseAiTactic(bool defenderSide, UnitData interceptor = null)
        {
            if (!TacticsCritical) return null;
            if (!defenderSide && _attacker?.Owner == _defender?.Owner) return null;
            // A human's cards are spent only by that human. An automated human side (Fast Resolve,
            // headless strikes) keeps its inventory; the AI policy is for AI owners.
            if ((defenderSide ? _defender : _attacker)?.Owner?.IsHuman == true) return null;
            var hand = HandOf(defenderSide);
            if (hand == null) return null;
            CardData best = null;
            int bestScore = 0;
            foreach (var card in hand.Cards)
            {
                if (!CanPlayTactic(defenderSide, card)) continue;
                int a = AttackerModifier, d = DefenderModifier;
                var before = CurrentResult;
                TacticRules.Apply(card.Definition, defenderSide,
                    defenderSide ? before.AttackerSuccesses : before.DefenderSuccesses, ref a, ref d);
                int score = Mode == BattleChallengeMode.CaptureKill
                    ? ScoreCaptureKillTactic(defenderSide, a, d)
                    : ScoreGroundTactic(defenderSide, a, d, interceptor);
                if (score > bestScore) { best = card; bestScore = score; }
            }
            return best;
        }

        // The target hero wants out (Escaped); the hunter wants a Capture, and a Kill over an Escape.
        private int ScoreCaptureKillTactic(bool defenderSide, int a, int d)
        {
            var oldOutcome = ResolveCaptureKillOutcome();
            var next = BattleRules.ResolveCaptureKill(AttackerTokens, DefenderTokens, a, d);
            if (next == oldOutcome) return 0;
            if (defenderSide) return next == CaptureKillOutcome.Escaped ? 100 : 0;
            return next == CaptureKillOutcome.Captured ? 100
                : oldOutcome == CaptureKillOutcome.Escaped ? 60 : 0;
        }

        private int ScoreGroundTactic(bool defenderSide, int a, int d, UnitData interceptor)
        {
            int oldDamage = ProjectDamage(AttackerModifier, DefenderModifier, interceptor, out var oldVictim);
            int nextDamage = ProjectDamage(a, d, interceptor, out var nextVictim);
            int oldHp = oldVictim?.HitPointsCurrent ?? int.MaxValue;
            int nextHp = nextVictim?.HitPointsCurrent ?? int.MaxValue;
            if (defenderSide)
                return oldDamage >= oldHp && nextDamage < nextHp ? 100
                    : oldDamage > 0 && nextDamage == 0 ? 20 : 0;
            return nextDamage >= nextHp && oldDamage < oldHp ? 100
                : oldDamage == 0 && nextDamage > 0 && _attacker.HasAbility(UnitAbilities.ShockAttack) ? 20 : 0;
        }

        private int ProjectDamage(int a, int d, UnitData interceptor, out UnitData victim)
        {
            var outcome = BattleRules.ResolveGroundAttack(AttackerTokens, DefenderTokens,
                _attacker, _defender, _magnitudes, a, d);
            victim = outcome.Damage > 0 && interceptor != null ? interceptor : _defender;
            return victim == interceptor && interceptor != null
                ? ChallengeResult.ApplyAbilityModifiers(outcome.RawDamage, _attacker, interceptor, _magnitudes)
                : outcome.Damage;
        }

        public bool HasMiss(bool defenderSide)
        {
            bool[] tokens = defenderSide ? DefenderTokens : AttackerTokens;
            if (tokens == null)
                return false;
            foreach (bool value in tokens)
                if (!value)
                    return true;
            return false;
        }

        public bool CanSpend(bool defenderSide)
            => (defenderSide ? DefenderFateRemaining : AttackerFateRemaining) > 0
                && HasMiss(defenderSide);

        public bool ShouldAiSpend(bool defenderSide)
        {
            int fate = defenderSide ? DefenderFateRemaining : AttackerFateRemaining;
            if (fate <= 0 || !HasMiss(defenderSide))
                return false;

            return FateDuelAi.ShouldSpendFate(
                AttackerTokens, DefenderTokens, fate, defenderSide,
                _attacker, _defender, _magnitudes,
                defenderSide && _defenderIsRetreating,
                _defender != null ? _defender.HitPointsCurrent : int.MaxValue,
                Mode == BattleChallengeMode.CaptureKill, AttackerModifier, DefenderModifier);
        }

        public bool TrySpend(bool defenderSide, out int rerolledIndex, out bool rerolledHit)
        {
            rerolledIndex = -1;
            rerolledHit = false;
            if (!CanSpend(defenderSide))
                return false;

            bool[] tokens = defenderSide ? DefenderTokens : AttackerTokens;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i])
                    continue;
                bool[] reroll = _rollTokens(1);
                bool hit = reroll != null && reroll.Length > 0 && reroll[0];
                tokens[i] = hit;
                rerolledIndex = i;
                rerolledHit = hit;
                LastSpendHit = hit;
                if (defenderSide)
                    DefenderFateRemaining--;
                else
                    AttackerFateRemaining--;
                return true;
            }
            return false;
        }

        public bool TryNextFateTurn(out bool defenderTurn)
        {
            bool hasTurn = _fateOrder.TryNext(out defenderTurn);
            _decisionSide = hasTurn ? defenderTurn : (bool?)null;
            return hasTurn;
        }

        public void ReportFateTurn(bool spent)
        {
            _decisionSide = null;
            _fateOrder.Report(spent);
        }

        public BattleRules.GroundAttackOutcome ResolveGroundOutcome() =>
            BattleRules.ResolveGroundAttack(
                AttackerTokens ?? Array.Empty<bool>(),
                DefenderTokens ?? Array.Empty<bool>(),
                _attacker, _defender, _magnitudes, AttackerModifier, DefenderModifier);

        public CaptureKillOutcome ResolveCaptureKillOutcome() =>
            BattleRules.ResolveCaptureKill(
                AttackerTokens ?? Array.Empty<bool>(),
                DefenderTokens ?? Array.Empty<bool>(), AttackerModifier, DefenderModifier);

        public int RawDamage =>
            new ChallengeResult(
                AttackerTokens ?? Array.Empty<bool>(),
                DefenderTokens ?? Array.Empty<bool>(), AttackerModifier, DefenderModifier).Damage;

        public BattleChallengeRollResult ToRollResult() =>
            new BattleChallengeRollResult(
                AttackerTokens ?? Array.Empty<bool>(),
                DefenderTokens ?? Array.Empty<bool>(),
                AttackerFateRemaining, DefenderFateRemaining, AttackerModifier, DefenderModifier);
    }
}
