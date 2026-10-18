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

    // Presentation-neutral mutable state for one dice/Fate challenge. The popup may animate and
    // ask a human what to do, but dice, remaining Fate and rerolls live here.
    public sealed class BattleChallengeSession
    {
        private readonly UnitData _attacker;
        private readonly UnitData _defender;
        private readonly AbilityMagnitudes _magnitudes;
        private readonly bool _defenderIsRetreating;
        private readonly Func<int, bool[]> _rollDice;
        private FateDuelOrder _fateOrder;
        private bool? _decisionSide;
        private bool _attackerTacticUsed, _defenderTacticUsed;
        public int AttackerModifier { get; private set; }
        public int DefenderModifier { get; private set; }
        public bool TacticsCritical { get; }

        public BattleChallengeMode Mode { get; }
        public int AttackerPoolSize { get; }
        public int DefenderPoolSize { get; }
        public bool[] AttackerDice { get; private set; }
        public bool[] DefenderDice { get; private set; }
        public int AttackerFateRemaining { get; private set; }
        public int DefenderFateRemaining { get; private set; }
        public bool LastSpendHit { get; private set; }

        public BattleChallengeSession(BattleChallengeMode mode,
            UnitData attacker, UnitData defender,
            int attackerPoolSize, int defenderPoolSize,
            int attackerFate, int defenderFate,
            AbilityMagnitudes magnitudes, bool defenderIsRetreating = false,
            Func<int, bool[]> rollDice = null, bool tacticsCritical = false)
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
            _rollDice = rollDice ?? ChallengeResolver.RollDice;
            TacticsCritical = tacticsCritical;
        }

        public void Roll()
        {
            AttackerDice = _rollDice(AttackerPoolSize) ?? Array.Empty<bool>();
            DefenderDice = _rollDice(DefenderPoolSize) ?? Array.Empty<bool>();
            LastSpendHit = false;
        }

        public bool HasAnyFate => AttackerFateRemaining > 0 || DefenderFateRemaining > 0;
        public bool HasAnyDecision => HasAnyFate || HasTactic(false) || HasTactic(true);
        public ChallengeResult CurrentResult => new ChallengeResult(
            AttackerDice ?? Array.Empty<bool>(), DefenderDice ?? Array.Empty<bool>(),
            AttackerModifier, DefenderModifier);

        public string TacticUnavailableReason(bool defenderSide, CardData card)
        {
            if (card == null) return "Select a tactical card first.";
            if (AttackerDice == null || DefenderDice == null) return "Wait for the dice roll to finish.";
            if (defenderSide ? _defenderTacticUsed : _attackerTacticUsed)
                return "You have already used a tactical card in this challenge.";
            var owner = (defenderSide ? _defender : _attacker)?.Owner;
            if (owner?.Tactics.Contains(card) != true) return "This tactical card is not in your hand.";
            var result = CurrentResult;
            return TacticRules.UnavailableReason(card.Definition,
                defenderSide ? result.AttackerSuccesses : result.DefenderSuccesses);
        }

        public bool CanPlayTactic(bool defenderSide, CardData card) => TacticUnavailableReason(defenderSide, card) == null;

        public bool HasTactic(bool defenderSide)
        {
            var hand = (defenderSide ? _defender : _attacker)?.Owner?.Tactics;
            if (hand == null) return false;
            foreach (var card in hand.Cards) if (CanPlayTactic(defenderSide, card)) return true;
            return false;
        }

        public bool TryPlayTactic(bool defenderSide, CardData card)
            => TryPlayTactic(defenderSide, card, out _);

        public bool TryPlayTactic(bool defenderSide, CardData card, out string reason)
        {
            reason = _decisionSide != defenderSide ? "Wait for your challenge decision. The defender acts first."
                : TacticUnavailableReason(defenderSide, card);
            if (reason != null) return false;
            var result = CurrentResult;
            int a = AttackerModifier, d = DefenderModifier;
            TacticRules.Apply(card.Definition, defenderSide,
                defenderSide ? result.AttackerSuccesses : result.DefenderSuccesses, ref a, ref d);
            if (defenderSide) _defenderTacticUsed = true; else _attackerTacticUsed = true;
            AttackerModifier = a; DefenderModifier = d;
            (defenderSide ? _defender : _attacker).Owner.Tactics.Consume(card);
            return true;
        }

        // Scarce cards are considered only in an admitted critical encounter, after the existing
        // Fate policy finishes. Compare real ability-modified damage, not just dice differences.
        public CardData ChooseAiTactic(bool defenderSide, UnitData interceptor = null)
        {
            if (!TacticsCritical) return null;
            var hand = (defenderSide ? _defender : _attacker)?.Owner?.Tactics;
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
                int score;
                if (Mode == BattleChallengeMode.CaptureKill)
                {
                    var oldOutcome = ResolveCaptureKillOutcome();
                    var next = BattleResolutionRules.ResolveCaptureKill(AttackerDice, DefenderDice, a, d);
                    score = next == oldOutcome ? 0 : defenderSide
                        ? (next == CaptureKillOutcome.Escaped ? 100 : 0)
                        : (next == CaptureKillOutcome.Captured ? 100 : 0);
                }
                else
                {
                    int oldDamage = ProjectDamage(AttackerModifier, DefenderModifier, interceptor, out var oldVictim);
                    int nextDamage = ProjectDamage(a, d, interceptor, out var nextVictim);
                    int hp = nextVictim?.HitPointsCurrent ?? int.MaxValue;
                    score = defenderSide
                        ? oldDamage >= (oldVictim?.HitPointsCurrent ?? int.MaxValue) && nextDamage < hp ? 100
                            : oldDamage > 0 && nextDamage == 0 ? 20 : 0
                        : nextDamage >= hp && oldDamage < (oldVictim?.HitPointsCurrent ?? int.MaxValue) ? 100
                            : oldDamage == 0 && nextDamage > 0 && _attacker.HasAbility(UnitAbilities.ShockAttack) ? 20 : 0;
                }
                if (score > bestScore) { best = card; bestScore = score; }
            }
            return best;
        }

        private int ProjectDamage(int a, int d, UnitData interceptor, out UnitData victim)
        {
            var outcome = BattleResolutionRules.ResolveGroundAttack(AttackerDice, DefenderDice,
                _attacker, _defender, _magnitudes, a, d);
            victim = outcome.Damage > 0 && interceptor != null ? interceptor : _defender;
            return victim == interceptor && interceptor != null
                ? ChallengeResult.ApplyAbilityModifiers(outcome.RawDamage, _attacker, interceptor, _magnitudes)
                : outcome.Damage;
        }

        public bool HasMiss(bool defenderSide)
        {
            bool[] dice = defenderSide ? DefenderDice : AttackerDice;
            if (dice == null)
                return false;
            foreach (bool value in dice)
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
                AttackerDice, DefenderDice, fate, defenderSide,
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

            bool[] dice = defenderSide ? DefenderDice : AttackerDice;
            for (int i = 0; i < dice.Length; i++)
            {
                if (dice[i])
                    continue;
                bool[] reroll = _rollDice(1);
                bool hit = reroll != null && reroll.Length > 0 && reroll[0];
                dice[i] = hit;
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

        public BattleResolutionRules.GroundAttackOutcome ResolveGroundOutcome() =>
            BattleResolutionRules.ResolveGroundAttack(
                AttackerDice ?? Array.Empty<bool>(),
                DefenderDice ?? Array.Empty<bool>(),
                _attacker, _defender, _magnitudes, AttackerModifier, DefenderModifier);

        public CaptureKillOutcome ResolveCaptureKillOutcome() =>
            BattleResolutionRules.ResolveCaptureKill(
                AttackerDice ?? Array.Empty<bool>(),
                DefenderDice ?? Array.Empty<bool>(), AttackerModifier, DefenderModifier);

        public int RawDamage =>
            new ChallengeResult(
                AttackerDice ?? Array.Empty<bool>(),
                DefenderDice ?? Array.Empty<bool>(), AttackerModifier, DefenderModifier).Damage;

        public BattleChallengeRollResult ToRollResult() =>
            new BattleChallengeRollResult(
                AttackerDice ?? Array.Empty<bool>(),
                DefenderDice ?? Array.Empty<bool>(),
                AttackerFateRemaining, DefenderFateRemaining, AttackerModifier, DefenderModifier);
    }
}
