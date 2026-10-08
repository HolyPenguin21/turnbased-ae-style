#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Ai;
using Game.Players;
using Game.Units;
using UnityEngine;
using NUnit.Framework;

namespace Game.EditorTests
{
    // Regression coverage alongside AiProductionScoreAlignmentTests; no separate test harness.
    public sealed class AiIndependentDevelopmentTests
    {
        [TestCase(ResourceType.Human)]
        [TestCase(ResourceType.Energy)]
        [TestCase(ResourceType.Tech)]
        public void FacilityHeadroomIgnoresEveryUnusedResource(ResourceType unused)
        {
            var surplus = new ResourceBundle { Human = 1f, Energy = 1f, Materials = 0.8f, Tech = 1f };
            float before = DevelopmentOpportunityEvaluator.StepResourceHeadroom(surplus, new ResourceCost(materials: 2));
            surplus.Add(unused, -1f);
            Assert.That(DevelopmentOpportunityEvaluator.StepResourceHeadroom(surplus, new ResourceCost(materials: 2)),
                Is.EqualTo(before));
            surplus.Materials = 0f;
            Assert.That(DevelopmentOpportunityEvaluator.StepResourceHeadroom(surplus, new ResourceCost(materials: 2)), Is.Zero);
        }

        [Test]
        public void OperatorHeadroomUsesOnlyItsHumanAndTechBill()
        {
            var surplus = new ResourceBundle { Human = 0.9f, Tech = 0.7f };
            Assert.That(DevelopmentOpportunityEvaluator.StepResourceHeadroom(surplus,
                new ResourceCost(human: 1, tech: 1)), Is.EqualTo(0.7f));
            Assert.That(DevelopmentOpportunityEvaluator.StepResourceHeadroom(default, null), Is.EqualTo(1f),
                "A delivery with no resource bill has no resource blocker");
            Assert.That(DevelopmentOpportunityEvaluator.StepResourceHeadroom(default, new ResourceCost()), Is.EqualTo(1f));
        }

        [Test]
        public void OnlyANonCommanderHeroOfALedFieldArmyIsDetachedForDelivery()
        {
            var player = new PlayerSetupData();
            var lead = new UnitData { Owner = player, IsHero = true, Name = "lead" };
            var support = new UnitData { Owner = player, IsHero = true, Name = "support" };
            var army = new ArmyData { Owner = player };
            army.Members.Add(lead); army.Members.Add(support); army.Members.Add(new UnitData { Owner = player });
            Assert.That(AiArmyRoles.IsDetachedFieldDelivery(army, support), Is.True);
            Assert.That(AiArmyRoles.IsDetachedFieldDelivery(army, lead), Is.False, "the commander stays");
            var single = new ArmyData { Owner = player };
            single.Members.Add(lead);
            Assert.That(AiArmyRoles.IsDetachedFieldDelivery(single, lead), Is.False,
                "a one-hero army still moves whole (IsHeroLed)");
            army.IsGarrison = true;
            Assert.That(AiArmyRoles.IsDetachedFieldDelivery(army, support), Is.False, "garrisons use extraction");
        }

        [Test]
        public void PreparationSupportPathRequiresTheSameQualifiedRole()
        {
            var snapshot = new WorldSnapshot { Development = new DevelopmentReadiness
                { ProductionPreparationViable = true } };
            Assert.That(StrategicCardEvaluator.HasDevelopmentRolePath(snapshot, new[] { UnitAbilities.Assembler }), Is.True);
            Assert.That(StrategicCardEvaluator.HasDevelopmentRolePath(snapshot, new[] { UnitAbilities.Researcher }), Is.False);
            Assert.That(StrategicCardEvaluator.HasDevelopmentRolePath(snapshot, new[] { UnitAbilities.Barracks }), Is.False);
        }

        [Test]
        public void GeneratedOperatorBindingSurvivesAPShortageAndExpiresWithoutLeakingToAnotherSite()
        {
            var state = new MissionIntentState();
            var card = new CardData(new CardDefinition { cardType = CardType.Hero });
            var site = new HexCoord(1, 2);
            state.Development.RememberGeneratedDevelopmentOperator(card, site, ResearchProductionMode.Production, 2);
            Assert.That(state.Development.GeneratedOperatorFor(site, ResearchProductionMode.Production, 3), Is.SameAs(card));
            Assert.That(state.Development.CanUseGeneratedOperatorAt(card, new HexCoord(2, 2), ResearchProductionMode.Production, 3), Is.False);
            Assert.That(state.Development.CanUseGeneratedOperatorAt(card, site, ResearchProductionMode.Research, 3), Is.False);
            Assert.That(state.Development.CanUseGeneratedOperatorAt(card, site, ResearchProductionMode.Production, 3), Is.True);
            int expired = 3 + Math.Max(1, AiConfigV2.commitmentStallTurns);
            Assert.That(state.Development.GeneratedOperatorFor(site, ResearchProductionMode.Production, expired), Is.Null);
            Assert.That(state.Development.CanUseGeneratedOperatorAt(card, new HexCoord(2, 2), ResearchProductionMode.Production, expired), Is.True);
        }

        [Test]
        public void OutputForecastWithoutExecutableSourceIsNotAPreparationStep()
        {
            var output = new DevelopmentOpportunity { Card = new CardDefinition { cardType = CardType.Unit } };
            Assert.That(output.IsPreparation, Is.False);
            var preparation = new DevelopmentOpportunity { PreparationKind = DevelopmentPreparationKind.Operator };
            Assert.That(preparation.IsPreparation, Is.True);
            Assert.That(preparation.Card, Is.Null);
            Assert.That(preparation.RecipientCard, Is.Null);
        }

        [Test]
        public void GeneratedOperatorClaimProtectsOnlyItsExactPhysicalCard()
        {
            var reservation = new MaterializationReservation();
            var def = new CardDefinition { cardType = CardType.Hero };
            var operatorCard = new CardData(def) { ResearchProductionCreated = true };
            var unrelatedCard = new CardData(def) { ResearchProductionCreated = true };
            Assert.That(reservation.ClaimsDevelopmentOperatorCard(operatorCard), Is.False);
            reservation.ClaimDevelopmentOperatorCard(operatorCard);
            Assert.That(reservation.ClaimsDevelopmentOperatorCard(operatorCard), Is.True);
            Assert.That(reservation.ClaimsDevelopmentOperatorCard(unrelatedCard), Is.False,
                "Same-definition Hero copies must remain available for ordinary missions");
            Assert.That(reservation.ClaimsDevelopmentOperatorCard(null), Is.False);
        }

        [Test]
        public void MintedOperatorSurvivesTurnBoundaryAndOnlyItsPhysicalCardIsClaimed()
        {
            var persistent = new MissionIntentState();
            var factory = new HexCoord(3, -2);
            var definition = new CardDefinition { cardType = CardType.Hero };
            var minted = new CardData(definition) { ResearchProductionCreated = true };
            var otherCopy = new CardData(definition) { ResearchProductionCreated = true };
            persistent.Development.RememberGeneratedDevelopmentOperator(minted, factory,
                ResearchProductionMode.Production, 2);
            var nextTurn = new MaterializationReservation(); // not the T2 instance
            foreach (CardData card in persistent.Development.ReconcileGeneratedDevelopmentOperators(3,
                (c, site, mode) => site.Equals(factory)
                    && mode == ResearchProductionMode.Production))
                nextTurn.ClaimDevelopmentOperatorCard(card);
            Assert.That(nextTurn.ClaimsDevelopmentOperatorCard(minted), Is.True);
            Assert.That(nextTurn.ClaimsDevelopmentOperatorCard(otherCopy), Is.False);
        }

        [Test]
        public void MintedOperatorClaimReleasesOnInvalidDestinationAndFiniteExpiry()
        {
            var persistent = new MissionIntentState();
            var minted = new CardData(new CardDefinition { cardType = CardType.Hero });
            var factory = new HexCoord(3, -2);
            persistent.Development.RememberGeneratedDevelopmentOperator(minted, factory,
                ResearchProductionMode.Production, 2);
            var reservation = new MaterializationReservation();
            reservation.ClaimDevelopmentOperatorCard(minted);
            reservation.ReconcileDevelopmentOperatorCards(
                persistent.Development.ReconcileGeneratedDevelopmentOperators(3, (c, site, mode) => false));
            Assert.That(reservation.ClaimsDevelopmentOperatorCard(minted), Is.False,
                "Lost, staffed, contested or full factory must release its Hero");

            persistent.Development.RememberGeneratedDevelopmentOperator(minted, factory,
                ResearchProductionMode.Production, 2);
            int expiredTurn = 2 + System.Math.Max(1, AiConfigV2.commitmentStallTurns) + 1;
            Assert.That(persistent.Development.ReconcileGeneratedDevelopmentOperators(expiredTurn,
                (c, site, mode) => true), Is.Empty,
                "No permanent reservation when AP or structural availability never recovers");
        }

        [Test]
        public void GeneratedOperatorRequiresAuthenticQualifiedHeroAndPositiveChallengeChance()
        {
            var hero = new CardDefinition
            {
                cardType = CardType.Hero, authoredKey = "generated-assembler",
                grantedAbilities = new List<string> { UnitAbilities.Assembler },
            };
            var source = new GenerationStep { CardDef = hero, SuccessChance = 0.5f };
            Assert.That(DevelopmentOpportunityEvaluator.IsGeneratedOperatorCandidate(
                source, ResearchProductionMode.Production), Is.True);
            Assert.That(DevelopmentOpportunityEvaluator.IsGeneratedOperatorCandidate(
                source, ResearchProductionMode.Research), Is.False);

            source.SuccessChance = 0f;
            Assert.That(DevelopmentOpportunityEvaluator.IsGeneratedOperatorCandidate(
                source, ResearchProductionMode.Production), Is.False);
            source.SuccessChance = 0.5f;
            hero.authoredKey = null;
            Assert.That(DevelopmentOpportunityEvaluator.IsGeneratedOperatorCandidate(
                source, ResearchProductionMode.Production), Is.False);
            hero.authoredKey = "generated-assembler";
            hero.cardType = CardType.Unit;
            Assert.That(DevelopmentOpportunityEvaluator.IsGeneratedOperatorCandidate(
                source, ResearchProductionMode.Production), Is.False);
        }

        [Test]
        public void ProspectiveAviationStaysInCanonicalNonCombatLaneAndRequiresRealPlacement()
        {
            var plane = new CardDefinition
            {
                cardType = CardType.Unit, isAviation = true, authoredKey = "candidate-plane",
            };
            Assert.That(NonCombatCardPlayer.LaneFor(plane),
                Is.EqualTo(NonCombatCardPlayer.PlayKind.Aviation));
            float withoutRealFacility = NonCombatCardPlayer.ProjectedAviationInvestmentValue(
                plane, ResearchProductionMode.Production, new HexCoord(0, 0),
                null, null, null, null, null, null);
            Assert.That(withoutRealFacility, Is.EqualTo(float.NegativeInfinity),
                "An aviation-only catalog may justify investment only with a real airfield and operator");
        }

        [TestCase(CardType.Unit, CapabilityKind.FieldCombatPower)]
        [TestCase(CardType.Hero, CapabilityKind.Hero)]
        public void ProspectiveGeneratedBodyUsesCanonicalPlanAndCorrectSurplusCapability(
            CardType type, CapabilityKind expectedCapability)
        {
            var definition = new CardDefinition
            {
                cardType = type, authoredKey = "prospective-asset",
                resourceCost = new ResourceCost { energy = 4, materials = 3, tech = 0 },
            };
            var generation = new GenerationStep
            {
                CardDef = definition, SuccessChance = 0.8f,
                CardKey = "investment-preview:prospective-asset",
            };
            var option = new PlacementOption(new HexCoord(0, 0), DeploymentKind.NewArmy, null);
            var abilities = MaterializationChainMatching.EffectiveAbilities(definition, null);
            var plan = MaterializationPlanFactory.MakeGeneratedPlan(
                MaterializationChainKind.GenerateDeploy, null, generation,
                baseInHand: null, baseIdx: -1, generatedIsEquipment: false,
                opt: option, projected: abilities);
            plan.FinalCapability = MaterializationChainEnumerator.SurplusCapability(
                definition, abilities, option);

            Assert.That(plan.GeneratedBaseDef, Is.SameAs(definition));
            Assert.That(plan.Generation, Is.SameAs(generation));
            Assert.That(plan.FinalCapability, Is.EqualTo(expectedCapability));
            Assert.That(plan.ResCost.energy, Is.EqualTo(4));
            Assert.That(plan.ResCost.materials, Is.EqualTo(3));
            Assert.That(plan.ResCost.tech, Is.Zero,
                "An irrelevant zero-Tech requirement cannot enter the prospective chain");
            Assert.That(plan.ApCost, Is.GreaterThanOrEqualTo(
                ResearchProductionSystem.AttemptApCost(definition)),
                "Prospective Unit/Hero valuation must include the real Challenge AP");
        }

        [Test]
        public void ResourceCostIgnoresStockpileOfResourceAbsentFromChain()
        {
            var snapshot = new WorldSnapshot
            {
                Self = new SelfSnapshot
                {
                    Stockpile = new ResourceBundle { Human = 12f, Energy = 12f, Materials = 8f },
                    PerTurnIncome = new ResourceBundle { Energy = 1f, Materials = 1f },
                    Hand = Array.Empty<CardData>(),
                    Deck = Array.Empty<CardDefinition>(),
                },
            };
            var cost = new ResourceCost { energy = 4, materials = 3, tech = 0 };
            float noTech = StrategicCardEvaluator.StrategicResourceCostValue(cost, snapshot);
            snapshot.Self.Stockpile = new ResourceBundle
            {
                Human = 12f, Energy = 12f, Materials = 8f, Tech = 100f,
            };
            float abundantTech = StrategicCardEvaluator.StrategicResourceCostValue(cost, snapshot);
            Assert.That(noTech, Is.EqualTo(abundantTech).Within(0.0001f),
                "An irrelevant Tech stockpile must not affect the price of an Energy/Materials chain");
        }

        [Test]
        public void UpgradePlanPricesTheWholeChallengeAndAttachmentExactlyOnce()
        {
            var equipment = new CardDefinition
            {
                cardType = CardType.Equipment,
                activationApCost = 2,
                resourceCost = new ResourceCost { energy = 4, materials = 3 },
            };
            var generation = new GenerationStep
            {
                CardDef = equipment, ProducesEquipment = true, CardKey = "factory:equipment",
            };
            var host = new CardData(new CardDefinition { cardType = CardType.Unit });
            var opportunity = new DevelopmentOpportunity
            {
                Card = equipment, Generation = generation, RecipientCard = host,
                RecipientKind = DevRecipientKind.HandCard, RecipientLabel = "hand:host",
            };
            var demand = new AxisDemand
            {
                RequestingAxis = DesireAxis.Development,
                Capability = CapabilityKind.CardUpgrade,
                DevOpportunity = opportunity,
            };

            MaterializationPlan plan = MaterializationPlanFactory.MakeDevelopmentUpgradePlan(demand);
            Assert.That(plan, Is.Not.Null);
            Assert.That(plan.ApCost, Is.EqualTo(ResearchProductionSystem.AttemptApCost(equipment)));
            Assert.That(plan.DeferredAttachmentAp, Is.EqualTo(2));
            float stagedScore = StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, null, null, null, null);
            plan.ApCost += plan.DeferredAttachmentAp;
            plan.DeferredAttachmentAp = 0;
            Assert.That(StrategicCardEvaluator.ScoreGeneratedEquipmentUpgrade(
                opportunity, plan, null, null, null, null), Is.EqualTo(stagedScore).Within(0.0001f),
                "Splitting funding across turns must not discount the full prospective cost");
            Assert.That(plan.ResCost, Is.Not.Null);
            Assert.That(plan.ResCost.energy, Is.EqualTo(4));
            Assert.That(plan.ResCost.materials, Is.EqualTo(3));
            Assert.That(plan.ResCost.human, Is.Zero);
            Assert.That(plan.ResCost.tech, Is.Zero);
            Assert.That(plan.UpgradeTargetCard, Is.SameAs(host));
            Assert.That(plan.Generation, Is.SameAs(generation));
        }

    }

    // Requires the Unity runtime: verifies actual registry/preflight/admission contracts.
    // It does not simulate authoritative Challenge or movement transactions.
    public sealed class AiProductionStagedPreparationTests
    {
        private static readonly HexCoord Site = new HexCoord(91, -17);
        private PlayerSetupData _player;
        private PlayerRoot _root;
        private AiHandData _hand;
        private WorldSnapshot _snapshot;
        private AiTurnContext _ctx;
        private BuildingData _base;
        private ArmyData _garrison;
        private CardData _facility, _operator;
        private ResearchProductionCatalog _catalog;
        private FactionCardCatalog _cards;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear();
            MissionIntentRegistry.Clear(); DevelopmentInvestmentGate.Clear();
            StrategicResourceReservationLedger.ClearAll();
            _player = new PlayerSetupData();
            _root = PlayerRoot.Create(_player, "staged production test");
            PlayerRootRegistry.Register(_player, _root); _root.ActionPoints = 20;
            _base = new BuildingData { Owner = _player, Hex = Site, IsBase = true };
            _base.Abilities.Add(UnitAbilities.Barracks); BuildingRegistry.Register(Site, _base);
            _garrison = new ArmyData { Owner = _player, Hex = Site, IsGarrison = true };
            ArmyRegistry.Register(_garrison);
            _hand = new AiHandData(null, default, 0);
            _facility = new CardData(new CardDefinition
            {
                cardType = CardType.Facility, authoredKey = "test-factory",
                grantedAbilities = new List<string> { UnitAbilities.Production },
                resourceCost = new ResourceCost(materials: 2),
            });
            _operator = new CardData(new CardDefinition
            {
                cardType = CardType.Hero, authoredKey = "test-assembler", commandRating = 6,
                requiredBuildingAbility = UnitAbilities.Barracks,
                grantedAbilities = new List<string> { UnitAbilities.Assembler },
            });
            _hand.AddCard(_facility); _hand.AddCard(_operator);
            // No recipient and an unpayable future output: neither is a preparation dependency.
            var output = new CardDefinition
                { cardType = CardType.Equipment, authoredKey = "test-output", resourceCost = new ResourceCost(tech: 99) };
            _cards = ScriptableObject.CreateInstance<FactionCardCatalog>(); _cards.cards.Add(output);
            _catalog = ScriptableObject.CreateInstance<ResearchProductionCatalog>(); _catalog.cardCatalogs.Add(_cards);
            _catalog.productionCards.Add(new ResearchProductionEntry { cardKey = output.authoredKey });
            _ctx = new AiTurnContext { TurnNumber = 2, ResearchProductionCatalog = _catalog };
            _snapshot = new WorldSnapshot
            {
                Observer = _player, TurnNumber = 2,
                Self = new SelfSnapshot { BaseHexes = new[] { Site }, Armies = Array.Empty<ArmySnapshot>(),
                    Hand = _hand.Hand, Deck = Array.Empty<CardDefinition>() },
                Development = new DevelopmentReadiness(),
                Economy = new EconomyStanding { PerType = Array.Empty<EconomyResourceStanding>() },
            };
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear();
            MissionIntentRegistry.Clear(); DevelopmentInvestmentGate.Clear();
            StrategicResourceReservationLedger.ClearAll(); ResourceStarvationRegistry.Clear();
            if (_catalog != null) UnityEngine.Object.DestroyImmediate(_catalog);
            if (_cards != null) UnityEngine.Object.DestroyImmediate(_cards);
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root.gameObject);
        }

        private List<DevelopmentOpportunity> Facts(IReadOnlyList<MissionIntent> intents = null) =>
            DevelopmentOpportunityEvaluator.PreparationFacts(_snapshot, _player, _root, _hand, _ctx, intents);
        private List<DevelopmentOpportunity> Admitted() => DevelopmentOpportunityEvaluator.Enumerate(
            _snapshot, _player, _root, _hand, _ctx, null);
        private void OpenMaterialsWindow()
        {
            _snapshot.Development.InvestmentSurplusByType.Materials = 1f;
            _snapshot.TurnNumber = 1; DevelopmentInvestmentGate.Observe(_player, _snapshot);
            _snapshot.TurnNumber = 2; DevelopmentInvestmentGate.Observe(_player, _snapshot);
        }

        [Test]
        public void BothFirstStepsExistWithoutSpecificProductOrRecipient()
        {
            var steps = Facts();
            Assert.That(steps.Select(o => o.PreparationKind), Is.EquivalentTo(new[]
                { (DevelopmentPreparationKind?)DevelopmentPreparationKind.Facility, DevelopmentPreparationKind.Operator }));
            Assert.That(steps.All(o => o.Card == null && o.RecipientCard == null && o.RecipientUnit == null), Is.True);
            Assert.That(steps.Single(o => o.PreparationKind == DevelopmentPreparationKind.Facility).StageResourceCost.tech, Is.Zero);
            Assert.That(steps.Single(o => o.PreparationKind == DevelopmentPreparationKind.Operator).StageResourceCost, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FreeOperatorCanComeFirstWhenFacilityIsUnaffordableOrStillInDeck(bool facilityInDeck)
        {
            if (facilityInDeck)
            {
                _hand.RemoveCard(_facility); _snapshot.Self.Deck = new[] { _facility.Definition };
            }
            var steps = Admitted();
            Assert.That(steps, Has.Count.EqualTo(1));
            Assert.That(steps[0].PreparationKind, Is.EqualTo(DevelopmentPreparationKind.Operator));
            Assert.That(ResearchProductionSystem.IsEligible(_player, Site, ResearchProductionMode.Production, out _), Is.False);
        }

        // 2026-10-08 — the walk of an existing hero is a TaskScore: the card-scale facility value
        // (nonCombatFacilityValue) must be read through the one conversion, or the 2 AP container
        // alone (2 TaskScore) always outweighs it (1.1) and the delivery is never admitted.
        [Test]
        public void RemoteOperatorDeliveryIsValuedOnTheTaskScoreScale()
        {
            var farHex = new HexCoord(Site.Q + 1, Site.R);
            var facility = new FacilityData(); facility.Abilities.Add(UnitAbilities.Production);
            _base.FacilitySlots[0] = facility;
            _hand.RemoveCard(_operator); _hand.RemoveCard(_facility);
            var hero = new UnitData { Owner = _player, IsHero = true, Name = "remote" };
            hero.Abilities.Add(UnitAbilities.Assembler);
            var far = new ArmyData { Owner = _player, Hex = farHex, IsGarrison = true };
            far.Members.Add(hero); far.Members.Add(new UnitData { Owner = _player });
            ArmyRegistry.Register(far);
            var mapObject = new GameObject("TestHexMap");
            try
            {
                var map = mapObject.AddComponent<HexMap>();
                var data = new Dictionary<HexCoord, Game.Terrain.TerrainTypeEntry>
                {
                    [Site] = new Game.Terrain.TerrainTypeEntry { moveCost = 1 },
                    [farHex] = new Game.Terrain.TerrainTypeEntry { moveCost = 1 },
                };
                map.SetData(10, 1f, data);
                _ctx.Map = map;
                DevelopmentOpportunity step = Admitted().SingleOrDefault(o => o.IsPreparation);
                Assert.That(step, Is.Not.Null, "A free operator one hex away is a worthwhile delivery");
                Assert.That(step.PreparationExistingHero, Is.SameAs(hero));
                float benefit = ActionPrice.CardScoreToTaskScore(AiConfigV2.nonCombatFacilityValue);
                Assert.That(step.WorldTaskScore.Value, Is.LessThan(benefit),
                    "the delivery's net is its benefit minus a real price");
                Assert.That(step.WorldTaskScore.Value, Is.GreaterThan(0f));
            }
            finally { UnityEngine.Object.DestroyImmediate(mapObject); }
        }

        [Test]
        public void CardStepsAreRankedByTheirCardScoreAndCarryNoWorldTaskScore()
        {
            _hand.RemoveCard(_facility); _snapshot.Self.Deck = new[] { _facility.Definition };
            DevelopmentOpportunity step = Admitted().Single();
            Assert.That(step.PreparationKind, Is.EqualTo(DevelopmentPreparationKind.Operator));
            Assert.That(step.PreparationCardScore, Is.Not.Null.And.GreaterThan(0f),
                "A hand operator is admitted on its positive card score");
            Assert.That(step.PreparationRank, Is.EqualTo(step.PreparationCardScore.Value).Within(0.0001f),
                "The rank is the card score itself, not another bonus on top of it");
            Assert.That(step.WorldTaskScore.Value, Is.Zero,
                "No fixed infrastructure TaskScore rides along with a card step");
        }

        [Test]
        public void FacilityCanComeFirstWithQualifiedOperatorOnlyInDeck()
        {
            _hand.RemoveCard(_operator); _snapshot.Self.Deck = new[] { _operator.Definition };
            _root.AddResource(ResourceType.Materials, 2); OpenMaterialsWindow();
            var steps = Admitted();
            Assert.That(steps, Has.Count.EqualTo(1));
            Assert.That(steps[0].PreparationKind, Is.EqualTo(DevelopmentPreparationKind.Facility));
            Assert.That(steps[0].PreparationExistingHero, Is.Null);
            Assert.That(ResearchProductionSystem.FindActor(_player, Site, ResearchProductionMode.Production), Is.Null);
        }

        [Test]
        public void PreparedOperatorFinishesDeliveryButDoesNotAuthorizeProductionBeforeFacility()
        {
            var hero = new UnitData { Owner = _player, IsHero = true }; hero.Abilities.Add(UnitAbilities.Assembler);
            _garrison.Members.Add(hero);
            Assert.That(DevelopmentOpportunityEvaluator.OperatorPreparedAt(_player, hero, Site, ResearchProductionMode.Production), Is.True);
            Assert.That(ResearchProductionSystem.IsEligible(_player, Site, ResearchProductionMode.Production, out _), Is.False);
            Assert.That(Facts().All(o => o.PreparationKind == DevelopmentPreparationKind.Facility), Is.True);
            var facility = new FacilityData(); facility.Abilities.Add(UnitAbilities.Production); _base.FacilitySlots[0] = facility;
            Assert.That(ResearchProductionSystem.IsEligible(_player, Site, ResearchProductionMode.Production, out _), Is.True);
            Assert.That(Facts(), Is.Empty);
            _base.Owner = new PlayerSetupData();
            Assert.That(DevelopmentOpportunityEvaluator.OperatorPreparedAt(_player, hero, Site, ResearchProductionMode.Production), Is.False);
        }

        [Test]
        public void CompletedPreparationKeepsOneSitePerModeAndNeverPullsTheOperatorToASecondBase()
        {
            var second = new HexCoord(92, -17);
            var secondBase = new BuildingData { Owner = _player, Hex = second, IsBase = true };
            secondBase.Abilities.Add(UnitAbilities.Barracks); BuildingRegistry.Register(second, secondBase);
            var secondGarrison = new ArmyData { Owner = _player, Hex = second, IsGarrison = true };
            ArmyRegistry.Register(secondGarrison);
            _snapshot.Self = new SelfSnapshot { BaseHexes = new[] { Site, second }, Armies = Array.Empty<ArmySnapshot>(),
                Hand = _hand.Hand, Deck = Array.Empty<CardDefinition>() };
            var hero = new UnitData { Owner = _player, IsHero = true }; hero.Abilities.Add(UnitAbilities.Assembler);
            _garrison.Members.Add(hero);
            Assert.That(DevelopmentOpportunityEvaluator.SelectedPreparationSite(ResearchProductionMode.Production,
                _snapshot, _player, _hand, _ctx, null), Is.EqualTo((HexCoord?)Site));
            Assert.That(Facts().All(o => o.FacilityHex.Equals(Site)), Is.True,
                "the prepared base is the only site still offered; the other base yields");
            Assert.That(Facts().Any(o => o.PreparationKind == DevelopmentPreparationKind.Operator), Is.False,
                "its qualified operator is not sold again as a delivery to the second base");
        }

        [Test]
        public void WholeArmyDepartureIsBlockedOnlyWhenNoQualifiedOperatorStaysOutsideIt()
        {
            var facility = new FacilityData(); facility.Abilities.Add(UnitAbilities.Production); _base.FacilitySlots[0] = facility;
            var lead = new UnitData { Owner = _player, IsHero = true }; lead.Abilities.Add(UnitAbilities.Assembler);
            var field = new ArmyData { Owner = _player, Hex = Site };
            field.Members.Add(lead); ArmyRegistry.Register(field);
            Assert.That(AiArmyRoles.DepartureStripsOperator(_player, field), Is.True,
                "the army's only hero is the facility's only operator");
            var other = new UnitData { Owner = _player, IsHero = true }; other.Abilities.Add(UnitAbilities.Assembler);
            _garrison.Members.Add(other);
            Assert.That(AiArmyRoles.DepartureStripsOperator(_player, field), Is.False,
                "another qualified operator stays on the hex outside the leaving army");
            _garrison.Members.Remove(other); field.Members.Add(other);
            Assert.That(AiArmyRoles.DepartureStripsOperator(_player, field), Is.True,
                "an operator inside the leaving army is not a kept operator");
            field.Hex = new HexCoord(5, 5);
            Assert.That(AiArmyRoles.DepartureStripsOperator(_player, field), Is.False,
                "an army away from the facility serves nothing");
        }

        [Test]
        public void SoleCommanderOperatorCannotStayHomeSoTheWholeDepartureIsBlocked()
        {
            var facility = new FacilityData(); facility.Abilities.Add(UnitAbilities.Production); _base.FacilitySlots[0] = facility;
            var op = new UnitData { Owner = _player, IsHero = true, Name = "op" }; op.Abilities.Add(UnitAbilities.Assembler);
            var field = new ArmyData { Owner = _player, Hex = Site };
            field.Members.Add(op); ArmyRegistry.Register(field);
            Assert.That(LocalOperatorRelease.CanKeepOperatorsHome(_player, field, null, null, out string why), Is.False);
            Assert.That(why, Does.Contain("only legal commander"));
            Assert.That(DevelopmentOpportunityEvaluator.OperatorDutyBlocksDeparture(_player, field, null, null), Is.True,
                "no second legal commander: Economy must not take the only operator away");
            field.Hex = new HexCoord(5, 5);
            Assert.That(DevelopmentOpportunityEvaluator.OperatorDutyBlocksDeparture(_player, field, null, null), Is.False,
                "away from the served hex nothing is blocked");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OperatorWithASecondLegalCommanderMayStayHomeSoEconomyCanUseTheArmy(bool operatorLeads)
        {
            var facility = new FacilityData(); facility.Abilities.Add(UnitAbilities.Production); _base.FacilitySlots[0] = facility;
            var op = new UnitData { Owner = _player, IsHero = true, Name = "op", CommandRating = 5 };
            op.Abilities.Add(UnitAbilities.Assembler);
            var lead = new UnitData { Owner = _player, IsHero = true, Name = "lead", CommandRating = 3 };
            var field = new ArmyData { Owner = _player, Hex = Site };
            if (operatorLeads) { field.Members.Add(op); field.Members.Add(lead); }
            else { field.Members.Add(lead); field.Members.Add(op); }
            ArmyRegistry.Register(field);
            Assert.That(AiArmyRoles.DepartureStripsOperator(_player, field), Is.True);
            Assert.That(LocalOperatorRelease.CanKeepOperatorsHome(_player, field, null, null, out string why), Is.True, why);
            Assert.That(DevelopmentOpportunityEvaluator.OperatorDutyBlocksDeparture(_player, field, null, null), Is.False,
                "the operator can be left in the free local garrison: the remaining army is a valid builder");
            Assert.That(AiArmyRoles.IsHeroLed(field), Is.False, "the live multi-hero rule is unchanged");
            Assert.That(LocalOperatorRelease.CanKeepOperatorsHome(_player, field, null, null,
                out why, out ArmyData departure), Is.True, why);
            Assert.That(departure.Commander, Is.SameAs(lead));
            Assert.That(departure.Members, Is.EquivalentTo(new[] { lead }));
            Assert.That(field.Members.Count, Is.EqualTo(2), "planning never changes the real roster");
            Assert.That(ProvisioningManager.IsMobileEconomyHero(field, _player), Is.True,
                "Provisioning admits the legal departing roster, not the original two-hero shape");
            Assert.That(ProvisioningManager.IsMobileEconomyHero(field, _player, departureNeeded: false), Is.False);
            var facts = WorldAnalysis.ToArmySnapshot(field, _player, true, 0);
            facts.EconomyDeparture = WorldAnalysis.ToArmySnapshot(departure, _player, true, 0);
            facts.EconomyDeparture.ArmyId = field.Id;
            _snapshot.Self.Armies = new[] { facts };
            var route = new EconomyBuilderRouteSnapshot { ArmyId = field.Id, TravelCost = 1,
                MaxMovement = 1, CurrentMovement = 1, RouteThreats = Array.Empty<AiMapMemory.KnownEnemySighting>() };
            Assert.That(DemandLayer.EconomyBuilderCandidateRejection(_snapshot, new HexCoord(92, -17),
                new[] { route }, field.Id, Array.Empty<MissionIntent>(), null),
                Is.EqualTo("ranking_rejected (suitability or loan gate)"), "the structural Demand gate passes");
            var choice = DemandLayer.AssessEconomyArmy(_snapshot, new HexCoord(92, -17), route,
                facts, 0f, false, requiresFoundingGarrison: false);
            Assert.That(choice.Army, Is.SameAs(facts.EconomyDeparture),
                "AP, commander and escort assessment use the departing roster");
            int apBefore = _root.ActionPoints;
            Assert.That(LocalOperatorRelease.ReleaseBeforeDeparture(_player, _ctx, field, null), Is.EqualTo(1));
            Assert.That(field.Commander, Is.SameAs(lead));
            Assert.That(AiArmyRoles.IsHeroLed(field), Is.True);
            Assert.That(_garrison.Members, Does.Contain(op));
            Assert.That(_root.ActionPoints, Is.EqualTo(apBefore));
        }

        [Test]
        public void SingleGarrisonExtractionCountsAnotherOperatorInTheSameGarrisonAsStaying()
        {
            var fast = new UnitData { Owner = _player, IsHero = true, Name = "A", CommandRating = 12 };
            var backup = new UnitData { Owner = _player, IsHero = true, Name = "B", CommandRating = 12 };
            fast.Abilities.Add(UnitAbilities.Assembler); backup.Abilities.Add(UnitAbilities.Assembler);
            _garrison.Members.Add(fast); _garrison.Members.Add(backup);
            for (int i = 0; i < AiConfig.secureBaseMinNonHeroUnits; i++)
                _garrison.Members.Add(new UnitData { Owner = _player });
            Func<ResearchProductionMode, HexCoord?> site = mode =>
                mode == ResearchProductionMode.Production ? Site : (HexCoord?)null;
            Assert.That(AiArmyRoles.FacilityNeedsHero(_player, _garrison, fast, site), Is.True);
            Assert.That(AiArmyRoles.FacilityNeedsHero(_player, _garrison, fast, site, new[] { fast }), Is.False);
            Assert.That(AiArmyRoles.BestSparableEconomyHero(_player, _garrison, site), Is.Not.Null,
                "the remaining operator protects preparation without rejecting the entire garrison");
            _garrison.Members.Remove(backup);
            Assert.That(AiArmyRoles.BestSparableEconomyHero(_player, _garrison, site), Is.Null);
            var free = new UnitData { Owner = _player, IsHero = true, Name = "free", CommandRating = 12 };
            _garrison.Members.Add(free);
            Assert.That(AiArmyRoles.BestSparableEconomyHero(_player, _garrison, site), Is.SameAs(free),
                "a blocked operator is filtered before ranking; another legal hero remains available");
        }

        [TestCase("claimed")]
        [TestCase("activation")]
        [TestCase("full")]
        public void EconomyDepartureProjectionCannotBypassLocalGarrisonConstraints(string obstacle)
        {
            var facility = new FacilityData(); facility.Abilities.Add(UnitAbilities.Production);
            _base.FacilitySlots[0] = facility;
            var op = new UnitData { Owner = _player, IsHero = true, Name = "op", CommandRating = 5 };
            op.Abilities.Add(UnitAbilities.Assembler);
            var lead = new UnitData { Owner = _player, IsHero = true, Name = "lead", CommandRating = 3 };
            var field = new ArmyData { Owner = _player, Hex = Site };
            field.Members.Add(lead); field.Members.Add(op); ArmyRegistry.Register(field);
            var commitments = new ActorCommitments();
            if (obstacle == "claimed") commitments.Claim(_garrison.Id);
            if (obstacle == "activation") _garrison.MarkActivated();
            if (obstacle == "full")
                for (int i = 0; i < 5; i++) _garrison.Members.Add(new UnitData { Owner = _player });
            Assert.That(LocalOperatorRelease.CanKeepOperatorsHome(_player, field, commitments, null,
                out _, out _), Is.False);
            Assert.That(ProvisioningManager.IsMobileEconomyHero(field, _player, commitments), Is.False);
            Assert.That(field.Members.Count, Is.EqualTo(2));
            Assert.That(_root.ActionPoints, Is.EqualTo(20));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DeferredEconomyExtractionRechecksPinnedOperatorBeforeAnyMutation(bool facilityBuilt)
        {
            AiHandRegistry.Clear();
            try
            {
                var op = new UnitData { Owner = _player, IsHero = true, Name = "pinned", CommandRating = 12 };
                op.Abilities.Add(UnitAbilities.Assembler); _garrison.Members.Add(op);
                for (int i = 0; i < AiConfig.secureBaseMinNonHeroUnits; i++)
                    _garrison.Members.Add(new UnitData { Owner = _player });
                Assert.That(AiArmyRoles.CanSpareGarrisonMember(_player, _garrison, op), Is.True);
                var plan = ProvisioningManager.GarrisonExtractionCandidate.Yes(
                    ProvisioningManager.GarrisonExtractionTier.Create, op, null, ArmyActions.CreateArmyApCost);
                // Duty appears AFTER binding: either a ready facility or its selected preparation site.
                if (facilityBuilt)
                {
                    var facility = new FacilityData(); facility.Abilities.Add(UnitAbilities.Production);
                    _base.FacilitySlots[0] = facility;
                }
                else
                {
                    AiHandRegistry.GetOrCreate(_player, null, 0).AddCard(_facility);
                    Assert.That(DevelopmentOpportunityEvaluator.SelectedPreparationSite(
                        ResearchProductionMode.Production, _player, AiHandRegistry.Peek(_player), _ctx), Is.EqualTo(Site));
                }
                var pm = new ProvisionedMission { Kind = MissionKind.Economy,
                    EconomyExtractionGarrisonArmyId = _garrison.Id, EconomyExtractionPlan = plan,
                    EconomyTarget = new EconomyMissionTarget { Kind = EconomyTaskKind.BuildExtraction,
                        TargetHex = new HexCoord(92, -17) },
                    EconomyExtractionPreparation = ProvisioningManager.EconomyCompletionPlan.Yes(null, null,
                        new List<UnitData>(), new List<UnitData>(), true, true, null, 0f, null) };
                var result = new ExecutionResult();
                int apBefore = _root.ActionPoints;
                int armiesBefore = ArmyRegistry.AllForOwner(_player).Count();
                int versionBefore = WorldDeltaLifecycle.Current;
                var apply = typeof(TaskExecutor).GetMethod("ApplyEconomyPreparation",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Assert.That(apply.Invoke(null, new object[] { _player, _root, _ctx, pm, result,
                    apBefore, _snapshot }), Is.EqualTo(false));
                Assert.That(result.StopReason, Is.EqualTo(ExecutionStopReason.TargetInvalidated));
                Assert.That(result.ActualActorArmyId, Is.EqualTo(_garrison.Id));
                Assert.That(_garrison.Members, Does.Contain(op));
                Assert.That(ArmyRegistry.AllForOwner(_player).Count(), Is.EqualTo(armiesBefore));
                Assert.That(_root.ActionPoints, Is.EqualTo(apBefore));
                Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(versionBefore));
            }
            finally { AiHandRegistry.Clear(); }
        }

        [Test]
        public void BoundIncomingOperatorPreventsDuplicatePreparationForSameSiteAndRole()
        {
            var hero = new UnitData { Owner = _player, IsHero = true }; hero.Abilities.Add(UnitAbilities.Assembler);
            var army = new ArmyData { Owner = _player, Hex = new HexCoord(90, -17) };
            army.Members.Add(hero); ArmyRegistry.Register(army);
            var intent = new MissionIntent { Kind = MissionKind.Development, Status = IntentStatus.Active,
                Objective = new DevelopmentIntent { Hero = hero, FacilityHex = Site, Mode = ResearchProductionMode.Production } };
            Assert.That(Facts(new[] { intent }).Select(o => o.PreparationKind),
                Is.EquivalentTo(new[] { (DevelopmentPreparationKind?)DevelopmentPreparationKind.Facility }));
        }
    }
}
#endif

