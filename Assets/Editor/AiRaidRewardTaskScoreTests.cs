#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiRaidRewardTaskScoreTests
    {
        [Test]
        public void NeutralRaidReward_IsEightAndIndependentOfDefenderPowerValue()
        {
            RaidTargetRef target = RaidTargetRef.ForNeutralArmy(42);
            AggressionObjective weak = Evaluate(target, 0.1f);
            AggressionObjective strong = Evaluate(target, 1000f);

            Assert.That(weak.TaskScore.RaidReward, Is.EqualTo(8f));
            Assert.That(strong.TaskScore.RaidReward, Is.EqualTo(8f));
            Assert.That(weak.TaskScore.EconomicHexBenefit, Is.Zero);
            Assert.That(weak.BaseValue, Is.EqualTo(8f));
            Assert.That(strong.BaseValue, Is.EqualTo(weak.BaseValue),
                "defender combat strength must not masquerade as loot value");
        }

        [Test]
        public void EventGuardRaid_ReceivesSameFixedReward()
        {
            AggressionObjective guard = Evaluate(
                RaidTargetRef.ForEventGuard(new HexCoord(3, 0)), 999f);

            Assert.That(guard.TaskScore.RaidReward, Is.EqualTo(AiConfigV2.RaidReward));
            Assert.That(guard.BaseValue, Is.EqualTo(8f));
        }

        [Test]
        public void RewardIsNotImplicitOnEconomyOrReconOrLifecycleScores()
        {
            var economy = new TaskScore(economicHexBenefit: 12f);
            var recon = new TaskScore(infoGain: 10f);
            var lifecycle = default(TaskScore);

            Assert.That(economy.RaidReward, Is.Zero);
            Assert.That(recon.RaidReward, Is.Zero);
            Assert.That(lifecycle.RaidReward, Is.Zero);
            Assert.That(economy.Value, Is.EqualTo(12f));
            Assert.That(recon.Value, Is.EqualTo(10f));
            Assert.That(lifecycle.Value, Is.Zero);
        }

        private static AggressionObjective Evaluate(RaidTargetRef target, float defenderValue)
        {
            HexCoord hex = new HexCoord(3, 0);
            var opportunity = new CombatOpportunity(
                hasTarget: true, targetHex: hex, target: target,
                targetOwner: null, targetIsNeutral: true,
                defenderCount: 1, readyWinChance: 0.9f,
                assemblableWinChance: 0.9f, canCoverAll: true,
                battleCostProxy: 0.1f, eta: 1,
                targetValue: defenderValue, confidence: 1f,
                gatePassed: true, opportunityScore: 1f);
            var report = new CombatOpportunityReport
            {
                All = new[] { opportunity },
                NeutralOpportunities = new[] { opportunity },
            };
            var snap = new WorldSnapshot { Self = new SelfSnapshot() };
            return AggressionObjectiveEvaluator.ForTrackedTarget(snap, report, target);
        }
    }
}
#endif
