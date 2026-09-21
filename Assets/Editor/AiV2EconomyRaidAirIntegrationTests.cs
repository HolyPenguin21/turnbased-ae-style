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
        public void ReconOutboundCap_IsFrozenFromLaunchBudgetAndEndurance(
            int movement, int endurance, int expectedCap)
        {
            ArmyData wing = Wing(movement, endurance);
            var state = new ReconAirSortieState();

            state.EnsureLaunchProfile(wing);
            wing.Members[0].MoveCurrent = 1;
            state.EnsureLaunchProfile(wing);

            Assert.That(state.LaunchMovementBudget, Is.EqualTo(movement));
            Assert.That(state.OutboundMovementCap, Is.EqualTo(expectedCap));
        }

        [Test]
        public void ReconOutboundCap_MixedWingUsesMostLimitedEndurance()
        {
            ArmyData wing = Wing(10, 1);
            wing.Members.Add(Aircraft(10, 0, 1));
            var state = new ReconAirSortieState();

            state.EnsureLaunchProfile(wing);

            Assert.That(state.LaunchSafeUnlandedEnds, Is.Zero);
            Assert.That(state.OutboundMovementCap, Is.EqualTo(5));
        }

        [Test]
        public void MobileCollection_IsProposedWithoutInfrastructureDemand()
        {
            HexCoord target = new HexCoord(3, 2);
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
                    MobileCollectionOpportunities = new[]
                    {
                        new MobileCollectionOpportunity(target, ResourceType.Materials,
                            2, 0, 2, 1, 0f,
                            new TaskScore(economicHexBenefit: 6f, payback: 2f,
                                cardPrice: 1.5f, delivery: 3f),
                            new HexCoord(0, 0)),
                    },
                },
            };

            List<MissionProposal> proposals = EconomyMissionPlanner.Propose(snapshot,
                new DesireBreakdown(), Array.Empty<MissionIntent>(), null);

            MissionProposal mission = proposals.Single();
            var payload = (EconomyMissionTarget)mission.Target;
            Assert.That(payload.Kind, Is.EqualTo(EconomyTaskKind.MobileCollection));
            Assert.That(payload.CollectorArmyId, Is.EqualTo(0), "army id zero is valid");
            Assert.That(mission.Requirements.RequiresHero, Is.False);
            Assert.That(mission.BaseValue, Is.EqualTo(3.5f));
            Assert.That(mission.LocalAdmissionScore, Is.EqualTo(3.5f),
                "the proposal must transport the canonical TaskScore fold unchanged");
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
                    aircraft.Select(x => x.Attack).ToList(), 0f, 0f, defenders,
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
