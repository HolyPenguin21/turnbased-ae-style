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
        public void ActiveDefenceValue_IsIndependentOfMilitaryPotentialRealization()
        {
            WorldSnapshot low = DefenceWorld(bestStack: 10f, totalPotential: 100f);
            WorldSnapshot high = DefenceWorld(bestStack: 90f, totalPotential: 100f);

            ActiveDefenceObjective lowDefence = ActiveDefenceObjectiveEvaluator.Enumerate(low)[0];
            ActiveDefenceObjective highDefence = ActiveDefenceObjectiveEvaluator.Enumerate(high)[0];

            Assert.That(highDefence.BaseValue, Is.EqualTo(lowDefence.BaseValue));
            Assert.That(highDefence.TaskScore.Value, Is.EqualTo(lowDefence.TaskScore.Value));
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

        private static WorldSnapshot DefenceWorld(float bestStack, float totalPotential)
        {
            var enemy = new PlayerSetupData { Nickname = "DefenceEnemy", ColorIndex = 2 };
            var hex = new HexCoord(4, 0);
            var body = new WorthIt.DefenderProfile(3f, false, null, 4f, 8f, 2);
            var contact = new EnemyContactSnapshot
            {
                Army = new ArmySnapshot
                {
                    ArmyId = 42,
                    Owner = enemy,
                    EffectiveArmyPower = 7f,
                    Members = new[] { body },
                    MemberCount = 1,
                },
                Source = ContactSource.Honest,
                Knowledge = ContactKnowledge.Exact,
                Position = hex,
                LastObservedTurn = 5,
                Confidence = 1f,
            };
            return new WorldSnapshot
            {
                TurnNumber = 5,
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new[] { new HexCoord(0, 0) },
                    BestStackPotential = bestStack,
                    TotalMilitaryPotential = totalPotential,
                },
                Known = new KnownSnapshot
                {
                    EnemySightings = new[]
                    {
                        new AiMapMemory.KnownEnemySighting(hex, enemy, "enemy", 1,
                            3f, 4f, new List<WorthIt.DefenderProfile> { body },
                            hasAntiAir: false, recceRadius: 0, recceSpotStrength: 0,
                            seenTurn: 5, armyId: 42),
                    },
                },
                Threat = new ThreatModel
                {
                    Threats = new[]
                    {
                        new AssetThreatSnapshot
                        {
                            Contact = contact,
                            Asset = new StrategicAssetSnapshot
                            {
                                Kind = AssetKind.Base,
                                Hex = new HexCoord(0, 0),
                                Value = 10f,
                            },
                            EnemyEta = 1,
                            PotentialDamage = 0.5f,
                            Confidence = 1f,
                            Severity = 0.8f,
                        },
                    },
                },
            };
        }
    }
}
#endif
