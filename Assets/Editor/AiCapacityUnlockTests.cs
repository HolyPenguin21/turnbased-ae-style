#if UNITY_INCLUDE_TESTS
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Core;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    // The stand-alone CapacityUnlock step of Research/Production preparation: a full own Base buys ITS
    // next level (and nothing else) while the Facility card waits in hand; the placement is a separate,
    // later action. Real registries, real bank, real gameplay primitive.
    public sealed class AiCapacityUnlockTests
    {
        private static readonly HexCoord Site = new HexCoord(92, -18);
        private PlayerSetupData _player;
        private PlayerRoot _root;
        private AiHandData _hand;
        private WorldSnapshot _snapshot;
        private AiTurnContext _ctx;
        private BuildingData _base;
        private CardData _facility, _operator;
        private ResearchProductionCatalog _catalog;
        private FactionCardCatalog _cards;
        private GameConfig _config;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear();
            MissionIntentRegistry.Clear(); DevelopmentInvestmentGate.Clear();
            StrategicResourceReservationLedger.ClearAll();
            _player = new PlayerSetupData();
            _root = PlayerRoot.Create(_player, "capacity unlock test");
            PlayerRootRegistry.Register(_player, _root); _root.ActionPoints = 20;
            _base = new BuildingData { Owner = _player, Hex = Site, IsBase = true };
            _base.Abilities.Add(UnitAbilities.Barracks); BuildingRegistry.Register(Site, _base);
            // Intel + Research already stand in the two open slots: the Base is full at level 1.
            _base.FacilitySlots[0] = FacilityData.FromDefinition(new CardDefinition
                { cardType = CardType.Facility, authoredKey = "intel", displayName = "Intel",
                  grantedAbilities = new List<string> { "Intel" } });
            _base.FacilitySlots[1] = FacilityData.FromDefinition(new CardDefinition
                { cardType = CardType.Facility, authoredKey = "lab", displayName = "Lab",
                  grantedAbilities = new List<string> { UnitAbilities.Research } });
            ArmyRegistry.Register(new ArmyData { Owner = _player, Hex = Site, IsGarrison = true });
            _hand = new AiHandData(null, default, 0);
            _facility = new CardData(new CardDefinition
            {
                cardType = CardType.Facility, authoredKey = "test-factory", displayName = "Factory",
                apCost = 2, resourceCost = new ResourceCost(human: 1, tech: 1),
                grantedAbilities = new List<string> { UnitAbilities.Production },
            });
            _operator = new CardData(new CardDefinition
            {
                cardType = CardType.Hero, authoredKey = "test-assembler", commandRating = 6,
                requiredBuildingAbility = UnitAbilities.Barracks,
                grantedAbilities = new List<string> { UnitAbilities.Assembler },
            });
            _hand.AddCard(_facility); _hand.AddCard(_operator);
            var output = new CardDefinition
                { cardType = CardType.Equipment, authoredKey = "test-output", resourceCost = new ResourceCost(tech: 99) };
            _cards = ScriptableObject.CreateInstance<FactionCardCatalog>(); _cards.cards.Add(output);
            _catalog = ScriptableObject.CreateInstance<ResearchProductionCatalog>(); _catalog.cardCatalogs.Add(_cards);
            _catalog.productionCards.Add(new ResearchProductionEntry { cardKey = output.authoredKey });
            _config = ScriptableObject.CreateInstance<GameConfig>();
            _config.baseUpgradeTiers = new[]
            {
                new BaseUpgradeTier { apCost = 2, cost = new ResourceCost(1, 1, 1, 1), defenseGain = 1, resistanceGain = 1 },
                new BaseUpgradeTier { apCost = 4, cost = new ResourceCost(2, 2, 2, 2), defenseGain = 1, resistanceGain = 1 },
                new BaseUpgradeTier { apCost = 6, cost = new ResourceCost(4, 4, 4, 4), defenseGain = 1, resistanceGain = 1 },
            };
            _ctx = new AiTurnContext { TurnNumber = 2, ResearchProductionCatalog = _catalog, GameConfig = _config };
            _snapshot = new WorldSnapshot
            {
                Observer = _player, TurnNumber = 2,
                Self = new SelfSnapshot { BaseHexes = new[] { Site }, Armies = Array.Empty<ArmySnapshot>(),
                    Hand = _hand.Hand, Deck = Array.Empty<CardDefinition>() },
                Development = new DevelopmentReadiness(),
                Economy = new EconomyStanding { PerType = Array.Empty<EconomyResourceStanding>() },
            };
            foreach (ResourceType t in ResourceBundle.All) _root.AddResource(t, 5);
            OpenWindow();
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear(); BuildingRegistry.Clear(); PlayerRootRegistry.Clear();
            MissionIntentRegistry.Clear(); DevelopmentInvestmentGate.Clear();
            StrategicResourceReservationLedger.ClearAll(); ResourceStarvationRegistry.Clear();
            if (_catalog != null) UnityEngine.Object.DestroyImmediate(_catalog);
            if (_cards != null) UnityEngine.Object.DestroyImmediate(_cards);
            if (_config != null) UnityEngine.Object.DestroyImmediate(_config);
            if (_root != null) UnityEngine.Object.DestroyImmediate(_root.gameObject);
        }

        // Surplus of every resource over the threshold for the required consecutive turns.
        private void OpenWindow()
        {
            foreach (ResourceType t in ResourceBundle.All)
                _snapshot.Development.InvestmentSurplusByType.Add(t, 1f);
            for (int turn = 0; turn <= AiConfigV2.devInvestmentSurplusTurns; turn++)
            {
                _snapshot.TurnNumber = _ctx.TurnNumber - AiConfigV2.devInvestmentSurplusTurns + turn;
                DevelopmentInvestmentGate.Observe(_player, _snapshot);
            }
            _snapshot.TurnNumber = _ctx.TurnNumber;
        }

        private List<DevelopmentOpportunity> Admitted() => DevelopmentOpportunityEvaluator.Enumerate(
            _snapshot, _player, _root, _hand, _ctx, null);
        private List<DevelopmentOpportunity> Facts() => DevelopmentOpportunityEvaluator.PreparationFacts(
            _snapshot, _player, _root, _hand, _ctx, null);

        private AxisDemand DemandFor(DevelopmentOpportunity op) => new AxisDemand
        {
            RequestingAxis = DesireAxis.Development, Capability = CapabilityKind.DevelopmentInfrastructure,
            DesiredAmount = 1, TargetHex = op.FacilityHex, DevelopmentOperatorMode = op.Mode, DevOpportunity = op,
            Value = op.PreparationCardScore ?? 0f,
        };

        private int[] Bank() => new[]
        {
            _root.ActionPoints, _root.GetResource(ResourceType.Human), _root.GetResource(ResourceType.Energy),
            _root.GetResource(ResourceType.Materials), _root.GetResource(ResourceType.Tech),
        };

        private InfraFulfillResult Fulfill(DevelopmentOpportunity op) => InfrastructureFulfillment.TryFulfill(
            _snapshot, _player, _root, _hand, _ctx, DemandFor(op), PhaseAApBudget.Create(_root));

        private DevelopmentOpportunity CapacityStep()
        {
            DevelopmentOpportunity step = Admitted().SingleOrDefault(
                o => o.PreparationKind == DevelopmentPreparationKind.CapacityUnlock);
            Assert.That(step, Is.Not.Null, "a full Base with a confirmed Facility card offers CapacityUnlock");
            return step;
        }

        // ---- B01 / enumeration -------------------------------------------------------------------------
        [Test]
        public void FullBaseOffersTheTierAloneAndNotTheFacility()
        {
            var admitted = Admitted();
            Assert.That(admitted.Any(o => o.PreparationKind == DevelopmentPreparationKind.Facility), Is.False,
                "no executable Facility step on a full site before a slot opens");
            DevelopmentOpportunity step = CapacityStep();
            Assert.That(step.PreparationCapacityTier, Is.SameAs(_config.baseUpgradeTiers[0]));
            Assert.That(step.PreparationFacilityCard, Is.SameAs(_facility));
            Assert.That(step.StageResourceCost.human, Is.EqualTo(1));
            Assert.That(step.StageResourceCost.energy, Is.EqualTo(1));
            Assert.That(step.StageResourceCost.materials, Is.EqualTo(1));
            Assert.That(step.StageResourceCost.tech, Is.EqualTo(1), "only the tier bill - not the Factory's 1H/1T on top");
            Assert.That(step.Card, Is.Null);
            Assert.That(step.RecipientCard, Is.Null);
            Assert.That(step.Generation, Is.Null);
            float expected = DevelopmentPreparationScorer.CapacityUnlock(_config.baseUpgradeTiers[0], _snapshot,
                t => StrategicSpendability.SpendableAmount(_player, _root, _ctx, t), _player);
            Assert.That(step.PreparationCardScore.Value, Is.EqualTo(expected).Within(1e-4f));
            Assert.That(step.PreparationRank, Is.EqualTo(expected).Within(1e-4f));
            Assert.That(step.WorldTaskScore.Value, Is.Zero, "no second value on the world-task scale");
        }

        [Test]
        public void OperatorStaysAnEqualVariantAndTheWorldIsNotMutatedByLooking()
        {
            int[] before = Bank();
            int level = _base.Level;
            var kinds = Admitted().Select(o => o.PreparationKind).ToList();
            Facts(); Admitted(); Admitted();
            Assert.That(kinds, Does.Contain(DevelopmentPreparationKind.Operator));
            Assert.That(kinds, Does.Contain(DevelopmentPreparationKind.CapacityUnlock));
            Assert.That(Bank(), Is.EqualTo(before));
            Assert.That(_base.Level, Is.EqualTo(level));
            Assert.That(StrategicResourceReservationLedger.Rows(_player, _ctx.TurnNumber), Is.Empty,
                "enumerating creates no reservation");
        }

        [Test]
        public void TheUnlockDoesNotDependOnTodaysPlacementBill()
        {
            // Exactly the tier's bank: the Factory's own bill (2 AP, 1H, 1T) could not be added on top.
            _root.ActionPoints = _config.baseUpgradeTiers[0].apCost;
            foreach (ResourceType t in ResourceBundle.All)
                _root.AddResource(t, -_root.GetResource(t) + 1);
            DevelopmentOpportunity step = CapacityStep();
            Assert.That(step.StageResourceCost.tech, Is.EqualTo(1));
            InfraFulfillResult r = Fulfill(step);
            Assert.That(r.CapacityUnlocked, Is.True, r.Detail);
        }

        [Test]
        public void FreeSlotMeansOnlyTheFacilityStep()
        {
            _base.Level = 2;   // slot 2 is open
            var kinds = Admitted().Select(o => o.PreparationKind).ToList();
            Assert.That(kinds, Does.Contain(DevelopmentPreparationKind.Facility));
            Assert.That(kinds, Does.Not.Contain(DevelopmentPreparationKind.CapacityUnlock));
        }

        [Test]
        public void FacilityOnlyInTheDeckBuysNoSlotInAdvance()
        {
            _hand.RemoveCard(_facility); _snapshot.Self.Deck = new[] { _facility.Definition };
            Assert.That(Admitted().Any(o => o.PreparationKind == DevelopmentPreparationKind.CapacityUnlock), Is.False);
            Assert.That(Facts().Any(o => o.PreparationKind == DevelopmentPreparationKind.CapacityUnlock), Is.False);
        }

        [Test]
        public void NoOperatorPathMeansNoCapacityStep()
        {
            _hand.RemoveCard(_operator);
            Assert.That(Facts().Any(o => o.PreparationKind == DevelopmentPreparationKind.CapacityUnlock), Is.False);
        }

        [Test]
        public void ExhaustedTiersOrFullyOpenBaseHaveNoCapacityStep()
        {
            _base.Level = 4;   // all five slots unlocked: fill every one
            for (int i = 0; i < _base.FacilitySlots.Length; i++)
                if (_base.FacilitySlots[i] == null)
                    _base.FacilitySlots[i] = FacilityData.FromDefinition(new CardDefinition
                        { cardType = CardType.Facility, authoredKey = "f" + i, displayName = "F" + i });
            Assert.That(_base.FindFirstAvailableFacilitySlot(), Is.LessThan(0));
            Assert.That(Facts().Any(o => o.PreparationKind == DevelopmentPreparationKind.CapacityUnlock), Is.False,
                "no tier left: the site has no facility-capacity path");
        }

        // ---- B02 / B05 / B06 / B07 end to end ----------------------------------------------------------
        [Test]
        public void UnlockThenPlacement_PaysEachBillOnce_AndPublishesTwoMutations()
        {
            DevelopmentOpportunity step = CapacityStep();
            int[] before = Bank();
            int version = WorldDeltaLifecycle.Current;
            int levelBefore = _base.Level, unlockedBefore = _base.UnlockedFacilitySlots;
            int defenseBefore = _base.Defense, resistanceBefore = _base.Resistance;
            var slotsBefore = (FacilityData[])_base.FacilitySlots.Clone();
            int cardsInHand = _hand.Hand.Count;

            InfraFulfillResult unlock = Fulfill(step);

            Assert.That(unlock.CapacityUnlocked, Is.True, unlock.Detail);
            Assert.That(unlock.Built, Is.False, "no facility exists yet");
            Assert.That(unlock.CardPlayed, Is.False);
            Assert.That(unlock.AdditionalCardsConsumed, Is.Zero);
            Assert.That(unlock.Generated || unlock.GenerationAttempted, Is.False);
            Assert.That(unlock.Outcome.Succeeded, Is.True);
            Assert.That(unlock.Outcome.Created, Is.False, "a level is not a new building");
            Assert.That(unlock.Outcome.Played, Is.False);
            Assert.That(unlock.Outcome.NeedsReplan, Is.True);
            Assert.That(_base.Level, Is.EqualTo(levelBefore + 1));
            Assert.That(_base.UnlockedFacilitySlots, Is.EqualTo(unlockedBefore + 1));
            Assert.That(_base.Defense, Is.EqualTo(defenseBefore + 1));
            Assert.That(_base.Resistance, Is.EqualTo(resistanceBefore + 1));
            Assert.That(_base.FacilitySlots, Is.EqualTo(slotsBefore), "filled slots keep their indices and content");
            Assert.That(_hand.Hand.Count, Is.EqualTo(cardsInHand));
            Assert.That(_hand.Hand, Does.Contain(_facility), "the same card instance waits in hand");
            BaseUpgradeTier tier = _config.baseUpgradeTiers[0];
            int[] after = Bank();
            Assert.That(before[0] - after[0], Is.EqualTo(tier.apCost));
            Assert.That(before[1] - after[1], Is.EqualTo(tier.cost.human));
            Assert.That(before[2] - after[2], Is.EqualTo(tier.cost.energy));
            Assert.That(before[3] - after[3], Is.EqualTo(tier.cost.materials));
            Assert.That(before[4] - after[4], Is.EqualTo(tier.cost.tech));
            Assert.That(unlock.ApSpent, Is.EqualTo(2f));
            Assert.That(unlock.StateVersionAfter, Is.EqualTo(version + 1), "exactly one world receipt");
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(version + 1));

            // Refreshed world: the tier is gone, the Facility is a NEW action with its own bill.
            var next = Admitted();
            Assert.That(next.Any(o => o.PreparationKind == DevelopmentPreparationKind.CapacityUnlock), Is.False,
                "no repeat purchase for the same witness");
            DevelopmentOpportunity placement = next.SingleOrDefault(o => o.PreparationKind == DevelopmentPreparationKind.Facility);
            Assert.That(placement, Is.Not.Null);
            Assert.That(placement.StageResourceCost.human, Is.EqualTo(1));
            Assert.That(placement.StageResourceCost.energy, Is.Zero, "no tier bill left inside the placement");
            Assert.That(placement.StageResourceCost.materials, Is.Zero);

            int[] beforePlacement = Bank();
            InfraFulfillResult placed = Fulfill(placement);
            Assert.That(placed.Built, Is.True, placed.Detail);
            Assert.That(placed.CapacityUnlocked, Is.False);
            int[] afterPlacement = Bank();
            Assert.That(beforePlacement[0] - afterPlacement[0], Is.EqualTo(_facility.EffectivePlayApCost));
            Assert.That(beforePlacement[1] - afterPlacement[1], Is.EqualTo(_facility.EffectivePlayResourceCost.human));
            Assert.That(beforePlacement[3] - afterPlacement[3], Is.EqualTo(_facility.EffectivePlayResourceCost.materials));
            Assert.That(placed.StateVersionAfter, Is.EqualTo(version + 2), "the placement is its own mutation");
            Assert.That(_base.FacilitySlots[2], Is.Not.Null);
            Assert.That(_hand.Hand, Does.Not.Contain(_facility));
        }

        // ---- B16 / B17 stale plans ------------------------------------------------------------------------
        [Test]
        public void StalePlansBuyNothing()
        {
            DevelopmentOpportunity step = CapacityStep();
            int[] before = Bank();
            int level = _base.Level;
            int version = WorldDeltaLifecycle.Current;

            _hand.RemoveCard(_facility);                                   // the witness left the hand
            InfraFulfillResult noCard = Fulfill(step);
            _hand.AddCard(_facility);
            _base.Level = level + 1;                                       // the level moved on / a slot opened
            InfraFulfillResult moved = Fulfill(step);
            _base.Level = level;
            _base.Owner = new PlayerSetupData();                           // someone else owns the Base
            InfraFulfillResult foreign = Fulfill(step);
            _base.Owner = _player;

            foreach (InfraFulfillResult r in new[] { noCard, moved, foreign })
            {
                Assert.That(r.CapacityUnlocked, Is.False);
                Assert.That(r.Built, Is.False);
                Assert.That(r.StateChanged, Is.False);
            }
            Assert.That(Bank(), Is.EqualTo(before));
            Assert.That(_base.Level, Is.EqualTo(level));
            Assert.That(WorldDeltaLifecycle.Current, Is.EqualTo(version), "a refusal bumps nothing");
        }

        [Test]
        public void ClosedInvestmentWindowBlocksTheStepAlone()
        {
            DevelopmentInvestmentGate.Clear();
            Assert.That(Admitted().Any(o => o.PreparationKind == DevelopmentPreparationKind.CapacityUnlock), Is.False);
        }

        // ---- R01-R05 bank --------------------------------------------------------------------------------
        [Test]
        public void ForeignDeferredEconomyHoldBlocksTheUnlockAndSurvivesUntouched()
        {
            DevelopmentOpportunity step = CapacityStep();
            // An Economy build holds all the Materials: raw stock suffices, the free amount does not.
            StrategicResourceReservationLedger.Upsert(_player, _ctx.TurnNumber, new StrategicResourceReservation
            {
                Owner = "economy-build", Reason = StrategicReservationReason.EconomyDeferredBuild,
                Resource = StrategicReservedResource.Materials, Amount = _root.GetResource(ResourceType.Materials),
                ExpirationStage = StrategicReservationExpiry.EndOfTurn,
            });
            string rows = StrategicResourceReservationLedger.DebugLine(_player, _ctx.TurnNumber);
            int[] before = Bank();
            InfraFulfillResult r = Fulfill(step);
            Assert.That(r.CapacityUnlocked, Is.False);
            Assert.That(r.StateChanged, Is.False);
            Assert.That(Bank(), Is.EqualTo(before), "nothing paid");
            Assert.That(StrategicResourceReservationLedger.DebugLine(_player, _ctx.TurnNumber), Is.EqualTo(rows),
                "the foreign row keeps owner, reason, resource and amount");
            Assert.That(InfrastructureFulfillment.SpendAuthorityFor(DemandFor(step)).Owner, Is.Null);
            Assert.That(InfrastructureFulfillment.SpendAuthorityFor(DemandFor(step)).EconomyCompletesNow, Is.False,
                "default authority: no Economy completion bypass");
        }

        [Test]
        public void SuccessfulPaymentLeavesForeignRowsUnchangedAndReportsTheRealSpend()
        {
            StrategicResourceReservationLedger.Upsert(_player, _ctx.TurnNumber, new StrategicResourceReservation
            {
                Owner = "economy-build", Reason = StrategicReservationReason.EconomyDeferredBuild,
                Resource = StrategicReservedResource.Tech, Amount = 2f,
                ExpirationStage = StrategicReservationExpiry.EndOfTurn,
            });
            string rows = StrategicResourceReservationLedger.DebugLine(_player, _ctx.TurnNumber);
            DevelopmentOpportunity step = CapacityStep();
            int[] before = Bank();
            InfraFulfillResult r = Fulfill(step);
            Assert.That(r.CapacityUnlocked, Is.True, r.Detail);
            int[] after = Bank();
            ResourceCost bill = _config.baseUpgradeTiers[0].cost;
            Assert.That(r.ResourcesSpent.human, Is.EqualTo(before[1] - after[1]));
            Assert.That(r.ResourcesSpent.tech, Is.EqualTo(before[4] - after[4]));
            Assert.That(before[4] - after[4], Is.EqualTo(bill.tech));
            Assert.That(StrategicResourceReservationLedger.DebugLine(_player, _ctx.TurnNumber), Is.EqualTo(rows));
        }

        [Test]
        public void ExecutorRefusesWhenTheSharedApPoolIsShort()
        {
            DevelopmentOpportunity step = CapacityStep();
            _root.ActionPoints = _config.baseUpgradeTiers[0].apCost - 1;
            int[] before = Bank();
            Assert.That(Fulfill(step).CapacityUnlocked, Is.False);
            Assert.That(Bank(), Is.EqualTo(before));
            Assert.That(_base.Level, Is.EqualTo(1));
        }

        // ---- gameplay primitive ----------------------------------------------------------------------------
        [Test]
        public void UpgradePrimitiveIsAtomic()
        {
            BaseUpgradeTier[] tiers = _config.baseUpgradeTiers;
            int[] before = Bank();
            BaseUpgradeOutcome stale = InfrastructureActions.TryUpgradeBase(_base, tiers, expectedLevel: 3);
            Assert.That(stale.Ok, Is.False);
            Assert.That(Bank(), Is.EqualTo(before));
            _root.ActionPoints = 1;
            Assert.That(InfrastructureActions.TryUpgradeBase(_base, tiers).Ok, Is.False);
            Assert.That(_base.Level, Is.EqualTo(1));
            _root.ActionPoints = 20;
            BaseUpgradeOutcome ok = InfrastructureActions.TryUpgradeBase(_base, tiers, expectedLevel: 1);
            Assert.That(ok.Ok, Is.True);
            Assert.That(ok.LevelBefore, Is.EqualTo(1));
            Assert.That(ok.LevelAfter, Is.EqualTo(2));
            Assert.That(ok.UnlockedSlotsAfter, Is.EqualTo(3));
            Assert.That(ok.ApSpent, Is.EqualTo(2));
        }

        [Test]
        public void HumanUpgradeStillBuysAnyLegalLevelWithoutAWitness()
        {
            _hand.RemoveCard(_facility);
            for (int level = 1; level <= 3; level++)
                Assert.That(InfrastructureActions.TryUpgradeBase(_base, _config.baseUpgradeTiers).Ok, Is.True, "level " + level);
            Assert.That(_base.Level, Is.EqualTo(4));
            Assert.That(InfrastructureActions.TryUpgradeBase(_base, _config.baseUpgradeTiers).Ok, Is.False,
                "no tier past the last one");
        }

        // ---- Phase B ------------------------------------------------------------------------------------------
        [Test]
        public void PhaseBCarriesTheSameStepFullyPricedOnce()
        {
            DevelopmentOpportunity step = CapacityStep();
            var candidates = StrategicMaintenancePolicy.EnumerateCandidates(_player, _root, _hand, _ctx, _snapshot);
            StrategicSpendCandidate unlock = candidates.SingleOrDefault(c => c.FullyPriced);
            Assert.That(unlock, Is.Not.Null);
            Assert.That(unlock.Utility, Is.EqualTo(step.PreparationCardScore.Value).Within(1e-4f),
                "the canonical net score, not a second AP subtraction");
            Assert.That(unlock.ApCost, Is.EqualTo(_config.baseUpgradeTiers[0].apCost));
            Assert.That(unlock.ResCost.human, Is.EqualTo(_config.baseUpgradeTiers[0].cost.human));

            int[] before = Bank();
            int level = _base.Level;
            bool ok = unlock.Execute(_player, _root, _ctx, out bool changed, out bool progressed);
            Assert.That(ok && changed && progressed, Is.True);
            Assert.That(_base.Level, Is.EqualTo(level + 1));
            Assert.That(before[0] - Bank()[0], Is.EqualTo(_config.baseUpgradeTiers[0].apCost));
            // The step is gone from BOTH phases; a second run buys nothing.
            Assert.That(Admitted().Any(o => o.PreparationKind == DevelopmentPreparationKind.CapacityUnlock), Is.False);
            Assert.That(StrategicMaintenancePolicy.EnumerateCandidates(_player, _root, _hand, _ctx, _snapshot)
                .Any(c => c.FullyPriced), Is.False);
            bool again = unlock.Execute(_player, _root, _ctx, out _, out _);
            Assert.That(again, Is.False, "a stale candidate is refused by its live re-check");
            Assert.That(_base.Level, Is.EqualTo(level + 1));
        }

        [Test]
        public void NonDevelopmentCapacityUpgradeKeepsItsOwnPricing()
        {
            _hand.RemoveCard(_facility);
            var other = new CardData(new CardDefinition
            {
                cardType = CardType.Facility, authoredKey = "apbonus", displayName = "Relay",
                grantedAbilities = new List<string> { UnitAbilities.ApBonus },
            });
            _hand.AddCard(other);
            var candidates = StrategicMaintenancePolicy.EnumerateCandidates(_player, _root, _hand, _ctx, _snapshot);
            Assert.That(candidates.Any(c => c.FullyPriced), Is.False,
                "only a Development CapacityUnlock is fully priced; the rest keep utility - AP and the marginal resource cost");
        }

        // ---- trace invariants --------------------------------------------------------------------------------------
        [Test]
        public void IndependentStampSeesTheLevelChangeThatCountsCannot()
        {
            V2InfraWorldStamp before = AiV2Trace.InfraStamp(_player, _root);
            InfrastructureActions.TryUpgradeBase(_base, _config.baseUpgradeTiers);
            V2InfraWorldStamp after = AiV2Trace.InfraStamp(_player, _root);
            Assert.That(after.OwnedBuildings, Is.EqualTo(before.OwnedBuildings));
            Assert.That(after.FilledFacilitySlots, Is.EqualTo(before.FilledFacilitySlots));
            Assert.That(after.BaseLevelSum, Is.EqualTo(before.BaseLevelSum + 1));
            Assert.That(after.UnlockedFacilitySlots, Is.EqualTo(before.UnlockedFacilitySlots + 1));
            Assert.That(before.SameAs(after), Is.False);
        }
    }

    // Pure arithmetic of the step score (no Unity runtime objects): runs everywhere.
    public sealed class AiCapacityUnlockScoreTests
    {
        private static BaseUpgradeTier Tier(int ap, int each) => new BaseUpgradeTier
            { apCost = ap, cost = new ResourceCost(each, each, each, each) };

        [Test]
        public void ScoreIsStructuralValueMinusTheTierBillOnTheOnePriceTable()
        {
            var snap = new WorldSnapshot { Self = new SelfSnapshot
                { Hand = Array.Empty<CardData>(), Deck = Array.Empty<CardDefinition>() } };
            BaseUpgradeTier tier = Tier(2, 1);
            float expected = AiConfigV2.nonCombatFacilityValue
                - ActionPrice.ToCardScore(2f)
                - StrategicCardEvaluator.StrategicResourceCostValue(tier.cost, snap, null, null);
            Assert.That(DevelopmentPreparationScorer.CapacityUnlock(tier, snap, null, null),
                Is.EqualTo(expected).Within(1e-5f));
            Assert.That(DevelopmentPreparationScorer.CapacityUnlock(tier, snap, null, null), Is.GreaterThan(0f),
                "the cheapest tier of the shipped config (2 AP, 1/1/1/1) is worth buying at neutral scarcity");
        }

        [Test]
        public void DearerTiersScoreLowerAndNothingIsClampedUp()
        {
            var snap = new WorldSnapshot { Self = new SelfSnapshot
                { Hand = Array.Empty<CardData>(), Deck = Array.Empty<CardDefinition>() } };
            float first = DevelopmentPreparationScorer.CapacityUnlock(Tier(2, 1), snap, null, null);
            float second = DevelopmentPreparationScorer.CapacityUnlock(Tier(4, 2), snap, null, null);
            float third = DevelopmentPreparationScorer.CapacityUnlock(Tier(6, 4), snap, null, null);
            Assert.That(second, Is.LessThan(first));
            Assert.That(third, Is.LessThan(second));
            Assert.That(third, Is.LessThan(0f), "a tier dearer than the structural value is a signed loss");
            Assert.That(DevelopmentPreparationScorer.CapacityUnlock(null, snap, null, null), Is.EqualTo(float.NegativeInfinity));
        }

        [Test]
        public void StepScoreDoesNotReadTheFacilityCardOrAnOutput()
        {
            // The signature takes the tier only: the placement bill, the card and any output cannot enter.
            var m = typeof(DevelopmentPreparationScorer).GetMethod("CapacityUnlock",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
            Assert.That(m.GetParameters().Select(p => p.ParameterType),
                Does.Not.Contain(typeof(CardData)));
        }
    }
}
#endif
