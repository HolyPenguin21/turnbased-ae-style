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
        public void AirSweep_UnknownCorridorPaysInfoGainButNotRefresh()
        {
            var snap = new WorldSnapshot {
                Self = new SelfSnapshot { Citadel = new HexCoord(0, 0) },
                Known = new KnownSnapshot(),
                TrueWorld = new TrueWorldSnapshot { EnemyArmies = new[] {
                    new ArmySnapshot { Hex = new HexCoord(6, 0), EffectiveArmyPower = 20f },
                } },
            };
            ReconObjective sweep = ReconObjectiveEvaluator.AirSweepOf(snap);
            Assert.That(sweep, Is.Not.Null);
            Assert.That(sweep.TaskScore.InfoGain, Is.GreaterThan(0f));
            Assert.That(sweep.TaskScore.Staleness, Is.Zero);
        }

        [Test]
        public void BaseCrowding_IsIntrinsicFiniteCostAndCanBeOutweighed()
        {
            float crowd = TaskScoreEvaluator.BaseCrowdingCost(1);
            var adjacent = new TaskScore(economicHexBenefit: 15f, cardPrice: 2f,
                ownTerritoryProximity: TaskScoreEvaluator.OwnTerritoryProximity(1), baseCrowdingCost: crowd);
            Assert.That(adjacent.Value, Is.GreaterThan(0), "a sufficiently good adjacent site still wins admission");
            Assert.That(crowd, Is.GreaterThan(adjacent.OwnTerritoryProximity));
            Assert.That(TaskScoreEvaluator.GroupOf(TaskSlot.BaseCrowdingCost), Is.EqualTo(TaskSlotGroup.Intrinsic));
            Assert.That(TaskScoreEvaluator.WithExecution(adjacent, new TaskScore(cardPrice: 3f)).BaseCrowdingCost,
                Is.EqualTo(crowd), "actor repricing cannot erase the placement fact");
            Assert.That(TaskScoreEvaluator.BaseCrowdingCost(3), Is.Zero);
        }
        // The slot table is the only list of slots: every enum slot must round-trip through the
        // slot-wise constructor and fold with exactly its table sign. A slot added to the enum
        // but missed in the indexer / FromSlots fails here instead of vanishing from scores.
        [Test]
        public void SlotTable_EverySlotRoundTripsAndFoldsWithItsSign()
        {
            foreach (TaskSlot slot in Enum.GetValues(typeof(TaskSlot)))
            {
                TaskScore only = TaskScore.FromSlots(s => s == slot ? 1f : 0f);
                Assert.That(only[slot], Is.EqualTo(1f), slot.ToString());
                Assert.That(only.Value, Is.EqualTo(TaskScoreEvaluator.Sign(slot)), slot.ToString());
            }
        }

        [Test]
        public void CalibrationReport_ScoresEveryFamilyThroughTheRealConverters()
        {
            string json = Game.EditorTools.TaskScoreCalibrationReport.Build();
            foreach (string name in new[] { "Extraction", "Base", "Raid near", "Raid far",
                         "Attack enemy Base", "ActiveDefence intercept", "Recon Explore",
                         "Recon Refresh", "Mobile collection", "Development operator walk" })
                Assert.That(json, Does.Contain("\"name\":\"" + name + "\""), name);
            // Raid near: reward 8 + win 0.8 x 12 + signed proximity at 3 hexes 1 - activation 3 - one turn 3.
            Assert.That(json, Does.Contain("\"name\":\"Raid near\",\"family\":\"Military\""));
            Assert.That(json, Does.Contain("\"value\":12.6,\"benefit\":18.6,\"cost\":6"));
            Assert.That(json.Split('{').Length, Is.EqualTo(json.Split('}').Length),
                "balanced JSON objects");
        }

        [Test]
        public void Describe_PrintsOneLineInTheFourCategories()
        {
            var raid = new TaskScore(raidReward: 8f, winChance: 9.6f, ownTerritoryProximity: 1.5f,
                cardPrice: 3f, delivery: 3f);
            string line = TaskScoreEvaluator.Describe(raid);
            Assert.That(line, Does.StartWith("value=13.1 | benefit 19.1 ("));
            Assert.That(line, Does.Contain("RaidReward 8.0"));
            Assert.That(line, Does.Contain("| cost 6.0 (CardPrice 3.0, Delivery 3.0)"));
            Assert.That(line, Does.Contain("| risk 0.0 | opportunity 0.0"));
            Assert.That(TaskScoreEvaluator.CategoryTotal(raid, TaskSlotCategory.Benefit)
                - TaskScoreEvaluator.CategoryTotal(raid, TaskSlotCategory.Cost),
                Is.EqualTo(raid.Value).Within(0.0001f), "the four categories fold to Value");
        }

        [Test]
        public void WithExecution_KeepsEveryIntrinsicSlotAndReplacesOnlyExecution()
        {
            TaskScore intrinsic = TaskScore.FromSlots(s => 1f + (int)s);
            TaskScore execution = TaskScore.FromSlots(s => 100f + (int)s);
            TaskScore composed = TaskScoreEvaluator.WithExecution(intrinsic, execution);
            foreach (TaskSlot slot in Enum.GetValues(typeof(TaskSlot)))
            {
                float expected = TaskScoreEvaluator.GroupOf(slot) == TaskSlotGroup.Intrinsic
                    ? intrinsic[slot] : execution[slot];
                Assert.That(composed[slot], Is.EqualTo(expected), slot.ToString());
            }
        }

        [Test]
        public void WithResponse_NeverDropsAnIntrinsicSlot()
        {
            TaskScore intrinsic = TaskScore.FromSlots(s =>
                TaskScoreEvaluator.GroupOf(s) == TaskSlotGroup.Intrinsic ? 2f : 50f);
            TaskScore response = TaskScoreEvaluator.WithResponse(intrinsic, 0.5f, 1f, 1f, 2f, 3f);
            foreach (TaskSlot slot in Enum.GetValues(typeof(TaskSlot)))
                if (TaskScoreEvaluator.GroupOf(slot) == TaskSlotGroup.Intrinsic)
                    Assert.That(response[slot], Is.EqualTo(2f), slot.ToString());
            Assert.That(response.MoverOpportunityCost, Is.EqualTo(3f));
            Assert.That(response.WinChance, Is.EqualTo(TaskScoreEvaluator.WinChance(0.5f)));
        }

        [Test]
        public void NetChange_IsSlotWiseAndAddsOnlyThePhysicalPriceOfTheChange()
        {
            TaskScore from = TaskScore.FromSlots(s => 1f);
            TaskScore to = TaskScore.FromSlots(s => 3f);
            TaskScore change = TaskScoreEvaluator.NetChange(from, to,
                additionalCardPrice: 1.5f, additionalDelivery: 0.5f);
            foreach (TaskSlot slot in Enum.GetValues(typeof(TaskSlot)))
            {
                float extra = slot == TaskSlot.CardPrice ? 1.5f : slot == TaskSlot.Delivery ? 0.5f : 0f;
                Assert.That(change[slot], Is.EqualTo(2f + extra).Within(0.0001f), slot.ToString());
            }
        }

        [Test]
        public void ForceAmplification_IsCappedBelowWinningTheFightItAmplifies()
        {
            Assert.That(TaskScoreEvaluator.ForceAmplification(0f), Is.Zero);
            Assert.That(TaskScoreEvaluator.ForceAmplification(100f),
                Is.EqualTo(AiConfigV2.taskScoreForceAmplificationMax));
            Assert.That(AiConfigV2.taskScoreForceAmplificationMax,
                Is.LessThan(AiConfigV2.taskScoreWinChanceMax));
        }

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
            // Falloff distance is 9 hexes (AiConfigV2.taskScoreProximityFullFalloffDistance):
            // +3 at home, 0 at the 4.5-hex midpoint, -3 from 9 hexes out.
            Assert.That(TaskScoreEvaluator.OwnTerritoryProximity(0f), Is.EqualTo(3f).Within(0.0001f));
            Assert.That(TaskScoreEvaluator.OwnTerritoryProximity(3f), Is.EqualTo(1f).Within(0.0001f));
            Assert.That(TaskScoreEvaluator.OwnTerritoryProximity(6f), Is.EqualTo(-1f).Within(0.0001f));
            Assert.That(TaskScoreEvaluator.OwnTerritoryProximity(9f), Is.EqualTo(-3f).Within(0.0001f));
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
        public void ActionPrice_OneApOnePrice_AndTwoCurrenciesAreScalesOfIt()
        {
            // A card's AP, an activation now and one more turn of a march are the same AP.
            Assert.That(TaskScoreEvaluator.Price(ActionPrice.Ap(1f)),
                Is.EqualTo(TaskScoreEvaluator.Price(ActionPrice.RecurringAp(1f, 2f))),
                "a real AP has one price whether it pays a card or one extra turn of delivery");
            Assert.That(ActionPrice.RecurringAp(3f, 1f), Is.Zero,
                "a same-turn march pays no recurring activation");

            // No snapshot: every resource unit is at the neutral scarcity price.
            var cost = new Game.Cards.ResourceCost { energy = 2, materials = 1 };
            Assert.That(ActionPrice.Resources(cost),
                Is.EqualTo(3f * AiConfigV2.actionPriceResourceAp).Within(0.0001f));

            // The two currencies price the same AP-equivalents at their own scale only.
            const float apEquivalents = 5f;
            Assert.That(ActionPrice.ToTaskScore(apEquivalents) / ActionPrice.ToCardScore(apEquivalents),
                Is.EqualTo(AiConfigV2.taskScorePerApEquivalent / AiConfigV2.cardScorePerApEquivalent)
                    .Within(0.0001f));
            Assert.That(ActionPrice.FromTaskScore(ActionPrice.ToTaskScore(apEquivalents)),
                Is.EqualTo(apEquivalents).Within(0.0001f));
        }

        [Test]
        public void ActionPrice_ScarcityFollowsPendingDemandAgainstSupply()
        {
            var card = new Game.Cards.CardDefinition
                { resourceCost = new Game.Cards.ResourceCost { energy = 6 } };
            var scarce = new WorldSnapshot { Self = new SelfSnapshot
            {
                Deck = new[] { card }, Hand = System.Array.Empty<Game.Cards.CardData>(),
                Stockpile = new ResourceBundle { Energy = 0f },
            } };
            var plentiful = new WorldSnapshot { Self = new SelfSnapshot
            {
                Deck = System.Array.Empty<Game.Cards.CardDefinition>(),
                Hand = System.Array.Empty<Game.Cards.CardData>(),
                Stockpile = new ResourceBundle { Energy = 20f },
            } };
            Assert.That(ActionPrice.Scarcity(Game.Economy.ResourceType.Energy, scarce),
                Is.EqualTo(AiConfigV2.actionPriceScarcityMax).Within(0.0001f),
                "the deck wants Energy and none is coming: the dearest price");
            Assert.That(ActionPrice.Scarcity(Game.Economy.ResourceType.Energy, plentiful),
                Is.EqualTo(AiConfigV2.actionPriceScarcityMin).Within(0.0001f),
                "nothing else wants Energy: the cheapest price");
        }

        // Task 8 correction — the previous "SameReactivationAp_PricesIdenticallyAcrossEconomy
        // RaidAndRecon" test called the delivery converter TWICE with the SAME
        // hand-picked literal arguments and asserted the result equalled itself: a tautology that
        // exercised no Economy/Raid/Recon production code at all and would pass even if any of the
        // three real cost models were completely broken. It also embedded a false premise: Economy's
        // real "extra AP" accounting (EstimateEconomyAssignmentAp: every outbound turn's activation,
        // because the mover's OWN first-turn activation is never separately priced via cardPrice the
        // way Recon/Raid's ActivationApNow/currentActivationAp split it out) is NOT the same turn
        // count as Recon/Raid's "eta-1" delivery convention — so asserting identical NUMBERS across
        // all three would have been asserting something false about the real domain, not just format.
        // What IS actually shared, and what these three tests verify by calling the real per-family
        // cost model with real, concrete physical inputs, is the one AP price (ActionPrice) each
        // family folds its own real per-turn reactivation AP fact through.
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
                Is.EqualTo(TaskScoreEvaluator.Price(4f * 1f)).Within(0.0001f),
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
            float raidDelivery = TaskScoreEvaluator.Price(ActionPrice.RecurringAp(
                estimate.RecurringActivationAp, estimate.Requirements.EtaTurns));
            Assert.That(raidDelivery, Is.EqualTo(TaskScoreEvaluator.Price(4f * 1f)).Within(0.0001f));
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
            float economyDelivery = TaskScoreEvaluator.Price(extraAp);
            Assert.That(economyDelivery, Is.EqualTo(TaskScoreEvaluator.Price(8f)).Within(0.0001f));

            // What genuinely IS shared across all three families (verified by the sibling tests
            // above using each family's own real numbers): the one price, applied to whatever real
            // per-turn AP fact that family's own cost model actually derived.
            Assert.That(AiConfigV2.taskScorePerApEquivalent, Is.GreaterThan(0f));
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
        public void ScoutEstimate_UsesNearestHomeForExplore()
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
            // The notional mover's whole ApDesired here is an activation (no stealth entry on this
            // route): real AP at the one price every AP has.
            Assert.That(objective.TaskScore.CardPrice,
                Is.EqualTo(TaskScoreEvaluator.Price(estimate.ActivationApNow)));
            Assert.That(objective.TaskScore.Delivery,
                Is.EqualTo(TaskScoreEvaluator.Price(ActionPrice.RecurringAp(
                    estimate.RecurringActivationAp, estimate.EtaTurns))));
            Assert.That(objective.BaseValue, Is.EqualTo(objective.TaskScore.Value));
            // info=10, signed home proximity at 6 hexes=-1, activation=1,
            // one extra turn of delivery=1.
            Assert.That(objective.BaseValue, Is.EqualTo(7f).Within(0.0001f));
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
                Members = System.Array.Empty<Game.Combat.WorthIt.DefenderProfile>(),
                MaxMovement = 3, CurrentMovement = 1, ActivationApCost = 5,
            };
            var cheaper = new ArmySnapshot
            {
                ArmyId = 10, Hex = hex, HasHero = true,
                IsMobileEconomyBuilder = true, MemberCount = 1,
                Members = System.Array.Empty<Game.Combat.WorthIt.DefenderProfile>(),
                MaxMovement = 3, CurrentMovement = 3, ActivationApCost = 1,
            };
            var pinnedRoute = new EconomyBuilderRouteSnapshot {
                ArmyId = 9, TravelCost = 6, ReturnTravelCost = 6,
                CurrentMovement = 1, MaxMovement = 3, ActivationApCost = 5, ArmySize = 1,
            };
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Armies = new List<ArmySnapshot> { pinned, cheaper },
                },
                Economy = new EconomyStanding { ExtractionOpportunities = new[] {
                    new EconomyExtractionOpportunity {
                        Hex = hex, ResourceType = ResourceType.Materials,
                        BuilderRoutes = new[] { pinnedRoute, new EconomyBuilderRouteSnapshot {
                            ArmyId = 10, IsOnTarget = true, TravelCost = 0,
                            CurrentMovement = 3, MaxMovement = 3, ActivationApCost = 1, ArmySize = 1,
                        } },
                    },
                } },
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
            wrongBuilder.WorldTaskScore = new TaskScore(economicHexBenefit: 6f);
            // Requirements read current fog-honest route witnesses, not the old scalar travel hint.
            pinnedRoute.TravelCost = 7;
            snapshot.Economy.ExtractionOpportunities = new[] { new EconomyExtractionOpportunity {
                Hex = hex, ResourceType = ResourceType.Materials, BuilderRoutes = new[] { pinnedRoute },
            } };
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
            RaidObjective objective = RaidObjectiveEvaluator.ForTrackedTarget(
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
                ownTerritoryProximity: objective.TaskScore.OwnTerritoryProximity,
                raidReward: objective.TaskScore.RaidReward,
                winChance: TaskScoreEvaluator.WinChance(resolvedTarget.ReadyWinChance),
                cardPrice: TaskScoreEvaluator.Price(pinned.ActivationApCost),
                delivery: TaskScoreEvaluator.Price(ActionPrice.RecurringAp(pinned.ActivationApCost,
                    result.Requirements.EtaTurns)));
            Assert.That(result.BaseValue, Is.EqualTo(expected.Value).Within(0.0001f));
            Assert.That(result.LocalAdmissionScore, Is.EqualTo(expected.Value).Within(0.0001f));
            // 9 hexes from the only BaseHex, at the falloff distance (9) -> fully saturated -3.
            Assert.That(objective.TaskScore.OwnTerritoryProximity, Is.EqualTo(-3f).Within(0.0001f));
            // No fresh opportunity report: a started stationary neutral/event Raid keeps
            // the exact same intrinsic target value. Fog changes visibility only; it does not
            // move the objective or change its intrinsic value. Actual target destruction/invalidation
            // is a separate continuity decision.
            intent.Raid.LastKnownHex = destination;
            breakdown.OpportunityReport = new CombatOpportunityReport
            {
                All = Array.Empty<CombatOpportunity>(),
                NeutralOpportunities = Array.Empty<CombatOpportunity>(),
            };
            MissionProposal fog = AggressionMissionLayer.Propose(snap, breakdown,
                new[] { intent }, Array.Empty<RaidObjective>()).Single();
            float expectedFog = objective.TaskScore.OwnTerritoryProximity
                + objective.TaskScore.RaidReward
                - TaskScoreEvaluator.Price(pinned.ActivationApCost)
                - TaskScoreEvaluator.Price(ActionPrice.RecurringAp(pinned.ActivationApCost,
                    fog.Requirements.EtaTurns));
            Assert.That(objective.TaskScore.RaidReward, Is.Zero,
                "a plain neutral has no reward of its own (2026-10-07)");
            Assert.That(fog.BaseValue, Is.EqualTo(expectedFog).Within(0.0001f),
                "fog must preserve the stationary Raid target's intrinsic value");
            Assert.That(TaskScoreEvaluator.IntelAgePenalty(1f), Is.GreaterThan(0f),
                "shared intel-age price remains available for future mobile player targets");
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
