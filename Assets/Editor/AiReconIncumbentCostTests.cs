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
            // Task 8 correction: BuildProposal() legitimately carries the cheapest-actor pre-funding
            // WITNESS (est.PreferredMoverArmyId) for a brand-new candidate — it is not a hard
            // reservation (ReconAssignmentPlanner still owns the final one-actor/one-job bind and
            // may pick a different actor). The witness must be the SAME cheap actor Requirements
            // were priced against (army #20, ActivationApCost 1), never left dangling as null.
            Assert.That(fresh.PreferredMoverArmyId, Is.EqualTo(20),
                "a fresh candidate's pre-funding witness must name the actor its own Requirements were priced against");
            Assert.That(fresh.Requirements.MoverKnown, Is.True);
            Assert.That(fresh.Requirements.ApDesired, Is.EqualTo(1f));
        }

        [Test]
        public void SpentIncumbent_WitnessFollowsTheActorThePriceWasActuallyComputedAgainst()
        {
            // Task 5 (R1) regression: army #10 (the durable incumbent) has 0 CurrentMovement this
            // turn, so ScoutMoverSelector.Eligible() excludes it entirely (a structural "spent this
            // turn" fact) and ScoutCostModel.PlanGroundCost silently reprices against the cheapest
            // OTHER eligible actor, army #20 (ActivationApCost 1). Before the fix, BuildProposal()
            // kept PreferredMoverArmyId pinned to #10 (the witness) while BaseValue/Requirements
            // were both priced against #20 — an internally inconsistent proposal (price for one
            // actor, witness naming a different one). All three facts must now name the SAME actor.
            var player = new PlayerSetupData { Nickname = "Recon cost regression" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, focus);
            ((List<ArmySnapshot>)snap.Self.Armies)[0].CurrentMovement = 0;

            MissionProposal continuing = ReconMissionPlanner.Propose(snap,
                new DesireBreakdown { ReconExplorePressure = 1f },
                new[] { Incumbent(focus, 10) }, new List<ReconObjective>()).Single();

            // Witness must follow the actor actually priced (#20), not the structurally-ineligible
            // nominal incumbent (#10). This is a proposal-internal consistency fix only — durable
            // ownership (MissionIntent.PreferredMoverArmyId) is set from the real post-execution
            // MissionTurnOutcome.MoverArmyId in MissionContinuityLayer, never from this witness.
            Assert.That(continuing.PreferredMoverArmyId, Is.EqualTo(20),
                "witness must name the same actor Requirements/BaseValue were actually priced against");
            Assert.That(continuing.Requirements.ApDesired, Is.EqualTo(1f),
                "pricing may consider another eligible mover but must not bind it here");

            ReconObjective directEstimate = ReconObjectiveEvaluator.ExploreAt(snap, focus,
                preferredMoverArmyId: 10);
            Assert.That(continuing.BaseValue, Is.EqualTo(directEstimate.BaseValue).Within(0.0001f),
                "BaseValue must reflect the same fallback actor (#20) Requirements/witness now agree on");
        }

        [Test]
        public void SpentIncumbent_ProposalIsInternallyConsistentAndAdmissionDoesNotHideTheConflict()
        {
            // Task 5 (R1) — the comprehensive check the project owner asked for: BaseValue,
            // Requirements (ApDesired/EtaTurns) and PreferredMoverArmyId must all describe the SAME
            // real candidate, and the pre-funding LocalAdmissionScore (the beam/admission gate that
            // runs BEFORE ReconAssignmentPlanner ever resolves a live actor) must not smuggle a
            // hidden mismatch between the declared witness and the actually-cheap candidate through
            // to Allocation. Army #10 is pinned but 0-MP (structurally ineligible); army #20 is the
            // only real eligible ground actor and is what everything must key on.
            var player = new PlayerSetupData { Nickname = "Recon cost regression" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, focus);
            ((List<ArmySnapshot>)snap.Self.Armies)[0].CurrentMovement = 0;

            MissionProposal continuing = ReconMissionPlanner.Propose(snap,
                new DesireBreakdown { ReconExplorePressure = 1f },
                new[] { Incumbent(focus, 10) }, new List<ReconObjective>()).Single();

            const int actuallyPricedActor = 20;
            ReconObjective directEstimate = ReconObjectiveEvaluator.ExploreAt(snap, focus,
                preferredMoverArmyId: 10);
            ScoutCostEstimate directCost = ScoutCostModel.Estimate(snap,
                new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = focus },
                preferredMoverArmyId: 10);

            // 1) All three facts key on the same actor.
            Assert.That(continuing.PreferredMoverArmyId, Is.EqualTo(actuallyPricedActor));
            Assert.That(directCost.PreferredMoverArmyId, Is.EqualTo(actuallyPricedActor));

            // 2) BaseValue.
            Assert.That(continuing.BaseValue, Is.EqualTo(directEstimate.BaseValue).Within(0.0001f));

            // 3) Requirements (ApDesired / EtaTurns) match the actor named by the witness.
            Assert.That(continuing.Requirements.ApDesired, Is.EqualTo(directCost.ApDesired).Within(0.0001f));
            Assert.That(continuing.Requirements.EtaTurns, Is.EqualTo(directCost.EtaTurns));
            Assert.That(continuing.Requirements.ApDesired, Is.EqualTo(1f));

            // 4) Admission: LocalAdmissionScore is BaseValue-derived (ComputeLocalAdmissionScore),
            // so it must already reflect the SAME fallback-priced actor, not the nominal incumbent's
            // (unreachable) envelope. If admission and requirements disagreed here, MissionLayer's
            // beam could admit a proposal at a price ReconAssignmentPlanner can never actually honour
            // for the witnessed actor, producing an avoidable MoverContended later at Assignment.
            Assert.That(continuing.LocalAdmissionScore, Is.EqualTo(continuing.BaseValue).Within(0.0001f),
                "admission must score the same value the witness/requirements were actually priced at");
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
            // Task 5 (Problem A) fix: a future re-activation is the same real per-turn AP
            // Economy/Raid price at taskScoreReactivationApWeight, never at the higher
            // taskScoreCardPriceApWeight reserved for a genuine one-time ability spend.
            Assert.That(objective.TaskScore.Delivery,
                Is.EqualTo(8f * AiConfigV2.taskScoreReactivationApWeight).Within(0.001f),
                "two later turns require two REAL 4-AP activations, even if this turn is free");
            Assert.That(objective.BaseValue, Is.EqualTo(objective.TaskScore.Value));
        }

        [Test]
        public void ContinuingScout_BaseValuePricesTheSamePreferredActorAsRequirements()
        {
            // Task 5 (Problem B) regression: army #10 (preferred, ActivationApCost 4) is more
            // expensive than free army #20 (ActivationApCost 1). Before the fix, TryMaterializeIntent
            // priced BaseValue via ExploreAt() with NO preferred actor (so it silently used #20's
            // cheap 1-AP cost) while BuildProposal() priced Requirements against the pinned #10's
            // real 4-AP cost — two different actors backing the same TaskScore. Both must now agree.
            var player = new PlayerSetupData { Nickname = "Recon cost regression" };
            HexCoord focus = new HexCoord(4, 3);
            WorldSnapshot snap = Snapshot(player, focus);

            MissionProposal continuing = ReconMissionPlanner.Propose(snap,
                new DesireBreakdown { ReconExplorePressure = 1f },
                new[] { Incumbent(focus, 10) }, new List<ReconObjective>()).Single();

            Assert.That(continuing.PreferredMoverArmyId, Is.EqualTo(10));
            Assert.That(continuing.Requirements.ApDesired, Is.EqualTo(4f));
            // BaseValue must reflect the SAME 4-AP actor Requirements were priced against, not the
            // cheaper unrelated army #20's 1-AP envelope.
            ReconObjective directEstimate = ReconObjectiveEvaluator.ExploreAt(snap, focus,
                preferredMoverArmyId: 10);
            Assert.That(continuing.BaseValue, Is.EqualTo(directEstimate.BaseValue).Within(0.0001f));
            Assert.That(directEstimate.TaskScore.CardPrice,
                Is.EqualTo(4f * AiConfigV2.taskScoreReactivationApWeight).Within(0.0001f),
                "BaseValue's own CardPrice must be army #10's real activation fee, not army #20's");
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
