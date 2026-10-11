#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Game.Core;
using Game.HexGrid;
using Game.Map;
using Game.Players;
using Game.Units;
using NUnit.Framework;

namespace Game.EditorTests
{
    public sealed class StealthBuildingContactTests
    {
        private readonly HexCoord _hex = new HexCoord(2, -1);
        private PlayerSetupData _owner;
        private PlayerSetupData _enemy;
        private UnitData _unit;
        private ArmyData _army;
        private BuildingData _building;

        [SetUp]
        public void SetUp()
        {
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            StealthSystem.Clear();
            GameSession.Players = new List<PlayerSetupData>();
            _owner = new PlayerSetupData();
            _enemy = new PlayerSetupData();
            _unit = new UnitData { Owner = _owner, IsHidden = true, HitPointsCurrent = 5 };
            _army = new ArmyData { Owner = _owner, Hex = _hex };
            _army.Members.Add(_unit);
            ArmyRegistry.Register(_army);
            _building = new BuildingData { Owner = _enemy, Hex = _hex, Name = "Extraction" };
            BuildingRegistry.Register(_hex, _building);
        }

        [TearDown]
        public void TearDown()
        {
            Game.Ai.AiMapMemory.Clear();
            ArmyRegistry.Clear();
            BuildingRegistry.Clear();
            VisionSystem.Clear();
            StealthSystem.Clear();
            GameSession.Players = new List<PlayerSetupData>();
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void ResidentRevealResolvesUndefendedBuilding(bool hero, bool isBase)
        {
            _unit.IsHero = hero;
            _building.IsBase = isBase;
            BuildingRegistry.CaptureOrDestroyIfUndefended(_hex, _owner, null, _army);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building), "Hidden arrival must stay passive.");
            Assert.That(_building.Owner, Is.SameAs(_enemy));

            StealthSystem.ExitStealth(_unit);

            Assert.That(_unit.IsHidden, Is.False);
            if (isBase)
            {
                Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
                Assert.That(_building.Owner, Is.SameAs(_owner));
            }
            else
                Assert.That(BuildingRegistry.FindAt(_hex), Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void VisibleGuardPreventsTakeoverOnReveal(bool hero)
        {
            _unit.IsHero = hero;
            var guard = new ArmyData { Owner = _enemy, Hex = _hex };
            guard.Members.Add(new UnitData { Owner = _enemy, HitPointsCurrent = 5 });
            ArmyRegistry.Register(guard);
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
            Assert.That(_building.Owner, Is.SameAs(_enemy));
        }

        [Test]
        public void FirstVisibleMemberActsWhileOtherMembersStayHidden()
        {
            var other = new UnitData { Owner = _owner, IsHidden = true };
            _army.Members.Add(other);
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.Null);
            Assert.That(other.IsHidden, Is.True);
        }

        [Test]
        public void PersonalDetectionDoesNotMakeHiddenResidentAct()
        {
            StealthSystem.MarkDetected(_unit, _enemy);
            BuildingRegistry.CaptureOrDestroyIfUndefended(_hex, _owner, null, _army);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
            Assert.That(_unit.IsHidden, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AviationRevealDoesNotTakeOver(bool airfield)
        {
            _unit.IsAviation = true;
            _army.IsAirfield = airfield;
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
        }

        [Test]
        public void OwnBuildingSurvivesReveal()
        {
            _building.Owner = _owner;
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
        }

        [Test]
        public void RemovedUnitDoesNotAttackBuilding()
        {
            StealthSystem.OnUnitRemoved(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
            Assert.That(_unit.IsHidden, Is.False);
        }

        [Test]
        public void UnregisteredArmyDoesNotTakeOverOnReveal()
        {
            ArmyRegistry.Unregister(_army);
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
        }

        [Test]
        public void RepeatedExitDoesNotDestroyReplacementBuilding()
        {
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.Null);
            BuildingRegistry.Register(_hex, _building);
            StealthSystem.ExitStealth(_unit);
            Assert.That(BuildingRegistry.FindAt(_hex), Is.SameAs(_building));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RevealUpdatesBuildingMemoryAndRouteRevisionBeforeFormerOwnerLosesVision(bool isBase)
        {
            Game.Ai.AiMapMemory.Clear();
            Game.Ai.AiMapMemory.EnsureSubscribed();
            GameSession.Players = new List<PlayerSetupData> { _owner, _enemy };
            _building.IsBase = isBase;
            VisionSystem.NotifyContentChanged(_hex);
            try
            {
                Assert.That(Game.Ai.AiMapMemory.KnownBuildingAt(_owner, _hex)?.Owner, Is.SameAs(_enemy));
                long routeBefore = Game.Ai.AiMapMemory.RouteMemoryVersionFor(_owner);
                int knowledgeBefore = Game.Ai.AiMapMemory.KnowledgeVersionFor(_owner);

                StealthSystem.ExitStealth(_unit);

                Assert.That(Game.Ai.AiMapMemory.RouteMemoryVersionFor(_owner), Is.GreaterThan(routeBefore));
                Assert.That(Game.Ai.AiMapMemory.KnowledgeVersionFor(_owner), Is.GreaterThan(knowledgeBefore));
                Assert.That(Game.Ai.AiMapMemory.KnownGroundArrival(_owner, _army, _hex).HasOutcome, Is.False);
                Assert.That(VisionSystem.IsVisible(_enemy, _hex), Is.False);
                if (isBase)
                {
                    Assert.That(Game.Ai.AiMapMemory.KnownBuildingAt(_owner, _hex)?.Owner, Is.SameAs(_owner));
                    Assert.That(Game.Ai.AiMapMemory.KnownBuildingAt(_enemy, _hex)?.Owner, Is.SameAs(_owner),
                        "Capture must be remembered before the former owner's vision disappears.");
                }
                else
                {
                    Assert.That(Game.Ai.AiMapMemory.KnownBuildingAt(_owner, _hex).HasValue, Is.False);
                    Assert.That(Game.Ai.AiMapMemory.KnownBuildingAt(_enemy, _hex).HasValue, Is.False,
                        "Observed destruction must not leave a stale structure under fog.");
                }
            }
            finally
            {
                Game.Ai.AiMapMemory.Clear();
            }
        }
    }
}
#endif
