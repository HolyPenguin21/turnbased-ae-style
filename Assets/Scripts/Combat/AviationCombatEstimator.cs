using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Aviation;
using Game.Units;
using UnityEngine;

namespace Game.Combat
{
    // Pure, side-effect-free readout of what a NOT-YET-FLOWN air strike series would probably do to
    // a known target roster. Sits next to WorthIt (the AI's other pure combat-estimate home) rather
    // than inside AviationCombatPresenter, the MonoBehaviour that RUNS the real strike
    // (RunAirStrike) and must never be driven from a planning pass.
    //
    // Shape — the real air strike, not a ground battle: each aircraft attacks once per strike turn,
    // one uniformly-random still-alive defender, with no return fire. Mechanics — the real one:
    // every attack is ONE armed exchange of the shared BattleSimulationKernel.ResolveExchange
    // (the same dice, Fate duel and BattleResolutionRules.ResolveGroundAttack the real
    // BattleEngine.ResolveStandaloneAttack applies), the aircraft's attack pool against the
    // target's Defense — a hero's FateMax, already folded into DefenderProfile.Defense — with the
    // defending commander's Fate (the real strike passes the target army's hero as defenderHero).
    // The aircraft side has no hero and therefore no Fate.
    //
    // A local System.Random seeded from the inputs drives the trials; UnityEngine.Random (the
    // game's RNG) is never touched. Within one WorthIt estimate-cache scope (one AI turn) equal
    // inputs return the memoised estimate; the key is the complete input (see AppendKey).
    public static class AviationCombatEstimator
    {
        // Same bounded budget as WorthIt's ground Monte Carlo.
        internal const int Trials = 25;
        private const int KeyVersion = 2;
        private const int CacheMaxEntries = 20000;

        public readonly struct AirStrikeEstimate
        {
            public readonly float ExpectedDefenseAfter;
            public readonly float ExpectedAttackAfter;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> ExpectedDefendersAfter;
            public readonly float ExpectedDamage;
            // Read off the same trials ExpectedDamage averages over, never a second pass.
            public readonly float KillAnyProbability;
            public readonly float ExpectedKillCount;
            public readonly float WipeProbability;
            // For each ExpectedDefendersAfter entry, its index in the defenders the estimate was
            // run on (GroundCombatAirSupport.AfterStrike splits survivors back into their armies).
            public readonly IReadOnlyList<int> SurvivorSourceIndices;

            public AirStrikeEstimate(float expectedDefenseAfter, float expectedAttackAfter,
                IReadOnlyList<WorthIt.DefenderProfile> expectedDefendersAfter, float expectedDamage,
                float killAnyProbability = 0f, float expectedKillCount = 0f, float wipeProbability = 0f,
                IReadOnlyList<int> survivorSourceIndices = null)
            {
                SurvivorSourceIndices = survivorSourceIndices;
                ExpectedDefenseAfter = expectedDefenseAfter;
                ExpectedAttackAfter = expectedAttackAfter;
                ExpectedDefendersAfter = expectedDefendersAfter;
                ExpectedDamage = expectedDamage;
                KillAnyProbability = killAnyProbability;
                ExpectedKillCount = expectedKillCount;
                WipeProbability = wipeProbability;
            }
        }

        // Live-roster overload (the aircraft as they fly now).
        public static AirStrikeEstimate EstimateAirStrike(IReadOnlyList<UnitData> aircraft,
            IReadOnlyList<WorthIt.DefenderProfile> knownDefenders, AirStrikePolicy policy,
            int strikePasses = 1, int defenderFate = 0) =>
            EstimateAirStrike(aircraft?.Where(x => x != null).Select(WorthIt.FromLiveUnit).ToList(),
                knownDefenders, policy, strikePasses, defenderFate);

        // `aircraft` — the attackers' current profiles (Attack and Abilities are read).
        // `knownDefenders` — the remembered target roster. Null/empty means the roster is UNKNOWN:
        // nothing is simulated (no fictitious empty fight) and the estimate is a no-op with zero
        // damage; callers must treat it as "unknown", never as "no target".
        // `strikePasses` — strike turns in the series (AviationRange.StrikeTurns); each pass every
        // aircraft attacks once, wounds carry over between passes.
        public static AirStrikeEstimate EstimateAirStrike(IReadOnlyList<WorthIt.DefenderProfile> aircraft,
            IReadOnlyList<WorthIt.DefenderProfile> knownDefenders, AirStrikePolicy policy,
            int strikePasses = 1, int defenderFate = 0)
        {
            if (aircraft == null || aircraft.Count == 0
                || knownDefenders == null || knownDefenders.Count == 0 || strikePasses <= 0)
                return new AirStrikeEstimate(SumDefense(knownDefenders), SumAttack(knownDefenders),
                    knownDefenders ?? System.Array.Empty<WorthIt.DefenderProfile>(), 0f);

            AbilityMagnitudes magnitudes = AbilityMagnitudes.Default;
            int[] key = BuildKey(aircraft, knownDefenders, policy, strikePasses, defenderFate, magnitudes);
            if (TryCached(key, out AirStrikeEstimate cached))
                return cached;
            AirStrikeEstimate result = Simulate(aircraft, knownDefenders, policy, strikePasses,
                Mathf.Max(0, defenderFate), magnitudes, Seed(key));
            Store(key, result);
            return result;
        }

        private static AirStrikeEstimate Simulate(IReadOnlyList<WorthIt.DefenderProfile> aircraft,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, AirStrikePolicy policy, int passes,
            int defenderFate, AbilityMagnitudes magnitudes, int seed)
        {
            var rng = new System.Random(seed);
            int n = defenders.Count;
            var hpSum = new float[n];
            var attackSum = new float[n];
            var defenseSum = new float[n];
            var survivalCount = new int[n];
            float totalDamageSum = 0f;
            int killAnyTrials = 0, wipeTrials = 0;
            float killCountSum = 0f;

            for (int trial = 0; trial < Trials; trial++)
            {
                var hp = new float[n];
                var attack = new int[n];
                var defense = new int[n];
                for (int i = 0; i < n; i++)
                {
                    hp[i] = Mathf.Max(1f, defenders[i].HitPoints);
                    attack[i] = Mathf.RoundToInt(defenders[i].Attack);
                    defense[i] = Mathf.RoundToInt(defenders[i].Defense);
                }
                float startHp = hp.Sum();
                var alive = new List<int>(n);
                for (int i = 0; i < n; i++)
                    alive.Add(i);
                int fate = defenderFate;

                for (int pass = 0; pass < passes; pass++)
                {
                    foreach (WorthIt.DefenderProfile plane in aircraft)
                    {
                        // Matches RunAirStrike: stop once only the policy's survivors remain.
                        if (alive.Count <= policy.MinimumSurvivors)
                            break;
                        int idx = alive[rng.Next(alive.Count)];
                        int attackerFate = 0;
                        BattleSimExchangeOutcome outcome = BattleSimulationKernel.ResolveExchange(
                            Mathf.RoundToInt(plane.Attack),
                            defenders[idx].IsHero ? defenders[idx].FateMax : defense[idx], plane.Abilities,
                            defenders[idx].TypeTags, defenders[idx].Abilities, ref attackerFate, ref fate,
                            Mathf.CeilToInt(hp[idx]), magnitudes, rng);
                        float hpLeft = hp[idx];
                        BattleSimulationKernel.ApplyPrimaryOutcome(outcome, plane.Abilities,
                            defenders[idx].Abilities, ref hpLeft, ref attack[idx], ref defense[idx],
                            magnitudes, out _);
                        hp[idx] = Mathf.Max(0f, hpLeft);
                        if (hp[idx] <= 0f)
                            alive.Remove(idx);
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    hpSum[i] += hp[i];
                    if (hp[i] > 0f)
                    {
                        survivalCount[i]++;
                        attackSum[i] += attack[i];
                        defenseSum[i] += defenders[i].IsHero ? defenders[i].FateMax : defense[i];
                    }
                }
                totalDamageSum += startHp - hp.Sum();
                int killed = n - alive.Count;
                killCountSum += killed;
                if (killed >= 1)
                    killAnyTrials++;
                if (killed == n)
                    wipeTrials++;
            }

            var expectedDefenders = new List<WorthIt.DefenderProfile>();
            var survivorIndices = new List<int>();
            float expectedDefense = 0f, expectedAttack = 0f;
            for (int i = 0; i < n; i++)
            {
                float meanHp = hpSum[i] / Trials;
                if (meanHp <= 0.01f)
                    continue; // expected dead on average
                WorthIt.DefenderProfile o = defenders[i];
                // Standalone air strikes retain Berserk changes; carry the surviving trials'
                // stats into the subsequent ground estimate as well as their wounds.
                float meanAttack = attackSum[i] / survivalCount[i];
                float meanDefense = defenseSum[i] / survivalCount[i];
                expectedDefenders.Add(new WorthIt.DefenderProfile(meanDefense, o.HasCeramicArmor, o.TypeTags,
                    meanAttack, meanHp, o.Initiative, o.Abilities, o.MaxHitPoints, o.IsGroundCombatant,
                    o.IsHero, o.FateMax, o.IsSummoned));
                survivorIndices.Add(i);
                expectedDefense += meanDefense;
                expectedAttack += meanAttack;
            }

            return new AirStrikeEstimate(expectedDefense, expectedAttack, expectedDefenders,
                totalDamageSum / Trials, (float)killAnyTrials / Trials, killCountSum / Trials,
                (float)wipeTrials / Trials, survivorIndices);
        }

        private static float SumDefense(IReadOnlyList<WorthIt.DefenderProfile> d) =>
            d == null ? 0f : d.Sum(x => x.Defense);
        private static float SumAttack(IReadOnlyList<WorthIt.DefenderProfile> d) =>
            d == null ? 0f : d.Sum(x => x.Attack);

        // ---- cache (one WorthIt estimate-cache scope) ----------------------------------------
        private static readonly Dictionary<string, AirStrikeEstimate> Cache =
            new Dictionary<string, AirStrikeEstimate>();
        private static int _cacheScope = int.MinValue;
        internal static int CacheHits { get; private set; }
        internal static int CacheMisses { get; private set; }

        private static bool TryCached(int[] key, out AirStrikeEstimate estimate)
        {
            estimate = default;
            if (!WorthIt.EstimateCacheActive)
                return false;
            if (_cacheScope != WorthIt.EstimateCacheScopeId)
            {
                Cache.Clear();
                _cacheScope = WorthIt.EstimateCacheScopeId;
            }
            if (Cache.TryGetValue(KeyString(key), out estimate))
            {
                CacheHits++;
                return true;
            }
            CacheMisses++;
            return false;
        }

        private static void Store(int[] key, AirStrikeEstimate estimate)
        {
            if (!WorthIt.EstimateCacheActive || _cacheScope != WorthIt.EstimateCacheScopeId)
                return;
            if (Cache.Count >= CacheMaxEntries)
                Cache.Clear();
            Cache[KeyString(key)] = estimate;
        }

        private static string KeyString(int[] key) => string.Join(",", key);

        // The COMPLETE input of one estimate: every aircraft's simulated attack facts, every
        // defender profile field the simulation reads (current HP, max HP, Defense incl. hero
        // FateMax, Attack, abilities, type tags), the policy (exact target / survivor floor), the
        // number of strike passes, the defending commander's Fate, the magnitudes and the trial
        // count. A damaged, re-equipped or reduced roster under the same army id is a new key.
        internal static int[] BuildKey(IReadOnlyList<WorthIt.DefenderProfile> aircraft,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, AirStrikePolicy policy, int passes,
            int defenderFate, AbilityMagnitudes m)
        {
            var buf = new List<int> { KeyVersion, Trials, passes, defenderFate, (int)policy.Kind,
                policy.ExactTargetArmyId ?? int.MinValue, policy.MinimumSurvivors,
                System.BitConverter.SingleToInt32Bits(m.CriticalDamageMultiplier),
                m.HyperkineticBonusDamage, m.CeramicArmorReduction, m.PyrokineticBonusDamage,
                m.BerserkAttackGain, m.BerserkDefenseLoss };
            void Profile(WorthIt.DefenderProfile p)
            {
                buf.Add(System.BitConverter.SingleToInt32Bits(p.Attack));
                buf.Add(System.BitConverter.SingleToInt32Bits(p.Defense));
                buf.Add(System.BitConverter.SingleToInt32Bits(p.HitPoints));
                buf.Add(System.BitConverter.SingleToInt32Bits(p.MaxHitPoints));
                buf.Add(p.IsHero ? 1 : 0);
                buf.Add(p.FateMax);
                buf.Add(p.IsGroundCombatant ? 1 : 0);
                buf.Add(p.IsSummoned ? 1 : 0);
                buf.Add(p.Initiative);
                buf.Add(p.HasCeramicArmor ? 1 : 0);
                int abilities = p.Abilities?.Count ?? -1;
                buf.Add(abilities);
                for (int i = 0; i < abilities; i++)
                    buf.Add(StableHash(p.Abilities[i]));
                int tags = p.TypeTags?.Count ?? -1;
                buf.Add(tags);
                for (int i = 0; i < tags; i++)
                    buf.Add((int)p.TypeTags[i]);
            }
            buf.Add(-1000 - aircraft.Count);
            foreach (WorthIt.DefenderProfile a in aircraft)
                Profile(a);
            buf.Add(-2000 - defenders.Count);
            foreach (WorthIt.DefenderProfile d in defenders)
                Profile(d);
            return buf.ToArray();
        }

        // Deterministic per-matchup seed built only from the numeric key (never object hashes).
        private static int Seed(int[] key)
        {
            unchecked
            {
                int hash = 17;
                foreach (int v in key)
                    hash = hash * 31 + v;
                return hash;
            }
        }

        private static int StableHash(string s)
        {
            if (s == null)
                return 0;
            unchecked
            {
                int h = 23;
                foreach (char c in s)
                    h = h * 31 + c;
                return h;
            }
        }
    }
}
