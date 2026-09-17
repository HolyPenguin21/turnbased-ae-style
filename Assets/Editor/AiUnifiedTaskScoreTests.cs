#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Combat;
using Game.Economy;
using Game.HexGrid;
using Game.Players;
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
                hexThreatRisk: 2f);
            Assert.That(score.Value, Is.EqualTo(12f).Within(0.0001f));
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
            const float ap = 2f, resources = 3f, perTurnAp = 1f, etaTurns = 3f;
            var extraction = new TaskScore(
                cardPrice: TaskScoreEvaluator.CardPrice(ap, resources),
                delivery: TaskScoreEvaluator.DeliveryFromEta(perTurnAp, etaTurns,
                    AiConfigV2.taskScoreCardPriceApWeight));
            var foundation = new TaskScore(
                cardPrice: TaskScoreEvaluator.CardPrice(ap, resources),
                delivery: TaskScoreEvaluator.DeliveryFromEta(perTurnAp, etaTurns,
                    AiConfigV2.taskScoreCardPriceApWeight));
            // One extra turn beyond the first prices identically to that same AP spent on a card —
            // both are the SAME real AP, at the SAME shared rate, just paid on a different turn.
            Assert.That(TaskScoreEvaluator.CardPrice(1f, 0f),
                Is.EqualTo(TaskScoreEvaluator.DeliveryFromEta(1f, 2f,
                    AiConfigV2.taskScoreCardPriceApWeight)),
                "a real AP must have the same intrinsic cost when spent on a card or on one extra turn of delivery");
            Assert.That(extraction.CardPrice, Is.EqualTo(foundation.CardPrice));
            Assert.That(extraction.Delivery, Is.EqualTo(foundation.Delivery));
            Assert.That(extraction.Value, Is.EqualTo(foundation.Value));
        }

        [Test]
        public void SameReactivationAp_PricesIdenticallyAcrossEconomyRaidAndRecon()
        {
            // Task 8 replacement for the formal-symmetry test above: prove the SAME real fact
            // (one future re-activation of the SAME AP cost) is priced identically by whichever
            // family folds it through the shared taskScoreReactivationApWeight rate, instead of
            // merely comparing two calls with the same hand-picked weight argument.
            const float perTurnAp = 4f, etaTurns = 2f;
            float economyDelivery = TaskScoreEvaluator.DeliveryFromEta(perTurnAp, etaTurns,
                AiConfigV2.taskScoreReactivationApWeight);
            float raidDelivery = TaskScoreEvaluator.DeliveryFromEta(perTurnAp, etaTurns,
                AiConfigV2.taskScoreReactivationApWeight);
            Assert.That(economyDelivery, Is.EqualTo(raidDelivery));
            Assert.That(economyDelivery, Is.EqualTo(perTurnAp * AiConfigV2.taskScoreReactivationApWeight));
        }

        [Test]
        public void EconomicDeficitBonus_MatchesLoweredCanonicalCap()
        {
            // Task 1 acceptance matrix: Extraction +1 resource, no deficit -> 5 (unchanged).
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(1f, 0f), Is.EqualTo(5f).Within(0.0001f));
            // Extraction +1 resource, maximum deficit -> 8 (5 physical + 3 deficit, was 17).
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(1f, 1f), Is.EqualTo(8f).Within(0.0001f));
            Assert.That(AiConfigV2.taskScoreEconomicDeficitBonusMax, Is.EqualTo(3f).Within(0.0001f));
            // Zero gain, maximum deficit -> 0 (deficit never creates value without marginal income).
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(0f, 1f), Is.EqualTo(0f));
            // Several deficit resources at once still never exceed the single shared 3-point cap.
            var multiResource = new List<(float Gain, float Priority)>
            {
                (1f, 1f), (2f, 1f), (0.5f, 1f),
            };
            float multi = TaskScoreEvaluator.EconomicHexBenefit(multiResource);
            float multiPhysical = TaskScoreEvaluator.EconomicHexBenefit(3.5f, 0f);
            Assert.That(multi - multiPhysical, Is.LessThanOrEqualTo(3f + 0.0001f));
        }

        [Test]
        public void ScoutEstimate_UsesCitadelWithoutBases_AndPricesSurveillanceTravel()
        {
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new List<HexCoord>(),
                    Armies = new List<ArmySnapshot> { new ArmySnapshot { MaxMovement = 3 } },
                },
            };
            var explore = new ScoutMissionTarget
            {
                Kind = ScoutTargetKind.Explore,
                FocusHex = new HexCoord(6, 0),
                Stealth = StealthRequirement.None,
            };
            ScoutCostEstimate exploreCost = ScoutCostModel.Estimate(snapshot, explore);
            Assert.That(exploreCost.EstimatedDistance, Is.EqualTo(6f));
            Assert.That(exploreCost.EtaTurns, Is.EqualTo(2));
            Assert.That(exploreCost.ApDesired, Is.EqualTo(1f));
            // A distant Base must not override a much closer existing Citadel.
            snapshot.Self.BaseHexes = new List<HexCoord> { new HexCoord(20, 0) };
            ScoutCostEstimate fromNearestHome = ScoutCostModel.Estimate(snapshot, explore);
            Assert.That(fromNearestHome.EstimatedDistance, Is.EqualTo(6f));

            var surveillance = new ScoutMissionTarget
            {
                Kind = ScoutTargetKind.Surveil,
                FocusHex = explore.FocusHex,
                Stealth = StealthRequirement.Required,
            };
            ScoutCostEstimate surveillanceCost = ScoutCostModel.Estimate(snapshot, surveillance);
            Assert.That(surveillanceCost.EstimatedDistance, Is.EqualTo(6f));
            Assert.That(surveillanceCost.EtaTurns, Is.EqualTo(2));
            Assert.That(surveillanceCost.ApDesired, Is.EqualTo(2f));
        }

        [Test]
        public void ReconExplore_FoldsTheSameNotionalPhysicalPriceAsRaid()
        {
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new List<HexCoord>(),
                    Armies = new List<ArmySnapshot> { new ArmySnapshot { MaxMovement = 3 } },
                },
            };
            HexCoord hex = new HexCoord(6, 0);
            ReconObjective objective = ReconObjectiveEvaluator.BuildExplore(snapshot, hex,
                freshNeighbors: 4, distFromBase: 6,
                enemyExposure: false, stealthDetectionRisk: false);
            ScoutCostEstimate estimate = ScoutCostModel.Estimate(snapshot, objective.ToTarget());
            Assert.That(objective.TaskScore.CardPrice,
                Is.EqualTo(TaskScoreEvaluator.CardPrice(estimate.ApDesired, 0f)));
            Assert.That(objective.TaskScore.Delivery,
                Is.EqualTo(TaskScoreEvaluator.DeliveryFromEta(estimate.ApDesired, estimate.EtaTurns,
                    AiConfigV2.taskScoreCardPriceApWeight)));
            Assert.That(objective.BaseValue, Is.EqualTo(objective.TaskScore.Value));
            // info=10, home proximity=3, activation=2, one extra turn of delivery=2.
            Assert.That(objective.BaseValue, Is.EqualTo(9f).Within(0.0001f));
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

        [Test]
        public void BaseDeficit_BelongsToTheResourceThatIsActuallyProduced()
        {
            var tinyEnergy = new List<(float Gain, float Priority)>
            {
                (0.1f, 1f), (1f, 0f),
            };
            var fullEnergy = new List<(float Gain, float Priority)>
            {
                (1f, 1f), (1f, 0f),
            };
            float tiny = TaskScoreEvaluator.EconomicHexBenefit(tinyEnergy);
            float full = TaskScoreEvaluator.EconomicHexBenefit(fullEnergy);
            float tinyPhysical = TaskScoreEvaluator.EconomicHexBenefit(1.1f, 0f);
            float fullPhysical = TaskScoreEvaluator.EconomicHexBenefit(2f, 0f);
            Assert.That(tiny - tinyPhysical, Is.LessThan(
                AiConfigV2.taskScoreEconomicDeficitBonusMax));
            Assert.That(full - fullPhysical, Is.EqualTo(
                AiConfigV2.taskScoreEconomicDeficitBonusMax).Within(0.0001f));
            Assert.That(full, Is.GreaterThan(tiny));
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(
                new List<(float Gain, float Priority)> { (0f, 1f), (1f, 0f) }),
                Is.EqualTo(TaskScoreEvaluator.EconomicHexBenefit(1f, 0f)));
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(
                new List<(float Gain, float Priority)> { (1f, 1f), (1f, 1f) })
                - TaskScoreEvaluator.EconomicHexBenefit(2f, 0f),
                Is.LessThanOrEqualTo(AiConfigV2.taskScoreEconomicDeficitBonusMax + 0.0001f));
        }

        [Test]
        public void BaseStaging_UsefulButNetNegativeCanWait_EmptyProximityCannot()
        {
            var useful = new TaskScore(economicHexBenefit: 2f,
                ownTerritoryProximity: 5f, cardPrice: 20f);
            Assert.That(useful.Value, Is.LessThan(0f));
            Assert.That(DemandLayer.HasMeaningfulBaseBenefit(useful), Is.True);
            var empty = new TaskScore(ownTerritoryProximity: 5f, cardPrice: 20f);
            Assert.That(DemandLayer.HasMeaningfulBaseBenefit(empty), Is.False);
            Assert.That(DemandLayer.HasMeaningfulBaseBenefit(new TaskScore()), Is.False);
        }

        [Test]
        public void EconomyContinuation_StoresFullScoreAndNeverSubstitutesSiteMerit()
        {
            var owner = new PlayerSetupData { Nickname = "EconomyScoreOwner" };
            var hex = new HexCoord(5, 0);
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicInfrastructure,
                TargetHex = hex, EconomyResourceType = ResourceType.Materials,
                Value = 4f, EconomySiteValue = 17f,
            };
            MissionIntent intent = MissionContinuityLayer.BeginEconomyDelivery(
                owner, demand, 9, 2);
            Assert.That(intent.Economy.IntrinsicValue, Is.EqualTo(4f));
            Assert.That(intent.Economy.BuildValue, Is.EqualTo(17f));
            MissionProposal resumed = EconomyMissionPlanner.Propose(null, null,
                new[] { intent }, Array.Empty<AxisDemand>()).Single();
            Assert.That(resumed.BaseValue, Is.EqualTo(4f));
            Assert.That(((EconomyMissionTarget)resumed.Target).BuildValue, Is.EqualTo(17f));
        }

        [Test]
        public void EconomyIncumbent_RejectsOtherBuildersScoreAndRequirements()
        {
            var hex = new HexCoord(6, 0);
            var pinned = new ArmySnapshot
            {
                ArmyId = 9, Hex = new HexCoord(0, 0), HasHero = true,
                IsMobileEconomyBuilder = true, MemberCount = 1,
                MaxMovement = 3, CurrentMovement = 1, ActivationApCost = 5,
            };
            var cheaper = new ArmySnapshot
            {
                ArmyId = 10, Hex = hex, HasHero = true,
                IsMobileEconomyBuilder = true, MemberCount = 1,
                MaxMovement = 3, CurrentMovement = 3, ActivationApCost = 1,
            };
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Armies = new List<ArmySnapshot> { pinned, cheaper },
                },
            };
            var intent = new MissionIntent
            {
                Kind = MissionKind.Economy, Status = IntentStatus.Active,
                Funding = CommitmentTier.Hard,
                Objective = new EconomyIntent
                {
                    Kind = EconomyTaskKind.BuildExtraction,
                    TargetHex = hex, ResourceType = ResourceType.Materials,
                    BuilderArmyId = 9, BuildApCost = 1f, MinimumFollowupAp = 1f,
                    BuildValue = 18f, IntrinsicValue = 7f,
                },
                PreferredMoverArmyId = 9,
            };
            var wrongBuilder = new AxisDemand
            {
                RequestingAxis = DesireAxis.Economy,
                Capability = CapabilityKind.EconomicInfrastructure,
                TargetHex = hex, EconomyResourceType = ResourceType.Materials,
                EconomyPreferredBuilderArmyId = 10, EconomySiteValue = 90f,
                EconomyTravelCost = 0f, Value = 90f,
            };
            MissionProposal resumed = EconomyMissionPlanner.Propose(snapshot, null,
                new[] { intent }, new[] { wrongBuilder }).Single();
            Assert.That(resumed.PreferredMoverArmyId, Is.EqualTo(9));
            Assert.That(((EconomyMissionTarget)resumed.Target).BuilderArmyId, Is.EqualTo(9));
            Assert.That(resumed.BaseValue, Is.EqualTo(7f));
            Assert.That(resumed.Requirements.EstimatedDistance,
                Is.EqualTo(HexGridMath.Distance(pinned.Hex, hex)));
            Assert.That(resumed.Requirements.ApDesired, Is.EqualTo(5f));

            wrongBuilder.EconomyPreferredBuilderArmyId = 9;
            wrongBuilder.Value = 6f;
            wrongBuilder.EconomyTravelCost = 7f;
            MissionProposal correctRefresh = EconomyMissionPlanner.Propose(snapshot, null,
                new[] { intent }, new[] { wrongBuilder }).Single();
            Assert.That(correctRefresh.BaseValue, Is.EqualTo(6f));
            Assert.That(correctRefresh.Requirements.EstimatedDistance, Is.EqualTo(7));
            Assert.That(correctRefresh.PreferredMoverArmyId, Is.EqualTo(9));
        }

        [Test]
        public void RaidIncumbent_PricesPinnedPrimary_NotTheCheaperFreeArmy()
        {
            // Army #0 is a valid identity. A nearby already-activated army must not donate its
            // zero activation cost and short route to the distant, more expensive durable actor.
            var own = new PlayerSetupData { Nickname = "RaidScoreOwn" };
            var neutral = new PlayerSetupData { IsNeutral = true, Nickname = "RaidScoreNeutral" };
            var strong = new WorthIt.DefenderProfile(defense: 2f, hasCeramicArmor: false,
                attack: 20f, hitPoints: 20f, maxHitPoints: 20f);
            var weak = new WorthIt.DefenderProfile(defense: 1f, hasCeramicArmor: false,
                attack: 1f, hitPoints: 5f, maxHitPoints: 5f);
            var pinned = new ArmySnapshot
            {
                ArmyId = 0, Owner = own, Hex = new HexCoord(0, 0),
                IsStructuralRaidActor = true, MemberCount = 2,
                Members = new List<WorthIt.DefenderProfile> { strong, strong },
                MaxMovement = 4, CurrentMovement = 4, ActivationApCost = 3,
            };
            var cheaper = new ArmySnapshot
            {
                ArmyId = 7, Owner = own, Hex = new HexCoord(8, 0),
                IsStructuralRaidActor = true, MemberCount = 2,
                Members = new List<WorthIt.DefenderProfile> { strong, strong },
                MaxMovement = 4, CurrentMovement = 4, ActivationApCost = 1,
                HasActivatedThisTurn = true,
            };
            HexCoord destination = new HexCoord(9, 0);
            var target = RaidTargetRef.ForNeutralArmy(42);
            var snap = new WorldSnapshot
            {
                TurnNumber = 1,
                Self = new SelfSnapshot
                {
                    Armies = new List<ArmySnapshot> { pinned, cheaper },
                    BaseHexes = new List<HexCoord> { new HexCoord(0, 0) },
                    FieldPower = 100f,
                },
                Known = new KnownSnapshot
                {
                    NeutralSightings = new List<Game.Ai.AiMapMemory.KnownEnemySighting>
                    {
                        new Game.Ai.AiMapMemory.KnownEnemySighting(destination, neutral,
                            "Weak target", 1, weak.Defense, weak.Attack,
                            new List<WorthIt.DefenderProfile> { weak }, armyId: 42),
                    },
                },
            };
            var opportunity = new CombatOpportunity(true, destination, target, neutral, true,
                1, 0.9f, 0.9f, true, 0.1f, 1, 8f, 1f, true, 0.9f);
            var report = new CombatOpportunityReport
            {
                All = new[] { opportunity },
                NeutralOpportunities = new[] { opportunity },
            };
            var breakdown = new DesireBreakdown
            {
                OpportunityReport = report,
                AggRaidOpportunity = 1f,
            };
            AggressionObjective objective = AggressionObjectiveEvaluator.ForTrackedTarget(
                snap, report, target);
            Assert.That(objective, Is.Not.Null);
            var intent = new MissionIntent
            {
                Kind = MissionKind.Raid,
                Objective = new RaidIntent
                {
                    Target = target, TargetIsNeutral = true,
                    Phase = RaidMissionPhase.Assault, PrimaryArmyId = 0,
                    OperationStarted = true,
                },
                PreferredMoverArmyId = 0,
                Funding = CommitmentTier.Hard,
                Status = IntentStatus.Active,
            };
            intent.IntentKey = MissionIntentKey.For(intent);

            var proposals = AggressionMissionLayer.Propose(snap, breakdown,
                new[] { intent }, new[] { objective });
            Assert.That(proposals, Has.Count.EqualTo(1));
            MissionProposal result = proposals[0];
            Assert.That(result.PreferredMoverArmyId, Is.EqualTo(0));
            Assert.That(result.Requirements.MoverKnown, Is.True);
            Assert.That(result.Requirements.ApDesired, Is.EqualTo(3f));
            int distance = HexGridMath.Distance(pinned.Hex, destination);
            Assert.That(result.Requirements.EstimatedDistance, Is.EqualTo(distance));
            var resolvedTarget = (RaidMissionTarget)result.Target;
            var expected = new TaskScore(
                staleness: objective.TaskScore.Staleness,
                militaryTargetRelevance: objective.TaskScore.MilitaryTargetRelevance,
                winChance: TaskScoreEvaluator.WinChance(resolvedTarget.ReadyWinChance),
                cardPrice: pinned.ActivationApCost * AiConfigV2.taskScoreReactivationApWeight,
                delivery: TaskScoreEvaluator.DeliveryFromEta(pinned.ActivationApCost,
                    result.Requirements.EtaTurns, AiConfigV2.taskScoreReactivationApWeight));
            Assert.That(result.BaseValue, Is.EqualTo(expected.Value).Within(0.0001f));
            Assert.That(result.LocalAdmissionScore, Is.EqualTo(expected.Value).Within(0.0001f));
        }
    }
}
#endif
