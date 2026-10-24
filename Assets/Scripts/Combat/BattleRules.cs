using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Map;
using Game.Units;

namespace Game.Combat
{
    /// <summary>
    /// Once-per-round Bodyguard reactions already spent this round. BattleEngine owns the live
    /// one and clears it at round start; a shadow-grid projection keeps its own per simulated round.
    /// </summary>
    public sealed class BattleReactionState
    {
        public readonly HashSet<UnitData> BodyguardsUsed = new HashSet<UnitData>();

        public void Clear()
        {
            BodyguardsUsed.Clear();
        }
    }

    /// <summary>
    /// Canonical tactical-battle rules shared by the live engine, the UI and simulations:
    /// pre-battle arrangement legality, grid-derived protection, and resolution of rolled tokens.
    /// </summary>
    public static class BattleRules
    {
        // ---- Arrangement ----
        // Legality for the pre-battle Arrangement phase. Tactical movement after round start is
        // intentionally NOT governed here.
        public static bool IsOwnDeploymentRow(int row, int frontRow, int backRow)
            => row == frontRow || row == backRow;

        public static bool CanPlace(UnitData unit, int row, int col, int frontRow, int backRow)
        {
            if (unit == null || !BattleGrid.InBounds(row, col))
                return false;
            if (!IsOwnDeploymentRow(row, frontRow, backRow))
                return false;

            // Heroes are commanders/support pieces, not ordinary front-line combatants.
            // They may occupy any column of the back row; the historical column-0 reservation
            // is only a default-layout convention, not a unique legal hero slot.
            if (unit.IsHero)
                return row == backRow;

            // Ordinary combatants may be arranged freely between the side's front/back rows.
            // Range influences AI preference, not legality.
            return true;
        }

        public static bool CanSwap(BattleGrid grid, int fromRow, int fromCol, int toRow, int toCol,
            int frontRow, int backRow)
        {
            if (grid == null
                || !BattleGrid.InBounds(fromRow, fromCol)
                || !BattleGrid.InBounds(toRow, toCol)
                || !IsOwnDeploymentRow(fromRow, frontRow, backRow)
                || !IsOwnDeploymentRow(toRow, frontRow, backRow))
                return false;

            UnitData moving = grid.Get(fromRow, fromCol);
            UnitData displaced = grid.Get(toRow, toCol);
            if (moving == null || !CanPlace(moving, toRow, toCol, frontRow, backRow))
                return false;

            return displaced == null || CanPlace(displaced, fromRow, fromCol, frontRow, backRow);
        }

        // ---- Protection ----
        // Spatial defensive rules that are derived from the live tactical grid.
        public const int GuardedDefenseBonus = 1;

        private static readonly int[] OrthogonalRows = { -1, 1, 0, 0 };
        private static readonly int[] OrthogonalCols = { 0, 0, -1, 1 };

        public static int GetTotalDefenseBonus(BattleGrid grid, UnitData unit, ArmyData battleDefender,
            int defenderHexBonus)
        {
            int hex = battleDefender != null && unit != null && battleDefender.Members.Contains(unit)
                ? defenderHexBonus : 0;
            return hex + GetGuardedDefenseBonus(grid, unit, battleDefender);
        }

        public static int GetGuardedDefenseBonus(BattleGrid grid, UnitData unit, ArmyData battleDefender)
        {
            if (grid == null || unit == null || (!unit.IsGroundCombatant && !unit.IsHero)
                || !grid.TryFindPosition(unit, out int row, out int col))
                return 0;

            bool defenderSide = battleDefender != null && battleDefender.Members.Contains(unit);
            int ownBackRow = defenderSide ? BattleGrid.DefenderBackRow : BattleGrid.AttackerBackRow;
            int ownFrontRow = defenderSide ? BattleGrid.DefenderFrontRow : BattleGrid.AttackerFrontRow;
            if (row != ownBackRow)
                return 0;

            int minCol = System.Math.Max(0, col - 1);
            int maxCol = System.Math.Min(BattleGrid.Columns - 1, col + 1);
            for (int c = minCol; c <= maxCol; c++)
            {
                UnitData guard = grid.Get(ownFrontRow, c);
                if (guard != null && guard.IsGroundCombatant && guard.Owner == unit.Owner
                    && guard.HitPointsCurrent > 0)
                    return GuardedDefenseBonus;
            }

            return 0;
        }

        // UnitAbilities.Bodyguard — the guard that takes a hit aimed at `victim`: a living,
        // unspent, same-owner ground combatant carrying Bodyguard on an orthogonal neighbour cell
        // (never the victim or the attacker). The most current HP wins, ties go to the first cell
        // in up/down/left/right order. `hpOf` lets a projection read its shadow HP.
        public static UnitData FindBodyguard(BattleGrid grid, UnitData victim, UnitData attacker,
            Func<UnitData, float> hpOf, BattleReactionState reactions)
        {
            if (grid == null || victim == null || hpOf == null
                || !grid.TryFindPosition(victim, out int row, out int col))
                return null;

            UnitData best = null;
            float bestHp = 0f;
            for (int i = 0; i < OrthogonalRows.Length; i++)
            {
                UnitData guard = grid.Get(row + OrthogonalRows[i], col + OrthogonalCols[i]);
                if (guard == null || guard == victim || guard == attacker || guard.Owner != victim.Owner
                    || !guard.IsGroundCombatant || guard.IsPrisoner
                    || !guard.HasAbility(UnitAbilities.Bodyguard)
                    || (reactions != null && reactions.BodyguardsUsed.Contains(guard)))
                    continue;
                float hp = hpOf(guard);
                if (hp > bestHp)
                {
                    best = guard;
                    bestHp = hp;
                }
            }
            return best;
        }

        // UnitAbilities.Counterattack — whether `victim` (after the hit, at `victimHp`) answers
        // `attacker` now. "Within 1 cell" is the same square range BattleGrid.IsInRange uses for
        // a Range-1 attack.
        public static bool CanCounterattack(BattleGrid grid, UnitData victim, UnitData attacker,
            float victimHp, int primaryDamage)
        {
            if (grid == null || victim == null || attacker == null || victim.Owner == attacker.Owner
                || victim.IsPrisoner
                || !grid.TryFindPosition(victim, out int vr, out int vc)
                || !grid.TryFindPosition(attacker, out int ar, out int ac))
                return false;
            return BattleSimulationKernel.TriggersCounterattack(victim.Abilities, victim.IsHero, victimHp,
                BattleGrid.IsInRange(vr, vc, ar, ac, 1),
                primaryDamage, attacker.Abilities);
        }

        // ---- Resolution ----
        // Pure resolution rules for already-rolled challenge tokens. UI and simulations must both
        // consume these results rather than restating success subtraction or capture/kill thresholds.
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

        public static GroundAttackOutcome ResolveGroundAttack(bool[] attackerTokens, bool[] defenderTokens,
            UnitData attacker, UnitData defender, AbilityMagnitudes magnitudes,
            int attackerModifier = 0, int defenderModifier = 0)
            => ResolveGroundAttack(attackerTokens, defenderTokens, attacker?.Abilities, defender?.TypeTags,
                defender?.Abilities, magnitudes, attackerModifier, defenderModifier);

        public static GroundAttackOutcome ResolveGroundAttack(bool[] attackerTokens, bool[] defenderTokens,
            IEnumerable<string> attackerAbilities, IReadOnlyCollection<UnitTypeTag> defenderTypeTags,
            IEnumerable<string> defenderAbilities, AbilityMagnitudes magnitudes,
            int attackerModifier = 0, int defenderModifier = 0)
        {
            var challenge = new ChallengeResult(attackerTokens ?? System.Array.Empty<bool>(),
                defenderTokens ?? System.Array.Empty<bool>(), attackerModifier, defenderModifier);
            int damage = ChallengeResult.ApplyAbilityModifiers(challenge.Damage, attackerAbilities,
                defenderTypeTags, defenderAbilities, magnitudes, out List<string> appliedAbilities);
            return new GroundAttackOutcome(challenge.AttackerSuccesses, challenge.DefenderSuccesses,
                challenge.Damage, damage, appliedAbilities);
        }

        // Ground effects come from the engine's actual collateral plan and pending turn state.
        // Field attacks (aviation/AA/explosions) pass no ground effects, but still apply Berserk.
        public static void AppendHitEffectAbilities(List<string> appliedAbilities, UnitData attacker,
            UnitData victim, int damage, IReadOnlyList<string> groundEffects = null)
        {
            if (appliedAbilities == null || attacker == null || victim == null || damage <= 0)
                return;
            if (groundEffects != null)
                foreach (string effect in groundEffects) Add(effect);
            if (victim.HasAbility(UnitAbilities.Berserk)) Add(UnitAbilities.Berserk);

            void Add(string ability)
            {
                if (!appliedAbilities.Contains(ability)) appliedAbilities.Add(ability);
            }
        }

        // The live popup and the knowledge-only estimate use the same hunter pool.
        public static int CaptureKillHunterPool(int nonHeroUnitCount) =>
            1 + System.Math.Max(0, nonHeroUnitCount) / 2;

        public static CaptureKillOutcome ResolveCaptureKill(bool[] attackerTokens, bool[] defenderTokens,
            int attackerModifier = 0, int defenderModifier = 0)
        {
            var challenge = new ChallengeResult(attackerTokens ?? System.Array.Empty<bool>(),
                defenderTokens ?? System.Array.Empty<bool>(), attackerModifier, defenderModifier);
            if (challenge.AttackerSuccesses < challenge.DefenderSuccesses)
                return CaptureKillOutcome.Escaped;
            if (challenge.AttackerSuccesses > challenge.DefenderSuccesses)
                return CaptureKillOutcome.Captured;
            return CaptureKillOutcome.Killed;
        }
    }
}
