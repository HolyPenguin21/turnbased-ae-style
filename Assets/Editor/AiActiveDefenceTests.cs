#if UNITY_INCLUDE_TESTS
using Game.Ai.V2;
using Game.HexGrid;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiActiveDefenceTests
    {
        [Test]
        public void StableIdentity_UsesEnemyArmyIdIncludingZero_NotMovingHex()
        {
            MissionIntentKey zero = MissionIntentKey.ForActiveDefence(0);
            var first = new MissionProposal
            {
                Kind = MissionKind.ActiveDefence,
                Target = new ActiveDefenceMissionTarget
                {
                    EnemyArmyId = 0, LastKnownHex = new HexCoord(1, 2),
                },
            };
            var moved = new MissionProposal
            {
                Kind = MissionKind.ActiveDefence,
                Target = new ActiveDefenceMissionTarget
                {
                    EnemyArmyId = 0, LastKnownHex = new HexCoord(8, 9),
                },
            };

            Assert.That(zero.ObjectiveId, Is.Zero);
            Assert.That(MissionIntentKey.For(first), Is.EqualTo(MissionIntentKey.For(moved)));
            Assert.That(StableMissionKey.For(first), Is.EqualTo(StableMissionKey.For(moved)));
        }

        [Test]
        public void Preemption_IsStrictAndIncludesSwitchingCost()
        {
            const float eps = 0.001f;
            Assert.That(ResourceAllocator.ActiveDefencePreemptsRaid(12.001f, 10f, 2f, eps), Is.False);
            Assert.That(ResourceAllocator.ActiveDefencePreemptsRaid(12.002f, 10f, 2f, eps), Is.True);
        }

        [Test]
        public void ActiveDefenceScore_UsesCanonicalSlotsWithoutSeverityMultiplier()
        {
            var objective = new ActiveDefenceObjective
            {
                TaskScore = new TaskScore(strategicRelevance: 10f,
                    threatDirection: 4f, militaryTargetRelevance: 3f,
                    staleness: -1f, ownTerritoryProximity: 2f),
            };
            var actor = new ArmySnapshot
            {
                ActivationApCost = 2, HasActivatedThisTurn = false,
            };
            TaskScore score = ActiveDefenceObjectiveEvaluator.WithResponse(
                objective, actor, winChance: 0.75f, eta: 2, moverOpportunityCost: 1f);

            Assert.That(score.StrategicRelevance, Is.EqualTo(10f));
            Assert.That(score.ThreatDirection, Is.EqualTo(4f));
            Assert.That(score.MilitaryTargetRelevance, Is.EqualTo(3f));
            Assert.That(score.MoverOpportunityCost, Is.EqualTo(1f));
        }

        [Test]
        public void ActiveDefence_RemainsInAggressionLane()
        {
            Assert.That(MissionAdmissionPolicy.LaneFor(new MissionProposal
            {
                Kind = MissionKind.ActiveDefence,
            }), Is.EqualTo(ExecutionLane.Aggression));
        }

        [Test]
        public void PursuitStopsOnlyWhenEveryGuardAgrees()
        {
            Assert.That(ActiveDefenceObjectiveEvaluator.ShouldStopPursuit(
                false, true, true, false), Is.True);
            Assert.That(ActiveDefenceObjectiveEvaluator.ShouldStopPursuit(
                true, true, true, false), Is.False);
            Assert.That(ActiveDefenceObjectiveEvaluator.ShouldStopPursuit(
                false, false, true, false), Is.False);
            Assert.That(ActiveDefenceObjectiveEvaluator.ShouldStopPursuit(
                false, true, false, false), Is.False);
            Assert.That(ActiveDefenceObjectiveEvaluator.ShouldStopPursuit(
                false, true, true, true), Is.False);
        }
    }
}
#endif
