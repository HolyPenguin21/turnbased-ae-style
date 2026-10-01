#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
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
    }
}
#endif
