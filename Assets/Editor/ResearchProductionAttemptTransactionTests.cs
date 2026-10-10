#if UNITY_INCLUDE_TESTS
using Game.Ai;
using Game.Ai.V2;
using Game.Cards;
using Game.Economy;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class ResearchProductionAttemptTransactionTests
    {
        private static readonly HexCoord Site = new HexCoord(73, -11);
        private PlayerSetupData _player;
        private PlayerRoot _root;
        private UnitData _hero;
        private ResearchProductionCatalog _catalog;
        private FactionCardCatalog _factionCatalog;
        private CardDefinition _card;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            PlayerRootRegistry.Clear();
            StealthSystem.Clear();

            DevelopmentDiversity.ClearAll();

            _player = new PlayerSetupData();
            _root = PlayerRoot.Create(_player, "rp transaction owner");
            PlayerRootRegistry.Register(_player, _root);

            var building = new BuildingData { Hex = Site, Owner = _player };
            var researchFacility = new FacilityData();
            researchFacility.Abilities.Add(UnitAbilities.Research);
            building.FacilitySlots[0] = researchFacility;
            BuildingRegistry.Register(Site, building);

            _hero = new UnitData { Owner = _player, IsHero = true };
            _hero.Abilities.Add(UnitAbilities.Researcher);
            _hero.Abilities.Add(UnitAbilities.Stealth4);
            var army = new ArmyData { Owner = _player, Hex = Site };
            army.Members.Add(_hero);
            ArmyRegistry.Register(army);

            _card = new CardDefinition
            {
                authoredKey = "test-rp-card",
                displayName = "Test RP Card",
                apCost = 2,
                resourceCost = new ResourceCost { human = 1 },
            };
            _factionCatalog = ScriptableObject.CreateInstance<FactionCardCatalog>();
            _factionCatalog.cards.Add(_card);
            _catalog = ScriptableObject.CreateInstance<ResearchProductionCatalog>();
            _catalog.cardCatalogs.Add(_factionCatalog);
            _catalog.researchCards.Add(new ResearchProductionEntry
            {
                cardKey = _card.authoredKey,
                factionRestriction = Faction.None,
            });
        }

        [TearDown]
        public void TearDown()
        {
            DevelopmentDiversity.ClearAll();
            StealthSystem.Clear();
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            PlayerRootRegistry.Clear();
            if (_catalog != null) Object.DestroyImmediate(_catalog);
            if (_factionCatalog != null) Object.DestroyImmediate(_factionCatalog);
            if (_root != null) Object.DestroyImmediate(_root.gameObject);
        }

        private void HideHeroWithoutChangingFinalBudget()
        {
            _root.ActionPoints = 10;
            Assert.That(StealthSystem.TryEnterStealth(_hero, _root), Is.True);
            _root.ActionPoints = 5;
        }

        [TestCase(AttachmentSlot.Equipment, 0, true)]
        [TestCase(AttachmentSlot.Equipment, 6, false)]
        [TestCase(AttachmentSlot.Mutator, 0, true)]
        [TestCase(AttachmentSlot.Mutator, 6, false)]
        public void PaidAiAttachmentAttemptEntersHistoryOnceOnWinOrLoss(AttachmentSlot slot, int required, bool won)
        {
            _card.cardType = CardType.Equipment;
            _card.attachmentSlot = slot;
            _card.fate = required;
            _hero.Fate = 0;
            _root.ActionPoints = 5;
            _root.AddResource(ResourceType.Human, 2);
            var ctx = new AiTurnContext { TurnNumber = 8, ResearchProductionCatalog = _catalog };
            var hand = new AiHandData(null, default, 0);
            var outcome = MaterializationExecutor.TryGenerate(new GenerationStep
            {
                CardDef = _card, Hero = _hero, FacilityHex = Site,
                Mode = ResearchProductionMode.Research, ProducesEquipment = true,
            }, _player, _root, hand, ctx);
            Assert.That(outcome.Attempted, Is.True, outcome.FailReason);
            Assert.That(outcome.Success, Is.EqualTo(won));
            Assert.That(DevelopmentDiversity.RecentAttempts(_player, 8, _card), Is.EqualTo(1));
            Assert.That(_root.ActionPoints, Is.EqualTo(3));
            Assert.That(_root.GetResource(ResourceType.Human), Is.EqualTo(1));
        }

        [Test]
        public void RejectedAiAttachmentAttemptDoesNotEnterHistory()
        {
            _card.cardType = CardType.Equipment;
            _root.ActionPoints = 1;
            _root.AddResource(ResourceType.Human, 2);
            var outcome = MaterializationExecutor.TryGenerate(new GenerationStep
            {
                CardDef = _card, Hero = _hero, FacilityHex = Site,
                Mode = ResearchProductionMode.Research, ProducesEquipment = true,
            }, _player, _root, new AiHandData(null, default, 0),
                new AiTurnContext { TurnNumber = 8, ResearchProductionCatalog = _catalog });
            Assert.That(outcome.Attempted, Is.False);
            Assert.That(DevelopmentDiversity.RecentAttempts(_player, 8, _card), Is.Zero);
            Assert.That(_root.ActionPoints, Is.EqualTo(1));
        }

        [Test]
        [TestCase(AttachmentSlot.Equipment, false)]
        [TestCase(AttachmentSlot.Mutator, false)]
        [TestCase(AttachmentSlot.Equipment, true)]
        [TestCase(AttachmentSlot.Mutator, true)]
        public void StaleGeneratedAttachmentRejectsBeforePaymentOrHistory(AttachmentSlot slot, bool removeHost)
        {
            _card.cardType = CardType.Equipment;
            _card.attachmentSlot = slot;
            _card.equipment = AttachmentSlotTests.Attachment(slot).equipment;
            var body = new CardData(AttachmentSlotTests.Host());
            var hand = new AiHandData(null, default, 0);
            if (!removeHost) hand.AddCard(body);
            if (slot == AttachmentSlot.Equipment) body.Equipment = _card;
            else body.Mutator = _card;
            _root.ActionPoints = 5;
            _root.AddResource(ResourceType.Human, 2);
            var plan = new MaterializationPlan
            {
                Kind = MaterializationChainKind.GenerateAttachDeploy,
                BaseCardInHand = body, GeneratedEquipmentDef = _card,
                ApCost = 2, ResCost = _card.resourceCost,
                Generation = new GenerationStep { CardDef = _card, Hero = _hero, FacilityHex = Site,
                    Mode = ResearchProductionMode.Research, ProducesEquipment = true },
            };
            var result = MaterializationExecutor.Execute(null, _player, _root, hand,
                new AiTurnContext { TurnNumber = 8, ResearchProductionCatalog = _catalog }, plan, null);
            Assert.That(result.GenerationAttempted, Is.False);
            Assert.That(result.StateChanged, Is.False);
            Assert.That(result.PlacementStale, Is.True, result.FailReason);
            Assert.That(_root.ActionPoints, Is.EqualTo(5));
            Assert.That(_root.GetResource(ResourceType.Human), Is.EqualTo(2));
            Assert.That(DevelopmentDiversity.RecentAttempts(_player, 8, _card), Is.Zero);
        }

        [Test]
        public void ExpectedCostCannotFundAnUnaffordableSuccessfulChain()
        {
            _card.cardType = CardType.Equipment;
            _root.ActionPoints = 3;
            _root.AddResource(ResourceType.Human, 2);
            var plan = new MaterializationPlan
            {
                Kind = MaterializationChainKind.GenerateAttachDeploy, ApCost = 7,
                ResCost = _card.resourceCost,
                Generation = new GenerationStep { CardDef = _card, Hero = _hero, FacilityHex = Site,
                    Mode = ResearchProductionMode.Research, ProducesEquipment = true, SuccessChance = 0 },
            };
            var result = MaterializationExecutor.Execute(null, _player, _root, new AiHandData(null, default, 0),
                new AiTurnContext { TurnNumber = 8, ResearchProductionCatalog = _catalog }, plan, null);
            Assert.That(result.GenerationAttempted, Is.False);
            Assert.That(result.StateChanged, Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(3));
            Assert.That(_root.GetResource(ResourceType.Human), Is.EqualTo(2));
            Assert.That(DevelopmentDiversity.RecentAttempts(_player, 8, _card), Is.Zero);
        }

        [Test]
        public void GeneratedNonCombatAlsoRequiresTheFullChainBeforePayment()
        {
            _root.ActionPoints = 3;
            _root.AddResource(ResourceType.Human, 2);
            var play = new NonCombatCardPlayer.NonCombatPlay
            {
                Kind = NonCombatCardPlayer.PlayKind.Facility,
                Card = new CardData(_card) { ResearchProductionCreated = true },
                ApCost = 7, ResCost = _card.resourceCost,
                Generation = new GenerationStep { CardDef = _card, Hero = _hero, FacilityHex = Site,
                    Mode = ResearchProductionMode.Research, SuccessChance = 0 },
            };
            var result = NonCombatCardPlayer.Execute(play, null, _player, _root,
                new AiHandData(null, default, 0),
                new AiTurnContext { TurnNumber = 8, ResearchProductionCatalog = _catalog });
            Assert.That(result.GenerationAttempted, Is.False);
            Assert.That(result.StateChanged, Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(3));
            Assert.That(_root.GetResource(ResourceType.Human), Is.EqualTo(2));
        }

        [Test]
        [TestCase(AttachmentSlot.Equipment)]
        [TestCase(AttachmentSlot.Mutator)]
        public void RecipientAttachmentIdentityIsAnExactStableAdmissionInput(AttachmentSlot slot)
        {
            var first = AttachmentSlotTests.Attachment(slot);
            var second = AttachmentSlotTests.Attachment(slot);
            first.authoredKey = "attachment-first"; second.authoredKey = "attachment-second";
            if (slot == AttachmentSlot.Equipment) _hero.Equipment = first;
            else _hero.Mutator = first;
            string initial = DevelopmentAdmission.RecipientFacts(_player, null);
            Assert.That(DevelopmentAdmission.RecipientFacts(_player, null), Is.EqualTo(initial));
            if (slot == AttachmentSlot.Equipment) _hero.Equipment = second;
            else _hero.Mutator = second;
            Assert.That(DevelopmentAdmission.RecipientFacts(_player, null), Is.Not.EqualTo(initial),
                "different attachment identity must not disappear behind equal stats and occupancy");
        }

        [Test]
        public void FreeLostAttachmentAttemptStillInvalidatesTheDecisionSnapshot()
        {
            _card.cardType = CardType.Equipment;
            _card.apCost = 0;
            _card.resourceCost = null;
            _card.fate = 6;
            _hero.Fate = 0;
            var outcome = MaterializationExecutor.TryGenerate(new GenerationStep
            {
                CardDef = _card, Hero = _hero, FacilityHex = Site,
                Mode = ResearchProductionMode.Research, ProducesEquipment = true,
            }, _player, _root, new AiHandData(null, default, 0),
                new AiTurnContext { TurnNumber = 8, ResearchProductionCatalog = _catalog });
            Assert.That(outcome.Attempted, Is.True, outcome.FailReason);
            Assert.That(outcome.Success, Is.False);
            Assert.That(outcome.StateChanged, Is.True,
                "Repeat history is a decision input even when no physical resource was spent");
            Assert.That(DevelopmentDiversity.RecentAttempts(_player, 8, _card), Is.EqualTo(1));
        }

        [Test]
        public void ResearchAttemptRevalidatesRevealsAndPaysAsOneCommit()
        {
            HideHeroWithoutChangingFinalBudget();
            _root.AddResource(ResourceType.Human, 2);

            bool started = ResearchProductionSystem.TryStartAttempt(
                _player, _root, _hero, Site, ResearchProductionMode.Research,
                _card, _catalog, out string reason);

            Assert.That(started, Is.True, reason);
            Assert.That(_hero.IsHidden, Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(3));
            Assert.That(_root.GetResource(ResourceType.Human), Is.EqualTo(1));
        }

        [Test]
        public void UnaffordableAttemptDoesNotPartiallyRevealOrPay()
        {
            HideHeroWithoutChangingFinalBudget();
            _root.ActionPoints = 1;
            _root.AddResource(ResourceType.Human, 2);

            bool started = ResearchProductionSystem.TryStartAttempt(
                _player, _root, _hero, Site, ResearchProductionMode.Research,
                _card, _catalog, out _);

            Assert.That(started, Is.False);
            Assert.That(_hero.IsHidden, Is.True,
                "AI/headless Research must not reveal before the shared attempt transaction commits.");
            Assert.That(_root.ActionPoints, Is.EqualTo(1));
            Assert.That(_root.GetResource(ResourceType.Human), Is.EqualTo(2));
        }

        [Test]
        public void AlternateSameOwnerRootCannotFundAttempt()
        {
            _root.ActionPoints = 5;
            _root.AddResource(ResourceType.Human, 2);
            PlayerRoot alternate = PlayerRoot.Create(_player, "noncanonical rp root");
            alternate.ActionPoints = 5;
            alternate.AddResource(ResourceType.Human, 2);
            try
            {
                bool started = ResearchProductionSystem.TryStartAttempt(
                    _player, alternate, _hero, Site, ResearchProductionMode.Research,
                    _card, _catalog, out _);

                Assert.That(started, Is.False);
                Assert.That(alternate.ActionPoints, Is.EqualTo(5));
                Assert.That(alternate.GetResource(ResourceType.Human), Is.EqualTo(2));
                Assert.That(_root.ActionPoints, Is.EqualTo(5));
                Assert.That(_root.GetResource(ResourceType.Human), Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(alternate.gameObject);
            }
        }

        [Test]
        public void RemovedCatalogOfferRejectsBeforePayment()
        {
            _root.ActionPoints = 5;
            _root.AddResource(ResourceType.Human, 2);
            _catalog.researchCards.Clear();

            bool started = ResearchProductionSystem.TryStartAttempt(
                _player, _root, _hero, Site, ResearchProductionMode.Research,
                _card, _catalog, out _);

            Assert.That(started, Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(5));
            Assert.That(_root.GetResource(ResourceType.Human), Is.EqualTo(2));
        }
        [TestCase(ResearchProductionMode.Research)]
        [TestCase(ResearchProductionMode.Production)]
        public void HumanWithoutSelectedLoadoutHasNoOffersAndCannotPay(ResearchProductionMode mode)
        {
            _player.IsHuman = true;
            _catalog.productionCards.Add(new ResearchProductionEntry { cardKey = _card.authoredKey });
            HideHeroWithoutChangingFinalBudget(); _root.AddResource(ResourceType.Human, 2);
            Assert.That(ResearchProductionSystem.OfferedCards(_catalog, mode, _player), Is.Empty);
            Assert.That(ResearchProductionSystem.TryStartAttempt(_player, _root, _hero, Site, mode,
                _card, _catalog, out var reason, out var attempt), Is.False);
            Assert.That(reason, Does.Contain("Select a saved deck")); Assert.That(attempt, Is.Null);
            Assert.That(ResearchProductionSystem.TryStartAttempt(_player, _root, _hero, Site, mode,
                _card, _catalog, out _), Is.False);
            Assert.That(_root.ActionPoints, Is.EqualTo(5));
            Assert.That(_root.GetResource(ResourceType.Human), Is.EqualTo(2));
            Assert.That(_hero.IsHidden, Is.True);
        }

        [TestCase(ResearchProductionMode.Research)]
        [TestCase(ResearchProductionMode.Production)]
        public void UnselectedBlueprintCannotBeOfferedOrPaidInEitherFacility(ResearchProductionMode mode)
        {
            _player.IsHuman = true; _player.Faction = Faction.IronConcord;
            _card.cardType = CardType.Equipment; _card.deckPointCost = 1; _card.deckCopyLimit = 4;
            _catalog.productionCards.Add(new ResearchProductionEntry { cardKey = _card.authoredKey });
            BuildingRegistry.FindAt(Site).FacilitySlots[0].Abilities.Add(UnitAbilities.Production);
            _hero.Abilities.Add(UnitAbilities.Assembler);
            var starting = ScriptableObject.CreateInstance<StartingDeckCatalog>();
            try
            {
                starting.catalogs.Add(_factionCatalog);
                var deck = new Game.Progression.SavedDeck { deckId = "empty", name = "Empty", faction = _player.Faction };
                _player.MatchLoadout = new MatchLoadout(deck, new DeckRules(starting, _catalog), _ => 4);
                _player.BlueprintQuota = new BlueprintQuota(_player.MatchLoadout);
                HideHeroWithoutChangingFinalBudget(); _root.AddResource(ResourceType.Human, 2);
                Assert.That(ResearchProductionSystem.OfferedCards(_catalog, mode, _player), Is.Empty);
                Assert.That(ResearchProductionSystem.IsEligible(_player, Site, mode, out _), Is.True);
                Assert.That(ResearchProductionSystem.TryStartAttempt(_player, _root, _hero, Site, mode,
                    _card, _catalog, out var reason, out var attempt), Is.False);
                Assert.That(reason, Does.Contain("Blueprint is not selected")); Assert.That(attempt, Is.Null);
                Assert.That(_root.ActionPoints, Is.EqualTo(5));
                Assert.That(_root.GetResource(ResourceType.Human), Is.EqualTo(2));
                Assert.That(_hero.IsHidden, Is.True);
            }
            finally { Object.DestroyImmediate(starting); }
        }

        [Test]
        public void HumanBlueprintQuotaRejectsDirectBypassAndDebitsOnlyEachRealAttempt()
        {
            _player.IsHuman = true; _player.Faction = Faction.IronConcord;
            _card.cardType = CardType.Equipment; _card.deckPointCost = 1; _card.deckCopyLimit = 4;
            var starting = ScriptableObject.CreateInstance<StartingDeckCatalog>();
            try
            {
                starting.catalogs.Add(_factionCatalog);
                var deck = new Game.Progression.SavedDeck { deckId = "quota", name = "Quota", faction = _player.Faction };
                deck.equipment.Add(new DeckCardEntry { cardKey = _card.authoredKey, count = 1 });
                _player.MatchLoadout = new MatchLoadout(deck, new DeckRules(starting, _catalog), _ => 1);
                _player.BlueprintQuota = new BlueprintQuota(_player.MatchLoadout);
                _root.ActionPoints = 10; _root.AddResource(ResourceType.Human, 5);
                Assert.That(ResearchProductionSystem.TryStartAttempt(_player, _root, _hero, Site,
                    ResearchProductionMode.Research, _card, _catalog, out _), Is.False, "Untracked human call must not debit.");
                Assert.That(_root.ActionPoints, Is.EqualTo(10));
                Assert.That(ResearchProductionSystem.TryStartAttempt(_player, _root, _hero, Site,
                    ResearchProductionMode.Research, _card, _catalog, out _, out var failed), Is.True);
                Assert.That(ResearchProductionSystem.TryStartAttempt(_player, _root, _hero, Site,
                    ResearchProductionMode.Research, _card, _catalog, out _, out _), Is.False, "Pending duplicate must not debit.");
                failed.Complete(false);
                Assert.That(_player.BlueprintQuota.Remaining(_card.authoredKey), Is.EqualTo(1));
                Assert.That(ResearchProductionSystem.TryStartAttempt(_player, _root, _hero, Site,
                    ResearchProductionMode.Research, _card, _catalog, out _, out var succeeded), Is.True);
                Assert.That(succeeded.Complete(true), Is.Not.Null); Assert.That(succeeded.Complete(true), Is.Null);
                Assert.That(ResearchProductionSystem.TryStartAttempt(_player, _root, _hero, Site,
                    ResearchProductionMode.Research, _card, _catalog, out _, out _), Is.False, "Exhausted quota must not debit.");
                Assert.That(_root.ActionPoints, Is.EqualTo(6));
                Assert.That(_root.GetResource(ResourceType.Human), Is.EqualTo(3));
            }
            finally { Object.DestroyImmediate(starting); }
        }
    }
}
#endif
