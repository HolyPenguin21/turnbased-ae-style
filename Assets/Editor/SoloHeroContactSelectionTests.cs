#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Core;
using Game.Combat;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class SoloHeroContactSelectionTests
    {
        private readonly HexCoord _hex = new HexCoord(2, -1);
        private ArmyData _hero;
        private PlayerSetupData _enemy;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            StealthSystem.Clear();
            GameSession.Players = new List<PlayerSetupData>();
            _hero = Army(new PlayerSetupData(), true);
            _enemy = new PlayerSetupData();
            ArmyRegistry.Register(_hero);
        }

        [TearDown]
        public void TearDown()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            StealthSystem.Clear();
            GameSession.Players = new List<PlayerSetupData>();
        }

        private ArmyData Army(PlayerSetupData owner, bool hero, bool hidden = false)
        {
            var army = new ArmyData { Owner = owner, Hex = _hex };
            army.Members.Add(new UnitData { Owner = owner, IsHero = hero, IsHidden = hidden, HitPointsCurrent = 5 });
            return army;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HeroSelectsVisibleEnemyOrNeutralHunter(bool neutral)
        {
            _enemy.IsNeutral = neutral;
            ArmyData hunter = Army(_enemy, false);
            ArmyRegistry.Register(hunter);
            Assert.That(BattleInitiator.FindEnemyAt(_hex, _hero), Is.SameAs(hunter));
        }

        [Test]
        public void HeroDoesNotContactAnotherSoloHero()
        {
            ArmyRegistry.Register(Army(_enemy, true));
            Assert.That(BattleInitiator.FindEnemyAt(_hex, _hero), Is.Null);
        }

        [Test]
        public void HeroSkipsSoloHeroToFindActualHunter()
        {
            ArmyRegistry.Register(Army(_enemy, true));
            ArmyData hunter = Army(_enemy, false);
            ArmyRegistry.Register(hunter);
            Assert.That(BattleInitiator.FindEnemyAt(_hex, _hero), Is.SameAs(hunter));
        }

        [Test]
        public void HeroDoesNotExposeUnseenCombatantBehindVisibleHero()
        {
            ArmyData other = Army(_enemy, true);
            other.Members.Add(new UnitData { Owner = _enemy, IsHidden = true });
            ArmyRegistry.Register(other);
            Assert.That(BattleInitiator.FindEnemyAt(_hex, _hero), Is.Null);
        }

        [Test]
        public void HiddenEnemyCannotHuntOnContact()
        {
            ArmyRegistry.Register(Army(_enemy, false, true));
            Assert.That(BattleInitiator.FindEnemyAt(_hex, _hero), Is.Null);
        }

        [Test]
        public void FriendlyArmyCannotHuntOnContact()
        {
            ArmyRegistry.Register(Army(_hero.Owner, false));
            Assert.That(BattleInitiator.FindEnemyAt(_hex, _hero), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AviationCannotHuntOnContact(bool airfield)
        {
            ArmyData air = Army(_enemy, false);
            air.Members[0].IsAviation = true;
            air.IsAirfield = airfield;
            ArmyRegistry.Register(air);
            Assert.That(BattleInitiator.FindEnemyAt(_hex, _hero), Is.Null);
        }

        [Test]
        public void MultipleHuntersUseStableSelectionWithoutHeroBattleEstimate()
        {
            ArmyData first = Army(_enemy, false);
            ArmyData second = Army(new PlayerSetupData(), false);
            ArmyRegistry.Register(second);
            ArmyRegistry.Register(first);
            Assert.That(BattleInitiator.FindEnemyAt(_hex, _hero), Is.SameAs(first));
            BattleEncounterContext encounter = BattleEncounterCoordinator.PrepareCommittedEncounter(_hex,
                new List<ArmyData> { _hero, first });
            Assert.That(encounter.TargetHeroOnly, Is.True);
            Assert.That(encounter.Initiator, Is.SameAs(first));
        }
    }
}
#endif
