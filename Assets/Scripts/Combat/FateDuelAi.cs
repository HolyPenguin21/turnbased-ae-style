using System.Collections.Generic;
using UnityEngine;
using Game.Cards;
using Game.Units;

namespace Game.Combat
{
    // AI decision logic for whether to spend Fate during a duel turn (BattleAttackPopupUI.
    // RunAiTurn) — split out of BattleAi.cs to keep that file to arrangement/retreat/tactical-move
    // logic only.
    //
    // ---- Priority-cap model (per the user's own spec) ----
    // The defender keeps trying any damaging result while Fate and misses remain.
    // The attacker wants damage > 0; ability combos only change how many CURRENT
    // missed tokens it's willing to spend Fate rerolling toward it — `cap` = 2 when the attacker
    // carries ShockAttack, or a type-matched Hyperkinetic-vs-Armored/Pyrokinetic-vs-Bio/
    // Ionic-vs-Mechanical combo (see
    // AttackerCap), else 1 (base — don't blow the whole Fate pool on one attack).
    // `minRerolls` = fewest of the side's own current misses that need to flip to a hit for the
    // goal to be reached, found by literally trying k = 1, 2, ... against the SAME
    // ChallengeResult.ApplyAbilityModifiers the real roll resolves through (so this can never
    // disagree with what rerolling actually produces) — not just "how many misses exist". If no k
    // within the misses actually on the table gets there, the goal is physically unreachable and
    // spending is never worth it, regardless of cap.
    //
    // Attackers stop after a failed reroll; defenders re-evaluate and keep trying. Live and
    // simulated turn loops apply that rule after each spend.
    // THE turn order of a Fate duel (one owner for the live battle, BattleAttackPopupUI.RunDuel,
    // and the estimator, WorthIt.ResolveExchange): the defender decides first, the sides
    // alternate, a side that is done is skipped, and a side that spent re-opens the other one.
    // The duel ends when both sides are done. A plain mutable struct on purpose — the estimator
    // plays it inside a Monte-Carlo loop and must not allocate:
    //     var order = new FateDuelOrder();
    //     while (order.TryNext(out bool defenderTurn)) { ...one side's turn...; order.Report(spent); }
    public struct FateDuelOrder
    {
        private bool _defenderDone;
        private bool _attackerDone;
        private bool _attackerTurn;   // default false: the defender opens the duel

        // The side that decides next; false once both are done.
        public bool TryNext(out bool isDefenderTurn)
        {
            while (!_defenderDone || !_attackerDone)
            {
                if (_attackerTurn ? _attackerDone : _defenderDone)
                {
                    _attackerTurn = !_attackerTurn;
                    continue;
                }
                isDefenderTurn = !_attackerTurn;
                return true;
            }
            isDefenderTurn = false;
            return false;
        }

        // The side TryNext named has finished its turn; `spent` — it spent Fate at least once.
        public void Report(bool spent)
        {
            if (_attackerTurn) _attackerDone = true; else _defenderDone = true;
            if (spent)
            {
                if (_attackerTurn) _defenderDone = false; else _attackerDone = false;
            }
            _attackerTurn = !_attackerTurn;
        }
    }

    public static class FateDuelAi
    {
        // isDefender: true when evaluating the DEFENDER's own spend, false for the ATTACKER's.
        // isRetreating/defendingUnitHp remain in the API for existing callers; defending
        // Fate decisions now depend only on the current result and available rerolls.
        // isCaptureKill: the target hero's own Fate spend during a Capture Kill Challenge (see
        // BattleAttackPopupUI.ResolveCaptureKill) — a TIED roll resolves as Killed there, not the
        // safe non-event a 0-damage tie is in Ground Combat, so the defending hero must keep
        // rerolling on a tie too. That challenge deals no HP damage, so none of the ability-cap
        // logic below applies to it — same branch as before this refactor, unchanged.
        public static bool ShouldSpendFate(bool[] attackerTokens, bool[] defenderTokens, int fateAvailable, bool isDefender,
            UnitData attacker, UnitData defender, AbilityMagnitudes magnitudes,
            bool isRetreating = false, int defendingUnitHp = int.MaxValue, bool isCaptureKill = false,
            int attackerModifier = 0, int defenderModifier = 0) =>
            ShouldSpendFate(attackerTokens, defenderTokens, fateAvailable, isDefender,
                attacker?.Abilities, defender?.TypeTags, defender?.Abilities, magnitudes,
                isRetreating, defendingUnitHp, isCaptureKill, attackerModifier, defenderModifier);

        // The same decision on the ability/type facts alone — what WorthIt's simulated battles
        // carry (no live UnitData). One spending policy for the real duel and for the estimate.
        public static bool ShouldSpendFate(bool[] attackerTokens, bool[] defenderTokens, int fateAvailable, bool isDefender,
            IEnumerable<string> attackerAbilities, IReadOnlyCollection<UnitTypeTag> defenderTypeTags,
            IEnumerable<string> defenderAbilities, AbilityMagnitudes magnitudes,
            bool isRetreating = false, int defendingUnitHp = int.MaxValue, bool isCaptureKill = false,
            int attackerModifier = 0, int defenderModifier = 0)
        {
            var m = new Matchup(attackerAbilities, defenderTypeTags, defenderAbilities, magnitudes);
            if (fateAvailable <= 0)
                return false;
            bool[] ownTokens = isDefender ? defenderTokens : attackerTokens;
            if (ownTokens == null || !HasMiss(ownTokens))
                return false;

            var result = new ChallengeResult(attackerTokens, defenderTokens, attackerModifier, defenderModifier);

            if (isCaptureKill)
                return isDefender
                    ? result.AttackerSuccesses >= result.DefenderSuccesses
                    : result.Damage <= 0;

            int damage = m.Damage(result.Damage);

            return isDefender
                ? damage > 0
                : ShouldAttackerSpend(result, m, damage);
        }

        // The attacker's abilities and the defender's type/abilities of one exchange — all the
        // policy below ever needs to read about the two units.
        private readonly struct Matchup
        {
            private readonly IEnumerable<string> _attackerAbilities;
            private readonly IReadOnlyCollection<UnitTypeTag> _defenderTypeTags;
            private readonly IEnumerable<string> _defenderAbilities;
            private readonly AbilityMagnitudes _magnitudes;

            public Matchup(IEnumerable<string> attackerAbilities,
                IReadOnlyCollection<UnitTypeTag> defenderTypeTags, IEnumerable<string> defenderAbilities,
                AbilityMagnitudes magnitudes)
            {
                // Membership reads only — no HashSet copy (it was allocated on every Fate decision
                // inside WorthIt's Monte Carlo).
                _attackerAbilities = attackerAbilities ?? System.Array.Empty<string>();
                _defenderTypeTags = defenderTypeTags ?? System.Array.Empty<UnitTypeTag>();
                _defenderAbilities = defenderAbilities ?? System.Array.Empty<string>();
                _magnitudes = magnitudes;
            }

            public bool AttackerHas(string ability)
            {
                if (_attackerAbilities is IReadOnlyList<string> list)
                {
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] == ability)
                            return true;
                    return false;
                }
                foreach (string a in _attackerAbilities)
                    if (a == ability)
                        return true;
                return false;
            }
            public bool DefenderIs(UnitTypeTag tag) => System.Linq.Enumerable.Contains(_defenderTypeTags, tag);
            public int Damage(int rawDamage) => ChallengeResult.ApplyAbilityModifiers(rawDamage,
                _attackerAbilities, _defenderTypeTags, _defenderAbilities, _magnitudes);
        }

        private static bool ShouldAttackerSpend(ChallengeResult result, Matchup m, int currentDamage)
        {
            if (currentDamage > 0)
                return false; // already lands, nothing to fix

            int ownMisses = CountMisses(result.AttackerTokens);
            if (!TryMinRerollsForAttackerDamage(result, m, ownMisses, out int minRerolls))
                return false; // physically unreachable within the misses actually on the table
            return minRerolls <= AttackerCap(m);
        }

        // cap = 2 whenever the ATTACKER carries a combo that makes an eventual landed hit worth
        // more than a plain one — ShockAttack (knocks the target out of the rest of this round on
        // a hit) or a type-matched Hyperkinetic/Pyrokinetic bonus (the hit itself deals more once
        // it lands). Defensive spending does not use this cap.
        private static int AttackerCap(Matchup m)
        {
            bool boosted = m.AttackerHas(UnitAbilities.ShockAttack)
                || m.AttackerHas(UnitAbilities.Splash)   // a landed hit also spreads to neighbours
                || m.AttackerHas(UnitAbilities.Scorcher)
                || (m.AttackerHas(UnitAbilities.Hyperkinetic) && m.DefenderIs(UnitTypeTag.Armored))
                || (m.AttackerHas(UnitAbilities.Pyrokinetic) && m.DefenderIs(UnitTypeTag.Bio))
                || (m.AttackerHas(UnitAbilities.Ionic) && m.DefenderIs(UnitTypeTag.Mechanical));
            return boosted ? 2 : 1;
        }

        private static bool TryMinRerollsForAttackerDamage(ChallengeResult result, Matchup m,
            int ownMisses, out int minRerolls)
        {
            for (int k = 1; k <= ownMisses; k++)
            {
                int rawDamage = Mathf.Max(0, (result.AttackerSuccesses + k) - result.DefenderSuccesses);
                int damage = m.Damage(rawDamage);
                if (damage > 0)
                {
                    minRerolls = k;
                    return true;
                }
            }
            minRerolls = 0;
            return false;
        }

        private static int CountMisses(bool[] tokens)
        {
            int misses = 0;
            foreach (bool hit in tokens)
                if (!hit) misses++;
            return misses;
        }

        private static bool HasMiss(bool[] tokens)
        {
            if (tokens == null)
                return false;
            foreach (bool hit in tokens)
                if (!hit)
                    return true;
            return false;
        }
    }
}
