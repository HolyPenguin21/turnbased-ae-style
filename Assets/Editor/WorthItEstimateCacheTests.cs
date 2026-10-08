#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Cards;
using Game.Ai.V2;
using Game.Combat;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Guards WorthIt's exact estimate cache (WorthIt.EstimateCache.cs). If a test here fails after
    // a combat change, the cache key no longer describes the full simulation input: add the new
    // field/parameter to the key (or declare it non-simulated) — do not just update the list.
    public sealed class WorthItEstimateCacheTests
    {
        [SetUp]
        public void SetUp() => WorthIt.EndEstimateCacheScope();

        [TearDown]
        public void TearDown() => WorthIt.EndEstimateCacheScope();

        private static WorthIt.DefenderProfile Unit(float attack, float defense, float hp,
            int initiative = 2, params string[] abilities) =>
            new WorthIt.DefenderProfile(defense, false, new List<UnitTypeTag>(), attack, hp, initiative,
                abilities.ToList(), hp);

        private static List<WorthIt.DefenderProfile> Attackers() => new List<WorthIt.DefenderProfile>
            { Unit(3, 2, 4), Unit(2, 2, 3), Unit(4, 1, 3, 3, UnitAbilities.Berserk) };

        private static List<WorthIt.DefenderProfile> Defenders() => new List<WorthIt.DefenderProfile>
            { Unit(2, 3, 4), Unit(3, 2, 3) };

        private static string[] InstanceFields(System.Type t) =>
            t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => f.Name).OrderBy(n => n, System.StringComparer.Ordinal).ToArray();

        private static string[] Sorted(IEnumerable<string> names) =>
            names.OrderBy(n => n, System.StringComparer.Ordinal).ToArray();

        // ---- 2026-10-08: the cached estimate is policy-free; the verdict is applied outside it ----

        [Test]
        public void OneEstimateServesTwoThresholds_EachWithItsOwnVerdict()
        {
            var strong = new List<WorthIt.DefenderProfile> { Unit(30, 10, 60), Unit(30, 10, 60), Unit(30, 10, 60) };
            var weak = new[] { new WorthIt.DefendingArmy(Defenders(), default) };
            WorthIt.BeginEstimateCacheScope();
            bool atLocal = Game.Ai.V2.GroundCombatFeasibility.Clears(strong, default, weak, 0.40f, 0f,
                out float w1, out _, true);
            bool atFresh = Game.Ai.V2.GroundCombatFeasibility.Clears(strong, default, weak, 0.80f, 0f,
                out float w2, out _, true);
            WorthIt.EstimateCacheStats stats = WorthIt.EndEstimateCacheScope();
            Assert.That(w2, Is.EqualTo(w1), "the same simulation answered both");
            Assert.That(stats.Hits, Is.GreaterThanOrEqualTo(1), "second threshold is a cache hit, not a re-simulation");
            Assert.That(atLocal, Is.EqualTo(w1 >= 0.40f));
            Assert.That(atFresh, Is.EqualTo(w2 >= 0.80f));
        }

        [Test]
        public void AWoundedOwnBody_IsACacheMiss_NotAStaleHit()
        {
            var healthy = new List<WorthIt.DefenderProfile> { Unit(3, 2, 10), Unit(3, 2, 10) };
            var wounded = new List<WorthIt.DefenderProfile> { Unit(3, 2, 10), Unit(3, 2, 4) };
            var opp = new[] { new WorthIt.DefendingArmy(Defenders(), default) };
            WorthIt.BeginEstimateCacheScope();
            WorthIt.EstimateSequential(healthy, default, opp, 0f);
            WorthIt.EstimateSequential(healthy, default, opp, 0f);
            WorthIt.EstimateSequential(wounded, default, opp, 0f);
            WorthIt.EstimateCacheStats stats = WorthIt.EndEstimateCacheScope();
            Assert.That(stats.Hits, Is.EqualTo(1));
            Assert.That(stats.Misses, Is.EqualTo(2));
        }

        // ---- Key completeness guards ----

        [Test]
        public void KeyCoversEveryBattleUnitField()
        {
            System.Type battleUnit = typeof(WorthIt).GetNestedType("BattleUnit", BindingFlags.NonPublic);
            Assert.That(battleUnit, Is.Not.Null);
            Assert.That(InstanceFields(battleUnit), Is.EqualTo(Sorted(WorthIt.EstimateKeyBattleUnitFields)),
                "BattleUnit changed: add the new field to WorthIt.EstimateCache AppendKey(List<BattleUnit>).");
        }

        [Test]
        public void KeyCoversEverySideCommanderField()
        {
            Assert.That(InstanceFields(typeof(WorthIt.SideCommander)),
                Is.EqualTo(Sorted(WorthIt.EstimateKeySideCommanderFields)),
                "SideCommander changed: add the new field to WorthIt.EstimateCache AppendKey(SideCommander).");
        }

        [Test]
        public void KeyCoversEveryAbilityMagnitude()
        {
            Assert.That(InstanceFields(typeof(AbilityMagnitudes)),
                Is.EqualTo(Sorted(WorthIt.EstimateKeyAbilityMagnitudesFields)),
                "AbilityMagnitudes changed: add the new field to WorthIt.EstimateCache AppendKey(AbilityMagnitudes).");
        }

        [Test]
        public void EveryDefenderProfileFieldHasADeclaredKeyFate()
        {
            string[] declared = Sorted(WorthIt.EstimateKeyDefenderProfileSimulatedFields
                .Concat(WorthIt.EstimateKeyDefenderProfileNonSimulatedFields));
            Assert.That(InstanceFields(typeof(WorthIt.DefenderProfile)), Is.EqualTo(declared),
                "DefenderProfile changed: if the simulation reads the new field, carry it into BattleUnit "
                + "(ToBattleUnits) and the key; otherwise list it as non-simulated.");
        }

        [Test]
        public void SimulationSignatureMatchesTheKey()
        {
            MethodInfo simulate = typeof(WorthIt).GetMethod("SimulateOneBattle",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(simulate, Is.Not.Null);
            Assert.That(simulate.GetParameters().Select(p => p.Name).ToArray(),
                Is.EqualTo(WorthIt.EstimateKeySimulateOneBattleParameters),
                "SimulateOneBattle gained/lost an input: put it into the WorthIt estimate cache key.");
        }

        // ---- Behaviour ----

        [Test]
        public void SingleEstimate_ReusesTrialCollections()
        {
            var attackers = new[] { Unit(2, 0, 1), Unit(2, 0, 1) };
            var defenders = new[] { Unit(2, 0, 1), Unit(2, 0, 1) };
            var bytes = AiPowerOptimizationTests.AllocatedBytes();
            WorthIt.Estimate(attackers, defenders, 0);
            long before = bytes();
            WorthIt.Estimate(attackers, defenders, 0);
            long allocated = bytes() - before;
            TestContext.WriteLine($"single estimate allocated: {allocated}");
            // The original implementation uses 48,648 bytes here under Mono. Dice arrays are
            // still allocated by the shared combat kernel; this guards removal of trial lists.
            Assert.That(allocated, Is.LessThan(24 * 1024),
                "Trial scratch must be reused across all 25 virtual battles");
        }

        [Test]
        public void SimulationResults_MatchRecordedBaseline()
        {
            var text = new System.Text.StringBuilder();
            string[] abilities = { UnitAbilities.Berserk, UnitAbilities.ShockAttack,
                UnitAbilities.CeramicArmor, UnitAbilities.Splash, UnitAbilities.Scorcher,
                UnitAbilities.Regeneration, UnitAbilities.CriticalDamage };
            for (int i = 0; i < 30; i++)
            {
                var attackers = new[] { Unit(2 + i % 4, i % 3, 3 + i % 5, 1 + i % 3,
                    abilities[i % abilities.Length]), Unit(3, 1, 4, 2) };
                var defenders = new[] { Unit(2, 1 + i % 3, 4, 2,
                    abilities[(i + 2) % abilities.Length]), Unit(3, 2, 3, 1) };
                var commander = new WorthIt.SideCommander(i % 3, i % 4);
                var enemyCommander = new WorthIt.SideCommander(i % 2, i % 3);
                var single = WorthIt.Estimate(attackers, defenders, i % 2, commander, enemyCommander);
                var sequence = WorthIt.EstimateSequential(attackers, commander, new[] {
                    new WorthIt.DefendingArmy(defenders, enemyCommander, i % 2),
                    new WorthIt.DefendingArmy(new[] { Unit(2, 1, 3) }, default, 0),
                }, 0);
                foreach (var result in new[] { single, sequence })
                    foreach (float value in new[] { result.WinChance, result.ExpectedSurvivingHpRatioOnWin,
                        result.CriticalAfterBattleChance })
                        text.Append(System.BitConverter.ToString(System.BitConverter.GetBytes(value))).Append('|');
            }
            using (var hash = System.Security.Cryptography.SHA256.Create())
            {
                string actual = System.BitConverter.ToString(hash.ComputeHash(
                    System.Text.Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
                Assert.That(actual, Is.EqualTo("1EFAECD28958C4B4475EBAE17624222809C4BF396342B0E8597A05DF539FDAA4"));
            }
        }

        [Test]
        [TestCase(EquipmentStat.Defense)]
        [TestCase(EquipmentStat.HitPoints)]
        [TestCase(EquipmentStat.Initiative)]
        public void MutatorProjectionWritesANewCacheEntryAndReadsItOnRepeat(EquipmentStat stat)
        {
            var host = AttachmentSlotTests.Host();
            var equipment = AttachmentSlotTests.Attachment(AttachmentSlot.Equipment);
            var mutator = AttachmentSlotTests.Attachment(AttachmentSlot.Mutator, stat);
            var before = new[] { AiPower.ToDefenderProfile(host, equipment, null) };
            var after = new[] { AiPower.ToDefenderProfile(host, equipment, mutator) };
            WorthIt.BeginEstimateCacheScope();
            WorthIt.Estimate(before, Defenders(), 0);
            var first = WorthIt.Estimate(after, Defenders(), 0);
            var repeated = WorthIt.Estimate(after, Defenders(), 0);
            var stats = WorthIt.EndEstimateCacheScope();
            Assert.That(stats.Misses, Is.EqualTo(2));
            Assert.That(stats.Hits, Is.EqualTo(1));
            Assert.That(repeated.WinChance, Is.EqualTo(first.WinChance));
            Assert.That(repeated.ExpectedSurvivingHpRatioOnWin, Is.EqualTo(first.ExpectedSurvivingHpRatioOnWin));
        }

        [Test]
        public void CachedResultIsIdenticalToUncached()
        {
            WorthIt.BattleEstimate plain = WorthIt.Estimate(Attackers(), Defenders(), 1f);
            WorthIt.BeginEstimateCacheScope();
            WorthIt.BattleEstimate first = WorthIt.Estimate(Attackers(), Defenders(), 1f);
            WorthIt.BattleEstimate second = WorthIt.Estimate(Attackers(), Defenders(), 1f);
            WorthIt.EstimateCacheStats stats = WorthIt.EndEstimateCacheScope();

            Assert.That(first.WinChance, Is.EqualTo(plain.WinChance));
            Assert.That(second.WinChance, Is.EqualTo(plain.WinChance));
            Assert.That(second.ExpectedSurvivingHpRatioOnWin, Is.EqualTo(plain.ExpectedSurvivingHpRatioOnWin));
            Assert.That(second.CriticalAfterBattleChance, Is.EqualTo(plain.CriticalAfterBattleChance));
            Assert.That(stats.Hits, Is.EqualTo(1));
            Assert.That(stats.Misses, Is.EqualTo(1));
        }

        [Test]
        public void SequentialCachedResultIsIdenticalToUncached()
        {
            var armies = new[]
            {
                new WorthIt.DefendingArmy(Defenders(), new WorthIt.SideCommander(1, 1)),
                new WorthIt.DefendingArmy(new List<WorthIt.DefenderProfile> { Unit(2, 2, 3) }, default, 2f),
            };
            WorthIt.BattleEstimate plain = WorthIt.EstimateSequential(Attackers(), default, armies, 1f);
            WorthIt.BeginEstimateCacheScope();
            WorthIt.EstimateSequential(Attackers(), default, armies, 1f);
            WorthIt.BattleEstimate cached = WorthIt.EstimateSequential(Attackers(), default, armies, 1f);
            WorthIt.EndEstimateCacheScope();

            Assert.That(cached.WinChance, Is.EqualTo(plain.WinChance));
            Assert.That(cached.ExpectedSurvivingHpRatioOnWin, Is.EqualTo(plain.ExpectedSurvivingHpRatioOnWin));
        }

        [Test]
        public void DifferentHeroRolesWithSameSeed_DoNotShareCachedEstimate()
        {
            // +1 IsHero and -31 FateMax cancel in AccumulateProfileHash. The exact cache key
            // must distinguish these rosters even when the seed cannot.
            var body = new WorthIt.DefenderProfile(0, false, attack: 2, hitPoints: 4,
                initiative: 2, fateMax: 31);
            var hero = new WorthIt.DefenderProfile(0, false, attack: 2, hitPoints: 4,
                initiative: 2, isHero: true, fateMax: 0);
            var ordinary = new List<WorthIt.DefenderProfile> { Unit(2, 2, 3), body };
            var heroic = new List<WorthIt.DefenderProfile> { Unit(2, 2, 3), hero };
            var defenders = Defenders();
            MethodInfo seed = typeof(WorthIt).GetMethod("BuildRosterSeed",
                BindingFlags.NonPublic | BindingFlags.Static);
            object Seed(List<WorthIt.DefenderProfile> roster) => seed.Invoke(null,
                new object[] { roster, defenders, 1f, default(WorthIt.SideCommander), default(WorthIt.SideCommander) });
            Assert.That(Seed(ordinary), Is.EqualTo(Seed(heroic)));
            var expected = WorthIt.Estimate(heroic, defenders, 1f);
            WorthIt.BeginEstimateCacheScope();
            WorthIt.Estimate(ordinary, defenders, 1f);
            var actual = WorthIt.Estimate(heroic, defenders, 1f);
            var stats = WorthIt.EndEstimateCacheScope();
            Assert.That(stats.Misses, Is.EqualTo(2));
            Assert.That(stats.Hits, Is.Zero);
            Assert.That(actual.WinChance, Is.EqualTo(expected.WinChance));
            Assert.That(actual.ExpectedSurvivingHpRatioOnWin, Is.EqualTo(expected.ExpectedSurvivingHpRatioOnWin));
        }

        [Test]
        public void EverySimulatedInputChangeMissesTheCache()
        {
            WorthIt.BeginEstimateCacheScope();
            WorthIt.Estimate(Attackers(), Defenders(), 1f);

            var variants = new List<System.Action>
            {
                () => WorthIt.Estimate(Attackers(), Defenders(), 2f),                          // hex bonus
                () => WorthIt.Estimate(Attackers(), new List<WorthIt.DefenderProfile>
                    { Unit(2, 3, 4), Unit(3, 2, 2) }, 1f),                                       // defender hp
                () => WorthIt.Estimate(Attackers(), new List<WorthIt.DefenderProfile>
                    { Unit(2, 3, 4), Unit(3, 2, 3, 2, UnitAbilities.CeramicArmor) }, 1f),        // ability
                () => WorthIt.Estimate(Attackers(), Defenders(), 1f, new WorthIt.SideCommander(2, 1)), // commander
                () => WorthIt.Estimate(Attackers(), Defenders(), 1f, default, new WorthIt.SideCommander(0, 2)),
                () => WorthIt.Estimate(Attackers(), Defenders(), 1f, default, default,
                    new AbilityMagnitudes(3f, 2, 1, 2, 1, 1)),                                   // magnitudes
            };
            foreach (System.Action v in variants)
                v();

            WorthIt.EstimateCacheStats stats = WorthIt.EndEstimateCacheScope();
            Assert.That(stats.Hits, Is.EqualTo(0), "a changed input must never be served from the cache");
            Assert.That(stats.Misses, Is.EqualTo(1 + variants.Count));
        }

        [Test]
        public void NothingIsCachedOutsideAScope()
        {
            WorthIt.Estimate(Attackers(), Defenders(), 1f);
            WorthIt.Estimate(Attackers(), Defenders(), 1f);
            Assert.That(WorthIt.CurrentEstimateCacheStats.Hits, Is.EqualTo(0));
            Assert.That(WorthIt.CurrentEstimateCacheStats.Entries, Is.EqualTo(0));
        }

        [Test]
        public void BeginStartsEmpty()
        {
            WorthIt.BeginEstimateCacheScope();
            WorthIt.Estimate(Attackers(), Defenders(), 1f);
            WorthIt.BeginEstimateCacheScope(); // a leaked scope from a stopped coroutine
            WorthIt.Estimate(Attackers(), Defenders(), 1f);
            WorthIt.EstimateCacheStats stats = WorthIt.EndEstimateCacheScope();
            Assert.That(stats.Hits, Is.EqualTo(0));
        }
    }
}
#endif
