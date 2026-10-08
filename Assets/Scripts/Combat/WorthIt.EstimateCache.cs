using System;
using System.Collections.Generic;

namespace Game.Combat
{
    // Exact memo of WorthIt's Monte Carlo readouts (2026-09-29, AI turn hitches: the same matchups
    // were re-simulated hundreds of times per AI turn by CombatOpportunityAnalyzer, ForceNeedModel
    // and the Economy escort search).
    //
    // WHY IT IS EXACT: every trial runs on a System.Random seeded from the matchup itself
    // (BuildRosterSeed), and the simulation (SimulateOneBattle -> BattleSimulationKernel,
    // ChallengeResult, FateDuelAi) reads nothing but its arguments. So the key below is the COMPLETE
    // input of one estimate: the seed, every simulated BattleUnit of both sides (after conversion,
    // so hex/structure defence and commander initiative are already folded in), both sides' Fate,
    // the AbilityMagnitudes, and the trial/round constants. Equal key => bit-identical result.
    //
    // WHEN YOU CHANGE COMBAT (see docs cache audit, "WorthIt estimate cache"):
    //   * a new field on BattleUnit / SideCommander / AbilityMagnitudes / DefenderProfile, or a new
    //     parameter on SimulateOneBattle / EstimateCore, must be added to the key here (or declared
    //     non-simulated below). WorthItEstimateCacheTests fails until you do.
    //   * the simulation must stay a pure function of its arguments — no global/config reads inside
    //     it. If it ever needs one, put that value into the key.
    //   * bump KeyVersion if the key layout changes meaning.
    //
    // LIFETIME: active only between BeginEstimateCacheScope/EndEstimateCacheScope (one AI turn,
    // AiTurnController.RunTurn). Outside a scope nothing is cached, so real battles, UI previews and
    // tests behave exactly as before. Begin always starts empty, so a scope leaked by a stopped
    // coroutine cannot carry entries into the next turn.
    public static partial class WorthIt
    {
        private const int EstimateKeyVersion = 2;
        private const int EstimateCacheMaxEntries = 50000;

        // The field sets the key covers. WorthItEstimateCacheTests compares them against the real
        // struct fields by reflection, so adding a field without deciding its key fate fails a test.
        internal static readonly string[] EstimateKeyBattleUnitFields =
            { "Attack", "Defense", "Abilities", "TypeTags", "Initiative", "Hp", "MaxHp",
                "IsHero", "HeroFate", "IsSummoned" };
        internal static readonly string[] EstimateKeySideCommanderFields =
            { "Present", "Initiative", "Fate" };
        internal static readonly string[] EstimateKeyAbilityMagnitudesFields =
        {
            "CriticalDamageMultiplier", "HyperkineticBonusDamage", "CeramicArmorReduction",
            "PyrokineticBonusDamage", "BerserkAttackGain", "BerserkDefenseLoss",
        };
        // DefenderProfile reaches the simulation only through ToBattleUnits (and TacticalTargetsOf's
        // filter, which decides WHICH units are converted), so its simulated fields are covered by
        // the BattleUnit key. The rest are read elsewhere, never by the simulation.
        internal static readonly string[] EstimateKeyDefenderProfileSimulatedFields =
            { "Attack", "Defense", "HitPoints", "MaxHitPoints", "Initiative", "Abilities", "TypeTags",
                "IsHero", "FateMax", "IsSummoned" };
        internal static readonly string[] EstimateKeyDefenderProfileNonSimulatedFields =
        {
            "HasCeramicArmor",   // CanDamage only; the simulation reads CeramicArmor from Abilities
            "IsGroundCombatant", // TacticalTargetsOf filter, applied before conversion
            "Range",             // read only by equipment valuation (can the enemy answer at distance d)
        };
        // Parameter list guarded by the same test. Scratch is NOT an input: Reset clears it before
        // every battle and binds it to the supplied armies; it only owns reusable working storage.
        internal static readonly string[] EstimateKeySimulateOneBattleParameters =
            { "attackers", "defenders", "rng", "attackerFate", "defenderFate", "magnitudesOverride", "scratch" };

        public struct EstimateCacheStats
        {
            public int Hits;
            public int Misses;
            public double MissMilliseconds;
            public int Entries;
        }

        private static readonly Dictionary<EstimateCacheKey, BattleEstimate> EstimateCacheEntries =
            new Dictionary<EstimateCacheKey, BattleEstimate>();
        private static readonly Dictionary<string, int> EstimateAbilityIds =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<int> EstimateKeyBuffer = new List<int>(256);
        private static int[] EstimateLookupBuffer = new int[256];
        private static bool _estimateCacheActive;
        private static EstimateCacheStats _estimateCacheStats;

        public static bool EstimateCacheActive => _estimateCacheActive;
        public static EstimateCacheStats CurrentEstimateCacheStats
        {
            get
            {
                EstimateCacheStats s = _estimateCacheStats;
                s.Entries = EstimateCacheEntries.Count;
                return s;
            }
        }

        // Identity of the current cache scope. Sibling estimators that memo inside the same scope
        // (AviationCombatEstimator) clear their own entries whenever this changes.
        public static int EstimateCacheScopeId { get; private set; }

        public static void BeginEstimateCacheScope()
        {
            EstimateCacheScopeId++;
            EstimateCacheEntries.Clear();
            _estimateCacheStats = default;
            _estimateCacheActive = true;
        }

        // Iterator-owned lifetime. An old/stopped owner must not close a newer turn's scope.
        internal static EstimateCacheScopeLease OpenEstimateCacheScope()
        {
            BeginEstimateCacheScope();
            return new EstimateCacheScopeLease(EstimateCacheScopeId);
        }

        internal readonly struct EstimateCacheScopeLease : IDisposable
        {
            private readonly int _scopeId;
            internal EstimateCacheScopeLease(int scopeId) => _scopeId = scopeId;
            public void Dispose()
            {
                if (_estimateCacheActive && EstimateCacheScopeId == _scopeId)
                    EndEstimateCacheScope();
            }
        }

        public static EstimateCacheStats EndEstimateCacheScope()
        {
            EstimateCacheStats stats = CurrentEstimateCacheStats;
            EstimateCacheEntries.Clear();
            _estimateCacheActive = false;
            EstimateCacheScopeId++;
            _estimateCacheStats = default;
            return stats;
        }

        // Runs `compute` through the cache when a scope is active. `appendKey` writes the complete
        // input of this estimate into the buffer (after the shared header).
        private static BattleEstimate CachedEstimate(int kind, int seed, AbilityMagnitudes magnitudes,
            Action<List<int>> appendKey, Func<BattleEstimate> compute)
        {
            using var __profile = new Game.Core.ProfileScope("Combat/WorthIt.Estimate");
            if (!EstimateCacheActive)
                return compute();

            List<int> buf = EstimateKeyBuffer;
            buf.Clear();
            buf.Add(EstimateKeyVersion);
            buf.Add(kind);
            buf.Add(seed);
            buf.Add(MonteCarloTrials);
            buf.Add(MaxSimulatedRounds);
            AppendKey(buf, magnitudes);
            appendKey(buf);
            // Borrow only for this synchronous lookup. The dictionary never retains the mutable
            // buffer: a miss snapshots it before compute (which may perform another estimate).
            if (EstimateLookupBuffer.Length < buf.Count)
                Array.Resize(ref EstimateLookupBuffer, Math.Max(buf.Count, EstimateLookupBuffer.Length * 2));
            buf.CopyTo(EstimateLookupBuffer);
            var key = new EstimateCacheKey(EstimateLookupBuffer, buf.Count);

            if (EstimateCacheEntries.TryGetValue(key, out BattleEstimate cached))
            {
                _estimateCacheStats.Hits++;
                return cached;
            }

            _estimateCacheStats.Misses++;
            key = new EstimateCacheKey(buf.ToArray(), buf.Count);
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            BattleEstimate result;
            using (new Game.Core.ProfileScope("Combat/WorthIt.Simulate"))
                result = compute();
            _estimateCacheStats.MissMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - start)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (EstimateCacheEntries.Count >= EstimateCacheMaxEntries)
                EstimateCacheEntries.Clear();
            EstimateCacheEntries[key] = result;
            return result;
        }

        private static void AppendKey(List<int> buf, AbilityMagnitudes m)
        {
            buf.Add(BitConverter.SingleToInt32Bits(m.CriticalDamageMultiplier));
            buf.Add(m.HyperkineticBonusDamage);
            buf.Add(m.CeramicArmorReduction);
            buf.Add(m.PyrokineticBonusDamage);
            buf.Add(m.BerserkAttackGain);
            buf.Add(m.BerserkDefenseLoss);
        }

        private static void AppendKey(List<int> buf, SideCommander c)
        {
            buf.Add(c.Present ? 1 : 0);
            buf.Add(c.Initiative);
            buf.Add(c.Fate);
        }

        // Order-preserving on purpose: the simulation walks units, abilities and tags in list order.
        private static void AppendKey(List<int> buf, List<BattleUnit> units)
        {
            buf.Add(-1000 - units.Count);
            foreach (BattleUnit u in units)
                AppendKey(buf, u);
        }

        private static void AppendKey(List<int> buf, IReadOnlyCollection<DefenderProfile> profiles,
            float defenseBonus, int initiativeBonus)
        {
            buf.Add(-1000 - profiles.Count);
            if (profiles is IReadOnlyList<DefenderProfile> indexed)
                for (int i = 0; i < indexed.Count; i++)
                    AppendKey(buf, ToBattleUnit(indexed[i], defenseBonus, initiativeBonus));
            else
                foreach (DefenderProfile p in profiles)
                    AppendKey(buf, ToBattleUnit(p, defenseBonus, initiativeBonus));
        }

        private static void AppendKey(List<int> buf, BattleUnit u)
        {
            buf.Add(BitConverter.SingleToInt32Bits(u.Attack));
            buf.Add(BitConverter.SingleToInt32Bits(u.Defense));
            buf.Add(u.Initiative);
            buf.Add(BitConverter.SingleToInt32Bits(u.Hp));
            buf.Add(BitConverter.SingleToInt32Bits(u.MaxHp));
            buf.Add(u.IsHero ? 1 : 0);
            buf.Add(u.HeroFate);
            buf.Add(u.IsSummoned ? 1 : 0);
            int abilities = u.Abilities?.Count ?? -1;
            buf.Add(abilities);
            for (int i = 0; i < abilities; i++)
                buf.Add(AbilityId(u.Abilities[i]));
            int tags = u.TypeTags?.Count ?? -1;
            buf.Add(tags);
            for (int i = 0; i < tags; i++)
                buf.Add((int)u.TypeTags[i]);
        }
        private static int AbilityId(string ability)
        {
            if (ability == null)
                return 0;
            if (!EstimateAbilityIds.TryGetValue(ability, out int id))
                EstimateAbilityIds[ability] = id = EstimateAbilityIds.Count + 1;
            return id;
        }

        private readonly struct EstimateCacheKey : IEquatable<EstimateCacheKey>
        {
            private readonly int[] _data;
            private readonly int _count;
            private readonly int _hash;

            public EstimateCacheKey(int[] data, int count)
            {
                _data = data;
                _count = count;
                unchecked
                {
                    int h = 17;
                    for (int i = 0; i < count; i++)
                        h = h * 31 + data[i];
                    _hash = h;
                }
            }

            public bool Equals(EstimateCacheKey other)
            {
                if (_hash != other._hash || _count != other._count)
                    return false;
                for (int i = 0; i < _count; i++)
                    if (_data[i] != other._data[i]) return false;
                return true;
            }

            public override bool Equals(object obj) => obj is EstimateCacheKey k && Equals(k);
            public override int GetHashCode() => _hash;
        }
    }
}
