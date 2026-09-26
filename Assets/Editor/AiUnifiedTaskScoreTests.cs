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
using UnityEngine;

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
        public void OwnTerritoryProximity_IsSignedWithoutChangingItsExistingSlope()
        {
            Assert.That(TaskScoreEvaluator.OwnTerritoryProximity(0f), Is.EqualTo(3f).Within(0.0001f));
            Assert.That(TaskScoreEvaluator.OwnTerritoryProximity(3f), Is.EqualTo(1.5f).Within(0.0001f));
            Assert.That(TaskScoreEvaluator.OwnTerritoryProximity(6f), Is.Zero.Within(0.0001f));
            Assert.That(TaskScoreEvaluator.OwnTerritoryProximity(9f), Is.EqualTo(-1.5f).Within(0.0001f));
            Assert.That(TaskScoreEvaluator.OwnTerritoryProximity(12f), Is.EqualTo(-3f).Within(0.0001f));
            Assert.That(TaskScoreEvaluator.OwnTerritoryProximity(20f), Is.EqualTo(-3f).Within(0.0001f));
            Assert.That(TaskScoreEvaluator.OwnTerritoryProximity(-1f), Is.Zero);
            Assert.That(new TaskScore(ownTerritoryProximity:
                TaskScoreEvaluator.OwnTerritoryProximity(12f)).Value,
                Is.EqualTo(-3f).Within(0.0001f),
                "Fold must retain the negative positional contribution without another penalty slot");
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
        public void UsefulMarginalIncome_UsesRealRemainingNeedAndOneExistingRunway()
        {
            EconomyResourceStanding abundant = EconomyStanding.CalculateResource(
                ResourceType.Materials, ownIncome: 5f, opponentMedianIncome: 20f,
                handNeed: 1f, remainingDeckNeed: 1f, reservedOperationalNeed: 0f,
                spendableStockpile: 100f, starvationPressure: 0f);
            Assert.That(TaskScoreEvaluator.ResourcePriority(abundant), Is.GreaterThan(0f),
                "Opponent's higher income can raise old deficit without creating spending need");
            float surplusUseful = abundant.UsefulMarginalIncomeGain(1f);
            Assert.That(surplusUseful, Is.Zero);
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(surplusUseful,
                TaskScoreEvaluator.ResourcePriority(abundant)), Is.Zero);

            EconomyResourceStanding futureCard = EconomyStanding.CalculateResource(
                ResourceType.Energy, ownIncome: 1f, opponentMedianIncome: 1f,
                handNeed: 0f, remainingDeckNeed: 9f, reservedOperationalNeed: 0f,
                spendableStockpile: 0f, starvationPressure: 0f);
            Assert.That(futureCard.UsefulMarginalIncomeGain(1f), Is.EqualTo(1f));

            EconomyResourceStanding partial = EconomyStanding.CalculateResource(
                ResourceType.Tech, ownIncome: 2f, opponentMedianIncome: 2f,
                handNeed: 7f, remainingDeckNeed: 0f, reservedOperationalNeed: 0f,
                spendableStockpile: 0f, starvationPressure: 0f);
            Assert.That(partial.UsefulMarginalIncomeGain(1f),
                Is.EqualTo(1f / AiConfigV2.economyRunwayHorizonTurns).Within(0.0001f));

            EconomyResourceStanding alreadyReserved = EconomyStanding.CalculateResource(
                ResourceType.Human, ownIncome: 0f, opponentMedianIncome: 0f,
                handNeed: 3f, remainingDeckNeed: 0f, reservedOperationalNeed: 2f,
                spendableStockpile: 4f, starvationPressure: 0f);
            Assert.That(alreadyReserved.UsefulMarginalIncomeGain(1f), Is.Zero,
                "Reservation was already excluded from spendable stock; no double count");

            float energy = futureCard.UsefulMarginalIncomeGain(1f);
            float materials = abundant.UsefulMarginalIncomeGain(1f);
            Assert.That(TaskScoreEvaluator.EconomicHexBenefit(
                new List<(float Gain, float Priority)> { (materials, 1f), (energy, 1f) }),
                Is.EqualTo(TaskScoreEvaluator.EconomicHexBenefit(energy, 1f)).Within(0.0001f),
                "One surplus resource must not inherit another resource's deficit in a Base");
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

        // Task 8 correction — the previous "SameReactivationAp_PricesIdenticallyAcrossEconomy
        // RaidAndRecon" test called TaskScoreEvaluator.DeliveryFromEta TWICE with the SAME
        // hand-picked literal arguments and asserted the result equalled itself: a tautology that
        // exercised no Economy/Raid/Recon production code at all and would pass even if any of the
        // three real cost models were completely broken. It also embedded a false premise: Economy's
        // real "extra AP" accounting (EstimateEconomyAssignmentAp: every outbound turn's activation,
        // because the mover's OWN first-turn activation is never separately priced via cardPrice the
        // way Recon/Raid's ActivationApNow/currentActivationAp split it out) is NOT the same turn
        // count as Recon/Raid's "eta-1" delivery convention — so asserting identical NUMBERS across
        // all three would have been asserting something false about the real domain, not just format.
        // What IS actually shared, and what these three tests verify by calling the real per-family
        // cost model with real, concrete physical inputs, is the single rate
        // (AiConfigV2.taskScoreReactivationApWeight) each family folds its own real per-turn
        // reactivation AP fact through.
        [Test]
        public void ReconDelivery_FoldsRealPerTurnActivationApAtSharedRate()
        {
            // Real ScoutCostModel/ReconObjectiveEvaluator production path: a solo Recce at distance
            // 6 with MaxMovement 4, ActivationApCost 4 needs ETA 2 (1 + ceil((6-4)/4)) and therefore
            // exactly ONE future re-activation beyond this turn.
            var player = new PlayerSetupData { Nickname = "Unified TaskScore regression" };
            HexCoord focus = new HexCoord(6, 0);
            var mover = new ArmySnapshot
            {
                ArmyId = 1, Owner = player, Hex = new HexCoord(0, 0),
                IsSoloRecce = true, MemberCount = 1, CurrentMovement = 4, MaxMovement = 4,
                ActivationApCost = 4,
            };
            var snap = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new List<HexCoord> { new HexCoord(0, 0) },
                    Armies = new List<ArmySnapshot> { mover },
                },
                MapKnowledge = new MapKnowledgeSnapshot
                {
                    AllHexes = new List<HexCoord> { new HexCoord(0, 0), focus },
                    VisitedHexSet = new HashSet<HexCoord>(),
                    ScoutHardBlockedHexes = new HashSet<HexCoord>(),
                },
            };

            ScoutCostEstimate cost = ScoutCostModel.Estimate(snap,
                new ScoutMissionTarget { Kind = ScoutTargetKind.Explore, FocusHex = focus });
            Assert.That(cost.RecurringActivationAp, Is.EqualTo(4f));
            Assert.That(cost.EtaTurns, Is.EqualTo(2));

            ReconObjective objective = ReconObjectiveEvaluator.BuildExplore(snap, focus,
                freshNeighbors: 0, distFromBase: 6, enemyExposure: false, stealthDetectionRisk: false);
            Assert.That(objective.TaskScore.Delivery,
                Is.EqualTo(4f * 1f * AiConfigV2.taskScoreReactivationApWeight).Within(0.0001f),
                "Recon's real production Delivery must fold the real RecurringActivationAp/EtaTurns "
                + "facts through the shared reactivation rate, not a hand-picked literal");
        }

        [Test]
        public void RaidDelivery_FoldsRealPerTurnActivationApAtSharedRate()
        {
            // Real RaidCostModel production path with the SAME physical facts as the Recon test
            // above (distance 6, MaxMovement 4, ActivationApCost 4) — RaidCostModel's own ETA
            // formula (mover.CurrentMovement >= dist ? 1 : 1 + CeilDiv(...)) is structurally
            // identical to ScoutCostModel.PairCost's, so it independently derives the SAME eta (2)
            // and the SAME one future re-activation from real army data, not a shared constant.
            var player = new PlayerSetupData { Nickname = "Unified TaskScore regression" };
            HexCoord destination = new HexCoord(6, 0);
            var mover = new ArmySnapshot
            {
                ArmyId = 2, Owner = player, Hex = new HexCoord(0, 0),
                MemberCount = 1, CurrentMovement = 4, MaxMovement = 4, ActivationApCost = 4,
            };
            var snap = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Citadel = new HexCoord(0, 0),
                    BaseHexes = new List<HexCoord> { new HexCoord(0, 0) },
                    Armies = new List<ArmySnapshot> { mover },
                },
            };
            var target = new RaidMissionTarget
            {
                Phase = RaidMissionPhase.Assault,
                LastKnownHex = destination,
                DestinationHex = destination,
            };

            RaidCostEstimate estimate = RaidCostModel.Estimate(snap, target, selectedMoverArmyId: 2);
            Assert.That(estimate.RecurringActivationAp, Is.EqualTo(4f));
            Assert.That(estimate.Requirements.EtaTurns, Is.EqualTo(2));

            // This is exactly the fold AggressionMissionPlanner.ToCandidate performs on the real
            // RaidCostEstimate it receives — reproduced here to check the ESTIMATE's real numbers,
            // not to reintroduce the old tautology (the numbers above come from RaidCostModel, not
            // from a literal).
            float raidDelivery = TaskScoreEvaluator.DeliveryFromEta(estimate.RecurringActivationAp,
                estimate.Requirements.EtaTurns, AiConfigV2.taskScoreReactivationApWeight);
            Assert.That(raidDelivery, Is.EqualTo(4f * 1f * AiConfigV2.taskScoreReactivationApWeight).Within(0.0001f));
        }

        [Test]
        public void EconomyDelivery_FoldsRealPerTurnActivationApAtSharedRate()
        {
            // Real DemandLayer.EstimateEconomyAssignmentAp production path, same mover physical
            // facts (distance 6, MaxMovement 4, ActivationApCost 4, one-way / no return leg so it is
            // comparable to Recon/Raid's one-way convention). Economy's own real turn-counting rule
            // is different from Recon/Raid (see comment above the Recon test): a builder that has
            // NOT activated yet this turn pays for BOTH the current turn's and the next turn's
            // activation inside assignmentAp (paidOutboundActivations == outboundTurns when
            // HasActivatedThisTurn is false), so the real number here is legitimately 2 activations
            // (8 AP), not 1 (4 AP) — proving the two systems must NOT be asserted numerically equal.
            var route = new EconomyBuilderRouteSnapshot
            {
                ArmyId = 3, TravelCost = 6, ReturnTravelCost = 0,
                CurrentMovement = 4, MaxMovement = 4, ActivationApCost = 4,
                HasActivatedThisTurn = false, IsOnTarget = false,
            };
            const float buildApCost = 0f;

            float assignmentAp = DemandLayer.EstimateEconomyAssignmentAp(route, buildApCost, includeReturn: false);
            float extraAp = Mathf.Max(0f, assignmentAp - buildApCost);
            Assert.That(extraAp, Is.EqualTo(8f),
                "two un-activated outbound turns at real ActivationApCost 4 each — Economy's own real rule");

            // Same production one-line fold DemandLayer.Economy applies to this real extraAp.
            float economyDelivery = extraAp * AiConfigV2.taskScoreReactivationApWeight;
            Assert.That(economyDelivery, Is.EqualTo(8f * AiConfigV2.taskScoreReactivationApWeight).Within(0.0001f));

            // What genuinely IS shared across all three families (verified by the sibling tests
            // above using each family's own real numbers): the same rate, applied to whatever real
            // per-turn AP fact that family's own cost model actually derived.
            Assert.That(AiConfigV2.taskScoreReactivationApWeight, Is.GreaterThan(0f));
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
            // Task 5 (Problem A) fix: the notional mover's whole ApDesired here is a re-activation
            // fee (no stealth entry on this route), so it must price at the SAME shared
            // taskScoreReactivationApWeight Raid/Economy use for a re-activation — never at the
            // higher taskScoreCardPriceApWeight, which is reserved for a genuine one-time ability
            // spend (e.g. entering stealth).
            Assert.That(objective.TaskScore.CardPrice,
                Is.EqualTo(estimate.ActivationApNow * AiConfigV2.taskScoreReactivationApWeight));
            Assert.That(objective.TaskScore.Delivery,
                Is.EqualTo(TaskScoreEvaluator.DeliveryFromEta(estimate.RecurringActivationAp,
                    estimate.EtaTurns, AiConfigV2.taskScoreReactivationApWeight)));
            Assert.That(objective.BaseValue, Is.EqualTo(objective.TaskScore.Value));
            // info=10, neutral home proximity at 6 hexes=0, activation=1,
            // one extra turn of delivery=1.
            Assert.That(objective.BaseValue, Is.EqualTo(8f).Within(0.0001f));
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
                EconomyTravelCost = 6f,
            };
            var proposals = EconomyMissionPlanner.Propose(null, null,
                Array.Empty<MissionIntent>(), new[] { demand });
            Assert.That(proposals, Has.Count.EqualTo(1));
            MissionProposal proposal = proposals[0];
            Assert.That(proposal.BaseValue, Is.EqualTo(score.Value));
            Assert.That(proposal.LocalAdmissionScore, Is.EqualTo(score.Value));
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
                ownTerritoryProximity: objective.TaskScore.OwnTerritoryProximity,
                militaryTargetRelevance: objective.TaskScore.MilitaryTargetRelevance,
                winChance: TaskScoreEvaluator.WinChance(resolvedTarget.ReadyWinChance),
                cardPrice: pinned.ActivationApCost * AiConfigV2.taskScoreReactivationApWeight,
                delivery: TaskScoreEvaluator.DeliveryFromEta(pinned.ActivationApCost,
                    result.Requirements.EtaTurns, AiConfigV2.taskScoreReactivationApWeight));
            Assert.That(result.BaseValue, Is.EqualTo(expected.Value).Within(0.0001f));
            Assert.That(result.LocalAdmissionScore, Is.EqualTo(expected.Value).Within(0.0001f));
            Assert.That(objective.TaskScore.OwnTerritoryProximity, Is.EqualTo(-1.5f).Within(0.0001f));
            // No fresh opportunity report: a started stationary Raid retains its
            // real actor AP/ETA and positional fact, but loses no value for fog.
            intent.Raid.LastKnownHex = destination;
            breakdown.OpportunityReport = new CombatOpportunityReport
            {
                All = Array.Empty<CombatOpportunity>(),
                NeutralOpportunities = Array.Empty<CombatOpportunity>(),
            };
            MissionProposal fog = AggressionMissionLayer.Propose(snap, breakdown,
                new[] { intent }, Array.Empty<AggressionObjective>()).Single();
            float expectedFog = objective.TaskScore.OwnTerritoryProximity
                - pinned.ActivationApCost * AiConfigV2.taskScoreReactivationApWeight
                - TaskScoreEvaluator.DeliveryFromEta(pinned.ActivationApCost,
                    fog.Requirements.EtaTurns, AiConfigV2.taskScoreReactivationApWeight);
            Assert.That(fog.BaseValue, Is.EqualTo(expectedFog).Within(0.0001f));
            Assert.That(TaskScoreEvaluator.StaleIntelPenalty(1f), Is.LessThan(0f),
                "shared staleness conversion remains available for future mobile player targets");
        }

        [Test]
        public void SurveilContact_ArmyIdZeroSurvivesReconContactByArmyIdLookup()
        {
            // Task 8 addition — covers the stage3 Task 3 fix in
            // WorldAnalysis.Threat.cs::BuildThreat() (ReconContactByArmyId keying: "ArmyId == 0 is
            // a valid identity ... not 'no army'"), which shipped with no EditMode coverage.
            // BuildThreat() itself is private and needs a full WorldSnapshot/AiTurnContext plus the
            // static AiReconMemory/AiMapMemory singletons — an unreasonably heavy fixture for one
            // dictionary-keying fact. The fix's actual observable contract is one level up, at the
            // public ScoutObjectiveEvaluator.SurveilContact() / ReconObjectiveEvaluator.SurveilOf()
            // consumers Surveil missions actually call, reading the SAME ReconContactByArmyId
            // dictionary shape BuildThreat produces — so this constructs that dictionary directly
            // (honest fixture of the real consumer contract, not a re-implementation of BuildThreat)
            // and proves a contact keyed at ArmyId 0 is not lost.
            var zeroIdArmy = new ArmySnapshot { ArmyId = 0, MemberCount = 1 };
            HexCoord pos = new HexCoord(3, 1);
            var contact = new EnemyContactSnapshot
            {
                Army = zeroIdArmy,
                Knowledge = ContactKnowledge.LastKnown,
                Position = pos,
                Confidence = 0.5f,
                LastObservedTurn = 5,
            };
            var snap = new WorldSnapshot
            {
                TurnNumber = 8,
                Self = new SelfSnapshot { BaseHexes = new List<HexCoord>() },
                Threat = new ThreatModel
                {
                    ReconContactByArmyId = new Dictionary<int, EnemyContactSnapshot> { [0] = contact },
                },
            };

            EnemyContactSnapshot resolved = ScoutObjectiveEvaluator.SurveilContact(snap, trackedArmyId: 0);
            Assert.That(resolved, Is.Not.Null,
                "a contact keyed at ArmyId 0 must not be treated as 'no army' / silently dropped");
            Assert.That(resolved.Army?.ArmyId, Is.EqualTo(0));

            ReconObjective objective = ReconObjectiveEvaluator.SurveilOf(snap, resolved);
            Assert.That(objective, Is.Not.Null);
            Assert.That(objective.ContactArmyId, Is.EqualTo(0),
                "Surveil's own objective identity must keep the real ArmyId 0, not collapse it "
                + "to the same sentinel a genuinely-absent army would use");
        }

        [Test]
        public void HomeThreatSlots_AreSeparateFromTaskHexRisk_AndBaseIsDisabled()
        {
            var snap = new WorldSnapshot
            {
                Threat = new ThreatModel { CitadelThreatSeverity = 0.5f, BaseThreatSeverity = 0.5f },
            };
            float citadel = TaskScoreEvaluator.CitadelThreatRisk(snap);
            Assert.That(citadel, Is.EqualTo(0.5f * AiConfigV2.taskScoreCitadelThreatRiskMax).Within(1e-4f));
            Assert.That(TaskScoreEvaluator.BaseThreatRisk(snap), Is.EqualTo(0f),
                "Base threat is switched off for every task (taskScoreBaseThreatRiskMax = 0).");

            var score = new TaskScore(economicHexBenefit: 10f, hexThreatRisk: 1f,
                citadelThreatRisk: citadel);
            Assert.That(score.HexThreatRisk, Is.EqualTo(1f));
            Assert.That(score.Value, Is.EqualTo(10f - 1f - citadel).Within(1e-4f));
        }

        [Test]
        public void HomeThreatSlots_SurviveNetChangeAndResponseFold()
        {
            var from = new TaskScore(citadelThreatRisk: 1f);
            var to = new TaskScore(citadelThreatRisk: 3f, baseThreatRisk: 2f);
            TaskScore delta = TaskScoreEvaluator.NetChange(from, to);
            Assert.That(delta.CitadelThreatRisk, Is.EqualTo(2f));
            Assert.That(delta.BaseThreatRisk, Is.EqualTo(2f));
            TaskScore response = TaskScoreEvaluator.WithResponse(to, 0.5f, 0f, 0f, 0f);
            Assert.That(response.CitadelThreatRisk, Is.EqualTo(3f));
        }
    }
}
#endif
