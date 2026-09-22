#if UNITY_INCLUDE_TESTS
using Game.Cards;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using NUnit.Framework;
using UnityEngine;

namespace Game.EditorTests
{
    public sealed class AiDeploymentRootIdentityTests
    {
        private PlayerSetupData _player;
        private PlayerRoot _registered;
        private PlayerRoot _alternate;
        private GameObject _controllerObject;
        private HexSelectionController _selector;
        private ArmyData _army;
        private CardDefinition _card;

        [SetUp]
        public void SetUp()
        {
            PlayerRootRegistry.Clear();
            BuildingRegistry.Clear();
            _player = new PlayerSetupData();
            _registered = PlayerRoot.Create(_player, "registered deployment account");
            _alternate = PlayerRoot.Create(_player, "unregistered duplicate deployment account");
            _registered.ActionPoints = 10;
            _alternate.ActionPoints = 10;
            PlayerRootRegistry.Register(_player, _registered);
            _controllerObject = new GameObject("inactive deployment controller");
            _controllerObject.SetActive(false);
            _selector = _controllerObject.AddComponent<HexSelectionController>();
            _army = new ArmyData { Owner = _player, Hex = new HexCoord(75, -16) };
            _card = new CardDefinition
            {
                cardType = CardType.Unit,
                displayName = "Domain root witness",
                apCost = 2,
                requiredBuildingAbility = UnitAbilities.Barracks,
            };
        }

        [TearDown]
        public void TearDown()
        {
            BuildingRegistry.Clear();
            PlayerRootRegistry.Clear();
            if (_controllerObject != null) Object.DestroyImmediate(_controllerObject);
            if (_alternate != null) Object.DestroyImmediate(_alternate.gameObject);
            if (_registered != null) Object.DestroyImmediate(_registered.gameObject);
        }

        [Test]
        public void SameOwnerAlternateRootCannotPayForDirectDeployment()
        {
            bool deployed = ArmyActions.DeployUnitFromCard(_card, _player, _army, _alternate,
                _selector, out string reason);
            Assert.That(deployed, Is.False);
            Assert.That(reason, Does.Contain("resource owner is invalid"),
                "Setup equality alone must not authorize a separate resource account.");
            Assert.That(_registered.ActionPoints, Is.EqualTo(10));
            Assert.That(_alternate.ActionPoints, Is.EqualTo(10));
            Assert.That(_army.Members, Is.Empty);

            // Positive identity witness: the canonical account advances to the next existing
            // domain prerequisite. No building is installed, so it must fail on the barracks
            // requirement rather than the owner gate; neither path performs a payment.
            deployed = ArmyActions.DeployUnitFromCard(_card, _player, _army, _registered,
                _selector, out reason);
            Assert.That(deployed, Is.False);
            Assert.That(reason, Does.Contain("requires your building"));
            Assert.That(_registered.ActionPoints, Is.EqualTo(10));
            Assert.That(_army.Members, Is.Empty);
        }

        [Test]
        public void RootRemovedFromRegistryCannotPayForDirectDeployment()
        {
            PlayerRootRegistry.Clear();
            bool deployed = ArmyActions.DeployUnitFromCard(_card, _player, _army, _registered,
                _selector, out string reason);
            Assert.That(deployed, Is.False);
            Assert.That(reason, Does.Contain("resource owner is invalid"));
            Assert.That(_registered.ActionPoints, Is.EqualTo(10));
            Assert.That(_army.Members, Is.Empty);
        }
    }
}
#endif
