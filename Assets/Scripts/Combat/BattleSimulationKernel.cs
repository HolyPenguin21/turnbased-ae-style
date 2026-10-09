using System;
using System.Collections.Generic;
using Game.Cards;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    public readonly struct BattleSimExchangeOutcome
    {
        public readonly int Damage;
        public readonly bool Hit;
        public readonly int AttackerSuccesses;
        public readonly int DefenderSuccesses;

        public BattleSimExchangeOutcome(int damage, bool hit, int attackerSuccesses, int defenderSuccesses)
        {
            Damage = damage;
            Hit = hit;
            AttackerSuccesses = attackerSuccesses;
            DefenderSuccesses = defenderSuccesses;
        }
    }

    public readonly struct BattleSimSecondaryTarget
    {
        public readonly int Index;
        public readonly string Skill;

        public BattleSimSecondaryTarget(int index, string skill)
        {
            Index = index;
            Skill = skill;
        }
    }

    // Shared stochastic combat kernel for tactical projections and strategic WorthIt estimates.
    // Geometry/target selection live outside this class; one armed exchange must be identical.
    public static class BattleSimulationKernel
    {
        // Exact expectation of ONE exchange, without Fate or geometry. Production uses this
        // bounded projection; full battle decisions still use WorthIt. The difference of the
        // two fair dice pools is Binomial(attack + defense, .5) - defense.
        public static float ExpectedExchangeDamage(int attack, int defense,
            IEnumerable<string> attackerAbilities, IReadOnlyCollection<UnitTypeTag> defenderTags,
            IEnumerable<string> defenderAbilities, int hpCap, out float hitChance,
            bool secondary = false, IEnumerable<string> secondaryAbilities = null)
        {
            attack = Mathf.Max(0, attack);
            defense = Mathf.Max(0, defense);
            int n = attack + defense;
            int center = n / 2;
            double mass = 0, damage = 0, hits = 0;
            void Accumulate(int successes, double weight)
            {
                int d = ChallengeResult.ApplyAbilityModifiers(Mathf.Max(0, successes - defense),
                    attackerAbilities, defenderTags, defenderAbilities, AbilityMagnitudes.Default);
                mass += weight;
                if (d > 0) hits += weight;
                if (secondary) d = SecondaryDamage(d, secondaryAbilities, AbilityMagnitudes.Default);
                damage += weight * Mathf.Min(d, Mathf.Max(0, hpCap));
            }
            Accumulate(center, 1);
            double w = 1;
            for (int k = center; k > 0; k--)
            {
                w *= (double)k / (n - k + 1);
                Accumulate(k - 1, w);
            }
            w = 1;
            for (int k = center; k < n; k++)
            {
                w *= (double)(n - k) / (k + 1);
                Accumulate(k + 1, w);
            }
            hitChance = (float)(hits / mass);
            return (float)(damage / mass);
        }

        public static BattleSimExchangeOutcome ResolveExchange(int attackPool, int defensePool,
            IEnumerable<string> attackerAbilities, IReadOnlyCollection<UnitTypeTag> defenderTypeTags,
            IEnumerable<string> defenderAbilities, ref int attackerFate, ref int defenderFate,
            int defenderHp, AbilityMagnitudes magnitudes, System.Random rng,
            bool defenderIsRetreating = false)
        {
            if (rng == null)
                throw new ArgumentNullException(nameof(rng));

            bool[] attackDice = RollDice(Mathf.Max(0, attackPool), rng);
            bool[] defenseDice = RollDice(Mathf.Max(0, defensePool), rng);

            ResolveFateDuel(attackDice, defenseDice, attackerAbilities, defenderTypeTags,
                defenderAbilities, ref attackerFate, ref defenderFate, defenderHp,
                magnitudes, rng, defenderIsRetreating, isCaptureKill: false);

            BattleResolutionRules.GroundAttackOutcome outcome = BattleResolutionRules.ResolveGroundAttack(
                attackDice, defenseDice, attackerAbilities, defenderTypeTags, defenderAbilities, magnitudes);
            return new BattleSimExchangeOutcome(outcome.Damage, outcome.Damage > 0,
                outcome.AttackerSuccesses, outcome.DefenderSuccesses);
        }

        public static CaptureKillOutcome ResolveCaptureKill(int hunterPool, int heroPool,
            ref int hunterFate, ref int heroFate, System.Random rng)
        {
            if (rng == null)
                throw new ArgumentNullException(nameof(rng));
            bool[] hunterDice = RollDice(hunterPool, rng);
            bool[] heroDice = RollDice(heroPool, rng);
            ResolveFateDuel(hunterDice, heroDice, null, null, null,
                ref hunterFate, ref heroFate, int.MaxValue, AbilityMagnitudes.Default,
                rng, defenderIsRetreating: false, isCaptureKill: true);
            return BattleResolutionRules.ResolveCaptureKill(hunterDice, heroDice);
        }

        private static void ResolveFateDuel(bool[] attackDice, bool[] defenseDice,
            IEnumerable<string> attackerAbilities, IReadOnlyCollection<UnitTypeTag> defenderTypeTags,
            IEnumerable<string> defenderAbilities, ref int attackerFate, ref int defenderFate,
            int defenderHp, AbilityMagnitudes magnitudes, System.Random rng,
            bool defenderIsRetreating, bool isCaptureKill)
        {
            if (attackerFate > 0 || defenderFate > 0)
            {
                var order = new FateDuelOrder();
                while (order.TryNext(out bool defenderTurn))
                {
                    bool spent = defenderTurn
                        ? RunFateTurn(attackDice, defenseDice, defenseDice, true, ref defenderFate,
                            attackerAbilities, defenderTypeTags, defenderAbilities, defenderHp,
                            magnitudes, rng, defenderIsRetreating, isCaptureKill)
                        : RunFateTurn(attackDice, defenseDice, attackDice, false, ref attackerFate,
                            attackerAbilities, defenderTypeTags, defenderAbilities, defenderHp,
                            magnitudes, rng, false, isCaptureKill);
                    order.Report(spent);
                }
            }
        }

        public static void ApplyPrimaryOutcome(BattleSimExchangeOutcome outcome,
            IEnumerable<string> attackerAbilities, IEnumerable<string> defenderAbilities,
            ref float defenderHp, ref int defenderAttack, ref int defenderDefense,
            AbilityMagnitudes magnitudes, out bool suppressesPendingTurn)
        {
            defenderHp -= outcome.Damage;
            ApplyBerserkIfHit(outcome.Hit, defenderAbilities,
                ref defenderAttack, ref defenderDefense, magnitudes);
            suppressesPendingTurn = SuppressesTurn(outcome.Damage, attackerAbilities);
        }

        public static void ApplyBerserkIfHit(bool hit, IEnumerable<string> defenderAbilities,
            ref int attack, ref int defense, AbilityMagnitudes magnitudes)
        {
            if (!hit || !HasAbility(defenderAbilities, UnitAbilities.Berserk))
                return;
            attack += magnitudes.BerserkAttackGain;
            defense = Mathf.Max(1, defense - magnitudes.BerserkDefenseLoss);
        }

        public static bool SuppressesTurn(int damage, IEnumerable<string> attackerAbilities) =>
            damage > 0 && HasAbility(attackerAbilities, UnitAbilities.ShockAttack);

        public static List<BattleSimSecondaryTarget> SelectSecondaryTargets(
            int candidateCount, Func<int, bool> isBio, bool splash, bool scorcher,
            System.Random rng = null)
        {
            var result = new List<BattleSimSecondaryTarget>();
            if (candidateCount <= 0 || (!splash && !scorcher))
                return result;

            var available = new List<int>(candidateCount);
            for (int i = 0; i < candidateCount; i++)
                available.Add(i);

            if (splash)
            {
                if (rng != null)
                    Shuffle(available, rng);
                int count = Mathf.Min(2, available.Count);
                for (int i = 0; i < count; i++)
                    result.Add(new BattleSimSecondaryTarget(available[i], "Splash"));
                available.RemoveRange(0, count);
            }

            if (scorcher && available.Count > 0)
            {
                int pickPosition = rng != null ? rng.Next(available.Count) : 0;
                int candidateIndex = available[pickPosition];
                if (isBio != null && isBio(candidateIndex))
                    result.Add(new BattleSimSecondaryTarget(candidateIndex, "Scorcher"));
            }

            return result;
        }

        public static int SecondaryDamage(int primaryDamage, IEnumerable<string> defenderAbilities,
            AbilityMagnitudes magnitudes)
        {
            int damage = Mathf.Max(0, primaryDamage / 2);
            if (damage > 0 && HasAbility(defenderAbilities, UnitAbilities.CeramicArmor))
                damage = Mathf.Max(0, damage - magnitudes.CeramicArmorReduction);
            return damage;
        }

        public static bool[] RollDice(int count, System.Random rng)
        {
            var dice = new bool[Mathf.Max(0, count)];
            for (int i = 0; i < dice.Length; i++)
                dice[i] = rng.NextDouble() < 0.5d;
            return dice;
        }

        private static bool RunFateTurn(bool[] attackerDice, bool[] defenderDice, bool[] ownDice,
            bool isDefender, ref int fate, IEnumerable<string> attackerAbilities,
            IReadOnlyCollection<UnitTypeTag> defenderTypeTags, IEnumerable<string> defenderAbilities,
            int defenderHp, AbilityMagnitudes magnitudes, System.Random rng, bool defenderIsRetreating,
            bool isCaptureKill)
        {
            bool spent = false;
            while (fate > 0 && FateDuelAi.ShouldSpendFate(
                       attackerDice, defenderDice, fate, isDefender,
                       attackerAbilities, defenderTypeTags, defenderAbilities, magnitudes,
                       defenderIsRetreating, defenderHp, isCaptureKill))
            {
                int miss = Array.IndexOf(ownDice, false);
                if (miss < 0)
                    break;
                ownDice[miss] = rng.NextDouble() < 0.5d;
                fate--;
                spent = true;
                if (!ownDice[miss])
                    break;
            }
            return spent;
        }

        private static void Shuffle<T>(IList<T> list, System.Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static bool HasAbility(IEnumerable<string> abilities, string ability)
        {
            if (abilities == null)
                return false;
            if (abilities is IReadOnlyList<string> list)
            {
                for (int i = 0; i < list.Count; i++)
                    if (list[i] == ability)
                        return true;
                return false;
            }
            foreach (string value in abilities)
                if (value == ability)
                    return true;
            return false;
        }
    }
}
