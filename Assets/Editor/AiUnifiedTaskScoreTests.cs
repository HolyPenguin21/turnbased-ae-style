#if UNITY_INCLUDE_TESTS
using System;
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiUnifiedTaskScoreTests
    {
        [Test]
        public void Fold_UsesEachSemanticContributionExactlyOnce()
        {
            var score = new TaskScore(
                economicHexBenefit: 13f, payback: 5f, ownTerritoryProximity: 3f,
                cardPrice: 4f, delivery: 2f, moverOpportunityCost: 1f,
                hexThreatRisk: 2f, existingValueLoss: 3f);
            Assert.That(score.Value, Is.EqualTo(9f).Within(0.0001f));
        }

        [Test]
        public void Deficit_NeverGeneratesValueWithoutMarginalIncome()
        {
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(0f, 1f), Is.Zero);
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(-1f, 1f), Is.Zero);
            float physical = TaskScoreEvaluator.EconomicHexBenefit(1f, 0f);
            float withDeficit = TaskScoreEvaluator.EconomicHexBenefit(1f, 1f);
            Assert.That(withDeficit - physical,
                Is.EqualTo(AiConfigV2.taskScoreEconomicDeficitBonusMax).Within(0.0001f));
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(100f, 1f)
                    - TaskScoreEvaluator.EconomicHexBenefit(100f, 0f),
                Is.LessThanOrEqualTo(AiConfigV2.taskScoreEconomicDeficitBonusMax + 0.0001f));
        }

        [Test]
        public void PhysicalCostConversions_UseOneSharedScale()
        {
            const float ap = 2f, resources = 3f, extraAp = 1f, distance = 6f;
            var extraction = new TaskScore(
                cardPrice: TaskScoreEvaluator.CardPrice(ap, resources),
                delivery: TaskScoreEvaluator.Delivery(extraAp, distance));
            var foundation = new TaskScore(
                cardPrice: TaskScoreEvaluator.CardPrice(ap, resources),
                delivery: TaskScoreEvaluator.Delivery(extraAp, distance));
            Assert.That(extraction.CardPrice, Is.EqualTo(foundation.CardPrice));
            Assert.That(extraction.Delivery, Is.EqualTo(foundation.Delivery));
            Assert.That(extraction.Value, Is.EqualTo(foundation.Value));
        }

        [Test]
        public void EconomyMission_TransportsDeliveredValue_NotSiteOnlyValue()
        {
            var score = new TaskScore(economicHexBenefit: 15f,
                cardPrice: 4f, delivery: 3f);
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicInfrastructure,
                TargetHex = new HexCoord(4, 2),
                Value = score.Value,
                WorldTaskScore = score,
                EconomySiteValue = 11f,
                EconomyStrategicUrgency = 3f,
                EconomyTravelCost = 6f,
            };
            var proposals = EconomyMissionPlanner.Propose(null, null,
                Array.Empty<MissionIntent>(), new[] { demand });
            Assert.That(proposals, Has.Count.EqualTo(1));
            MissionProposal proposal = proposals[0];
            Assert.That(proposal.BaseValue, Is.EqualTo(score.Value));
            Assert.That(proposal.LocalAdmissionScore, Is.EqualTo(score.Value + 3f));
            Assert.That(((EconomyMissionTarget)proposal.Target).BuildValue,
                Is.EqualTo(11f), "site merit is separate from delivered world-task merit");
        }

        [Test]
        public void EconomyAdmission_DoesNotDeductPhysicalCostTwice()
        {
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicInfrastructure,
                TargetHex = new HexCoord(1, 2),
                Value = 10f,
                EconomySiteValue = 14f,
            };
            MissionProposal proposal = EconomyMissionPlanner.Propose(null, null,
                Array.Empty<MissionIntent>(), new[] { demand })[0];
            proposal.Requirements.EtaTurns = 2; // No same-turn policy bonus.
            proposal.Requirements.ApDesired = 1f;
            proposal.Requirements.EstimatedDistance = 1f;
            float baseline = MissionAdmissionPolicy.AdmissionRank(proposal);
            proposal.Requirements.ApDesired = 9f;
            proposal.Requirements.EstimatedDistance = 30f;
            Assert.That(MissionAdmissionPolicy.AdmissionRank(proposal),
                Is.EqualTo(baseline).Within(0.0001f),
                "physical AP/distance were already priced once in TaskScore");
        }
    }
}
