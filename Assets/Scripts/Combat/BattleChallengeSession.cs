using System;
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
            Func<int, bool[]> rollDice = null)
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
        }

        public void Roll()
        {
            AttackerDice = _rollDice(AttackerPoolSize) ?? Array.Empty<bool>();
            DefenderDice = _rollDice(DefenderPoolSize) ?? Array.Empty<bool>();
            LastSpendHit = false;
        }

        public bool HasAnyFate => AttackerFateRemaining > 0 || DefenderFateRemaining > 0;

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
                Mode == BattleChallengeMode.CaptureKill);
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

        public bool TryNextFateTurn(out bool defenderTurn) =>
            _fateOrder.TryNext(out defenderTurn);

        public void ReportFateTurn(bool spent) =>
            _fateOrder.Report(spent);

        public BattleResolutionRules.GroundAttackOutcome ResolveGroundOutcome() =>
            BattleResolutionRules.ResolveGroundAttack(
                AttackerDice ?? Array.Empty<bool>(),
                DefenderDice ?? Array.Empty<bool>(),
                _attacker, _defender, _magnitudes);

        public CaptureKillOutcome ResolveCaptureKillOutcome() =>
            BattleResolutionRules.ResolveCaptureKill(
                AttackerDice ?? Array.Empty<bool>(),
                DefenderDice ?? Array.Empty<bool>());

        public int RawDamage =>
            new ChallengeResult(
                AttackerDice ?? Array.Empty<bool>(),
                DefenderDice ?? Array.Empty<bool>()).Damage;

        public BattleChallengeRollResult ToRollResult() =>
            new BattleChallengeRollResult(
                AttackerDice ?? Array.Empty<bool>(),
                DefenderDice ?? Array.Empty<bool>(),
                AttackerFateRemaining, DefenderFateRemaining);
    }
}
