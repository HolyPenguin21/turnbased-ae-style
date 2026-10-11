#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Ai;
using Game.Ai.V2;
using Game.Combat;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class AiCombatOpportunityWarmTests
    {
        [SetUp] public void SetUp() => WorthIt.EndEstimateCacheScope();
        [TearDown] public void TearDown() => WorthIt.EndEstimateCacheScope();

        private static WorldSnapshot Snapshot(float attack)
        {
            var body = new WorthIt.DefenderProfile(1, false, attack: attack, hitPoints: 4, initiative: 2);
            var commanders = new List<OwnCommandHero>();
            for (int i = 0; i < 4; i++)
                commanders.Add(new OwnCommandHero(new HeroRoleEvaluator.HeroProfile(3,
                    new WorthIt.SideCommander(i + 1, i + 1), 0, 1, i), ForceSource.Map));
            var neutral = new PlayerSetupData { IsNeutral = true };
            return new WorldSnapshot {
                Self = new SelfSnapshot {
                    Armies = new[] { new ArmySnapshot { ArmyId = 1, IsStructuralRaidActor = true,
                        Members = new[] { body, body }, MaxMovement = 2 } },
                    CommandHeroes = commanders,
                },
                Known = new KnownSnapshot {
                    NeutralSightings = new[] { new AiMapMemory.KnownEnemySighting(new HexCoord(1, 0),
                        neutral, "guard", 1, 1, 2,
                        new[] { new WorthIt.DefenderProfile(1, false, attack: 2, hitPoints: 4) }) },
                },
            };
        }

        [Test]
        public void WarmEstimates_ZeroBudgetYieldsBetweenCommanderEstimates()
        {
            WorthIt.BeginEstimateCacheScope();
            var warm = CombatOpportunityAnalyzer.WarmEstimates(Snapshot(3), budgetSeconds: 0);
            int previous = 0, yields = 0;
            while (warm.MoveNext())
            {
                int misses = WorthIt.CurrentEstimateCacheStats.Misses;
                Assert.That(misses - previous, Is.LessThanOrEqualTo(1),
                    "One target's commander candidates must not form an indivisible batch");
                previous = misses; yields++;
            }
            Assert.That(yields, Is.EqualTo(5));
            Assert.That(WorthIt.CurrentEstimateCacheStats.Misses - previous, Is.LessThanOrEqualTo(1));
            Assert.That(WorthIt.CurrentEstimateCacheStats.Misses, Is.EqualTo(5));
        }

        [Test]
        public void WarmEstimates_AfterChangedRosterAnalyzeUsesFreshCachedResults()
        {
            WorthIt.BeginEstimateCacheScope();
            var before = Snapshot(3);
            var warm = CombatOpportunityAnalyzer.WarmEstimates(before, 0);
            while (warm.MoveNext()) { }
            CombatOpportunityAnalyzer.Analyze(before);
            var after = Snapshot(7);
            int oldMisses = WorthIt.CurrentEstimateCacheStats.Misses;
            warm = CombatOpportunityAnalyzer.WarmEstimates(after, 0);
            while (warm.MoveNext()) { }
            int newMisses = WorthIt.CurrentEstimateCacheStats.Misses;
            Assert.That(newMisses, Is.GreaterThan(oldMisses));
            var warmed = CombatOpportunityAnalyzer.Analyze(after).Best;
            Assert.That(WorthIt.CurrentEstimateCacheStats.Misses, Is.EqualTo(newMisses));
            WorthIt.EndEstimateCacheScope();
            var plain = CombatOpportunityAnalyzer.Analyze(after).Best;
            Assert.That(warmed.ReadyWinChance, Is.EqualTo(plain.ReadyWinChance));
            Assert.That(warmed.AssemblableWinChance, Is.EqualTo(plain.AssemblableWinChance));
            Assert.That(warmed.OpportunityScore, Is.EqualTo(plain.OpportunityScore));
        }
    }
}
#endif
