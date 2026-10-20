using System.Collections.Generic;
using System.Linq;
using Game.Combat;
using Game.Aviation;
using Game.Units;
using Game.Cards;
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
    // (the same tokens, Fate duel and BattleResolutionRules.ResolveGroundAttack the real
    // BattleEngine.ResolveStandaloneAttack applies), the aircraft's attack pool against the
    // target's Defense — a hero's FateMax, already folded into DefenderProfile.Defense — with the
    // target hero's own current Fate, or its army commander's current Fate for a body target.
    // The aircraft side has no hero and therefore no Fate.
    //
    // A local System.Random seeded from the inputs drives the trials; UnityEngine.Random (the
    // game's RNG) is never touched. Within one WorthIt estimate-cache scope (one AI turn) equal
    // inputs return the memoised estimate; the key is the complete input (see AppendKey).
    public static class AviationCombatEstimator
    {
        // Same bounded budget as WorthIt's ground Monte Carlo.
        internal const int Trials = 25;
        private const int KeyVersion = 7;
        private const int CacheMaxEntries = 20000;

        public readonly struct ExplosionCollateral
        {
            public readonly WorthIt.DefenderProfile Profile;
            public readonly bool Friendly;
            public readonly bool OwnCitadel;
            public readonly int? ArmyId;
            public ExplosionCollateral(WorthIt.DefenderProfile profile, bool friendly, bool ownCitadel = false, int? armyId = null)
            { Profile = profile; Friendly = friendly; OwnCitadel = ownCitadel; ArmyId = armyId; }
        }

        public readonly struct AirStrikeEstimate
        {
            public readonly float ExpectedDefenseAfter;
            public readonly float ExpectedAttackAfter;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> ExpectedDefendersAfter;
            public readonly float ExpectedDamage;
            public readonly float ExpectedFriendlyDamage;
            public float NetExpectedDamage => ExpectedDamage - ExpectedFriendlyDamage;
            // Read off the same trials ExpectedDamage averages over, never a second pass.
            public readonly float KillAnyProbability;
            public readonly float ExpectedKillCount;
            public readonly float WipeProbability;
            // For each ExpectedDefendersAfter entry, its index in the defenders the estimate was
            // run on (GroundCombatAirSupport.AfterStrike splits survivors back into their armies).
            public readonly IReadOnlyList<int> SurvivorSourceIndices;
            // Remaining current Fate, aligned with survivors; air strikes do not replenish it.
            public readonly IReadOnlyList<int> ExpectedCurrentFatesAfter;

            public AirStrikeEstimate(float expectedDefenseAfter, float expectedAttackAfter,
                IReadOnlyList<WorthIt.DefenderProfile> expectedDefendersAfter, float expectedDamage,
                float killAnyProbability = 0f, float expectedKillCount = 0f, float wipeProbability = 0f,
                IReadOnlyList<int> survivorSourceIndices = null, IReadOnlyList<int> expectedCurrentFatesAfter = null,
                float expectedFriendlyDamage = 0f)
            {
                ExpectedCurrentFatesAfter = expectedCurrentFatesAfter == null ? null
                    : System.Array.AsReadOnly(expectedCurrentFatesAfter.ToArray());
                SurvivorSourceIndices = survivorSourceIndices == null ? null
                    : System.Array.AsReadOnly(survivorSourceIndices.ToArray());
                ExpectedDefenseAfter = expectedDefenseAfter;
                ExpectedAttackAfter = expectedAttackAfter;
                ExpectedDefendersAfter = expectedDefendersAfter == null ? null
                    : System.Array.AsReadOnly(expectedDefendersAfter.ToArray());
                ExpectedDamage = expectedDamage;
                ExpectedFriendlyDamage = expectedFriendlyDamage;
                KillAnyProbability = killAnyProbability;
                ExpectedKillCount = expectedKillCount;
                WipeProbability = wipeProbability;
            }
        }

        // One air-target army. Profiles are in observed roster order; each hero owns its own
        // current Fate. Army identity is retained until the strike policy has filtered targets.
        public readonly struct DefendingAirArmy
        {
            public readonly int ArmyId;
            public readonly IReadOnlyList<WorthIt.DefenderProfile> Units;
            public readonly IReadOnlyList<int> CurrentFates;
            public readonly int CommanderIndex;
            public DefendingAirArmy(int armyId, IReadOnlyList<WorthIt.DefenderProfile> units,
                IReadOnlyList<int> currentFates, int? commanderIndex = null)
            {
                ArmyId = armyId;
                var profiles = (units ?? System.Array.Empty<WorthIt.DefenderProfile>()).ToArray();
                Units = System.Array.AsReadOnly(profiles);
                int commander = commanderIndex ?? Enumerable.Range(0, profiles.Length)
                    .Where(i => profiles[i].IsHero).DefaultIfEmpty(-1).First();
                CommanderIndex = commander >= 0 && commander < Units.Count && Units[commander].IsHero
                    ? commander : -1;
                CurrentFates = System.Array.AsReadOnly(Enumerable.Range(0, profiles.Length)
                    .Select(i => profiles[i].IsHero && currentFates != null && i < currentFates.Count
                        ? Mathf.Max(0, currentFates[i]) : 0).ToArray());
            }
        }

        public static AirStrikeEstimate EstimateAirStrikeAgainstArmies(IReadOnlyList<UnitData> aircraft,
            IReadOnlyList<DefendingAirArmy> armies, AirStrikePolicy policy, int strikePasses = 1,
            IReadOnlyList<bool> firstPassSpent = null, IReadOnlyList<ExplosionCollateral> collateral = null) =>
            EstimateAirStrikeAgainstArmies(aircraft?.Where(x => x != null).Select(WorthIt.FromLiveUnit).ToList(),
                armies, policy, strikePasses, firstPassSpent, collateral);

        public static AirStrikeEstimate EstimateAirStrikeAgainstArmies(IReadOnlyList<WorthIt.DefenderProfile> aircraft,
            IReadOnlyList<DefendingAirArmy> armies, AirStrikePolicy policy, int strikePasses = 1,
            IReadOnlyList<bool> firstPassSpent = null, IReadOnlyList<ExplosionCollateral> collateral = null)
        {
            if (aircraft?.Any(p => ExplosionAttack.TargetLimit(p.Abilities) > 0) != true) collateral = null;
            var defenders = new List<WorthIt.DefenderProfile>();
            var groups = new List<int>();
            var fates = new List<int>();
            var commanders = new List<int>();
            foreach (DefendingAirArmy army in armies ?? System.Array.Empty<DefendingAirArmy>())
            {
                if (policy.ExactTargetArmyId.HasValue && army.ArmyId != policy.ExactTargetArmyId.Value)
                    continue;
                int group = groups.Count; // unique partition label, independent of global army IDs
                for (int i = 0; i < army.Units.Count; i++)
                {
                    defenders.Add(army.Units[i]);
                    groups.Add(group);
                    commanders.Add(army.CommanderIndex >= 0 ? group + army.CommanderIndex : -1);
                    fates.Add(army.CurrentFates[i]);
                }
            }
            if (aircraft == null || aircraft.Count == 0 || (defenders.Count == 0 && collateral?.Any(c => !c.Friendly && c.Profile.HitPoints > 0) != true) || strikePasses <= 0)
                return new AirStrikeEstimate(SumDefense(defenders), SumAttack(defenders), defenders, 0f);
            var magnitudes = AbilityMagnitudes.Default;
            int[] spent = Enumerable.Range(0, aircraft.Count)
                .Select(i => firstPassSpent != null && i < firstPassSpent.Count && firstPassSpent[i] ? 1 : 0).ToArray();
            int[] key = BuildKey(aircraft, defenders, policy, strikePasses, 0, magnitudes)
                .Concat(new[] { -3000 }).Concat(groups)
                .Concat(new[] { -4000 }).Concat(fates)
                .Concat(new[] { -5000 }).Concat(commanders)
                .Concat(new[] { -6000 }).Concat(spent).ToArray();
            key = WithCollateralKey(key, collateral);
            if (TryCached(key, out AirStrikeEstimate cached)) return cached;
            AirStrikeEstimate result = Simulate(aircraft, defenders, policy, strikePasses, 0,
                magnitudes, Seed(key), groups.ToArray(), fates.ToArray(), commanders.ToArray(), spent, collateral);
            Store(key, result);
            return result;
        }

        // Live-roster overload (the aircraft as they fly now).
        public static AirStrikeEstimate EstimateAirStrike(IReadOnlyList<UnitData> aircraft,
            IReadOnlyList<WorthIt.DefenderProfile> knownDefenders, AirStrikePolicy policy,
            int strikePasses = 1, int defenderFate = 0, IReadOnlyList<ExplosionCollateral> collateral = null) =>
            EstimateAirStrike(aircraft?.Where(x => x != null).Select(WorthIt.FromLiveUnit).ToList(),
                knownDefenders, policy, strikePasses, defenderFate, collateral);

        // `aircraft` — the attackers' current profiles (Attack and Abilities are read).
        // `knownDefenders` — the remembered target roster. Null/empty means the roster is UNKNOWN:
        // nothing is simulated (no fictitious empty fight) and the estimate is a no-op with zero
        // damage; callers must treat it as "unknown", never as "no target".
        // `strikePasses` — strike turns in the series (AviationRange.StrikeTurns); each pass every
        // aircraft attacks once, wounds carry over between passes.
        public static AirStrikeEstimate EstimateAirStrike(IReadOnlyList<WorthIt.DefenderProfile> aircraft,
            IReadOnlyList<WorthIt.DefenderProfile> knownDefenders, AirStrikePolicy policy,
            int strikePasses = 1, int defenderFate = 0, IReadOnlyList<ExplosionCollateral> collateral = null)
        {
            if (aircraft?.Any(p => ExplosionAttack.TargetLimit(p.Abilities) > 0) != true) collateral = null;
            if (aircraft == null || aircraft.Count == 0
                || ((knownDefenders == null || knownDefenders.Count == 0)
                    && collateral?.Any(c => !c.Friendly && c.Profile.HitPoints > 0) != true) || strikePasses <= 0)
                return new AirStrikeEstimate(SumDefense(knownDefenders), SumAttack(knownDefenders),
                    knownDefenders ?? System.Array.Empty<WorthIt.DefenderProfile>(), 0f);

            knownDefenders = knownDefenders ?? System.Array.Empty<WorthIt.DefenderProfile>();
            AbilityMagnitudes magnitudes = AbilityMagnitudes.Default;
            int[] key = WithCollateralKey(BuildKey(aircraft, knownDefenders, policy, strikePasses, defenderFate, magnitudes), collateral);
            if (TryCached(key, out AirStrikeEstimate cached))
                return cached;
            AirStrikeEstimate result = Simulate(aircraft, knownDefenders, policy, strikePasses,
                Mathf.Max(0, defenderFate), magnitudes, Seed(key), collateral: collateral);
            Store(key, result);
            return result;
        }

        private static AirStrikeEstimate Simulate(IReadOnlyList<WorthIt.DefenderProfile> aircraft,
            IReadOnlyList<WorthIt.DefenderProfile> defenders, AirStrikePolicy policy, int passes,
            int defenderFate, AbilityMagnitudes magnitudes, int seed,
            int[] groups = null, int[] currentFates = null, int[] commanders = null, int[] firstPassSpent = null,
            IReadOnlyList<ExplosionCollateral> collateral = null)
        {
            var rng = new System.Random(seed);
            int n = defenders.Count;
            var hpSum = new float[n];
            var attackSum = new float[n];
            var defenseSum = new float[n];
            var survivalCount = new int[n];
            var fateSum = new float[n];
            float totalDamageSum = 0f, friendlyDamageSum = 0f;
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
                int[] fates = currentFates != null ? (int[])currentFates.Clone() : null;
                var armed = aircraft.ToArray();
                var planeHp = aircraft.Select(p => Mathf.Max(0f, p.HitPoints)).ToArray();
                var planeFates = aircraft.Select(p => p.FateMax).ToArray();
                var extra = (collateral ?? System.Array.Empty<ExplosionCollateral>()).Select(c => c.Profile).ToArray();
                var extraHp = extra.Select(p => Mathf.Max(0f, p.HitPoints)).ToArray();
                var extraFates = extra.Select(p => p.FateMax).ToArray();
                float friendlyStart = planeHp.Sum() + Enumerable.Range(0, extra.Length)
                    .Where(i => collateral[i].Friendly).Sum(i => extraHp[i]);
                float extraEnemyStart = Enumerable.Range(0, extra.Length)
                    .Where(i => !collateral[i].Friendly).Sum(i => extraHp[i]);

                void HitDefender(int idx, WorthIt.DefenderProfile plane, int attackTokens)
                {
                        int attackerFate = 0;
                        int fateOwner = -1;
                        if (groups != null)
                        {
                            if (defenders[idx].IsHero) fateOwner = idx;
                            else if (commanders[idx] >= 0 && alive.Contains(commanders[idx]))
                                fateOwner = commanders[idx];
                            else if (commanders[idx] >= 0) foreach (int candidate in alive)
                                if (groups[candidate] == groups[idx] && defenders[candidate].IsHero)
                                { fateOwner = candidate; break; }
                        }
                        int remainingFate = fates == null ? fate : fateOwner >= 0 ? fates[fateOwner] : 0;
                        BattleSimExchangeOutcome outcome = BattleSimulationKernel.ResolveExchange(
                            attackTokens,
                            defenders[idx].IsHero ? defenders[idx].FateMax : defense[idx], plane.Abilities,
                            defenders[idx].TypeTags, defenders[idx].Abilities, ref attackerFate, ref remainingFate,
                            Mathf.CeilToInt(hp[idx]), magnitudes, rng);
                        if (fates == null) fate = remainingFate;
                        else if (fateOwner >= 0) fates[fateOwner] = remainingFate;
                        float hpLeft = hp[idx];
                        BattleSimulationKernel.ApplyPrimaryOutcome(outcome, plane.Abilities,
                            defenders[idx].Abilities, ref hpLeft, ref attack[idx], ref defense[idx],
                            magnitudes, out _);
                        hp[idx] = Mathf.Max(0f, hpLeft);
                        if (hp[idx] <= 0f)
                            alive.Remove(idx);

                }
                WorthIt.DefenderProfile HitOther(WorthIt.DefenderProfile p, WorthIt.DefenderProfile plane,
                    ref float hpLeft, ref int remainingFate)
                {
                    int a = 0;
                    var outcome = BattleSimulationKernel.ResolveExchange(ExplosionAttack.AttackTokens,
                        Mathf.RoundToInt(p.Defense), plane.Abilities, p.TypeTags, p.Abilities,
                        ref a, ref remainingFate, Mathf.CeilToInt(hpLeft), magnitudes, rng);
                    int attack = Mathf.RoundToInt(p.Attack), defense = Mathf.RoundToInt(p.Defense);
                    BattleSimulationKernel.ApplyPrimaryOutcome(outcome, plane.Abilities, p.Abilities,
                        ref hpLeft, ref attack, ref defense, magnitudes, out _);
                    hpLeft = Mathf.Max(0, hpLeft);
                    return new WorthIt.DefenderProfile(defense, p.HasCeramicArmor, p.TypeTags, attack, hpLeft,
                        p.Initiative, p.Abilities, p.MaxHitPoints, p.IsGroundCombatant, p.IsHero, p.FateMax,
                        p.IsSummoned, p.Range, p.Consumption, p.SingleUseUnit);
                }

                bool Eligible(int idx) => AviationRules.CanTargetFromAir(defenders[idx].IsHero,
                    alive.Count(j => groups == null || groups[j] == groups[idx]));
                bool EligibleExtra(int idx) => AviationRules.CanTargetFromAir(extra[idx].IsHero,
                    collateral[idx].ArmyId.HasValue ? Enumerable.Range(0, extra.Length).Count(j =>
                        extraHp[j] > 0 && collateral[j].ArmyId == collateral[idx].ArmyId) : 1);

                for (int pass = 0; pass < passes; pass++)
                {
                    for (int aircraftIndex = 0; aircraftIndex < aircraft.Count; aircraftIndex++)
                    {
                        if (pass == 0 && firstPassSpent != null && firstPassSpent[aircraftIndex] != 0) continue;
                        WorthIt.DefenderProfile plane = armed[aircraftIndex];
                        if (planeHp[aircraftIndex] <= 0f) continue;
                        int explosion = ExplosionAttack.TargetLimit(plane.Abilities);
                        if (explosion > 0)
                        {
                            if (policy.MinimumSurvivors > 0 || (alive.Count == 0 && !Enumerable.Range(0, extra.Length)
                                .Any(i => !collateral[i].Friendly && extraHp[i] > 0))) continue;
                            if (Enumerable.Range(0, extra.Length).Any(i => collateral[i].OwnCitadel
                                && extraHp[i] > 0 && ChallengeResult.ApplyAbilityModifiers(ExplosionAttack.AttackTokens,
                                    plane.Abilities, extra[i].TypeTags, extra[i].Abilities, magnitudes) >= extraHp[i])) continue;
                            var pool = alive.Where(Eligible).Select(i => (kind: 0, index: i)).ToList();
                            for (int i = 0; i < planeHp.Length; i++)
                                if (planeHp[i] > 0 && !(plane.SingleUseUnit && i == aircraftIndex)) pool.Add((1, i));
                            for (int i = 0; i < extraHp.Length; i++) if (extraHp[i] > 0 && EligibleExtra(i)) pool.Add((2, i));
                            int count = Mathf.Min(explosion, pool.Count);
                            // Match the live AI's refusal to launch a losing blast, including
                            // one sacrifice of an innate missile. Do not value an attack that
                            // execution would skip, or charge its self-hit and sacrifice twice.
                            float value = 0;
                            foreach (var target in pool)
                            {
                                int k = target.index;
                                var victim = target.kind == 0 ? defenders[k] : target.kind == 1 ? armed[k] : extra[k];
                                float victimHp = target.kind == 0 ? hp[k] : target.kind == 1 ? planeHp[k] : extraHp[k];
                                int victimDefense = victim.IsHero ? victim.FateMax
                                    : target.kind == 0 ? defense[k] : Mathf.RoundToInt(victim.Defense);
                                float victimAttack = target.kind == 0 ? attack[k] : victim.Attack;
                                bool friendly = target.kind == 1 || target.kind == 2 && collateral[k].Friendly;
                                value += (friendly ? -1 : 1) * ExplosionAttack.ExpectedDamageValue(plane.Abilities,
                                    victimDefense, victim.TypeTags, victim.Abilities, victimHp, victim.MaxHitPoints,
                                    WorthIt.CombatValue(victimAttack, victim.Defense, victim.MaxHitPoints, victim.Initiative));
                            }
                            value = ExplosionAttack.NetValue(value, pool.Count, explosion, plane.SingleUseUnit
                                ? WorthIt.CombatValue(plane.Attack, plane.Defense, planeHp[aircraftIndex], plane.Initiative) : 0f);
                            if (value <= 0) continue;
                            for (int i = 0; i < count; i++)
                            {
                                int j = rng.Next(i, pool.Count); (pool[i], pool[j]) = (pool[j], pool[i]);
                                var target = pool[i];
                                if (target.kind == 0) HitDefender(target.index, plane, ExplosionAttack.AttackTokens);
                                else if (target.kind == 1) armed[target.index] = HitOther(armed[target.index], plane,
                                    ref planeHp[target.index], ref planeFates[target.index]);
                                else extra[target.index] = HitOther(extra[target.index], plane,
                                    ref extraHp[target.index], ref extraFates[target.index]);
                            }
                            // Consume once after all victims, even when the launcher hit itself.
                            var surviving = armed[aircraftIndex];
                            var spentPlane = WorthIt.AfterAttack(new WorthIt.DefenderProfile(surviving.Defense,
                                surviving.HasCeramicArmor, surviving.TypeTags, surviving.Attack, planeHp[aircraftIndex],
                                surviving.Initiative, surviving.Abilities, surviving.MaxHitPoints, surviving.IsGroundCombatant,
                                surviving.IsHero, surviving.FateMax, surviving.IsSummoned, surviving.Range,
                                surviving.Consumption, surviving.SingleUseUnit));
                            armed[aircraftIndex] = spentPlane;
                            planeHp[aircraftIndex] = Mathf.Min(planeHp[aircraftIndex], spentPlane.HitPoints);
                            continue;
                        }
                        // Matches RunAirStrike: stop once only the policy's survivors remain.
                        if (alive.Count <= policy.MinimumSurvivors)
                            break;
                        var eligible = alive.Where(Eligible).ToArray();
                        if (eligible.Length == 0) continue;
                        int idx = eligible[rng.Next(eligible.Length)];
                        HitDefender(idx, plane, Mathf.RoundToInt(plane.Attack));
                        armed[aircraftIndex] = WorthIt.AfterAttack(plane);
                        planeHp[aircraftIndex] = Mathf.Min(planeHp[aircraftIndex], armed[aircraftIndex].HitPoints);
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    hpSum[i] += hp[i];
                    if (hp[i] > 0f)
                    {
                        survivalCount[i]++;
                        if (defenders[i].IsHero) fateSum[i] += fates == null ? fate : fates[i];
                        attackSum[i] += attack[i];
                        defenseSum[i] += defenders[i].IsHero ? defenders[i].FateMax : defense[i];
                    }
                }
                totalDamageSum += startHp - hp.Sum() + extraEnemyStart
                    - Enumerable.Range(0, extra.Length).Where(i => !collateral[i].Friendly).Sum(i => extraHp[i]);
                friendlyDamageSum += friendlyStart - planeHp.Sum()
                    - Enumerable.Range(0, extra.Length).Where(i => collateral[i].Friendly).Sum(i => extraHp[i]);
                int killed = n - alive.Count;
                killCountSum += killed;
                if (killed >= 1)
                    killAnyTrials++;
                if (killed == n)
                    wipeTrials++;
            }

            var expectedDefenders = new List<WorthIt.DefenderProfile>();
            var survivorIndices = new List<int>();
            var survivingFates = new List<int>();
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
                    o.IsHero, o.FateMax, o.IsSummoned, o.Range, o.Consumption, o.SingleUseUnit));
                survivorIndices.Add(i);
                survivingFates.Add(Mathf.RoundToInt(fateSum[i] / survivalCount[i]));
                expectedDefense += meanDefense;
                expectedAttack += meanAttack;
            }

            return new AirStrikeEstimate(expectedDefense, expectedAttack, expectedDefenders,
                totalDamageSum / Trials, (float)killAnyTrials / Trials, killCountSum / Trials,
                (float)wipeTrials / Trials, survivorIndices, survivingFates, friendlyDamageSum / Trials);
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
            // This key also seeds the bounded trials. Keep the old layout/seed for ordinary
            // rosters so adding consumable support does not reroll every existing matchup.
            bool consumable = aircraft.Any(p => p.Consumption != null) || defenders.Any(p => p.Consumption != null);
            var buf = new List<int> { consumable ? KeyVersion : 5, Trials, passes, defenderFate, (int)policy.Kind,
                policy.ExactTargetArmyId ?? int.MinValue, policy.MinimumSurvivors,
                System.BitConverter.SingleToInt32Bits(m.CriticalDamageMultiplier),
                m.HyperkineticBonusDamage, m.CeramicArmorReduction, m.PyrokineticBonusDamage,
                m.BerserkAttackGain, m.BerserkDefenseLoss, m.IonicBonusDamage, m.DrillAttackBonus };
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
                if (p.SingleUseUnit) { buf.Add(-19019); buf.Add(1); }
                buf.Add(p.HasCeramicArmor ? 1 : 0);
                if (consumable) WorthIt.AppendConsumptionKey(buf, p.Consumption);
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

        private static int[] WithCollateralKey(int[] key, IReadOnlyList<ExplosionCollateral> collateral)
        {
            if (collateral == null || collateral.Count == 0) return key;
            var result = key.ToList(); result.Add(-19020);
            var groups = new Dictionary<int, int>();
            foreach (var target in collateral)
            {
                int group = -1;
                if (target.ArmyId.HasValue && !groups.TryGetValue(target.ArmyId.Value, out group))
                    groups[target.ArmyId.Value] = group = groups.Count;
                result.Add(group);
                result.Add(target.Friendly ? 1 : 0);
                result.Add(target.OwnCitadel ? 1 : 0);
                result.AddRange(BuildKey(System.Array.Empty<WorthIt.DefenderProfile>(), new[] { target.Profile },
                    AirStrikePolicy.Standard, 1, 0, AbilityMagnitudes.Default));
            }
            return result.ToArray();
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
