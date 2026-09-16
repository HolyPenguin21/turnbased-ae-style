#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.HexGrid;
using Game.Players;
using NUnit.Framework;

namespace Game.EditorTests
{
    public class AiReconIncumbentCostTests
    {
        [TearDown]
        public void ClearState() => MissionIntentRegistry.Clear();

        [Test]
        public void ContinuingScout_PricesOwnedActor_NotCheapestUnrelatedScout()
        {
            var player = new PlayerSetupData { Nickname = "Recon cost regression" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, focus);
            MissionIntent incumbent = Incumbent(focus, 10);

            MissionProposal continuing = ReconMissionPlanner.Propose(snap,
                new DesireBreakdown { ReconExplorePressure = 1f },
                new[] { incumbent }, new List<ReconObjective>()).Single();

            Assert.That(continuing.FromDurableIntent, Is.True);
            Assert.That(continuing.PreferredMoverArmyId, Is.EqualTo(10));
            Assert.That(continuing.Requirements.MoverKnown, Is.True);
            Assert.That(continuing.Requirements.ApMinimum, Is.EqualTo(4f));
            Assert.That(continuing.Requirements.ApDesired, Is.EqualTo(4f));
            Assert.That(continuing.Requirements.ApMaximum, Is.EqualTo(4f));
            Assert.That(continuing.Requirements.EstimatedDistance, Is.EqualTo(1f));
        }

        [Test]
        public void NewScout_RemainsUnboundAndCanPriceCheaperActor()
        {
            var player = new PlayerSetupData { Nickname = "Recon cost regression" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, focus);
            var objective = new ReconObjective
            {
                Kind = ReconObjectiveKind.Explore,
                FocusHex = focus,
                FreshNeighbors = 6,
                TaskScore = new TaskScore(infoGain: 30f),
                BaseValue = 30f,
            };

            MissionProposal fresh = ReconMissionPlanner.Propose(snap,
                new DesireBreakdown { ReconExplorePressure = 1f },
                new List<MissionIntent>(), new[] { objective }).Single();

            Assert.That(fresh.FromDurableIntent, Is.False);
            Assert.That(fresh.PreferredMoverArmyId, Is.Null,
                "a planning estimate must not reserve an army before canonical assignment");
            Assert.That(fresh.Requirements.MoverKnown, Is.True);
            Assert.That(fresh.Requirements.ApDesired, Is.EqualTo(1f));
        }

        [Test]
        public void SpentIncumbent_UsesEligibleFallbackEstimateWithoutHardReservation()
        {
            var player = new PlayerSetupData { Nickname = "Recon cost regression" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, focus);
            ((List<ArmySnapshot>)snap.Self.Armies)[0].CurrentMovement = 0;

            MissionProposal continuing = ReconMissionPlanner.Propose(snap,
                new DesireBreakdown { ReconExplorePressure = 1f },
                new[] { Incumbent(focus, 10) }, new List<ReconObjective>()).Single();

            Assert.That(continuing.PreferredMoverArmyId, Is.EqualTo(10));
            Assert.That(continuing.Requirements.ApDesired, Is.EqualTo(1f),
                "pricing may consider another eligible mover but must not bind it here");
        }

        [Test]
        public void ActivatedScout_MultiTurnExploreStillPaysFutureActivationsInScore()
        {
            var player = new PlayerSetupData { Nickname = "Recon cost regression" };
            HexCoord distantFocus = new HexCoord(8, 3);
            WorldSnapshot snap = Snapshot(player, distantFocus);
            var armies = (List<ArmySnapshot>)snap.Self.Armies;
            armies[0].HasActivatedThisTurn = true;
            armies[0].CurrentMovement = 1;
            armies[1].CurrentMovement = 0;

            ScoutCostEstimate cost = ScoutCostModel.Estimate(snap,
                new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = distantFocus });
            Assert.That(cost.ApDesired, Is.EqualTo(0f), "activation was already paid this turn");
            Assert.That(cost.RecurringActivationAp, Is.EqualTo(4f));
            Assert.That(cost.EtaTurns, Is.EqualTo(3));

            ReconObjective objective = ReconObjectiveEvaluator.BuildExplore(snap, distantFocus,
                freshNeighbors: 6, distFromBase: 5, enemyExposure: false,
                stealthDetectionRisk: false);
            Assert.That(objective.TaskScore.CardPrice, Is.EqualTo(0f));
            Assert.That(objective.TaskScore.Delivery,
                Is.EqualTo(8f * AiConfigV2.taskScoreCardPriceApWeight).Within(0.001f),
                "two later turns require two REAL 4-AP activations, even if this turn is free");
            Assert.That(objective.BaseValue, Is.EqualTo(objective.TaskScore.Value));
        }

        private static MissionIntent Incumbent(HexCoord focus, int armyId)
        {
            var intent = new MissionIntent
            {
                Kind = MissionKind.Scout,
                Funding = CommitmentTier.None,
                Status = IntentStatus.Active,
                Objective = new ScoutIntent { Kind = ScoutTargetKind.Explore, FocusHex = focus },
                PreferredMoverArmyId = armyId,
            };
            intent.IntentKey = MissionIntentKey.For(intent);
            return intent;
        }

        private static WorldSnapshot Snapshot(PlayerSetupData player, HexCoord focus)
        {
            var hexes = new HashSet<HexCoord>(HexGridMath.Neighbors(focus))
            {
                focus, new HexCoord(0, 0),
            };
            return new WorldSnapshot
            {
                TurnNumber = 11,
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new List<HexCoord> { new HexCoord(0, 0) },
                    Armies = new List<ArmySnapshot>
                    {
                        new ArmySnapshot
                        {
                            ArmyId = 10, Owner = player, Hex = new HexCoord(3, 3),
                            IsSoloRecce = true, MemberCount = 1, CurrentMovement = 3,
                            MaxMovement = 3, ActivationApCost = 4,
                        },
                        new ArmySnapshot
                        {
                            ArmyId = 20, Owner = player, Hex = new HexCoord(3, 2),
                            IsSoloRecce = true, MemberCount = 1, CurrentMovement = 3,
                            MaxMovement = 3, ActivationApCost = 1,
                        },
                    },
                },
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    AllHexes = hexes.ToList(),
                    VisitedHexSet = new HashSet<HexCoord>(),
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                },
            };
        }
    }
}
#endif
