#if UNITY_INCLUDE_TESTS
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
    }
}
#endif
