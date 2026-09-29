#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Aviation;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiV2EconomyRaidAirIntegrationTests
    {
        [TestCase(10, 0, 5)]
        [TestCase(9, 0, 4)]
        [TestCase(10, 1, 10)]
        public void FirstTurnBudget_DerivesFromLiveEndurance(
            int movement, int endurance, int expectedCap)
        {
            ArmyData wing = Wing(movement, endurance);
            Assert.That(AviationRange.FirstTurnOutboundBudget(wing.Members), Is.EqualTo(expectedCap));
        }

        [Test]
        public void MixedWingUsesMostLimitedEndurance()
        {
            ArmyData wing = Wing(10, 1);
            wing.Members.Add(Aircraft(10, 0, 1));
            Assert.That(AviationRange.SafeUnlandedEndsRemaining(wing), Is.Zero);
            Assert.That(AviationRange.FirstTurnOutboundBudget(wing.Members), Is.EqualTo(5));
        }

        [Test]
        public void MobileCollection_SelectsBestCollectorByCanonicalTaskScore()
        {
            HexCoord target = new HexCoord(3, 2);
            var standing = new EconomyResourceStanding
            {
                Type = ResourceType.Materials,
                OwnIncome = 0f,
                HandResourceNeed = 100f,
                SpendableStockpile = 0f,
                DeficitScore = 1f,
            };
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Armies = new[]
                    {
                        new ArmySnapshot
                        {
                            ArmyId = 1, Hex = new HexCoord(1, 1), MaxMovement = 4,
                            CurrentMovement = 4, ActivationApCost = 3,
                        },
                        new ArmySnapshot
                        {
                            ArmyId = 2, Hex = new HexCoord(1, 1), MaxMovement = 4,
                            CurrentMovement = 4, ActivationApCost = 1,
                        },
                    },
                },
                Economy = new EconomyStanding
                {
                    PerType = new[] { standing },
                    MobileCollectionOpportunities = new[]
                    {
                        new MobileCollectionOpportunity(target, ResourceType.Materials,
                            2, 1, 2, 1, new HexCoord(0, 0)),
                        new MobileCollectionOpportunity(target, ResourceType.Materials,
                            2, 2, 2, 1, new HexCoord(0, 0)),
                    },
                },
            };

            List<MissionProposal> proposals = EconomyMissionPlanner.Propose(snapshot,
                new DesireBreakdown(), Array.Empty<MissionIntent>(), null);

            MissionProposal mission = proposals.Single();
            var payload = (EconomyMissionTarget)mission.Target;
            Assert.That(payload.CollectorArmyId, Is.EqualTo(2),
                "the task owner must compare candidate collectors on the canonical TaskScore");
        }

        [Test]
        public void MobileCollection_IsProposedWithoutInfrastructureDemand()
        {
            HexCoord target = new HexCoord(3, 2);
            var standing = new EconomyResourceStanding
            {
                Type = ResourceType.Materials,
                OwnIncome = 0f,
                HandResourceNeed = 100f,
                RemainingDeckResourceNeed = 0f,
                SpendableStockpile = 0f,
                DeficitScore = 1f,
            };
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Armies = new[]
                    {
                        new ArmySnapshot
                        {
                            ArmyId = 0, Hex = new HexCoord(1, 1), MaxMovement = 4,
                            CurrentMovement = 4, ActivationApCost = 1,
                        },
                    },
                },
                Economy = new EconomyStanding
                {
                    PerType = new[] { standing },
                    MobileCollectionOpportunities = new[]
                    {
                        new MobileCollectionOpportunity(target, ResourceType.Materials,
                            2, 0, 2, 1, new HexCoord(0, 0)),
                    },
                },
            };

            float usefulGain = standing.UsefulMarginalIncomeGain(2f);
            float priority = TaskScoreEvaluator.ResourcePriority(standing);
            int homeDistance = TaskScoreEvaluator.NearestOwnedHomeDistance(snapshot, target);
            var expectedScore = new TaskScore(
                economicHexBenefit: TaskScoreEvaluator.EconomicHexBenefit(usefulGain, priority),
                ownTerritoryProximity: UnityEngine.Mathf.Max(0f,
                    TaskScoreEvaluator.OwnTerritoryProximity(homeDistance)),
                cardPrice: TaskScoreEvaluator.Price(1f),
                delivery: TaskScoreEvaluator.Price(ActionPrice.RecurringAp(1f, 1f)));

            List<MissionProposal> proposals = EconomyMissionPlanner.Propose(snapshot,
                new DesireBreakdown(), Array.Empty<MissionIntent>(), null);

            MissionProposal mission = proposals.Single();
            var payload = (EconomyMissionTarget)mission.Target;
            Assert.That(payload.Kind, Is.EqualTo(EconomyTaskKind.MobileCollection));
            Assert.That(payload.CollectorArmyId, Is.EqualTo(0), "army id zero is valid");
            Assert.That(mission.Requirements.RequiresHero, Is.False);
            Assert.That(mission.BaseValue, Is.EqualTo(expectedScore.Value).Within(0.0001f));
            Assert.That(mission.LocalAdmissionScore, Is.EqualTo(expectedScore.Value).Within(0.0001f),
                "the proposal must be scored by the Economy task owner from raw snapshot facts");
        }

        [Test]
        public void RaidSupportEstimate_PreservesAtLeastOneDefender()
        {
            var aircraft = new List<UnitData>
            {
                Aircraft(6, 0, 100), Aircraft(6, 0, 100), Aircraft(6, 0, 100),
            };
            var defenders = new List<WorthIt.DefenderProfile>
            {
                Defender(), Defender(), Defender(),
            };

            AviationCombatEstimator.AirStrikeEstimate support =
                AviationCombatEstimator.EstimateAirStrike(aircraft, 0f, 0f, defenders,
                    AirStrikePolicy.RaidSupport(42));

            AviationCombatEstimator.AirStrikeEstimate snapshotSupport =
                AviationCombatEstimator.EstimateAirStrike(
                    aircraft.Select(x => (float)x.Attack).ToList(), 0f, 0f, defenders,
                    AirStrikePolicy.RaidSupport(42));

            Assert.That(support.WipeProbability, Is.Zero);
            Assert.That(support.ExpectedDefendersAfter.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(support.ExpectedKillCount, Is.LessThanOrEqualTo(2f));
            Assert.That(snapshotSupport.ExpectedDamage,
                Is.EqualTo(support.ExpectedDamage).Within(0.0001f),
                "planning and live provisioning must share one air-strike estimator");
            Assert.That(snapshotSupport.ExpectedKillCount,
                Is.EqualTo(support.ExpectedKillCount).Within(0.0001f));
        }

        [Test]
        public void CollectorReturnKey_UsesExactActorIncludingZero()
        {
            var proposal = new MissionProposal
            {
                Kind = MissionKind.Economy,
                Target = new EconomyMissionTarget
                {
                    Kind = EconomyTaskKind.ReturnCollector,
                    CollectorArmyId = 0,
                    TargetHex = new HexCoord(0, 0),
                },
            };

            StableMissionKey key = StableMissionKey.For(proposal);
            MissionIntentKey intentKey = MissionIntentKey.For(proposal);

            Assert.That(key.TargetId, Is.Zero);
            Assert.That(intentKey.ObjectiveId, Is.Zero);
            Assert.That(key.SubKind, Is.EqualTo((int)EconomyTaskKind.ReturnCollector));
        }

        private static ArmyData Wing(int movement, int endurance)
        {
            var wing = new ArmyData { IsAirArmy = true };
            wing.Members.Add(Aircraft(movement, endurance, 4));
            return wing;
        }

        private static UnitData Aircraft(int movement, int endurance, int attack) => new UnitData
        {
            IsAviation = true,
            MoveMax = movement,
            MoveCurrent = movement,
            TurnsWithoutRefuel = endurance,
            Attack = attack,
            HitPointsMax = 3,
            HitPointsCurrent = 3,
        };

        private static WorthIt.DefenderProfile Defender() =>
            new WorthIt.DefenderProfile(defense: 0f, hasCeramicArmor: false,
                attack: 1f, hitPoints: 1f, maxHitPoints: 1f);
    }
}
#endif
