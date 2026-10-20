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
        // two fair tokens pools is Binomial(attack + defense, .5) - defense.
        public static float ExpectedExchangeDamage(int attack, int defense,
            IEnumerable<string> attackerAbilities, IReadOnlyCollection<UnitTypeTag> defenderTags,
            IEnumerable<string> defenderAbilities, int hpCap, out float hitChance,
            bool secondary = false, IEnumerable<string> secondaryAbilities = null)
        {
            ExchangeEntry exchange = Exchange(attack, defense, attackerAbilities, defenderTags,
                defenderAbilities, hpCap, secondary, secondaryAbilities);
            hitChance = exchange.Hit;
            return exchange.Expected;
        }

        // Capped damage PMF: the last bucket contains every lethal/overkill outcome. Consumers
        // may carry HP distributions across contacts without reimplementing tokens or abilities.
        // The caller owns the returned array (a copy of the memoised one).
        public static double[] ExchangeDamageDistribution(int attack, int defense,
            IEnumerable<string> attackerAbilities, IReadOnlyCollection<UnitTypeTag> defenderTags,
            IEnumerable<string> defenderAbilities, int hpCap, out float hitChance,
            bool secondary = false, IEnumerable<string> secondaryAbilities = null)
        {
            ExchangeEntry exchange = Exchange(attack, defense, attackerAbilities, defenderTags,
                defenderAbilities, hpCap, secondary, secondaryAbilities);
            hitChance = exchange.Hit;
            return (double[])exchange.Distribution.Clone();
        }

        private sealed class ExchangeEntry
        {
            internal double[] Distribution;
            internal float Expected;
            internal float Hit;
        }

        // The exchange is a pure function of these numbers, the pair's modifier profile and the
        // (constant) default magnitudes: equipment valuation asks the same exchange thousands of
        // times per turn, so it is computed once per distinct input.
        private const int MaxExchanges = 65536;
        private static readonly Dictionary<(int, int, int, int), ExchangeEntry> s_exchanges =
            new Dictionary<(int, int, int, int), ExchangeEntry>();

        private static ExchangeEntry Exchange(int attack, int defense,
            IEnumerable<string> attackerAbilities, IReadOnlyCollection<UnitTypeTag> defenderTags,
            IEnumerable<string> defenderAbilities, int hpCap, bool secondary, IEnumerable<string> secondaryAbilities)
        {
            attack = Mathf.Max(0, attack);
            defense = Mathf.Max(0, defense);
            int cap = Mathf.Max(0, hpCap);
            var profile = ChallengeResult.ModifierProfile.Of(attackerAbilities, defenderTags, defenderAbilities);
            bool secondaryCeramic = secondary && HasAbility(secondaryAbilities, UnitAbilities.CeramicArmor);
            var key = (attack, defense, cap, profile.Bits | (secondary ? 32 : 0) | (secondaryCeramic ? 64 : 0));
            if (s_exchanges.TryGetValue(key, out ExchangeEntry known))
                return known;
            double[] distribution = ComputeDistribution(attack, defense, profile, cap, secondary,
                secondaryCeramic, out float hit);
            double damage = 0;
            for (int d = 1; d < distribution.Length; d++) damage += d * distribution[d];
            var entry = new ExchangeEntry { Distribution = distribution, Expected = (float)damage, Hit = hit };
            if (s_exchanges.Count >= MaxExchanges)
                s_exchanges.Clear();
            s_exchanges[key] = entry;
            return entry;
        }

        private static double[] ComputeDistribution(int attack, int defense,
            ChallengeResult.ModifierProfile profile, int cap, bool secondary, bool secondaryCeramic,
            out float hitChance)
        {
            var distribution = new double[cap + 1];
            int n = attack + defense;
            int center = n / 2;
            double mass = 0, hits = 0;
            void Accumulate(int successes, double weight)
            {
                int d = ChallengeResult.ApplyAbilityModifiers(Mathf.Max(0, successes - defense),
                    profile, AbilityMagnitudes.Default);
                mass += weight;
                if (d > 0) hits += weight;
                if (secondary) d = SecondaryDamage(d, secondaryCeramic, AbilityMagnitudes.Default);
                distribution[Mathf.Min(d, cap)] += weight;
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
            for (int d = 0; d < distribution.Length; d++) distribution[d] /= mass;
            return distribution;
        }

        public static BattleSimExchangeOutcome ResolveExchange(int attackPool, int defensePool,
            IEnumerable<string> attackerAbilities, IReadOnlyCollection<UnitTypeTag> defenderTypeTags,
            IEnumerable<string> defenderAbilities, ref int attackerFate, ref int defenderFate,
            int defenderHp, AbilityMagnitudes magnitudes, System.Random rng,
            bool defenderIsRetreating = false)
        {
            if (rng == null)
                throw new ArgumentNullException(nameof(rng));

            bool[] attackTokens = RollTokens(Mathf.Max(0, attackPool), rng);
            bool[] defenseTokens = RollTokens(Mathf.Max(0, defensePool), rng);

            ResolveFateDuel(attackTokens, defenseTokens, attackerAbilities, defenderTypeTags,
                defenderAbilities, ref attackerFate, ref defenderFate, defenderHp,
                magnitudes, rng, defenderIsRetreating, isCaptureKill: false);

            BattleResolutionRules.GroundAttackOutcome outcome = BattleResolutionRules.ResolveGroundAttack(
                attackTokens, defenseTokens, attackerAbilities, defenderTypeTags, defenderAbilities, magnitudes);
            return new BattleSimExchangeOutcome(outcome.Damage, outcome.Damage > 0,
                outcome.AttackerSuccesses, outcome.DefenderSuccesses);
        }

        public static CaptureKillOutcome ResolveCaptureKill(int hunterPool, int heroPool,
            ref int hunterFate, ref int heroFate, System.Random rng)
        {
            if (rng == null)
                throw new ArgumentNullException(nameof(rng));
            bool[] hunterTokens = RollTokens(hunterPool, rng);
            bool[] heroTokens = RollTokens(heroPool, rng);
            ResolveFateDuel(hunterTokens, heroTokens, null, null, null,
                ref hunterFate, ref heroFate, int.MaxValue, AbilityMagnitudes.Default,
                rng, defenderIsRetreating: false, isCaptureKill: true);
            return BattleResolutionRules.ResolveCaptureKill(hunterTokens, heroTokens);
        }

        private static void ResolveFateDuel(bool[] attackTokens, bool[] defenseTokens,
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
                        ? RunFateTurn(attackTokens, defenseTokens, defenseTokens, true, ref defenderFate,
                            attackerAbilities, defenderTypeTags, defenderAbilities, defenderHp,
                            magnitudes, rng, defenderIsRetreating, isCaptureKill)
                        : RunFateTurn(attackTokens, defenseTokens, attackTokens, false, ref attackerFate,
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

        // UnitAbilities.Bodyguard — the intercepted hit keeps its rolled tokens (raw = attacker
        // successes - defender successes) and is re-run through the canonical modifier chain
        // against the guard's own tags and abilities.
        public static int InterceptedDamage(int rawDamage, IEnumerable<string> attackerAbilities,
            IReadOnlyCollection<UnitTypeTag> guardTags, IEnumerable<string> guardAbilities,
            AbilityMagnitudes magnitudes) =>
            ChallengeResult.ApplyAbilityModifiers(Mathf.Max(0, rawDamage), attackerAbilities,
                guardTags, guardAbilities, magnitudes);

        // UnitAbilities.Counterattack — geometry-free half of the trigger (the "within 1 cell"
        // test is the caller's, it owns the positions). A surviving non-hero carrier answers once
        // per round unless a ShockAttack hit (positive damage) knocked it out of the round.
        public static bool TriggersCounterattack(IEnumerable<string> victimAbilities, bool victimIsHero,
            float victimHpAfter, bool attackerWithinOne, bool alreadyUsed, int primaryDamage,
            IEnumerable<string> attackerAbilities) =>
            victimHpAfter > 0f && !victimIsHero && attackerWithinOne && !alreadyUsed
            && HasAbility(victimAbilities, UnitAbilities.Counterattack)
            && !SuppressesTurn(primaryDamage, attackerAbilities);

        // UnitAbilities.Drill — which of `candidateCount` side members this round's `carriers`
        // buff: one each, highest Attack first, lowest index on a tie, never the same one twice.
        // Deterministic, so live combat and both projections pick alike.
        public static List<int> SelectDrillTargets(int candidateCount, int carriers,
            Func<int, bool> eligible, Func<int, int> attackOf)
        {
            var result = new List<int>();
            if (carriers <= 0 || candidateCount <= 0)
                return result;
            for (int i = 0; i < candidateCount; i++)
                if (eligible(i))
                    result.Add(i);
            result.Sort((a, b) =>
            {
                int byAttack = attackOf(b).CompareTo(attackOf(a));
                return byAttack != 0 ? byAttack : a.CompareTo(b);
            });
            if (result.Count > carriers)
                result.RemoveRange(carriers, result.Count - carriers);
            return result;
        }

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
            AbilityMagnitudes magnitudes) =>
            SecondaryDamage(primaryDamage, HasAbility(defenderAbilities, UnitAbilities.CeramicArmor), magnitudes);

        private static int SecondaryDamage(int primaryDamage, bool defenderCeramic, AbilityMagnitudes magnitudes)
        {
            int damage = Mathf.Max(0, primaryDamage / 2);
            if (damage > 0 && defenderCeramic)
                damage = Mathf.Max(0, damage - magnitudes.CeramicArmorReduction);
            return damage;
        }

        public static bool[] RollTokens(int count, System.Random rng)
        {
            var tokens = new bool[Mathf.Max(0, count)];
            for (int i = 0; i < tokens.Length; i++)
                tokens[i] = rng.NextDouble() < 0.5d;
            return tokens;
        }

        private static bool RunFateTurn(bool[] attackerTokens, bool[] defenderTokens, bool[] ownTokens,
            bool isDefender, ref int fate, IEnumerable<string> attackerAbilities,
            IReadOnlyCollection<UnitTypeTag> defenderTypeTags, IEnumerable<string> defenderAbilities,
            int defenderHp, AbilityMagnitudes magnitudes, System.Random rng, bool defenderIsRetreating,
            bool isCaptureKill)
        {
            bool spent = false;
            while (fate > 0 && FateDuelAi.ShouldSpendFate(
                       attackerTokens, defenderTokens, fate, isDefender,
                       attackerAbilities, defenderTypeTags, defenderAbilities, magnitudes,
                       defenderIsRetreating, defenderHp, isCaptureKill))
            {
                int miss = Array.IndexOf(ownTokens, false);
                if (miss < 0)
                    break;
                ownTokens[miss] = rng.NextDouble() < 0.5d;
                fate--;
                spent = true;
                if (!ownTokens[miss])
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

        // Shared by live combat, shadow-grid playouts and roster Monte Carlo. Eligibility
        // comes from each representation's CURRENT HP, never from the real unit in a projection.
        public static int SelectHealingTarget(int count, Func<int, bool> eligible, System.Random rng = null)
        {
            var candidates = new List<int>();
            for (int i = 0; i < count; i++)
                if (eligible(i)) candidates.Add(i);
            return candidates.Count == 0 ? -1 : candidates[rng != null ? rng.Next(candidates.Count) : 0];
        }

        public static float HealedHitPoints(float current, float maximum)
            => current > 0f ? Mathf.Min(maximum, current + 1f) : current;

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
