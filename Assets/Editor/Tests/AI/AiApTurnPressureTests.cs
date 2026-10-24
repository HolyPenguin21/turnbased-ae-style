#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiApTurnPressureTests
    {
        private static readonly PlayerSetupData Us = new PlayerSetupData { Nickname = "Us", ColorIndex = 1 };

        [TearDown]
        public void TearDown() => ApTurnPressure.ClearAll();

        private static ApTurnMeasure Measure(int startAp, int endAp, float unmet) =>
            new ApTurnMeasure(startAp, endAp, unmet, 0, 0f, 0, 0f, 0);

        [TestCase(0f, 2f)]
        [TestCase(2f, 0f)]
        public void AllocatorApDeferralPreservesThePhysicalShortfall(float tech, float missingTech)
        {
            var mission = new MissionProposal { Kind = MissionKind.Scout, BaseValue = 30f,
                EffectiveValue = 30f, LocalAdmissionScore = 30f,
                Target = new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = new HexCoord(4, 0) },
                Requirements = new MissionRequirements { ApMinimum = 6f, ApDesired = 6f, ApMaximum = 6f,
                    TechMinimum = 2f, TechDesired = 2f, TechMaximum = 2f } };
            mission.Axes.Value[DesireAxis.Recon] = 1f;
            var snapshot = new WorldSnapshot { TurnNumber = 1, Self = new SelfSnapshot {
                ActionPoints = 0, Stockpile = new ResourceBundle { Tech = tech } } };
            var allocation = ResourceAllocator.BeginTurn(snapshot, Radar.Even(),
                new List<MissionProposal> { mission }, new List<Commitment>(), null).Pack();
            DeferredEntry deferred = allocation.Deferred.Single();
            Assert.That(allocation.Funded, Is.Empty);
            Assert.That(deferred.Reason, Is.EqualTo(DeferReason.InsufficientBudget));
            Assert.That(deferred.Required.Ap, Is.EqualTo(6f));
            Assert.That(deferred.Required.Tech, Is.EqualTo(2f));
            Assert.That(deferred.Available.Tech, Is.EqualTo(tech));
            Assert.That(deferred.Missing.Ap, Is.EqualTo(6f));
            Assert.That(deferred.Missing.Tech, Is.EqualTo(missingTech),
                "AP is checked first, but extra AP cannot solve a simultaneous Tech shortage");
        }

        [TestCase(4, 6f, 6f)]
        [TestCase(14, 4f, 0f)]
        [TestCase(0, 4f, 4f)]
        [Category("UnityEngine")]
        public void CardDemandRequiresUsefulWorkThatRemainingApCannotCover(int endAp, float workload, float unmet)
        {
            var root = PlayerRoot.Create(Us, "AP demand test");
            try
            {
                root.ActionPoints = endAp;
                var m = ApTurnPressure.Measure(Us, root, null, null, 18, null, workload, 1);
                Assert.That(m.CardsUnmet, Is.EqualTo(unmet));
                Assert.That(m.Demand, Is.EqualTo(18 - endAp + unmet));
            }
            finally { UnityEngine.Object.DestroyImmediate(root.gameObject); }
        }

        [Test, Category("UnityEngine")]
        public void IdleApAndNoUsefulWitnessDoNotCreateUnmetDemand()
        {
            var m = ApTurnPressure.Measure(Us, null, null, null, 14, null);
            Assert.That(m.Unmet, Is.Zero);
        }

        [Test, Category("UnityEngine")]
        public void DeferredDemandCountsOnlyActualApScarcityAndDoesNotSumAlternatives()
        {
            var mission = new MissionProposal { BaseValue = 10,
                Requirements = new MissionRequirements { ApDesired = 9 } };
            var deferred = new[] {
                new DeferredEntry { Mission = mission, Reason = DeferReason.InsufficientBudget,
                    Required = new ResourceVector(6), Missing = new ResourceVector(6) },
                new DeferredEntry { Mission = mission, Reason = DeferReason.InsufficientBudget,
                    Required = new ResourceVector(8), Missing = new ResourceVector(8) },
                new DeferredEntry { Mission = mission, Reason = DeferReason.LowValuePerAp,
                    Required = new ResourceVector(20), Missing = new ResourceVector(20) },
                new DeferredEntry { Mission = mission, Reason = DeferReason.InsufficientBudget,
                    Required = new ResourceVector(30), Missing = new ResourceVector(30, 0, 0, 0, 1) } };
            var m = ApTurnPressure.Measure(Us, null, null, null, 0, deferred);
            Assert.That(m.MissionsUnmet, Is.EqualTo(8));
            Assert.That(m.MissionsDeferred, Is.EqualTo(2));
        }

        [Test]
        public void Demand_IsSpentPlusUnmet_AndPressureIsItOverTheAp()
        {
            ApTurnMeasure m = new ApTurnMeasure(10, 1, 2f, 1, 3f, 2, 6f, 4);
            Assert.That(m.Spent, Is.EqualTo(9));
            Assert.That(m.Unmet, Is.EqualTo(11f));
            Assert.That(m.Demand, Is.EqualTo(20f));
            Assert.That(m.Pressure, Is.EqualTo(2f));
        }

        [Test]
        public void WitnessedDemand_AveragesThePastTurnsOnly_AndKeepsTheLastFew()
        {
            Assert.That(ApTurnPressure.WitnessedDemand(Us, 1), Is.Null);
            ApTurnPressure.Record(Us, 1, Measure(10, 0, 10f));   // 20
            ApTurnPressure.Record(Us, 2, Measure(8, 0, 4f));     // 12
            Assert.That(ApTurnPressure.WitnessedDemand(Us, 2), Is.EqualTo(20f), "the current turn never feeds itself");
            Assert.That(ApTurnPressure.WitnessedDemand(Us, 3), Is.EqualTo(16f));

            for (int t = 3; t <= 2 + AiConfigV2.apWitnessedDemandHistoryTurns; t++)
                ApTurnPressure.Record(Us, t, Measure(6, 0, 0f)); // 6 each
            Assert.That(ApTurnPressure.WitnessedDemand(Us, 99), Is.EqualTo(6f), "older turns roll off");
        }

        private static WorldSnapshot Snap(float? witnessedHistory) => new WorldSnapshot
        {
            Self = new SelfSnapshot
            {
                ApEconomy = new ApActionEconomySnapshot
                {
                    BaseActionPoints = 12,
                    EstimatedArmyApDemand = 7f,
                    EstimatedCardApDemand = 4f,
                    EstimatedDrawApDemand = 2f,
                    WitnessedApDemand = witnessedHistory,
                },
            },
        };

        [Test]
        public void UsefulApDemand_PrefersTheWitnessedHistory_OverTheStructuralGuess()
        {
            // No history: structural (7 + 4 + draws 2) x confidence.
            Assert.That(EffectEvaluationContext.ResolveUsefulApDemand(Snap(null), null),
                Is.EqualTo(13f * AiConfigV2.apStructuralDemandConfidence).Within(1e-4f));
            // History: taken as is (playtest #4: real demand ~2x the AP while the guess said idle).
            Assert.That(EffectEvaluationContext.ResolveUsefulApDemand(Snap(24f), null), Is.EqualTo(24f));
            // An evaluation-time workload (cards only) never reads below the history.
            Assert.That(EffectEvaluationContext.ResolveUsefulApDemand(Snap(24f), 5f), Is.EqualTo(24f));
            Assert.That(EffectEvaluationContext.ResolveUsefulApDemand(Snap(24f), 30f), Is.EqualTo(30f));
            // Real scarcity makes one more AP fully valuable.
            Assert.That(EffectEvaluationContext.ResolveMarginalApUtility(Snap(24f), null), Is.EqualTo(1f));
        }

        [Test]
        public void CardScore_ReadsOnTheTaskScoreScale_ThroughTheSameApEquivalent()
        {
            // One AP-equivalent on the card scale is one AP-equivalent on the TaskScore scale.
            Assert.That(ActionPrice.CardScoreToTaskScore(AiConfigV2.cardScorePerApEquivalent),
                Is.EqualTo(AiConfigV2.taskScorePerApEquivalent).Within(1e-4f));
            // Playtest #5: a Base's +2 AP/turn effect of 0.84 card points is ~5.6 TaskScore points.
            Assert.That(ActionPrice.CardScoreToTaskScore(0.84f), Is.EqualTo(5.6f).Within(0.01f));
            Assert.That(ActionPrice.CardScoreToTaskScore(-1f), Is.Zero);
        }
    }
}
#endif
