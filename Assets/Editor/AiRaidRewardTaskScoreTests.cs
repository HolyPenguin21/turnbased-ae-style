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
            RaidObjective weak = Evaluate(target, 0.1f);
            RaidObjective strong = Evaluate(target, 1000f);

            Assert.That(weak.TaskScore.RaidReward, Is.EqualTo(8f));
            Assert.That(strong.TaskScore.RaidReward, Is.EqualTo(8f));
            Assert.That(weak.TaskScore.EconomicHexBenefit, Is.Zero);
            Assert.That(weak.BaseValue, Is.EqualTo(8f + weak.TaskScore.OwnTerritoryProximity));
            Assert.That(strong.BaseValue, Is.EqualTo(weak.BaseValue),
                "defender combat strength must not masquerade as loot value");
        }

        [Test]
        public void EventGuardRaid_ReceivesSameFixedReward_PlusTheEventReward()
        {
            RaidObjective guard = Evaluate(
                RaidTargetRef.ForEventGuard(new HexCoord(3, 0)), 999f);
            RaidObjective neutral = Evaluate(RaidTargetRef.ForNeutralArmy(42), 999f);

            Assert.That(guard.TaskScore.RaidReward, Is.EqualTo(AiConfigV2.RaidReward));
            // No remembered guard here: the unknown-tier reward.
            Assert.That(guard.TaskScore.EventReward, Is.EqualTo(AiConfigV2.taskScoreEventRewardUnknownTier));
            Assert.That(guard.BaseValue, Is.EqualTo(8f + AiConfigV2.taskScoreEventRewardUnknownTier
                + guard.TaskScore.OwnTerritoryProximity));
            Assert.That(neutral.TaskScore.EventReward, Is.Zero, "a neutral army carries no event reward");
        }

        [Test]
        public void EventReward_FollowsTheAuthoredGuardTier()
        {
            Assert.That(TaskScoreEvaluator.EventReward(0), Is.EqualTo(AiConfigV2.taskScoreEventRewardLight));
            Assert.That(TaskScoreEvaluator.EventReward(1), Is.EqualTo(AiConfigV2.taskScoreEventRewardMedium));
            Assert.That(TaskScoreEvaluator.EventReward(2), Is.EqualTo(AiConfigV2.taskScoreEventRewardHeavy));
            Assert.That(TaskScoreEvaluator.EventReward(-1), Is.EqualTo(AiConfigV2.taskScoreEventRewardUnknownTier));

            var definition = new Game.Cards.EventDefinition { name = "Bunker" };
            definition.variants.Add(new Game.Cards.EventVariant { guardArmyName = "Light" });
            definition.variants.Add(new Game.Cards.EventVariant { guardArmyName = "Medium" });
            definition.variants.Add(new Game.Cards.EventVariant { guardArmyName = "Heavy" });
            var entry = new Game.Map.HexEventRegistry.Entry { Definition = definition, GuardArmyName = "Heavy" };
            Assert.That(Game.Ai.HexEventGuardEstimate.RewardTier(entry), Is.EqualTo(2));
            entry.GuardArmyName = "Someone else";
            Assert.That(Game.Ai.HexEventGuardEstimate.RewardTier(entry), Is.EqualTo(-1));
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

        private static RaidObjective Evaluate(RaidTargetRef target, float defenderValue)
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
            return RaidObjectiveEvaluator.ForTrackedTarget(snap, report, target);
        }
    }
}
#endif

