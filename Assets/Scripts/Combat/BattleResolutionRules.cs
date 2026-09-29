using System.Collections.Generic;
using Game.Cards;
using Game.Units;

namespace Game.Combat
{
    /// <summary>
    /// Pure resolution rules for already-rolled challenge dice. UI and simulations must both
    /// consume these results rather than restating success subtraction or capture/kill thresholds.
    /// </summary>
    public static class BattleResolutionRules
    {
        public readonly struct GroundAttackOutcome
        {
            public readonly int AttackerSuccesses;
            public readonly int DefenderSuccesses;
            public readonly int RawDamage;
            public readonly int Damage;
            public readonly bool WasHit;
            public readonly IReadOnlyList<string> AppliedAbilities;

            public GroundAttackOutcome(int attackerSuccesses, int defenderSuccesses, int rawDamage,
                int damage, IReadOnlyList<string> appliedAbilities)
            {
                AttackerSuccesses = attackerSuccesses;
                DefenderSuccesses = defenderSuccesses;
                RawDamage = rawDamage;
                Damage = damage;
                WasHit = rawDamage > 0;
                AppliedAbilities = appliedAbilities ?? System.Array.Empty<string>();
            }
        }

        public static GroundAttackOutcome ResolveGroundAttack(bool[] attackerDice, bool[] defenderDice,
            UnitData attacker, UnitData defender, AbilityMagnitudes magnitudes)
            => ResolveGroundAttack(attackerDice, defenderDice, attacker?.Abilities, defender?.TypeTags,
                defender?.Abilities, magnitudes);

        public static GroundAttackOutcome ResolveGroundAttack(bool[] attackerDice, bool[] defenderDice,
            IEnumerable<string> attackerAbilities, IReadOnlyCollection<UnitTypeTag> defenderTypeTags,
            IEnumerable<string> defenderAbilities, AbilityMagnitudes magnitudes)
        {
            var challenge = new ChallengeResult(attackerDice ?? System.Array.Empty<bool>(),
                defenderDice ?? System.Array.Empty<bool>());
            int damage = ChallengeResult.ApplyAbilityModifiers(challenge.Damage, attackerAbilities,
                defenderTypeTags, defenderAbilities, magnitudes, out List<string> appliedAbilities);
            return new GroundAttackOutcome(challenge.AttackerSuccesses, challenge.DefenderSuccesses,
                challenge.Damage, damage, appliedAbilities);
        }

        public static CaptureKillOutcome ResolveCaptureKill(bool[] attackerDice, bool[] defenderDice)
        {
            var challenge = new ChallengeResult(attackerDice ?? System.Array.Empty<bool>(),
                defenderDice ?? System.Array.Empty<bool>());
            if (challenge.AttackerSuccesses < challenge.DefenderSuccesses)
                return CaptureKillOutcome.Escaped;
            if (challenge.AttackerSuccesses > challenge.DefenderSuccesses)
                return CaptureKillOutcome.Captured;
            return CaptureKillOutcome.Killed;
        }
    }
}
